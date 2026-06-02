using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.RequestToolService.Data;
using Microsoft.EntityFrameworkCore;

namespace Argus.RequestToolService.Services;

/// <summary>
/// Runs a fuzz attack: generates payload combinations, substitutes variables into the template
/// request, sends each request, extracts user-defined patterns, and stores results.
/// Execution is fully async and runs as a fire-and-forget background task after the run is
/// created. Status + incremental results are written to the DB so the frontend can poll.
/// </summary>
public interface IFuzzExecutor
{
    Task<FuzzRunRecord> CreateRunAsync(Guid sessionId, FuzzRunRequest request, CancellationToken ct);
    Task<FuzzRunRecord?> GetRunAsync(Guid runId, CancellationToken ct);
    Task<FuzzResultsPageDto> GetResultsAsync(Guid runId, int offset, int limit, CancellationToken ct);
    Task StopRunAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<FuzzRunStatusDto>> GetSessionRunsAsync(Guid sessionId, CancellationToken ct);
}

public sealed class FuzzExecutor(
    IDbContextFactory<RequestToolDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<FuzzExecutor> logger) : IFuzzExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Track live cancellation handles keyed by runId
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> LiveRuns = new();

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<FuzzRunRecord> CreateRunAsync(Guid sessionId, FuzzRunRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Validate template exchange exists
        var template = await db.Exchanges.FirstOrDefaultAsync(e => e.ExchangeId == request.TemplateExchangeId, ct)
            ?? throw new InvalidOperationException($"Exchange {request.TemplateExchangeId} not found");

        // Pre-compute total count for status display
        var combinations = FuzzPayloadGenerator.GenerateCombinations(
            request.Variables, request.AttackType, request.MaxRequests).Count();

        var run = new FuzzRunRecord
        {
            RunId = Guid.NewGuid(),
            SessionId = sessionId,
            TemplateExchangeId = request.TemplateExchangeId,
            AttackType = request.AttackType.ToString(),
            Status = "Pending",
            TotalCount = Math.Min(combinations, request.MaxRequests),
            ThrottleMs = Math.Max(0, request.ThrottleMs),
            MaxConcurrent = Math.Clamp(request.MaxConcurrent, 1, 50),
            MaxRequests = Math.Clamp(request.MaxRequests, 1, 10_000),
            VariablesJson = JsonSerializer.Serialize(request.Variables, Json),
            ExtractPatternsJson = JsonSerializer.Serialize(request.ExtractPatterns, Json),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.FuzzRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Fire-and-forget: run the attack in the background
        _ = RunAttackAsync(run.RunId, template, request);

        return run;
    }

    public async Task<FuzzRunRecord?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.FuzzRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct);
    }

    public async Task<FuzzResultsPageDto> GetResultsAsync(Guid runId, int offset, int limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var total = await db.FuzzResults.CountAsync(r => r.RunId == runId, ct);
        var rows = await db.FuzzResults
            .Where(r => r.RunId == runId)
            .OrderBy(r => r.SequenceNumber)
            .Skip(offset)
            .Take(Math.Min(limit, 500))
            .ToListAsync(ct);

        return new FuzzResultsPageDto(
            rows.Select(ToDto).ToList(),
            total,
            offset);
    }

    public async Task StopRunAsync(Guid runId, CancellationToken ct)
    {
        if (LiveRuns.TryGetValue(runId, out var cts))
            cts.Cancel();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.FuzzRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is not null && run.Status is "Running" or "Pending")
        {
            run.Status = "Stopped";
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<FuzzRunStatusDto>> GetSessionRunsAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.FuzzRuns
            .Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new FuzzRunStatusDto(
                r.RunId, r.SessionId, r.Status,
                Enum.Parse<FuzzAttackType>(r.AttackType),
                r.TotalCount, r.CompletedCount,
                r.CreatedAt, r.StartedAt, r.CompletedAt))
            .ToListAsync(ct);
    }

    // ── Attack execution ──────────────────────────────────────────────────────

    private async Task RunAttackAsync(Guid runId, HttpExchangeRecord template, FuzzRunRequest request)
    {
        var cts = new CancellationTokenSource();
        LiveRuns[runId] = cts;
        var ct = cts.Token;

        try
        {
            await UpdateStatusAsync(runId, "Running", startedAt: DateTimeOffset.UtcNow);

            var combinations = FuzzPayloadGenerator.GenerateCombinations(
                request.Variables, request.AttackType, request.MaxRequests).ToList();

            var semaphore = new SemaphoreSlim(request.MaxConcurrent, request.MaxConcurrent);
            var tasks = new List<Task>();
            var seq = 0;

            foreach (var payloads in combinations)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);

                var seqNum = seq++;
                var payloadsCopy = payloads;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await SendOneAsync(runId, template, payloadsCopy, seqNum, request.ExtractPatterns, ct);
                        if (request.ThrottleMs > 0)
                            await Task.Delay(request.ThrottleMs, ct);
                    }
                    catch (OperationCanceledException) { /* stopped */ }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Fuzz request #{Seq} failed", seqNum);
                    }
                    finally
                    {
                        semaphore.Release();
                        await IncrementCompletedAsync(runId);
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
            await UpdateStatusAsync(runId, "Completed", completedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            await UpdateStatusAsync(runId, "Stopped", completedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fuzz run {RunId} failed", runId);
            await UpdateStatusAsync(runId, "Error", completedAt: DateTimeOffset.UtcNow);
        }
        finally
        {
            LiveRuns.TryRemove(runId, out _);
            cts.Dispose();
        }
    }

    private async Task SendOneAsync(
        Guid runId,
        HttpExchangeRecord template,
        IReadOnlyDictionary<string, string> payloads,
        int seq,
        IReadOnlyList<FuzzExtractPattern> extractPatterns,
        CancellationToken ct)
    {
        var url = Substitute(template.RequestUrl, payloads);
        var body = template.RequestBodyInline is not null ? Substitute(template.RequestBodyInline, payloads) : null;

        // Parse stored headers JSON and substitute
        var rawHeaders = JsonSerializer.Deserialize<Dictionary<string, string[]>>(template.RequestHeaders, Json)
                         ?? new Dictionary<string, string[]>();
        var headers = rawHeaders.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(v => Substitute(v, payloads)).ToArray());

        int? statusCode = null;
        long? sizeBytes = null;
        int? durationMs = null;
        string? responseBody = null;
        string? networkError = null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var httpClient = httpClientFactory.CreateClient("request-tool-replay");
            using var msg = new HttpRequestMessage(new HttpMethod(template.RequestMethod), url);
            foreach (var (key, vals) in headers)
            {
                try { msg.Headers.TryAddWithoutValidation(key, vals); }
                catch { /* skip invalid headers */ }
            }
            if (body is not null)
            {
                msg.Content = new StringContent(body, Encoding.UTF8,
                    template.RequestContentType ?? "application/octet-stream");
            }

            using var resp = await httpClient.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            statusCode = (int)resp.StatusCode;
            durationMs = (int)sw.ElapsedMilliseconds;
            var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct);
            sizeBytes = bodyBytes.Length;
            // Store up to 64 KB for extract pattern matching
            responseBody = sizeBytes <= 65536
                ? Encoding.UTF8.GetString(bodyBytes)
                : Encoding.UTF8.GetString(bodyBytes, 0, 65536);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            durationMs = (int)sw.ElapsedMilliseconds;
            networkError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }

        // Apply extract patterns
        var extractValues = new Dictionary<string, string>();
        if (responseBody is not null)
        {
            foreach (var pattern in extractPatterns)
            {
                try
                {
                    var m = Regex.Match(responseBody, pattern.Regex, RegexOptions.None, TimeSpan.FromMilliseconds(500));
                    if (m.Success)
                        extractValues[pattern.Name] = pattern.CaptureGroup1 && m.Groups.Count > 1
                            ? m.Groups[1].Value : m.Value;
                }
                catch { /* invalid regex */ }
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.FuzzResults.Add(new FuzzResultRecord
        {
            ResultId = Guid.NewGuid(),
            RunId = runId,
            SequenceNumber = seq,
            PayloadValuesJson = JsonSerializer.Serialize(payloads, Json),
            StatusCode = statusCode,
            ResponseSizeBytes = sizeBytes,
            DurationMs = durationMs,
            ExtractValuesJson = JsonSerializer.Serialize(extractValues, Json),
            NetworkError = networkError,
            ResponseBody = responseBody,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(CancellationToken.None); // don't cancel mid-write
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Substitute(string template, IReadOnlyDictionary<string, string> payloads)
    {
        var result = template;
        foreach (var (name, value) in payloads)
            result = result.Replace($"§{name}§", value, StringComparison.Ordinal);
        return result;
    }

    private async Task UpdateStatusAsync(Guid runId, string status,
        DateTimeOffset? startedAt = null, DateTimeOffset? completedAt = null)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var run = await db.FuzzRuns.FirstOrDefaultAsync(r => r.RunId == runId);
            if (run is null) return;
            run.Status = status;
            if (startedAt.HasValue) run.StartedAt = startedAt;
            if (completedAt.HasValue) run.CompletedAt = completedAt;
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to update run status"); }
    }

    private async Task IncrementCompletedAsync(Guid runId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.FuzzRuns
                .Where(r => r.RunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CompletedCount, r => r.CompletedCount + 1));
        }
        catch { /* best-effort counter */ }
    }

    private static FuzzResultDto ToDto(FuzzResultRecord r) => new(
        r.ResultId, r.RunId, r.SequenceNumber,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.PayloadValuesJson, Json) ?? new(),
        r.StatusCode, r.ResponseSizeBytes, r.DurationMs,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.ExtractValuesJson, Json) ?? new(),
        r.NetworkError, r.CreatedAt);
}

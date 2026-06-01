using System.Diagnostics;
using System.Text.Json;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<Sublist3rWorker>();

await builder.Build().RunAsync();

internal sealed class Sublist3rWorker : IReconWorker
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "Sublist3rWorker",
        ["Domain"],
        ["Subdomain"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 10);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var domain = WorkerHelpers.GetString(task.InputPayloadJson, "domain");
        if (string.IsNullOrWhiteSpace(domain))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "Missing domain in input payload" }));
        }

        domain = domain.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (!IsValidRootDomain(domain))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "Invalid domain in input payload", domain }));
        }

        var sublist3rPath = Environment.GetEnvironmentVariable("SUBLIST3R_PATH") ?? "sublist3r";
        var timeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("SUBLIST3R_TIMEOUT_MINUTES"), out var parsedTimeout)
            ? parsedTimeout
            : (int)DefaultTimeout.TotalMinutes;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes));

        if (Environment.GetEnvironmentVariable("SUBLIST3R_DISABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            await context.ReportProgressAsync(100, "Sublist3r disabled via configuration", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, disabled = true }));
        }

        await context.ReportProgressAsync(10, $"Checking sublist3r availability for {domain}", null);

        var disabled = Environment.GetEnvironmentVariable("SUBLIST3R_DISABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        if (disabled)
        {
            await context.ReportProgressAsync(100, "Sublist3r disabled via configuration", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, disabled = true }));
        }

        await context.ReportProgressAsync(20, $"Starting subdomain enumeration for {domain}", null);

        var subdomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stderrBuilder = new System.Text.StringBuilder();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python3",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            startInfo.ArgumentList.Add(sublist3rPath);
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(domain);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("/dev/stdout");
            startInfo.ArgumentList.Add("-n");

            using var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var candidate = e.Data.Trim().ToLowerInvariant();
                    if (IsValidSubdomain(candidate, domain))
                    {
                        subdomains.Add(candidate);
                    }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    stderrBuilder.AppendLine(e.Data);
                }
            };

            await context.ReportProgressAsync(30, "Running sublist3r enumeration", null);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(timeoutCts.Token);

            await context.ReportProgressAsync(80, $"Found {subdomains.Count} subdomains", null);
        }
        catch (OperationCanceledException)
        {
            var message = cancellationToken.IsCancellationRequested
                ? "Sublist3r enumeration cancelled"
                : "Sublist3r enumeration timed out";
            await context.ReportProgressAsync(100, message, null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, timedOut = true, subdomains = subdomains.ToList() }));
        }
        catch (Exception ex)
        {
            await context.ReportProgressAsync(100, $"Sublist3r error: {ex.Message}", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, error = ex.Message }));
        }

        var foundSubdomains = subdomains.ToList();
        foundSubdomains.Sort(StringComparer.OrdinalIgnoreCase);

        var payload = JsonSerializer.Serialize(new
        {
            domain,
            subdomains = foundSubdomains,
            count = foundSubdomains.Count,
            source = "sublist3r"
        });

        var assets = foundSubdomains
            .Select(subdomain => new WorkerProducedAsset(
                "Subdomain",
                subdomain,
                null,
                0.85m,
                new Dictionary<string, string>
                {
                    ["source"] = "sublist3r",
                    ["domain"] = domain
                },
                ["sublist3r"]))
            .ToArray();

        await context.ReportProgressAsync(100, $"Sublist3r complete: {foundSubdomains.Count} subdomains found", null);

        return new WorkerProcessResult(false, payload, assets);
    }

    private static bool IsValidRootDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var parts = domain.Split('.');
        return parts.Length >= 2;
    }

    private static bool IsValidSubdomain(string candidate, string rootDomain)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!candidate.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.Equals(rootDomain, StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = candidate.Substring(0, candidate.Length - rootDomain.Length - 1);
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        if (prefix.Contains("..")) return false;
        return true;
    }
}
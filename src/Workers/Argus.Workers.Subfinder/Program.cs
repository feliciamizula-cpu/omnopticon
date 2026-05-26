using System.Diagnostics;
using System.Text.Json;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<SubfinderWorker>();

await builder.Build().RunAsync();

internal sealed class SubfinderWorker : IReconWorker
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "SubfinderWorker",
        ["Domain"],
        ["Subdomain"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 20);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
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

        var subfinderPath = Environment.GetEnvironmentVariable("SUBFINDER_EXE_PATH") ?? "subfinder";
        var timeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("SUBFINDER_TIMEOUT_MINUTES"), out var parsedTimeout)
            ? parsedTimeout
            : (int)DefaultTimeout.TotalMinutes;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes));

        if (Environment.GetEnvironmentVariable("SUBFINDER_DISABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            await context.ReportProgressAsync(100, "Subfinder disabled via configuration", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, disabled = true }));
        }

        await context.ReportProgressAsync(10, $"Checking subfinder availability for {domain}", null);
        if (!await IsToolAvailableAsync(subfinderPath, cancellationToken))
        {
            await context.ReportProgressAsync(100, "Subfinder executable not found", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, error = "subfinder not found" }));
        }

        await context.ReportProgressAsync(25, $"Starting passive subdomain enumeration for {domain}", null);

        var subdomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stderrBuilder = new System.Text.StringBuilder();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = subfinderPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            startInfo.ArgumentList.Add("-silent");
            startInfo.ArgumentList.Add("-all");
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(domain);

            using var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var candidate = e.Data.Trim().TrimEnd('.').ToLowerInvariant();
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

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await context.ReportProgressAsync(65, $"Running subfinder for {domain}", null);

            using var registration = timeoutCts.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });

            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, cancelled = true }));
        }
        catch (OperationCanceledException)
        {
            await context.ReportProgressAsync(100, $"Subfinder timed out for {domain}", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, timed_out = true }));
        }
        catch (Exception ex)
        {
            await context.ReportProgressAsync(100, $"Subfinder failed for {domain}: {ex.GetType().Name}", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, error = ex.Message }));
        }

        var assets = subdomains
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(subdomain => new WorkerProducedAsset(
                "Subdomain",
                subdomain,
                null,
                0.85m,
                new Dictionary<string, string>
                {
                    ["source"] = "subfinder",
                    ["domain"] = domain
                },
                ["subfinder"]))
            .ToArray();

        await context.ReportProgressAsync(100, $"Completed subfinder for {domain}: {assets.Length} subdomains", null);

        return new WorkerProcessResult(
            false,
            JsonSerializer.Serialize(new
            {
                domain,
                produced = assets.Length,
                stderr_preview = Truncate(stderrBuilder.ToString(), 2048)
            }),
            assets);
    }

    private static async Task<bool> IsToolAvailableAsync(string toolPath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-version");

            using var process = new Process
            {
                StartInfo = startInfo
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 || process.ExitCode == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidRootDomain(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 253 || !candidate.Contains('.'))
        {
            return false;
        }

        return candidate.Split('.', StringSplitOptions.RemoveEmptyEntries).All(label =>
            label.Length is > 0 and <= 63
            && label.All(ch => char.IsLetterOrDigit(ch) || ch == '-')
            && label[0] != '-'
            && label[^1] != '-');
    }

    private static bool IsValidSubdomain(string candidate, string rootDomain)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 253)
        {
            return false;
        }

        if (!candidate.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidate.Split('.', StringSplitOptions.RemoveEmptyEntries).All(label =>
            label.Length is > 0 and <= 63
            && label.All(ch => char.IsLetterOrDigit(ch) || ch == '-')
            && label[0] != '-'
            && label[^1] != '-');
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

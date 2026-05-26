using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<AmassWorker>();

await builder.Build().RunAsync();

internal sealed class AmassWorker : IReconWorker
{
    private static readonly Regex SubdomainRegex = new(
        @"^(?![Cc]name|(?:[A-Za-z]+\s+)?(?:[0-9]+\s+){2})([a-zA-Z0-9][a-zA-Z0-9\-\.]*[a-zA-Z0-9])\.([a-zA-Z0-9][a-zA-Z0-9\-\.]*[a-zA-Z0-9])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "AmassWorker",
        ["Domain"],
        ["Subdomain", "DnsRecord"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 10);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var domain = WorkerPayload.GetString(task.InputPayloadJson, "domain");
        if (string.IsNullOrWhiteSpace(domain))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "Missing domain in input payload" }));
        }

        var amassPath = Environment.GetEnvironmentVariable("AMASS_EXE_PATH") ?? "amass";
        var amassTimeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("AMASS_TIMEOUT_MINUTES"), out var t) ? t : 15;
        var timeout = TimeSpan.FromMinutes(amassTimeoutMinutes);

        await context.ReportProgressAsync(10, $"Checking amass availability for {domain}", null);

        var disabled = Environment.GetEnvironmentVariable("AMASS_DISABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        if (disabled)
        {
            await context.ReportProgressAsync(100, "Amass disabled via configuration", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, disabled = true }));
        }

        if (!await IsToolAvailableAsync(amassPath, cancellationToken))
        {
            await context.ReportProgressAsync(100, "Amass executable not found", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, error = "amass not found" }));
        }

        await context.ReportProgressAsync(20, $"Starting passive enumeration for {domain}", null);

        var subdomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dnsRecords = new List<(string subdomain, string recordType, string value)>();
        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = amassPath,
                    Arguments = "enum -passive -timeout 10 -o - -d " + domain,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = "/tmp"
                },
                EnableRaisingEvents = false
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    stdoutBuilder.AppendLine(e.Data);
                    var subdomain = e.Data.Trim();
                    if (IsValidSubdomain(subdomain, domain))
                    {
                        subdomains.Add(subdomain);
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

            await context.ReportProgressAsync(60, $"Running amass enumeration for {domain}", null);

            using var registration = cts.Token.Register(() =>
            {
                try { process.Kill(true); } catch { }
            });

            await process.WaitForExitAsync(cts.Token);

            await context.ReportProgressAsync(90, $"Parsing results for {domain}", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, cancelled = true }));
        }
        catch (OperationCanceledException)
        {
            await context.ReportProgressAsync(100, $"Amass timed out for {domain}", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, timed_out = true }));
        }
        catch (Exception ex)
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, error = ex.Message }));
        }

        var stderr = stderrBuilder.ToString();
        if (!string.IsNullOrWhiteSpace(stderr) && stderr.Contains("No API keys"))
        {
            // passive mode works without keys, just log
        }

        var rawOutput = stdoutBuilder.ToString();
        var artifactHash = ComputeHash(rawOutput);
        var artifactReference = new ArtifactReference("amass_output", $"{domain}-amass-output.txt", artifactHash);

        var assets = new List<WorkerProducedAsset>();
        foreach (var subdomain in subdomains)
        {
            assets.Add(new WorkerProducedAsset(
                "Subdomain",
                subdomain,
                null,
                null,
                new Dictionary<string, string> { ["source"] = "amass", ["domain"] = domain },
                ["amass"],
                [artifactReference]));
        }

        var produced = assets.Count;
        var summary = JsonSerializer.Serialize(new
        {
            domain,
            subdomains_found = produced,
            tool_output_size = rawOutput.Length
        });

        await context.ReportProgressAsync(100, $"Completed amass for {domain}: {produced} subdomains", null);

        return new WorkerProcessResult(
            false,
            summary,
            assets);
    }

    private static async Task<bool> IsToolAvailableAsync(string toolPath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = toolPath,
                    Arguments = "version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = false
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidSubdomain(string candidate, string baseDomain)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var lower = candidate.ToLowerInvariant();

        if (lower.Contains("->") || lower.StartsWith("[-]")) return false;

        if (candidate.StartsWith("[") && candidate.Contains("]")) return false;

        if (!candidate.Contains('.')) return false;

        if (candidate.Length > 253) return false;

        var parts = candidate.Split('.');
        if (parts.Length < 2) return false;

        return lower.EndsWith("." + baseDomain.ToLowerInvariant()) ||
               lower == baseDomain.ToLowerInvariant();
    }

    private static string ComputeHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal static class WorkerPayload
{
    public static string? GetString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }
}
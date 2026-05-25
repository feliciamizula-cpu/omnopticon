using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Assets;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient("asset-service");
builder.AddArgusWorker<RegexScannerWorker>();

await builder.Build().RunAsync();

internal sealed class RegexScannerWorker(IHttpClientFactory httpClientFactory) : IReconWorker
{
    private const int MaxScannedCharacters = 262_144;
    private const int MaxFindingsPerPattern = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyCollection<ScannerPattern> Patterns =
    [
        new(
            "aws-access-key-id",
            "AWS access key ID exposed",
            "SecretAwsKey",
            "high",
            95,
            new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),

        new(
            "google-api-key",
            "Google API key exposed",
            "SecretApiKey",
            "high",
            90,
            new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled)),

        new(
            "slack-token",
            "Slack token exposed",
            "SecretApiKey",
            "high",
            90,
            new Regex(@"\bxox[baprs]-[0-9A-Za-z-]{10,}\b", RegexOptions.Compiled)),

        new(
            "private-key",
            "Private key material exposed",
            "SecretPrivateKey",
            "critical",
            100,
            new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", RegexOptions.Compiled)),

        new(
            "jwt-token",
            "JWT token exposed",
            "SecretJwt",
            "medium",
            70,
            new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled)),

        new(
            "secret-assignment",
            "Secret-looking assignment exposed",
            "SecretGeneric",
            "high",
            85,
            new Regex(@"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token|authorization|bearer)\b\s*[:=]\s*['""]?[^'""\s]{8,}", RegexOptions.Compiled)),

        new(
            "env-file",
            "Environment file exposed",
            "ExposedEnvFile",
            "high",
            85,
            new Regex(@"(?i)(?:^|[/\\])\.env(?:\.[A-Za-z0-9_.-]+)?(?:$|[?#/\s])", RegexOptions.Compiled)),

        new(
            "source-control-folder",
            "Source control folder exposed",
            "ExposedSourceControl",
            "high",
            85,
            new Regex(@"(?i)(?:^|[/\\])\.(?:git|svn|hg)(?:/|$|[?#\s])", RegexOptions.Compiled)),

        new(
            "s3-bucket-url",
            "S3 bucket URL exposed",
            "S3BucketExposure",
            "medium",
            70,
            new Regex(@"(?i)\b(?:s3://[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]|https?://[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\.s3[.-][a-z0-9-]+\.amazonaws\.com|https?://s3[.-][a-z0-9-]+\.amazonaws\.com/[a-z0-9][a-z0-9.-]{1,61}[a-z0-9])\b", RegexOptions.Compiled)),

        new(
            "interesting-sensitive-path",
            "Sensitive path exposed",
            "SensitivePath",
            "medium",
            60,
            new Regex(@"(?i)(?:^|/)(?:backup|backups|dump|db|database|config|credentials|secrets?)(?:\.(?:zip|tar|gz|sql|bak|json|yml|yaml|txt|env))?(?:$|[?#/\s])", RegexOptions.Compiled))
    ];

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "RegexScannerWorker",
        ["Url", "ApiEndpoint", "HtmlPage", "JsonDocument", "JavaScriptFile", "HttpResponse", "Observation"],
        ["FindingCandidate"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 40);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(5, "Preparing regex scan", null);

        var assetId = TryGetAssetId(task.InputPayloadJson) ?? task.InputAssetId;
        var asset = assetId.HasValue
            ? await FetchAssetAsync(assetId.Value, cancellationToken)
            : null;

        var sourceValue = WorkerHelpers.GetString(task.InputPayloadJson, "value")
            ?? asset?.Value
            ?? task.InputPayloadJson
            ?? string.Empty;

        var sourceType = WorkerHelpers.GetString(task.InputPayloadJson, "assetType")
            ?? asset?.Type.ToString()
            ?? "Unknown";

        var scanText = BuildScanText(task.InputPayloadJson, asset);
        if (scanText.Length > MaxScannedCharacters)
        {
            scanText = scanText[..MaxScannedCharacters];
        }

        await context.ReportProgressAsync(30, $"Scanning {scanText.Length} characters from {sourceType}", null);

        var findings = new List<WorkerProducedAsset>();
        foreach (var pattern in Patterns)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var matchesForPattern = 0;

            foreach (Match match in pattern.Regex.Matches(scanText))
            {
                if (!match.Success || string.IsNullOrWhiteSpace(match.Value))
                {
                    continue;
                }

                var raw = match.Value.Trim();
                var fingerprint = $"{pattern.Id}:{raw}";
                if (!seen.Add(fingerprint))
                {
                    continue;
                }

                findings.Add(CreateFindingCandidate(pattern, sourceType, sourceValue, raw, scanText, match.Index, assetId));
                matchesForPattern++;

                if (matchesForPattern >= MaxFindingsPerPattern)
                {
                    break;
                }
            }
        }

        await context.ReportProgressAsync(100, $"Regex scan complete: {findings.Count} candidates", null);

        var summary = JsonSerializer.Serialize(new
        {
            sourceAssetId = assetId,
            sourceType,
            sourceValue,
            findingCandidates = findings.Count
        }, JsonOptions);

        return new WorkerProcessResult(false, summary, findings);
    }

    private async Task<AssetDto?> FetchAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("asset-service");
        var baseAddress = Environment.GetEnvironmentVariable("ARGUS_ASSET_SERVICE");
        client.BaseAddress = Uri.TryCreate(baseAddress, UriKind.Absolute, out var serviceUri)
            ? serviceUri
            : new Uri("http://asset-service");

        using var response = await client.GetAsync($"/assets/{assetId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<AssetDto>(JsonOptions, cancellationToken);
    }

    private static Guid? TryGetAssetId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty("assetId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string BuildScanText(string? payloadJson, AssetDto? asset)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            parts.Add(payloadJson);
        }

        if (asset is not null)
        {
            parts.Add(asset.Value);
            parts.Add(asset.Type.ToString());
            if (!string.IsNullOrWhiteSpace(asset.Subtype))
            {
                parts.Add(asset.Subtype);
            }

            foreach (var pair in asset.Metadata)
            {
                parts.Add(pair.Key);
                parts.Add(pair.Value);
            }

            foreach (var tag in asset.Tags)
            {
                parts.Add(tag);
            }
        }

        return string.Join('\n', parts);
    }

    private static WorkerProducedAsset CreateFindingCandidate(
        ScannerPattern pattern,
        string sourceType,
        string sourceValue,
        string rawMatch,
        string scanText,
        int matchIndex,
        Guid? sourceAssetId)
    {
        var redacted = Redact(rawMatch);
        var snippet = BuildSnippet(scanText, matchIndex, rawMatch.Length, redacted);
        var value = $"{pattern.Title} in {sourceValue}";
        if (value.Length > 1024)
        {
            value = value[..1024];
        }

        return new WorkerProducedAsset(
            "FindingCandidate",
            value,
            pattern.Subtype,
            0.95m,
            new Dictionary<string, string>
            {
                ["finding_type"] = pattern.Id,
                ["title"] = pattern.Title,
                ["severity"] = pattern.Severity,
                ["risk_score"] = pattern.RiskScore.ToString(),
                ["source_asset_id"] = sourceAssetId?.ToString("N") ?? string.Empty,
                ["source_asset_type"] = sourceType,
                ["source_value"] = sourceValue,
                ["matched_preview"] = redacted,
                ["evidence_snippet"] = snippet,
                ["reportable"] = "true"
            },
            ["finding-candidate", "regex", pattern.Id, pattern.Severity]);
    }

    private static string Redact(string value)
    {
        if (value.Length <= 8)
        {
            return "***";
        }

        return $"{value[..Math.Min(4, value.Length)]}***{value[^Math.Min(4, value.Length)..]}";
    }

    private static string BuildSnippet(string text, int matchIndex, int matchLength, string redacted)
    {
        var start = Math.Max(0, matchIndex - 80);
        var end = Math.Min(text.Length, matchIndex + matchLength + 80);
        var snippet = text[start..end].ReplaceLineEndings(" ");
        var raw = text.Substring(matchIndex, Math.Min(matchLength, text.Length - matchIndex));
        return snippet.Replace(raw, redacted, StringComparison.Ordinal);
    }
}

internal sealed record ScannerPattern(
    string Id,
    string Title,
    string Subtype,
    string Severity,
    int RiskScore,
    Regex Regex);

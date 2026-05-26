using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient("js-extractor");
builder.AddArgusWorker<JsEndpointExtractorWorker>();

await builder.Build().RunAsync();

internal sealed partial class JsEndpointExtractorWorker(IHttpClientFactory httpClientFactory) : IReconWorker
{
    private const int MaxScriptBytes = 2 * 1024 * 1024;
    private const int MaxProducedAssets = 250;
    private const int MaxFindingCandidates = 25;

    private static readonly Regex FetchEndpointRegex = FetchEndpointRegexGenerated();
    private static readonly Regex AbsoluteUrlRegex = AbsoluteUrlRegexGenerated();
    private static readonly Regex QuotedPathRegex = QuotedPathRegexGenerated();
    private static readonly Regex SecretAssignmentRegex = SecretAssignmentRegexGenerated();

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "JsExtractorWorker",
        ["JavaScriptFile"],
        ["ApiEndpoint", "Url", "JavaScriptFile", "FindingCandidate"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 30);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var jsUrl = WorkerHelpers.GetString(task.InputPayloadJson, "url")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "value");

        if (!Uri.TryCreate(jsUrl, UriKind.Absolute, out var scriptUri))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "A valid absolute JavaScript URL is required", jsUrl }));
        }

        var host = scriptUri.Host;

        await context.ReportProgressAsync(10, $"Checking fetch quota for {host}", null);
        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerHelpers.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { jsUrl, delayed = true }), []);
        }

        await context.ReportProgressAsync(25, $"Fetching JavaScript from {jsUrl}", null);
        var script = await FetchScriptAsync(scriptUri, cancellationToken);

        await context.ReportProgressAsync(60, $"Extracting endpoints and secrets from {jsUrl}", null);

        var produced = new List<WorkerProducedAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in ExtractEndpoints(script, scriptUri).Take(MaxProducedAssets))
        {
            if (!seen.Add(endpoint))
            {
                continue;
            }

            var assetType = DetermineAssetType(endpoint);
            produced.Add(new WorkerProducedAsset(
                assetType,
                endpoint,
                null,
                0.75m,
                new Dictionary<string, string>
                {
                    ["source"] = "javascript",
                    ["source_url"] = jsUrl
                },
                ["javascript", "extracted"]));
        }

        var findings = 0;
        foreach (Match match in SecretAssignmentRegex.Matches(script))
        {
            if (!match.Success || findings >= MaxFindingCandidates)
            {
                break;
            }

            var raw = match.Value.Trim();
            if (raw.Length < 12)
            {
                continue;
            }

            produced.Add(new WorkerProducedAsset(
                "FindingCandidate",
                $"Possible secret in {jsUrl}",
                "PossibleSecret",
                0.8m,
                new Dictionary<string, string>
                {
                    ["source"] = jsUrl,
                    ["scanner"] = "js-extractor",
                    ["matched_preview"] = Redact(raw),
                    ["evidence_snippet"] = BuildSnippet(script, match.Index, match.Length, Redact(raw)),
                    ["reportable"] = "true"
                },
                ["finding-candidate", "javascript", "secret"]));
            findings++;
        }

        await context.ReportProgressAsync(100, $"Extracted {produced.Count} assets from {jsUrl}", null);

        return new WorkerProcessResult(
            false,
            JsonSerializer.Serialize(new
            {
                jsUrl,
                scriptBytes = script.Length,
                produced = produced.Count,
                findingCandidates = findings
            }),
            produced);
    }

    private async Task<string> FetchScriptAsync(Uri scriptUri, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("js-extractor");
        using var request = new HttpRequestMessage(HttpMethod.Get, scriptUri);
        request.Headers.Accept.ParseAdd("application/javascript, text/javascript, */*");
        request.Headers.UserAgent.ParseAdd("Argus-JsExtractor/1.0");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxScriptBytes)
            {
                memory.Write(buffer, 0, Math.Max(0, read - (total - MaxScriptBytes)));
                break;
            }

            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IEnumerable<string> ExtractEndpoints(string script, Uri baseUri)
    {
        foreach (Match match in AbsoluteUrlRegex.Matches(script))
        {
            if (match.Success && TryResolveCandidate(match.Value, baseUri, out var resolved))
            {
                yield return resolved;
            }
        }

        foreach (Match match in FetchEndpointRegex.Matches(script))
        {
            var value = FirstNonEmptyGroup(match);
            if (TryResolveCandidate(value, baseUri, out var resolved))
            {
                yield return resolved;
            }
        }

        foreach (Match match in QuotedPathRegex.Matches(script))
        {
            var value = FirstNonEmptyGroup(match);
            if (TryResolveCandidate(value, baseUri, out var resolved))
            {
                yield return resolved;
            }
        }
    }

    private static string DetermineAssetType(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;
            if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                return "JavaScriptFile";
            }

            if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
                || path.Contains("graphql", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return "ApiEndpoint";
            }
        }

        return "Url";
    }

    private static string FirstNonEmptyGroup(Match match)
    {
        for (var i = 1; i < match.Groups.Count; i++)
        {
            var value = match.Groups[i].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return match.Value;
    }

    private static bool TryResolveCandidate(string candidate, Uri baseUri, out string resolved)
    {
        resolved = string.Empty;
        candidate = TrimCandidate(candidate);

        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, candidate, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        resolved = uri.ToString();
        return true;
    }

    private static string TrimCandidate(string value) =>
        value.Trim().Trim('"', '\'', '`', ',', ';', ')', ']', '}');

    private static string Redact(string value)
    {
        if (value.Length <= 10)
        {
            return "***";
        }

        return $"{value[..5]}***{value[^5..]}";
    }

    private static string BuildSnippet(string text, int matchIndex, int matchLength, string redacted)
    {
        var start = Math.Max(0, matchIndex - 80);
        var end = Math.Min(text.Length, matchIndex + matchLength + 80);
        var snippet = text[start..end].ReplaceLineEndings(" ");
        var raw = text.Substring(matchIndex, Math.Min(matchLength, text.Length - matchIndex));
        return snippet.Replace(raw, redacted, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?:https?:)?//[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AbsoluteUrlRegexGenerated();

    [GeneratedRegex(@"(?:fetch|axios|request|open)\s*\(\s*['""`]([^'""`]+)['""`]|(?:\.get|\.post|\.put|\.delete|\.patch)\s*\(\s*['""`]([^'""`]+)['""`]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FetchEndpointRegexGenerated();

    [GeneratedRegex(@"['""`](/(?:api|graphql|rest|v\d+|assets|static|js|admin|internal|oauth|auth|\.well-known)[^'""`\s]*)['""`]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex QuotedPathRegexGenerated();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token|authorization|bearer)\b\s*[:=]\s*['""][^'""\s]{8,}['""]", RegexOptions.Compiled)]
    private static partial Regex SecretAssignmentRegexGenerated();
}

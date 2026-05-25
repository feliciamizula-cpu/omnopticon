using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient("headless-spider");
builder.AddArgusWorker<HeadlessSpiderWorker>();

await builder.Build().RunAsync();

internal sealed partial class HeadlessSpiderWorker(IHttpClientFactory httpClientFactory) : IReconWorker
{
    private const int MaxHtmlBytes = 2 * 1024 * 1024;
    private const int MaxProducedAssets = 250;

    private static readonly Regex DomUrlRegex = DomUrlRegexGenerated();
    private static readonly Regex InlineEndpointRegex = InlineEndpointRegexGenerated();

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HeadlessSpiderWorker",
        ["Url"],
        ["Url", "ApiEndpoint", "JavaScriptFile"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 10);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var url = WorkerHelpers.GetString(task.InputPayloadJson, "url")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "value");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "A valid absolute URL is required", url }));
        }

        var host = baseUri.Host;

        await context.ReportProgressAsync(10, $"Checking browser crawl quota for {host}", "{\"stage\":\"quota\"}");
        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerHelpers.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { url, delayed = true }), []);
        }

        await context.ReportProgressAsync(35, $"Fetching page for crawl fallback {url}", "{\"stage\":\"fetch\"}");
        var html = await FetchHtmlAsync(baseUri, cancellationToken);

        await context.ReportProgressAsync(70, $"Extracting DOM and inline endpoints from {url}", "{\"stage\":\"extract\"}");

        var produced = new List<WorkerProducedAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ExtractCandidates(html, baseUri).Take(MaxProducedAssets))
        {
            if (!seen.Add(candidate))
            {
                continue;
            }

            var assetType = DetermineAssetType(candidate);
            produced.Add(new WorkerProducedAsset(
                assetType,
                candidate,
                null,
                0.7m,
                new Dictionary<string, string>
                {
                    ["source"] = "headless-spider",
                    ["source_url"] = url,
                    ["mode"] = "http-fallback"
                },
                ["headless-spider", "extracted"]));
        }

        await context.ReportProgressAsync(100, $"Headless spider extracted {produced.Count} assets from {url}", null);

        return new WorkerProcessResult(
            false,
            JsonSerializer.Serialize(new
            {
                url,
                mode = "http-fallback",
                produced = produced.Count,
                note = "Browser automation package is not configured; used deterministic HTTP extraction."
            }),
            produced);
    }

    private async Task<string> FetchHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("headless-spider");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, */*");
        request.Headers.UserAgent.ParseAdd("Argus-HeadlessSpider/1.0");

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
            if (total > MaxHtmlBytes)
            {
                memory.Write(buffer, 0, Math.Max(0, read - (total - MaxHtmlBytes)));
                break;
            }

            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IEnumerable<string> ExtractCandidates(string html, Uri baseUri)
    {
        foreach (Match match in DomUrlRegex.Matches(html))
        {
            var candidate = FirstNonEmptyGroup(match);
            if (TryResolveCandidate(candidate, baseUri, out var resolved))
            {
                yield return resolved;
            }
        }

        foreach (Match match in InlineEndpointRegex.Matches(html))
        {
            var candidate = FirstNonEmptyGroup(match);
            if (TryResolveCandidate(candidate, baseUri, out var resolved))
            {
                yield return resolved;
            }
        }
    }

    private static string DetermineAssetType(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Url";
        }

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
        candidate = candidate.Trim().Trim('"', '\'', '`', ',', ';', ')', ']', '}');

        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.StartsWith("#", StringComparison.Ordinal)
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

    [GeneratedRegex(@"(?:href|src|action)\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DomUrlRegexGenerated();

    [GeneratedRegex(@"(?:fetch|axios|request|open)\s*\(\s*['""`]([^'""`]+)['""`]|(?:\.get|\.post|\.put|\.delete|\.patch)\s*\(\s*['""`]([^'""`]+)['""`]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InlineEndpointRegexGenerated();
}

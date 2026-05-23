using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using HtmlAgilityPack;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HtmlDomSpiderWorker>();

await builder.Build().RunAsync();

internal sealed class HtmlDomSpiderWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HtmlDomSpiderWorker(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private static readonly Regex ApiEndpointPattern = new(
        @"^/api/(?:v\d+/)?[\w\-/.]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JsUrlPattern = new(
        @"(?:fetch|axios|request)\s*\(\s*['""]([^'""]+)['""]\)|\.get\s*\(\s*['""]([^'""]+)['""]\)|\.post\s*\(\s*['""]([^'""]+)['""]\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HtmlDomSpiderWorker",
        ["Url", "HtmlPage"],
        ["Url", "ApiEndpoint", "JavaScriptFile", "CssFile", "Form", "Observation"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 25);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var url = WorkerHelpers.GetString(task.InputPayloadJson, "url") ??
                  WorkerHelpers.GetString(task.InputPayloadJson, "htmlUrl");
        var artifactKey = WorkerHelpers.GetString(task.InputPayloadJson, "artifactKey");
        var depth = WorkerHelpers.GetInt(task.InputPayloadJson, "depth") ?? 0;

        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(artifactKey))
        {
            throw new InvalidOperationException("Either 'url' or 'artifactKey' must be provided in the task payload.");
        }

        var baseUrlText = !string.IsNullOrWhiteSpace(url)
            ? url
            : WorkerHelpers.GetString(task.InputPayloadJson, "baseUrl") ??
              WorkerHelpers.GetString(task.InputPayloadJson, "sourceUrl") ??
              "https://artifact.local/";

        if (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out var baseUrl))
        {
            throw new InvalidOperationException("A valid absolute 'url', 'baseUrl', or 'sourceUrl' is required when parsing HTML content.");
        }

        var host = baseUrl.Host;

        await context.ReportProgressAsync(10, $"Checking crawl quota for {host}", $"{{\"depth\":{depth}}}");

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

        await context.ReportProgressAsync(25, $"Fetching HTML for {url ?? artifactKey}", null);

        string htmlContent;
        if (!string.IsNullOrWhiteSpace(artifactKey))
        {
            htmlContent = await FetchFromArtifactAsync(artifactKey, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            htmlContent = await FetchFromUrlAsync(url, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Unable to determine HTML source.");
        }

        await context.ReportProgressAsync(50, $"Parsing DOM links from {url ?? artifactKey}", null);

        var parsedResult = ParseHtml(htmlContent, baseUrl);

        var producedAssets = new List<WorkerProducedAsset>();
        var observationEntries = new List<Dictionary<string, string>>();

        foreach (var discoveredUrl in parsedResult.Links)
        {
            var assetType = DetermineAssetType(discoveredUrl);
            var isInScope = IsUrlInScope(discoveredUrl, task.ScopeId);

            producedAssets.Add(new WorkerProducedAsset(
                assetType,
                discoveredUrl,
                null,
                isInScope ? 0.85m : 0.5m,
                new Dictionary<string, string>
                {
                    ["source"] = "html-dom",
                    ["sourceUrl"] = url ?? string.Empty,
                    ["depth"] = depth.ToString(),
                    ["inScope"] = isInScope.ToString().ToLowerInvariant()
                },
                isInScope ? new[] { "crawled", "in-scope" } : new[] { "crawled", "out-of-scope" }));

            observationEntries.Add(new Dictionary<string, string>
            {
                ["type"] = "link",
                ["value"] = discoveredUrl,
                ["assetType"] = assetType
            });
        }

        foreach (var jsFile in parsedResult.ScriptSources)
        {
            producedAssets.Add(new WorkerProducedAsset(
                "JavaScriptFile",
                jsFile,
                null,
                0.9m,
                new Dictionary<string, string>
                {
                    ["source"] = "script-tag",
                    ["sourceUrl"] = url ?? string.Empty
                },
                new[] { "js", "resource" }));

            observationEntries.Add(new Dictionary<string, string>
            {
                ["type"] = "script",
                ["value"] = jsFile
            });
        }

        foreach (var cssFile in parsedResult.CssLinks)
        {
            producedAssets.Add(new WorkerProducedAsset(
                "CssFile",
                cssFile,
                null,
                0.75m,
                new Dictionary<string, string>
                {
                    ["source"] = "link-tag",
                    ["sourceUrl"] = url ?? string.Empty
                },
                new[] { "css", "resource" }));

            observationEntries.Add(new Dictionary<string, string>
            {
                ["type"] = "stylesheet",
                ["value"] = cssFile
            });
        }

        foreach (var form in parsedResult.Forms)
        {
            producedAssets.Add(new WorkerProducedAsset(
                "Form",
                form.Action,
                form.Method.ToUpperInvariant(),
                0.8m,
                new Dictionary<string, string>
                {
                    ["source"] = "form-tag",
                    ["sourceUrl"] = url ?? string.Empty
                },
                new[] { "form", "interactive" }));

            observationEntries.Add(new Dictionary<string, string>
            {
                ["type"] = "form",
                ["action"] = form.Action,
                ["method"] = form.Method
            });
        }

        foreach (var endpoint in parsedResult.ApiEndpointCandidates)
        {
            producedAssets.Add(new WorkerProducedAsset(
                "ApiEndpoint",
                endpoint,
                "REST",
                0.7m,
                new Dictionary<string, string>
                {
                    ["source"] = "html-href",
                    ["sourceUrl"] = url ?? string.Empty
                },
                new[] { "api", "candidate" }));

            observationEntries.Add(new Dictionary<string, string>
            {
                ["type"] = "api-candidate",
                ["value"] = endpoint
            });
        }

        if (parsedResult.MetaRefreshUrl != null)
        {
            producedAssets.Add(new WorkerProducedAsset(
                "Url",
                parsedResult.MetaRefreshUrl,
                "MetaRefresh",
                0.7m,
                new Dictionary<string, string>
                {
                    ["source"] = "meta-refresh",
                    ["sourceUrl"] = url ?? string.Empty
                },
                new[] { "redirect", "meta" }));

            observationEntries.Add(new Dictionary<string, string>
            {
                ["type"] = "meta-redirect",
                ["value"] = parsedResult.MetaRefreshUrl
            });
        }

        if (parsedResult.IframeSrcs.Count > 0)
        {
            foreach (var iframeSrc in parsedResult.IframeSrcs)
            {
                producedAssets.Add(new WorkerProducedAsset(
                    "Url",
                    iframeSrc,
                    "Iframe",
                    0.65m,
                    new Dictionary<string, string>
                    {
                        ["source"] = "iframe-src",
                        ["sourceUrl"] = url ?? string.Empty
                    },
                    new[] { "iframe", "embedded" }));

                observationEntries.Add(new Dictionary<string, string>
                {
                    ["type"] = "iframe",
                    ["value"] = iframeSrc
                });
            }
        }

        if (observationEntries.Count > 0)
        {
            var observationArtifact = new WorkerProducedAsset(
                "Observation",
                $"DOM links parsed from {url ?? artifactKey}",
                "HtmlDomSpider",
                1.0m,
                new Dictionary<string, string>
                {
                    ["parsed.count"] = observationEntries.Count.ToString(),
                    ["links.count"] = parsedResult.Links.Count.ToString(),
                    ["scripts.count"] = parsedResult.ScriptSources.Count.ToString(),
                    ["css.count"] = parsedResult.CssLinks.Count.ToString(),
                    ["forms.count"] = parsedResult.Forms.Count.ToString(),
                    ["source"] = "html-dom-parse",
                    ["sourceUrl"] = url ?? string.Empty
                },
                new[] { "observation", "dom-parse" });

            producedAssets.Add(observationArtifact);
        }

        await context.ReportProgressAsync(100, $"Extracted {producedAssets.Count} assets from {url ?? artifactKey}", null);

        var outputSummary = JsonSerializer.Serialize(new
        {
            url,
            depth,
            produced = producedAssets.Count,
            links = parsedResult.Links.Count,
            scripts = parsedResult.ScriptSources.Count,
            css = parsedResult.CssLinks.Count,
            forms = parsedResult.Forms.Count,
            apiCandidates = parsedResult.ApiEndpointCandidates.Count
        });

        return new WorkerProcessResult(false, outputSummary, producedAssets);
    }

    private async Task<string> FetchFromUrlAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("html-spider");
        client.Timeout = TimeSpan.FromSeconds(30);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ArgusRecon/1.0 (bug-bounty-recon)");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html";

        var encoding = System.Text.Encoding.UTF8;
        if (contentType.Contains("charset", StringComparison.OrdinalIgnoreCase))
        {
            var charset = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    encoding = System.Text.Encoding.GetEncoding(charset);
                }
                catch
                {
                    encoding = System.Text.Encoding.UTF8;
                }
            }
        }

        return encoding.GetString(bytes);
    }

    private async Task<string> FetchFromArtifactAsync(string artifactKey, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(artifactKey, UriKind.Absolute, out var artifactUri)
            && (artifactUri.Scheme == Uri.UriSchemeHttp || artifactUri.Scheme == Uri.UriSchemeHttps))
        {
            return await FetchFromUrlAsync(artifactKey, cancellationToken);
        }

        if (File.Exists(artifactKey))
        {
            return await File.ReadAllTextAsync(artifactKey, cancellationToken);
        }

        var artifactRoot = Environment.GetEnvironmentVariable("ARGUS_ARTIFACT_ROOT");
        if (!string.IsNullOrWhiteSpace(artifactRoot))
        {
            var relativeKey = artifactKey
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            var rootedPath = Path.GetFullPath(Path.Combine(artifactRoot, relativeKey));
            var fullRoot = Path.GetFullPath(artifactRoot);
            var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (rootedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(rootedPath))
            {
                return await File.ReadAllTextAsync(rootedPath, cancellationToken);
            }
        }

        if (Guid.TryParse(artifactKey, out var artifactId))
        {
            var client = _httpClientFactory.CreateClient("artifact");
            client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("ARGUS_ARTIFACT_SERVICE") ?? "http://artifact-service");

            var preview = await client.GetFromJsonAsync<JsonObject>($"/artifacts/{artifactId}/preview", cancellationToken);
            var previewText = preview?["previewText"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(previewText))
            {
                return previewText;
            }
        }

        throw new InvalidOperationException(
            "Unable to fetch HTML artifact. Provide an absolute artifact URL, a readable file path, a key under ARGUS_ARTIFACT_ROOT, or an artifact id with preview text.");
    }

    private static HtmlParseResult ParseHtml(string html, Uri baseUri)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new HtmlParseResult();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchorNodes != null)
        {
            foreach (var node in anchorNodes)
            {
                var href = node.GetAttributeValue("href", null);
                if (string.IsNullOrWhiteSpace(href))
                    continue;

                var normalizedUrl = NormalizeUrl(href, baseUri);
                if (!string.IsNullOrEmpty(normalizedUrl) && seenUrls.Add(normalizedUrl))
                {
                    result.Links.Add(normalizedUrl);

                    if (IsLikelyApiEndpoint(href))
                    {
                        result.ApiEndpointCandidates.Add(normalizedUrl);
                    }
                }
            }
        }

        var scriptNodes = doc.DocumentNode.SelectNodes("//script[@src]");
        if (scriptNodes != null)
        {
            foreach (var node in scriptNodes)
            {
                var src = node.GetAttributeValue("src", null);
                if (string.IsNullOrWhiteSpace(src))
                    continue;

                var normalizedUrl = NormalizeUrl(src, baseUri);
                if (!string.IsNullOrEmpty(normalizedUrl) && seenUrls.Add(normalizedUrl))
                {
                    result.ScriptSources.Add(normalizedUrl);
                }
            }
        }

        var linkNodes = doc.DocumentNode.SelectNodes("//link[@href]");
        if (linkNodes != null)
        {
            foreach (var node in linkNodes)
            {
                var rel = node.GetAttributeValue("rel", null) ?? string.Empty;
                var href = node.GetAttributeValue("href", null);

                if (string.IsNullOrWhiteSpace(href))
                    continue;

                if (rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedUrl = NormalizeUrl(href, baseUri);
                    if (!string.IsNullOrEmpty(normalizedUrl) && seenUrls.Add(normalizedUrl))
                    {
                        result.CssLinks.Add(normalizedUrl);
                    }
                }
            }
        }

        var formNodes = doc.DocumentNode.SelectNodes("//form[@action]");
        if (formNodes != null)
        {
            foreach (var node in formNodes)
            {
                var action = node.GetAttributeValue("action", null);
                var method = node.GetAttributeValue("method", "get");

                if (string.IsNullOrWhiteSpace(action))
                    action = baseUri.ToString().TrimEnd('/');

                var normalizedUrl = NormalizeUrl(action, baseUri);
                if (!string.IsNullOrEmpty(normalizedUrl))
                {
                    result.Forms.Add(new FormInfo(normalizedUrl, method));
                }
            }
        }

        var iframeNodes = doc.DocumentNode.SelectNodes("//iframe[@src]");
        if (iframeNodes != null)
        {
            foreach (var node in iframeNodes)
            {
                var src = node.GetAttributeValue("src", null);
                if (string.IsNullOrWhiteSpace(src))
                    continue;

                var normalizedUrl = NormalizeUrl(src, baseUri);
                if (!string.IsNullOrEmpty(normalizedUrl) && seenUrls.Add(normalizedUrl))
                {
                    result.IframeSrcs.Add(normalizedUrl);
                }
            }
        }

        var metaRefresh = doc.DocumentNode.SelectSingleNode("//meta[@http-equiv='refresh' or @http-equiv='Refresh']");
        if (metaRefresh != null)
        {
            var content = metaRefresh.GetAttributeValue("content", null);
            if (!string.IsNullOrWhiteSpace(content))
            {
                var urlMatch = Regex.Match(content, @"url\s*=\s*['""]?([^'""]+)", RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                {
                    var refreshUrl = urlMatch.Groups[1].Value;
                    var normalizedUrl = NormalizeUrl(refreshUrl, baseUri);
                    if (!string.IsNullOrEmpty(normalizedUrl))
                    {
                        result.MetaRefreshUrl = normalizedUrl;
                    }
                }
            }
        }

        var inlineScripts = doc.DocumentNode.SelectNodes("//script[not(@src)]");
        if (inlineScripts != null)
        {
            foreach (var node in inlineScripts)
            {
                var scriptContent = node.InnerText;
                if (string.IsNullOrWhiteSpace(scriptContent))
                    continue;

                var matches = JsUrlPattern.Matches(scriptContent);
                foreach (Match match in matches)
                {
                    for (var i = 1; i < match.Groups.Count; i++)
                    {
                        if (match.Groups[i].Success)
                        {
                            var jsUrl = match.Groups[i].Value;
                            var normalizedUrl = NormalizeUrl(jsUrl, baseUri);
                            if (!string.IsNullOrEmpty(normalizedUrl) && seenUrls.Add(normalizedUrl))
                            {
                                if (!result.ScriptSources.Contains(normalizedUrl))
                                {
                                    result.ScriptSources.Add(normalizedUrl);
                                }
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    private static string? NormalizeUrl(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        href = href.Trim();

        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("#"))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
            {
                // On Linux, /path is considered an absolute file:///path URI.
                // We want to treat it as relative to the baseUri unless the scheme is a known web scheme.
                if (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
                {
                    return absoluteUri.ToString();
                }
            }

            if (Uri.TryCreate(baseUri, href, out var resolvedUri))
            {
                return resolvedUri.ToString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string DetermineAssetType(string url)
    {
        if (url.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/static/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/assets/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/dist/", StringComparison.OrdinalIgnoreCase))
        {
            return "JavaScriptFile";
        }

        if (url.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/styles/", StringComparison.OrdinalIgnoreCase))
        {
            return "CssFile";
        }

        if (IsLikelyApiEndpoint(url))
        {
            return "ApiEndpoint";
        }

        return "Url";
    }

    private static bool IsLikelyApiEndpoint(string url)
    {
        return ApiEndpointPattern.IsMatch(url) ||
               url.Contains("/api/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/graphql", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/rest/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUrlInScope(string url, Guid? scopeId)
    {
        if (!scopeId.HasValue)
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host == "example.com";
    }

    private static int GetInt(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return 0;

        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.TryGetProperty(propertyName, out var value))
        {
            return value.GetInt32();
        }

        return 0;
    }
}

internal sealed class HtmlParseResult
{
    public List<string> Links { get; } = new();
    public List<string> ScriptSources { get; } = new();
    public List<string> CssLinks { get; } = new();
    public List<FormInfo> Forms { get; } = new();
    public List<string> IframeSrcs { get; } = new();
    public string? MetaRefreshUrl { get; set; }
    public List<string> ApiEndpointCandidates { get; } = new();
}

internal sealed record FormInfo(string Action, string Method);
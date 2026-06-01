using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<FingerprintWorker>();

await builder.Build().RunAsync();

internal sealed class FingerprintWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FingerprintWorker> _logger;
    private readonly TechFingerprintDb _fingerprintDb;
    private readonly Uri _artifactServiceBaseAddress;

    public WorkerCapabilityDescriptor Capability { get; } = new(
        WorkerType: "FingerprintWorker",
        SubscribedAssetTypes: ["Url", "HttpResponse", "HtmlPage", "JavaScriptFile"],
        ProducedAssetTypes: ["Technology"],
        RequiresHttp: false,
        SupportsCheckpoint: false,
        MaxConcurrency: 50);

    public FingerprintWorker(
        IHttpClientFactory httpClientFactory,
        ILogger<FingerprintWorker> logger,
        IOptions<ArgusWorkerOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _fingerprintDb = new TechFingerprintDb();
        _artifactServiceBaseAddress = options.Value.ArtifactServiceBaseAddress;
    }

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var url = WorkerHelpers.GetString(task.InputPayloadJson, "url")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "value")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "target");

        if (string.IsNullOrWhiteSpace(url))
        {
            return WorkerProcessResult.Empty("No URL provided for fingerprinting");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            uri = new Uri($"http://{url}");
        }

        await context.ReportProgressAsync(10, $"Fingerprinting {uri.Host}", null);

        var detectedTechs = new List<(string Name, string Category, double Confidence)>();

        try
        {
            if (task.InputAssetId.HasValue && task.InputAssetId != Guid.Empty)
            {
                detectedTechs = await FingerprintFromStoredArtifactsAsync(task.InputAssetId.Value, cancellationToken);
            }

            if (detectedTechs.Count == 0)
            {
                detectedTechs = await FingerprintFromUrlAsync(uri, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fingerprinting {Url}", url);
        }

        await context.ReportProgressAsync(80, $"Found {detectedTechs.Count} technologies", null);

        var assets = detectedTechs.Select(t => new WorkerProducedAsset(
            AssetType: "Technology",
            Value: t.Name,
            Subtype: t.Category,
            Confidence: (decimal)t.Confidence,
            Metadata: new Dictionary<string, string> { ["target"] = uri.Host, ["url"] = url.ToString() },
            Tags: ["tech", "fingerprint"]
        )).ToList();

        var resultJson = JsonSerializer.Serialize(new
        {
            target = uri.Host,
            url = url.ToString(),
            technologies = detectedTechs.Select(t => t.Name).ToList(),
            count = detectedTechs.Count
        });

        return new WorkerProcessResult(false, resultJson, assets);
    }

    private async Task<List<(string Name, string Category, double Confidence)>> FingerprintFromStoredArtifactsAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var detected = new Dictionary<string, (string Name, string Category, double Confidence)>();

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = _artifactServiceBaseAddress;

            using var artifactsResp = await client.GetAsync($"/assets/{assetId}/artifacts", cancellationToken);
            if (!artifactsResp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Failed to get artifacts for asset {AssetId}: {Status}", assetId, artifactsResp.StatusCode);
                return detected.Values.ToList();
            }

            using var artifactsDoc = JsonDocument.Parse(await artifactsResp.Content.ReadAsStringAsync(cancellationToken));
            var artifacts = artifactsDoc.RootElement;

            string? headersContent = null;
            string? bodyContent = null;
            string? htmlContent = null;

            foreach (var artifactEl in artifacts.EnumerateArray())
            {
                var artifactType = artifactEl.TryGetProperty("artifactType", out var at) ? at.GetString() : null;
                var artifactId = artifactEl.TryGetProperty("artifactId", out var aid) ? aid.GetGuid() : (Guid?)null;

                if (artifactId == null) continue;

                if (artifactType == "HttpHeaders")
                {
                    headersContent = await FetchArtifactContentAsync(client, artifactId.Value, cancellationToken);
                }
                else if (artifactType == "HttpBody")
                {
                    var contentType = artifactEl.TryGetProperty("contentType", out var ct) ? ct.GetString() ?? "" : "";
                    if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                        contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
                    {
                        bodyContent = await FetchArtifactContentAsync(client, artifactId.Value, cancellationToken);
                        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                        {
                            htmlContent = bodyContent;
                        }
                    }
                }
            }

            if (headersContent != null || bodyContent != null)
            {
                detected = FingerprintContent(headersContent ?? "", bodyContent ?? "", htmlContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching stored artifacts for asset {AssetId}", assetId);
        }

        return detected.Values.ToList();
    }

    private async Task<string?> FetchArtifactContentAsync(HttpClient client, Guid artifactId, CancellationToken cancellationToken)
    {
        try
        {
            using var resp = await client.GetAsync($"/artifacts/{artifactId}/content", cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch artifact content {ArtifactId}", artifactId);
        }
        return null;
    }

    private Dictionary<string, (string Name, string Category, double Confidence)> FingerprintContent(
        string headersContent,
        string bodyContent,
        string? htmlContent = null)
    {
        var detected = new Dictionary<string, (string Name, string Category, double Confidence)>();

        var headersDict = ParseHeaders(headersContent);
        var cookies = ParseCookies(headersDict);
        var content = bodyContent + (htmlContent ?? "");
        var scriptUrls = ExtractScriptUrls(htmlContent ?? bodyContent);

        foreach (var (techName, patterns) in _fingerprintDb.Technologies)
        {
            double confidence = 0;

            if (patterns.TryGetValue("headers", out var headersObj))
            {
                var headersPatterns = headersObj as JsonNode;
                if (headersPatterns is JsonObject headersObj2)
                {
                    foreach (var (headerKey, patternNode) in headersObj2)
                    {
                        var pattern = patternNode?.GetValue<string>() ?? "";
                        if (MatchHeaderPattern(headerKey, pattern, headersDict))
                        {
                            confidence = Math.Max(confidence, 0.8);
                        }
                    }
                }
            }

            if (patterns.TryGetValue("cookies", out var cookiesObj))
            {
                var cookiesDict = cookiesObj as JsonNode;
                if (cookiesDict is JsonObject cookiesObj2)
                {
                    foreach (var (cookieName, _) in cookiesObj2)
                    {
                        if (cookies.ContainsKey(cookieName))
                        {
                            confidence = Math.Max(confidence, 0.9);
                        }
                    }
                }
            }

            if (patterns.TryGetValue("scriptSrc", out var scriptSrcObj))
            {
                var scriptPatterns = ExtractPatterns(scriptSrcObj);
                foreach (var scriptUrl in scriptUrls)
                {
                    foreach (var pattern in scriptPatterns)
                    {
                        if (Regex.IsMatch(scriptUrl, pattern, RegexOptions.IgnoreCase))
                        {
                            confidence = Math.Max(confidence, 0.85);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(content))
            {
                if (patterns.TryGetValue("js", out var jsObj))
                {
                    var jsPatterns = ExtractPatterns(jsObj);
                    foreach (var jsPattern in jsPatterns)
                    {
                        if (Regex.IsMatch(content, $@"(?:window\.|document\.)?{jsPattern}|{jsPattern}\s*=", RegexOptions.IgnoreCase))
                        {
                            confidence = Math.Max(confidence, 0.75);
                        }
                    }
                }

                if (patterns.TryGetValue("dom", out var domObj))
                {
                    var domPatterns = ExtractPatterns(domObj);
                    foreach (var domPattern in domPatterns)
                    {
                        if (Regex.IsMatch(content, domPattern, RegexOptions.IgnoreCase))
                        {
                            confidence = Math.Max(confidence, 0.7);
                        }
                    }
                }

                if (patterns.TryGetValue("meta", out var metaObj))
                {
                    var metaDict = metaObj as JsonNode;
                    if (metaDict is JsonObject metaObj2)
                    {
                        foreach (var (metaName, patternNode) in metaObj2)
                        {
                            var pattern = patternNode?.GetValue<string>() ?? "";
                            var metaRegex = $@"<meta[^>]+name\s*=\s*[""']?{metaName}[""']?[^>]+content\s*=\s*[""']([^""']+)[""']";
                            var matches = Regex.Matches(content, metaRegex, RegexOptions.IgnoreCase);
                            if (matches.Count > 0)
                            {
                                var contentValue = matches[0].Groups[1].Value;
                                if (Regex.IsMatch(contentValue, pattern))
                                {
                                    confidence = Math.Max(confidence, 0.85);
                                }
                            }
                        }
                    }
                }

                if (patterns.TryGetValue("text", out var textObj))
                {
                    var textPatterns = ExtractPatterns(textObj);
                    foreach (var textPattern in textPatterns)
                    {
                        if (Regex.IsMatch(content, textPattern, RegexOptions.IgnoreCase))
                        {
                            confidence = Math.Max(confidence, 0.6);
                        }
                    }
                }
            }

            if (confidence >= 0.5 && !detected.ContainsKey(techName))
            {
                var category = "Technology";
                if (patterns.TryGetValue("cats", out var catsObj))
                {
                    category = MapCategory(catsObj);
                }

                detected[techName] = (techName, category, Math.Min(confidence, 1.0));
            }
        }

        return detected;
    }

    private async Task<List<(string Name, string Category, double Confidence)>> FingerprintFromUrlAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var detected = new Dictionary<string, (string Name, string Category, double Confidence)>();

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.BaseAddress = uri;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ArgusFingerprint/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await client.GetAsync(uri, cts.Token);
            var allHeaders = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var h in response.Headers) allHeaders[h.Key] = h.Value;
            foreach (var h in response.Content.Headers) allHeaders[h.Key] = h.Value;

            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (allHeaders.TryGetValue("Set-Cookie", out var setCookies))
            {
                foreach (var cookie in setCookies)
                {
                    var parts = cookie.Split('=', 2);
                    if (parts.Length >= 1)
                    {
                        var cookieName = parts[0].Trim();
                        var cookieValue = parts.Length > 1 ? parts[1].Split(';')[0].Trim() : "";
                        cookies[cookieName] = cookieValue;
                    }
                }
            }

            string? htmlContent = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
            {
                htmlContent = await response.Content.ReadAsStringAsync(cts.Token);
            }

            var scriptUrls = new List<string>();
            if (htmlContent != null)
            {
                var scriptMatches = Regex.Matches(htmlContent, @"<script[^>]+src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                scriptUrls.AddRange(scriptMatches.Select(m => m.Groups[1].Value));
            }

            detected = FingerprintContent(
                SerializeHeaders(allHeaders),
                htmlContent ?? "",
                htmlContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during fingerprinting of {Uri}", uri);
        }

        return detected.Values.OrderByDescending(t => t.Confidence).ToList();
    }

    private static string SerializeHeaders(Dictionary<string, IEnumerable<string>> headers)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kvp in headers)
        {
            foreach (var value in kvp.Value)
            {
                sb.AppendLine($"{kvp.Key}: {value}");
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseHeaders(string headersText)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(headersText)) return dict;

        foreach (var line in headersText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var name = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim();
                dict[name] = value;
            }
        }
        return dict;
    }

    private static Dictionary<string, string> ParseCookies(Dictionary<string, string> headers)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers.TryGetValue("Set-Cookie", out var setCookie))
        {
            foreach (var cookie in setCookie.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = cookie.Trim().Split('=', 2);
                if (parts.Length >= 1)
                {
                    cookies[parts[0].Trim()] = parts.Length > 1 ? parts[1].Split(';')[0].Trim() : "";
                }
            }
        }
        return cookies;
    }

    private static List<string> ExtractScriptUrls(string? content)
    {
        var urls = new List<string>();
        if (string.IsNullOrEmpty(content)) return urls;

        var matches = Regex.Matches(content, @"<script[^>]+src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            urls.Add(m.Groups[1].Value);
        }
        return urls;
    }

    private bool MatchHeaderPattern(string headerKey, string pattern, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue(headerKey, out var value))
        {
            if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        if (headerKey.Contains(' '))
        {
            var parts = headerKey.Split(' ', 2);
            var name = parts[0];
            var headerPattern = parts.Length > 1 ? parts[1] : "";

            if (headers.TryGetValue(name, out value))
            {
                if (Regex.IsMatch(value, headerPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private List<string> ExtractPatterns(JsonNode? node)
    {
        var patterns = new List<string>();
        if (node == null) return patterns;

        if (node is JsonValue value)
        {
            patterns.Add(value.GetValue<string>());
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v)
                {
                    patterns.Add(v.GetValue<string>());
                }
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var (_, v) in obj)
            {
                if (v is JsonValue val)
                {
                    patterns.Add(val.GetValue<string>());
                }
            }
        }

        return patterns;
    }

    private string MapCategory(JsonNode? catsNode)
    {
        if (catsNode is JsonArray arr && arr.Count > 0)
        {
            var firstCat = arr[0]?.GetValue<int>() ?? 0;
            return firstCat switch
            {
                1 => "CMS",
                2 => "Message Board",
                3 => "Database",
                4 => "Documentation Tools",
                5 => "Video",
                6 => "Ecommerce",
                7 => "Wiki",
                8 => "Hosting",
                9 => "Analytics",
                10 => "Tag Manager",
                11 => "Advertising",
                12 => "JavaScript Framework",
                13 => "Issue Tracker",
                14 => "Video Player",
                15 => "Font",
                16 => "Captcha",
                17 => "Security",
                18 => "Web Framework",
                19 => "Javascript Graphics",
                20 => "Mobile Framework",
                21 => "LMS",
                22 => "Web Server",
                23 => "Cache Tools",
                24 => "Rich Text Editor",
                25 => "JavaScript Charts",
                26 => "JavaScript Maps",
                27 => "Programming Language",
                28 => "Operating System",
                29 => "Search Engine",
                30 => "Navigation",
                31 => "CDN",
                32 => "Marketing Automation",
                33 => "Email",
                34 => "Customer Data Platform",
                35 => "Internet of Things",
                36 => "Advertising",
                37 => "Widgets",
                38 => "Site Search",
                39 => "Live Chat",
                40 => "Webmaster Tools",
                41 => "Payment",
                42 => "Survey",
                43 => "Authentication",
                44 => "Social Chat",
                45 => "Comments",
                46 => "Privacy",
                47 => "A/B Testing",
                48 => "Personalization",
                49 => "Retargeting",
                50 => "Website Corp",
                51 => "Website Builder",
                52 => "Live Support",
                53 => "CRM",
                54 => "Accounting",
                55 => "Address Verification",
                56 => "SSL Seal",
                57 => "Marketing",
                58 => "Domain",
                59 => "JavaScript Library",
                60 => "Image Gallery",
                61 => "News",
                62 => "Low-code",
                63 => "Federated Search",
                64 => "SEO",
                65 => "Conversion Optimization",
                66 => "Bookmarks",
                67 => "Cookie Consent",
                68 => "Content Delivery Network",
                69 => "Digital Asset Management",
                70 => "Q&A",
                71 => "Affiliate",
                72 => "Scheduling",
                73 => "Password Manager",
                74 => "Contact Form",
                75 => "Email Marketing",
                76 => "Conversion",
                77 => "UX",
                78 => "Inventory Management",
                79 => "Product Recommendations",
                80 => "WordPress Theme",
                81 => "Landing Page Builder",
                82 => "WordPress Plugin",
                83 => "Registry",
                84 => "Other",
                85 => "Dark Mode",
                86 => "Mux",
                87 => "Form",
                88 => "Web Hosting",
                89 => "Network",
                90 => "Ticketing",
                91 => "Payments",
                92 => "Shipping",
                93 => "Returns",
                94 => "Product Options",
                95 => "Digital Asset Management",
                96 => "Accessibility",
                97 => "SEO",
                98 => "Customer Success",
                99 => "Logistics",
                100 => "Shopify",
                _ => "Technology"
            };
        }
        return "Technology";
    }
}

internal sealed class TechFingerprintDb
{
    public Dictionary<string, Dictionary<string, JsonNode>> Technologies { get; }

    public TechFingerprintDb()
    {
        var baseDir = AppContext.BaseDirectory;
        var jsonPath = Path.Combine(baseDir, "tech_id_data.json");

        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            var doc = JsonDocument.Parse(json);
            Technologies = new Dictionary<string, Dictionary<string, JsonNode>>();

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var techName = property.Name;
                var techData = new Dictionary<string, JsonNode>();

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in property.Value.EnumerateObject())
                    {
                        var parsed = JsonNode.Parse(field.Value.GetRawText());
                        techData[field.Name] = parsed ?? new JsonObject { ["value"] = JsonValue.Create(field.Value) };
                    }
                }

                Technologies[techName] = techData;
            }
        }
        else
        {
            Technologies = new Dictionary<string, Dictionary<string, JsonNode>>();
        }
    }
}

internal static class WorkerHelpers
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
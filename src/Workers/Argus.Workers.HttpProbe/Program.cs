using System.Text;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var allowInvalidTls = bool.TryParse(builder.Configuration["ARGUS_HTTP_PROBE_ALLOW_INVALID_TLS"], out var parsedAllowInvalidTls)
    && parsedAllowInvalidTls;

builder.Services.AddHttpClient("probe")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        CheckCertificateRevocationList = !allowInvalidTls,
        ServerCertificateCustomValidationCallback = allowInvalidTls
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null
    });
builder.AddArgusWorker<HttpProbeWorker>();

await builder.Build().RunAsync();

internal sealed partial class HttpProbeWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpProbeWorker> _logger;
    private static readonly Regex TitleRegex = TitleRegexGenerated();
    private const int MaxRedirects = 10;
    private const int MaxBodySizeBytes = 10 * 1024 * 1024;

    public HttpProbeWorker(IHttpClientFactory httpClientFactory, ILogger<HttpProbeWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HttpProbeWorker",
        ["Subdomain", "Ip", "Url", "ApiEndpoint", "JavaScriptFile", "JsonDocument"],
        ["Url", "HttpResponse", "HtmlPage", "JsonDocument", "Observation"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 40);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var explicitUrl = WorkerHelpers.GetString(task.InputPayloadJson, "url");
        var explicitUri = Uri.TryCreate(explicitUrl, UriKind.Absolute, out var parsedUri) ? parsedUri : null;
        var host = WorkerHelpers.GetString(task.InputPayloadJson, "host")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "domain")
            ?? WorkerHelpers.ExtractHost(explicitUrl)
            ?? throw new InvalidOperationException("No host or URL in task payload");

        var timeoutSeconds = WorkerHelpers.GetInt(task.InputPayloadJson, "timeout_seconds") ?? 30;
        var followRedirects = WorkerHelpers.GetBool(task.InputPayloadJson, "follow_redirects") ?? true;


        await context.ReportProgressAsync(5, $"Waiting for rate-limit token for {host}", null);

        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerHelpers.GetRegisteredDomain(host),
            null,
            Capability.WorkerType));

        if (!allowed)
        {
            await context.ReportProgressAsync(100, "Rate limited, will retry", null);
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { host, delayed = true }), []);
        }

        var producedAssets = new List<WorkerProducedAsset>();
        var producedArtifacts = new List<WorkerProducedArtifact>();

        var schemes = explicitUri is null ? new[] { "https", "http" } : new[] { explicitUri.Scheme };
        bool httpsSucceeded = false;
        bool inputConfirmed = false;
        string? httpsError = null;
        var redirectChain = new List<string>();
        string outputSummary = "";

        foreach (var scheme in schemes)
        {
            var probeUrl = explicitUri?.ToString() ?? $"{scheme}://{host}/";
            await context.ReportProgressAsync(10, $"Attempting {scheme} probe for {host}", null);
            var currentUri = new Uri(probeUrl);
            var redirectCount = 0;
            bool schemeSucceeded = false;

            while (redirectCount <= MaxRedirects)
            {
                try
                {
                    await context.ReportProgressAsync(20, $"Requesting {currentUri} (redirect: {redirectCount})", null);
                    using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    request.Headers.Accept.ParseAdd("*/*");
                    request.Headers.UserAgent.ParseAdd("Argus-HttpProbe/1.0");

                    var httpClient = _httpClientFactory.CreateClient("probe");
                    httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var statusCode = (int)response.StatusCode;
                    var effectiveCharset = response.Content.Headers.ContentType?.CharSet ?? "utf-8";

                    if (statusCode == 429)
                    {
                        var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
                        await context.SignalBackpressureAsync(new RateLimitBackpressureSignal(host, $"host:{host}", retryAfter, 429));
                        return new WorkerProcessResult(true, JsonSerializer.Serialize(new { host, error = "Rate limited", retryAfterSeconds = retryAfter.TotalSeconds }), []);
                    }

                    await context.ReportProgressAsync(40, $"Got {statusCode} from {currentUri}", null);

                    var headersDict = new Dictionary<string, string>();
                    foreach (var header in response.Headers.Concat(response.Content.Headers))
                    {
                        headersDict[header.Key] = string.Join(", ", header.Value);
                    }

                    var headersArtifact = new WorkerProducedArtifact(
                        "HttpHeaders",
                        $"headers-{host}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{redirectCount}",
                        "application/json",
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url = currentUri.ToString(), statusCode, headers = headersDict })),
                        new Dictionary<string, string>
                        {
                            ["url"] = currentUri.ToString(),
                            ["status_code"] = statusCode.ToString(),
                            ["redirect_count"] = redirectCount.ToString()
                        });

                    producedArtifacts.Add(headersArtifact);

                    producedAssets.Add(new WorkerProducedAsset(
                        "Url",
                        currentUri.ToString(),
                        null,
                        1.0m,
                        new Dictionary<string, string> { ["scheme"] = currentUri.Scheme, ["host"] = currentUri.Host, ["port"] = currentUri.Port.ToString() },
                        ["alive", "web"]));

                    producedAssets.Add(new WorkerProducedAsset(
                        "HttpResponse",
                        $"{currentUri} [{statusCode}]",
                        null,
                        1.0m,
                        new Dictionary<string, string> { ["url"] = currentUri.ToString(), ["status_code"] = statusCode.ToString() },
                        ["response"],
                        new[] { new ArtifactReference("HttpHeaders", headersArtifact.Name, headersArtifact.ComputeHash()) }));

                    producedAssets.Add(new WorkerProducedAsset(
                        "Observation",
                        $"HTTP headers from {currentUri}",
                        "HttpHeaders",
                        1.0m,
                        new Dictionary<string, string> { ["url"] = currentUri.ToString() },
                        ["observation", "headers"],
                        new[] { new ArtifactReference("HttpHeaders", headersArtifact.Name, headersArtifact.ComputeHash()) }));

                    if (!inputConfirmed && task.InputAssetId.HasValue)
                    {
                        await ConfirmInputAssetAsync(task.InputAssetId.Value, task.TaskId, $"HTTP {statusCode} from {currentUri}", cancellationToken);
                        inputConfirmed = true;
                    }

                    if (followRedirects && statusCode >= 300 && statusCode < 400 && response.Headers.Location != null)
                    {
                        redirectCount++;
                        redirectChain.Add(currentUri.ToString());
                        var redirectUri = new Uri(currentUri, response.Headers.Location);
                        if (redirectUri.Host != currentUri.Host)
                        {
                            // Cross-site redirect - we'll stop following but it's not a failure
                            schemeSucceeded = true;
                            break;
                        }
                        currentUri = redirectUri;
                        continue;
                    }

                    var actualContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                    var bodyToStore = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var truncated = false;
                    if (bodyToStore.Length > 1024 * 512)
                    {
                        bodyToStore = bodyToStore.Take(1024 * 512).ToArray();
                        truncated = true;
                    }

                    var bodyArtifact = new WorkerProducedArtifact(
                        "HttpBody",
                        $"body-{host}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{redirectCount}",
                        actualContentType,
                        bodyToStore,
                        new Dictionary<string, string>
                        {
                            ["url"] = currentUri.ToString(),
                            ["status_code"] = statusCode.ToString(),
                            ["content_type"] = actualContentType,
                            ["charset"] = effectiveCharset,
                            ["size_bytes"] = bodyToStore.Length.ToString(),
                            ["truncated"] = truncated.ToString().ToLowerInvariant()
                        });
                    producedArtifacts.Add(bodyArtifact);

                    if (IsTextualContent(actualContentType))
                    {
                        var bodyPreview = DecodeBody(bodyToStore, effectiveCharset);
                        if (!string.IsNullOrWhiteSpace(bodyPreview))
                        {
                            producedAssets.Add(new WorkerProducedAsset(
                                "Observation",
                                $"HTTP body preview from {currentUri}",
                                "HttpBodyPreview",
                                0.8m,
                                new Dictionary<string, string>
                                {
                                    ["url"] = currentUri.ToString(),
                                    ["status_code"] = statusCode.ToString(),
                                    ["content_type"] = actualContentType,
                                    ["body_preview"] = bodyPreview.Length > 16384 ? bodyPreview[..16384] : bodyPreview
                                },
                                ["observation", "body-preview", "regex-source"],
                                new[] { new ArtifactReference("HttpBody", bodyArtifact.Name, bodyArtifact.ComputeHash()) }));
                        }
                    }

                    if (scheme == "https")
                        httpsSucceeded = true;

                    await context.ReportProgressAsync(70, $"Storing {bodyToStore.Length} bytes from {currentUri.Host}", null);

                    if (actualContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    {
                        producedAssets.Add(new WorkerProducedAsset(
                            "JsonDocument",
                            currentUri.ToString(),
                            actualContentType,
                            0.9m,
                            new Dictionary<string, string>
                            {
                                ["size_bytes"] = bodyToStore.Length.ToString(),
                                ["charset"] = effectiveCharset
                            },
                            ["json", "api", "alive"],
                            new[] { new ArtifactReference("HttpBody", bodyArtifact.Name, bodyArtifact.ComputeHash()) }));
                    }

                    if (actualContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        var bodyText = DecodeBody(bodyToStore, effectiveCharset);
                        var title = ExtractTitle(bodyText);

                        producedAssets.Add(new WorkerProducedAsset(
                            "HtmlPage",
                            currentUri.ToString(),
                            actualContentType,
                            0.9m,
                            new Dictionary<string, string>
                            {
                                ["title"] = title ?? string.Empty,
                                ["size_bytes"] = bodyToStore.Length.ToString(),
                                ["charset"] = effectiveCharset,
                                ["redirect_count"] = redirectCount.ToString()
                            },
                            ["html", "webpage", "alive"],
                            new[] { new ArtifactReference("HttpBody", bodyArtifact.Name, bodyArtifact.ComputeHash()) }));

                        if (!string.IsNullOrEmpty(title))
                        {
                            producedAssets.Add(new WorkerProducedAsset(
                                "Observation",
                                title,
                                "HtmlTitle",
                                0.3m,
                                new Dictionary<string, string>
                                {
                                    ["size_bytes"] = bodyToStore.Length.ToString(),
                                    ["charset"] = effectiveCharset,
                                    ["redirect_count"] = redirectCount.ToString()
                                },
                                ["title", "metadata", "alive"],
                                Array.Empty<ArtifactReference>()));
                        }
                    }

                    schemeSucceeded = true;
                    break;
                }
                catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    await context.ReportProgressAsync(70, $"{scheme} probe failed: {ex.Message}", null);
                    if (scheme == "https")
                    {
                        httpsError = ex.Message;
                    }
                    break; // Move to next scheme
                }
                catch (Exception ex)
                {
                    await context.ReportProgressAsync(100, $"Unexpected error: {ex.Message}", null);
                    outputSummary = JsonSerializer.Serialize(new { host, error = ex.GetType().Name });
                    return new WorkerProcessResult(false, outputSummary, producedAssets) { ProducedArtifacts = producedArtifacts };
                }
            }

            if (schemeSucceeded && (scheme == "https" || explicitUri is not null))
            {
                break;
            }
        }

        if (explicitUri is not null)
        {
            outputSummary = JsonSerializer.Serialize(new { host, url = explicitUri.ToString(), redirects = redirectChain.Count });
        }
        else if (httpsSucceeded)
        {
            outputSummary = JsonSerializer.Serialize(new { host, scheme = "https", redirects = redirectChain.Count });
        }
        else if (httpsError != null)
        {
            outputSummary = JsonSerializer.Serialize(new { host, scheme = "http", httpsError });
        }
        else
        {
            outputSummary = JsonSerializer.Serialize(new { host, scheme = "http", redirects = redirectChain.Count });
        }

        await context.ReportProgressAsync(100, $"Complete: {producedAssets.Count} assets, {producedArtifacts.Count} artifacts", null);

        return new WorkerProcessResult(false, outputSummary, producedAssets) { ProducedArtifacts = producedArtifacts };
    }

    private async Task ConfirmInputAssetAsync(Guid assetId, Guid taskId, string notes, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var baseAddress = Environment.GetEnvironmentVariable("ARGUS_ASSET_SERVICE");
        client.BaseAddress = Uri.TryCreate(baseAddress, UriKind.Absolute, out var serviceUri)
            ? serviceUri
            : new Uri("http://asset-service");

        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/assets/{assetId}/confirm",
                new ConfirmAssetRequest(taskId, notes),
                cancellationToken);

            _ = response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Asset confirmation is best-effort; the probe result should still be published.
            _logger.LogDebug(ex, "Failed to confirm input asset {AssetId} from task {TaskId}", assetId, taskId);
        }
    }

    private static bool IsTextualContent(string contentType) =>
        contentType.Contains("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan GetRetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfterHeader)
    {
        if (retryAfterHeader?.Delta.HasValue == true)
        {
            return retryAfterHeader.Delta.Value;
        }
        if (retryAfterHeader?.Date.HasValue == true)
        {
            var diff = retryAfterHeader.Date.Value - DateTimeOffset.UtcNow;
            return diff < TimeSpan.Zero ? TimeSpan.FromSeconds(10) : diff;
        }
        return TimeSpan.FromSeconds(10);
    }

    private static string DetectContentType(string? contentType, string url)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var baseType = contentType.Split(';')[0].Trim();
            if (!string.IsNullOrEmpty(baseType))
            {
                return baseType;
            }
        }

        if (url.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return "text/html";
        }
        if (url.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return "application/json";
        }
        if (url.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return "application/xml";
        }

        return "application/octet-stream";
    }

    private static string DetectCharset(string? charset, string contentType)
    {
        if (!string.IsNullOrEmpty(charset))
        {
            return charset;
        }

        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return "utf-8";
        }

        return "utf-8";
    }

    private static string DecodeBody(byte[] body, string charset)
    {
        try
        {
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch
            {
                encoding = Encoding.UTF8;
            }
            return encoding.GetString(body);
        }
        catch
        {
            return Encoding.UTF8.GetString(body);
        }
    }

    private static string? ExtractTitle(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var match = TitleRegex.Match(html);
        if (match.Success)
        {
            var title = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                return title.Length > 500 ? title.Substring(0, 500) : title;
            }
        }

        return null;
    }

    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TitleRegexGenerated();
}
internal sealed record ConfirmAssetRequest(Guid? ConfirmedByTaskId, string? Notes);

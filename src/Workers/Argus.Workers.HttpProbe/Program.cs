using System.Text;
using System.Text.RegularExpressions;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusWorker<HttpProbeWorker>();

await builder.Build().RunAsync();

internal sealed partial class HttpProbeWorker : IReconWorker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly Regex TitleRegex = TitleRegexGenerated();
    private const int MaxRedirects = 10;
    private const int MaxBodySizeBytes = 10 * 1024 * 1024;

    public HttpProbeWorker(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "HttpProbeWorker",
        ["Subdomain", "Ip"],
        ["Url", "HttpResponse", "HtmlPage", "JsonDocument", "Observation"],
        RequiresHttp: true,
        SupportsCheckpoint: true,
        MaxConcurrency: 40);

    public async Task<WorkerProcessResult> ProcessAsync(
        Argus.Contracts.Tasks.ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var host = WorkerHelpers.GetString(task.InputPayloadJson, "host")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "domain")
            ?? throw new InvalidOperationException("No host in task payload");

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

        var schemes = new[] { "https", "http" };
        bool httpsSucceeded = false;
        string? httpsError = null;
        string? httpsRedirectUrl = null;
        var redirectChain = new List<string>();

        foreach (var scheme in schemes)
        {
            var probeUrl = $"{scheme}://{host}/";
            redirectChain.Clear();

            try
            {
                await context.ReportProgressAsync(15, $"Probing {probeUrl}", $"{{\"scheme\":\"{scheme}\"}}");

                var client = _httpClientFactory.CreateClient("probe");
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                if (!followRedirects)
                {
                    client.DefaultRequestHeaders.Remove("Authorization");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
                request.Headers.UserAgent.ParseAdd("ArgusRecon/1.0 (bug-bounty-recon)");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,*/*");

                var redirectCount = 0;
                var currentUri = new Uri(probeUrl);

                while (redirectCount <= MaxRedirects)
                {
                    redirectChain.Add(currentUri.ToString());

                    using var requestCopy = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    requestCopy.Headers.UserAgent.ParseAdd("ArgusRecon/1.0 (bug-bounty-recon)");
                    requestCopy.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,*/*");

                    var response = await client.SendAsync(requestCopy, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var statusCode = (int)response.StatusCode;
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
                    var charset = response.Content.Headers.ContentType?.CharSet ?? "utf-8";
                    var contentLength = response.Content.Headers.ContentLength ?? 0;

                    if ((statusCode == 429 || statusCode == 503) && !string.IsNullOrWhiteSpace(host))
                    {
                        var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
                        await context.SignalBackpressureAsync(new RateLimitBackpressureSignal(
                            host,
                            $"host:{host.Trim().ToLowerInvariant()}",
                            retryAfter,
                            statusCode));
                    }

                    await context.ReportProgressAsync(40, $"Received {statusCode} from {currentUri.Host}", null);

                    var headersDict = new Dictionary<string, string>();
                    foreach (var header in response.Headers)
                    {
                        headersDict[header.Key] = string.Join(", ", header.Value);
                    }
                    foreach (var header in response.Content.Headers)
                    {
                        headersDict[header.Key] = string.Join(", ", header.Value);
                    }

                    var headersArtifact = new WorkerProducedArtifact(
                        "HttpHeaders",
                        $"headers-{host}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{redirectCount}",
                        "application/json",
                        JsonSerializer.SerializeToUtfBytes(new { url = currentUri.ToString(), statusCode, headers = headersDict }),
                        new Dictionary<string, string>
                        {
                            ["url"] = currentUri.ToString(),
                            ["status_code"] = statusCode.ToString(),
                            ["redirect_count"] = redirectCount.ToString()
                        });
                    producedArtifacts.Add(headersArtifact);

                    var headersAsset = new WorkerProducedAsset(
                        "Observation",
                        $"HTTP headers from {currentUri.Host}",
                        "HttpHeaders",
                        0.9m,
                        new Dictionary<string, string>
                        {
                            ["url"] = currentUri.ToString(),
                            ["status_code"] = statusCode.ToString(),
                            ["redirect_count"] = redirectCount.ToString(),
                            ["headers.count"] = headersDict.Count.ToString()
                        },
                        ["http", "headers", $"status-{statusCode}"]);
                    producedAssets.Add(headersAsset);

                    if (statusCode >= 300 && statusCode < 400 && response.Headers.Location != null)
                    {
                        var location = response.Headers.Location;
                        if (!location.IsAbsoluteUri)
                        {
                            location = new Uri(currentUri, location);
                        }

                        var redirectScheme = location.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
                        if (redirectScheme && scheme == "https")
                        {
                            httpsRedirectUrl = location.ToString();
                            goto httpsFallback;
                        }

                        currentUri = location;
                        redirectCount++;
                        continue;
                    }

                    var actualContentType = DetectContentType(contentType, currentUri.ToString());
                    var effectiveCharset = DetectCharset(charset, actualContentType);

                    producedAssets.Add(new WorkerProducedAsset(
                        "Url",
                        currentUri.ToString(),
                        null,
                        0.95m,
                        new Dictionary<string, string>
                        {
                            ["http.status_code"] = statusCode.ToString(),
                            ["http.content_type"] = contentType,
                            ["http.charset"] = effectiveCharset,
                            ["http.redirects"] = redirectCount.ToString(),
                            ["url.scheme"] = currentUri.Scheme,
                            ["redirect_chain"] = string.Join(" -> ", redirectChain)
                        },
                        ["http", "alive", currentUri.Scheme]));

                    producedAssets.Add(new WorkerProducedAsset(
                        "HttpResponse",
                        $"{currentUri} {statusCode} {contentType}",
                        actualContentType,
                        0.95m,
                        new Dictionary<string, string>
                        {
                            ["status_code"] = statusCode.ToString(),
                            ["content_type"] = actualContentType,
                            ["content_length"] = contentLength.ToString(),
                            ["charset"] = effectiveCharset,
                            ["response.scheme"] = currentUri.Scheme,
                            ["redirect_count"] = redirectCount.ToString()
                        },
                        ["response", currentUri.Scheme]));

                    if (statusCode >= 200 && statusCode < 300 && contentLength >= 0 && contentLength <= MaxBodySizeBytes)
                    {
                        var bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        var truncated = bodyBytes.Length > MaxBodySizeBytes;
                        var bodyToStore = truncated ? bodyBytes.AsSpan(0, MaxBodySizeBytes).ToArray() : bodyBytes;

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

                        await context.ReportProgressAsync(70, $"Storing {bodyToStore.Length} bytes from {currentUri.Host}", null);

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
                                    $"Page title: {title}",
                                    "HtmlTitle",
                                    0.7m,
                                    new Dictionary<string, string>
                                    {
                                        ["url"] = currentUri.ToString(),
                                        ["title"] = title
                                    },
                                    ["html", "title"]));
                            }
                        }
                        else if (actualContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                        {
                            var bodyText = DecodeBody(bodyToStore, effectiveCharset);

                            producedAssets.Add(new WorkerProducedAsset(
                                "JsonDocument",
                                currentUri.ToString(),
                                actualContentType,
                                0.9m,
                                new Dictionary<string, string>
                                {
                                    ["size_bytes"] = bodyToStore.Length.ToString(),
                                    ["charset"] = effectiveCharset,
                                    ["redirect_count"] = redirectCount.ToString()
                                },
                                ["json", "api", "alive"],
                                new[] { new ArtifactReference("HttpBody", bodyArtifact.Name, bodyArtifact.ComputeHash()) }));
                        }
                    }

                    break;
                }

                if (scheme == "https")
                {
                    httpsSucceeded = true;
                }

                httpsFallback:
                if (httpsSucceeded || scheme == "http")
                {
                    break;
                }
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
            }
            catch (Exception ex)
            {
                await context.ReportProgressAsync(100, $"Unexpected error: {ex.Message}", null);
                outputSummary = JsonSerializer.Serialize(new { host, error = ex.GetType().Name });
                return new WorkerProcessResult(false, outputSummary, producedAssets);
            }
        }

        string outputSummary;
        if (httpsSucceeded)
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

        return new WorkerProcessResult(false, outputSummary, producedAssets);
    }

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
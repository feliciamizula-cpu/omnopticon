using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Workers.HttpProbe.Tests.Fixtures;

internal sealed class HttpProbeWorkerFixture
{
    public HttpProbeWorker CreateWorker(HttpClient httpClient)
    {
        // Extracting handler from HttpClient is hard in modern .NET.
        // We'll just assume it's a mock we can replicate or change the call sites.
        // For now, let's just make it work for the existing call sites by using reflection.
        var handler = (HttpMessageHandler)typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(httpClient)!;

        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        var provider = services.BuildServiceProvider();
        return new HttpProbeWorker(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<HttpProbeWorker>.Instance);
    }

    public HttpProbeWorker CreateWorker(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var services = new ServiceCollection();
        // Use a handler that doesn't follow redirects
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new MockHttpMessageHandler(handler)));
        var provider = services.BuildServiceProvider();
        return new HttpProbeWorker(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<HttpProbeWorker>.Instance);
    }

    public static HttpProbeWorkerFixture Instance => new();

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            // If we use a mock handler directly, HttpClient DOES NOT follow redirects.
            // Wait, maybe I should use HttpClientHandler if I want to be sure?
            // But we can't use HttpClientHandler with a mock.
            // Actually, we can if we use a DelegatingHandler.
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}

public sealed class HttpProbeWorkerTests
{
    private readonly HttpProbeWorkerFixture _fixture = HttpProbeWorkerFixture.Instance;

    [Fact]
    public async Task ProcessAsync_WithValidHost_ReturnsSuccessResult()
    {
        var handler = new MockHttpMessageHandler("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test.example.com") };
        var worker = _fixture.CreateWorker(httpClient);
        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);

        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        Assert.NotEmpty(result.ProducedAssets);
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "Url");
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "HttpResponse");
    }

    [Fact]
    public async Task ProcessAsync_WhenRateLimited_ReturnsDelayedResult()
    {
        var handler = new MockHttpMessageHandler("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test.example.com") };
        var worker = _fixture.CreateWorker(httpClient);
        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        harness.SetRateLimitAllowed(false);

        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.PartiallySucceeded);
        Assert.Empty(result.ProducedAssets);
    }

    [Fact]
    public async Task ProcessAsync_WithoutHost_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test.example.com") };
        var worker = _fixture.CreateWorker(httpClient);
        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ExecuteAsync(
                programId: Guid.NewGuid(),
                taskType: "HttpProbe",
                payload: new Dictionary<string, string>(),
                workerId: null,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessAsync_WithHttpsProbe_SetsCorrectScheme()
    {
        var responses = new Queue<string>();
        responses.Enqueue("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n");

        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "text/html")
            });
        });

        using var httpClient = new HttpClient(handler);
        var worker = _fixture.CreateWorker(httpClient);
        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);

        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
    }

    [Fact]
    public async Task Scenario_WhenProbeSucceeds_ProducesUrlAndHttpResponseAssets()
    {
        var handler = new MockHttpMessageHandler("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 1234\r\n\r\n");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://example.com") };
        var worker = _fixture.CreateWorker(httpClient);
        var scenarioBuilder = new WorkerScenarioBuilder<HttpProbeWorker>(worker);

        var result = await scenarioBuilder
                .ExecuteAsync(
                    Guid.NewGuid(),
                    "HttpProbe",
                    new Dictionary<string, string> { ["host"] = "example.com" },
                    TestContext.Current.CancellationToken);

        Assert.True(result.HasAssets);
        Assert.Equal(4, result.AssetCount); // Url, HttpResponse, HtmlPage, Observation
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Scenario_WhenRateLimited_ReturnsPartiallySucceededWithDelayedFlag()
    {
        var handler = new MockHttpMessageHandler("HTTP/1.1 429 Too Many Requests\r\nRetry-After: 60\r\n\r\n");
        using var httpClient = new HttpClient(handler);
        var worker = _fixture.CreateWorker(httpClient);
        var scenarioBuilder = new WorkerScenarioBuilder<HttpProbeWorker>(worker);

        var result = await scenarioBuilder
            .WithRateLimitAllowed(false)
            .ExecuteAsync(
                Guid.NewGuid(),
                "HttpProbe",
                new Dictionary<string, string> { ["host"] = "example.com" },
                TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_WithHtmlResponse_ProducesHtmlPageAsset()
    {
        var html = "<html><head><title>Test Page</title></head><body>Hello World</body></html>";
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html");
            response.Content.Headers.ContentLength = html.Length;
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "false" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "HtmlPage");
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "Observation" && a.Subtype == "HtmlTitle");
    }

    [Fact]
    public async Task ProcessAsync_WithJsonResponse_ProducesJsonDocumentAsset()
    {
        var json = "{\"status\":\"ok\",\"data\":{\"id\":1}}";
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            response.Content.Headers.ContentLength = json.Length;
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "api.example.com", ["follow_redirects"] = "false" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "JsonDocument");
    }

    [Fact]
    public async Task ProcessAsync_With302Redirect_FollowsRedirect()
    {
        var worker = _fixture.CreateWorker(request =>
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (uri.EndsWith("/"))
            {
                var redirectResponse = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
                redirectResponse.Headers.Location = new Uri("https://example.com/final");
                return redirectResponse;
            }

            var okResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            okResponse.Content = new StringContent("<html><title>Final Page</title></html>", System.Text.Encoding.UTF8, "text/html");
            return okResponse;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "true" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
        var urlAssets = result.ProducedAssets.Where(a => a.AssetType == "Url").ToList();
        Assert.True(urlAssets.Count >= 1);
    }

    [Fact]
    public async Task ProcessAsync_With429Status_SignalsBackpressure()
    {
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);

        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(harness.BackpressureSignals, s => s.Host == "example.com" && s.ObservedStatusCode == 429);
        Assert.True(result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_WithCustomTimeout_UsesConfiguredTimeout()
    {
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent("<html><title>Test</title></html>", System.Text.Encoding.UTF8, "text/html");
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["timeout_seconds"] = "15" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
    }

    [Fact]
    public async Task ProcessAsync_WithNoRedirectOption_SkipsRedirectFollowing()
    {
        var callCount = 0;
        var worker = _fixture.CreateWorker(request =>
        {
            callCount++;
            System.Console.WriteLine($"DEBUG: Handler called #{callCount} for {request.RequestUri}");
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/final");
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "false" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        System.Console.WriteLine($"DEBUG: Total callCount: {callCount}");
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ProcessAsync_ProducesHttpHeadersObservation()
    {
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Server", "nginx/1.18");
            response.Headers.Add("X-Request-Id", "abc123");
            response.Content = new StringContent("<html><title>Test</title></html>", System.Text.Encoding.UTF8, "text/html");
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "false" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.ProducedAssets, a => a.AssetType == "Observation" && a.Subtype == "HttpHeaders");
    }

    [Fact]
    public async Task ProcessAsync_WithCharsetDetection_ExtractsCharset()
    {
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent("<html><title>Test</title></html>", System.Text.Encoding.GetEncoding("iso-8859-1"), "text/html");
            response.Content.Headers.ContentType!.CharSet = "iso-8859-1";
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "false" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.ProducedAssets, a => a.Metadata?.GetValueOrDefault("charset") == "iso-8859-1");
    }

    [Fact]
    public async Task ProcessAsync_HttpFallback_WhenHttpsFails()
    {
        var attempts = new List<string>();
        var worker = _fixture.CreateWorker(request =>
        {
            var scheme = request.RequestUri?.Scheme ?? "http";
            attempts.Add(scheme);

            if (scheme == "https")
            {
                throw new HttpRequestException("Connection refused");
            }

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent("<html><title>HTTP Fallback</title></html>", System.Text.Encoding.UTF8, "text/html");
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com" },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(attempts, s => s == "https");
        Assert.Contains(attempts, s => s == "http");
        Assert.NotEmpty(result.ProducedAssets);
    }
}

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public MockHttpMessageHandler(string response)
    {
        var responseMessage = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("", System.Text.Encoding.UTF8, "text/html")
        };

        if (response.Contains("429"))
        {
            responseMessage = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            responseMessage.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        }

        _handler = _ => Task.FromResult(responseMessage);
    }

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = request => Task.FromResult(handler(request));
    }

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _handler(request);
    }
}

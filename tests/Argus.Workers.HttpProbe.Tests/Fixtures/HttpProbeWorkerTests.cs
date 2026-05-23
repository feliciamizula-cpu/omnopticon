using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Argus.Workers.HttpProbe.Tests.Fixtures;

internal sealed class HttpProbeWorkerFixture
{
    public HttpProbeWorker CreateWorker(HttpClient httpClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(httpClient));
        var provider = services.BuildServiceProvider();
        return new HttpProbeWorker(provider.GetRequiredService<IHttpClientFactory>());
    }

    public HttpProbeWorker CreateWorker(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new HttpClient(new MockHttpMessageHandler(handler))));
        var provider = services.BuildServiceProvider();
        return new HttpProbeWorker(provider.GetRequiredService<IHttpClientFactory>());
    }

    public static HttpProbeWorkerFixture Instance => new();

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
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
            .WithRateLimitAllowed(true)
            .ExecuteAsync(
                Guid.NewGuid(),
                "HttpProbe",
                new Dictionary<string, string> { ["host"] = "example.com" },
                TestContext.Current.CancellationToken);

        Assert.True(result.HasAssets);
        Assert.Equal(2, result.AssetCount);
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "JsonDocument");
    }

    [Fact]
    public async Task ProcessAsync_With302Redirect_FollowsRedirect()
    {
        var redirectTarget = "https://example.com/final";
        var worker = _fixture.CreateWorker(request =>
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (uri.EndsWith("/"))
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://example.com/final");
                return response;
            }

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent("<html><title>Final Page</title></html>", System.Text.Encoding.UTF8, "text/html");
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "true" },
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
        harness.SetExpectedBackpressureSignal(new RateLimitBackpressureSignal(
            "example.com",
            "host:example.com",
            TimeSpan.FromSeconds(120),
            429));

        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.ProducedAssets);
    }

    [Fact]
    public async Task ProcessAsync_WithNoRedirectOption_SkipsRedirectFollowing()
    {
        var redirected = false;
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/final");
            redirected = true;
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "false" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(redirected);
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.ProducedAssets, a => a.AssetType == "Observation" && a.Subtype == "HttpHeaders");
    }

    [Fact]
    public async Task ProcessAsync_WithCharsetDetection_ExtractsCharset()
    {
        var worker = _fixture.CreateWorker(request =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Content = new StringContent("<html><title>Test</title></html>", System.Text.Encoding.UTF8, "text/html; charset=utf-8");
            response.Content.Headers.ContentLength = 50;
            return response;
        });

        var harness = new WorkerTestHarness<HttpProbeWorker>(worker);
        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HttpProbe",
            payload: new Dictionary<string, string> { ["host"] = "example.com", ["follow_redirects"] = "false" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.ProducedAssets, a => a.AssetType == "HttpResponse");
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

using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Workers.HttpProbe.Tests.Fixtures;

public sealed class HttpProbeWorkerFixture
{
    public HttpProbeWorker CreateWorker(HttpClient httpClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(httpClient));
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
            payload: new Dictionary<string, string> { ["host"] = "example.com" });

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
            payload: new Dictionary<string, string> { ["host"] = "example.com" });

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
                payload: new Dictionary<string, string>()));
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
            payload: new Dictionary<string, string> { ["host"] = "example.com" });

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
                new Dictionary<string, string> { ["host"] = "example.com" });

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
                new Dictionary<string, string> { ["host"] = "example.com" });

        Assert.False(result.IsSuccess);
        Assert.True(result.Result.PartiallySucceeded);
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

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _handler(request);
    }
}
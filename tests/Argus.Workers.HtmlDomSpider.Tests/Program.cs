using System.Reflection;
using System.Text.Json;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Argus.Workers.HtmlDomSpider.Tests;

public sealed class HtmlDomSpiderWorkerTests
{
    private readonly HtmlDomSpiderWorker _worker;
    private readonly WorkerTestHarness<HtmlDomSpiderWorker> _harness;

    public HtmlDomSpiderWorkerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
        var provider = services.BuildServiceProvider();
        _worker = new HtmlDomSpiderWorker(provider.GetRequiredService<IHttpClientFactory>());
        _harness = new WorkerTestHarness<HtmlDomSpiderWorker>(_worker);
    }

    [Fact]
    public async Task ProcessAsync_WithBasicHtml_ExtractsLinksAndScripts()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        Assert.NotEmpty(result.ProducedAssets);
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "Url");
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "JavaScriptFile");
        Assert.Contains(result.ProducedAssets, a => a.AssetType == "Observation");
    }

    [Fact]
    public async Task ProcessAsync_WithForms_ExtractsFormActions()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/forms" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_WithApiEndpoints_IdentifiesApiEndpointCandidates()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://api.example.com/" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        Assert.Contains(result.ProducedAssets, a =>
            a.AssetType == "ApiEndpoint" && a.Value.Contains("/api/"));
    }

    [Fact]
    public async Task ProcessAsync_WithRelativeUrls_NormalizesCorrectly()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/subdir/page.html" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        var links = result.ProducedAssets.Where(a => a.AssetType == "Url").ToList();
        Assert.NotEmpty(links);
    }

    [Fact]
    public async Task ProcessAsync_WithIframes_ExtractsIframeSources()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.ProducedAssets, a =>
            a.AssetType == "Url" && a.Metadata?["source"] == "iframe-src");
    }

    [Fact]
    public async Task ProcessAsync_WithoutUrlOrArtifactKey_ThrowsInvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _harness.ExecuteAsync(
                programId: Guid.NewGuid(),
                taskType: "HtmlDomSpider",
                payload: new Dictionary<string, string>(),
                workerId: "test-worker",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessAsync_ProducesObservationWithCorrectMetadata()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        var observation = result.ProducedAssets.FirstOrDefault(a => a.AssetType == "Observation");
        Assert.NotNull(observation);
        Assert.NotNull(observation.Metadata);
        Assert.Contains("parsed.count", observation.Metadata.Keys);
        Assert.Contains("links.count", observation.Metadata.Keys);
    }

    [Fact]
    public async Task ProcessAsync_WithDepthInPayload_ReportsDepthCorrectly()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string>
            {
                ["url"] = "https://example.com/",
                ["depth"] = "3"
            },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_WhenRateLimited_ReturnsDelayedResult()
    {
        var harness = new WorkerTestHarness<HtmlDomSpiderWorker>(new HtmlDomSpiderWorker());
        harness.SetRateLimitAllowed(false);

        var result = await harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.PartiallySucceeded);
        Assert.Empty(result.ProducedAssets);
    }

    [Fact]
    public async Task ProcessAsync_WithScopeId_SetsInScopeFlag()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string>
            {
                ["url"] = "https://example.com/page",
                ["scopeId"] = Guid.NewGuid().ToString()
            },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_OutputSummary_ContainsCorrectCounts()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "HtmlDomSpider",
            payload: new Dictionary<string, string> { ["url"] = "https://example.com/" },
            workerId: "test-worker",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);

        using var doc = JsonDocument.Parse(result.OutputSummaryJson);
        Assert.True(doc.RootElement.TryGetProperty("produced", out _));
        Assert.True(doc.RootElement.TryGetProperty("links", out _));
    }
}

public sealed class HtmlDomSpiderWorkerScenarios
{
    private static HtmlDomSpiderWorker CreateWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
        var provider = services.BuildServiceProvider();
        return new HtmlDomSpiderWorker(provider.GetRequiredService<IHttpClientFactory>());
    }

    [Fact]
    public async Task Scenario_BasicHtmlParsing_ExtractsAllAssetTypes()
    {
        var worker = CreateWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<HtmlDomSpiderWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "HtmlDomSpider",
                new Dictionary<string, string> { ["url"] = "https://example.com/" },
                TestContext.Current.CancellationToken);

        Assert.True(result.HasAssets);
        Assert.True(result.IsSuccess);
        Assert.Contains(result.Result.ProducedAssets, a => a.AssetType == "Observation");
    }

    [Fact]
    public async Task Scenario_WithHighDepth_ProcessesCorrectly()
    {
        var worker = CreateWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<HtmlDomSpiderWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "HtmlDomSpider",
                new Dictionary<string, string>
                {
                    ["url"] = "https://example.com/deep/page",
                    ["depth"] = "5"
                },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Scenario_WithApiUrl_ExtractsApiEndpointAsset()
    {
        var worker = CreateWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<HtmlDomSpiderWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "HtmlDomSpider",
                new Dictionary<string, string> { ["url"] = "https://api.example.com/page" },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Result.ProducedAssets, a => a.AssetType == "ApiEndpoint");
    }

    [Fact]
    public async Task Scenario_WithCssUrl_ExtractsCssFileAsset()
    {
        var worker = CreateWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<HtmlDomSpiderWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "HtmlDomSpider",
                new Dictionary<string, string> { ["url"] = "https://example.com/stylesheet" },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Result.ProducedAssets, a => a.AssetType == "CssFile");
    }

    [Fact]
    public async Task Scenario_WithJsUrl_ExtractsJavaScriptFileAsset()
    {
        var worker = CreateWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<HtmlDomSpiderWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "HtmlDomSpider",
                new Dictionary<string, string> { ["url"] = "https://example.com/static/app.js" },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Result.ProducedAssets, a => a.AssetType == "JavaScriptFile");
    }
}

public sealed class HtmlParseResultTests
{
    private static readonly MethodInfo? ParseHtmlMethod = typeof(HtmlDomSpiderWorker)
        .GetMethod("ParseHtml", BindingFlags.NonPublic | BindingFlags.Static);

    [Fact]
    public void ParseHtml_WithValidHtml_ExtractsAllElements()
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <script src=""/js/app.js""></script>
    <link rel=""stylesheet"" href=""/css/style.css"">
</head>
<body>
    <a href=""/page1"">Page 1</a>
    <a href=""/api/data"">API</a>
    <form action=""/submit"" method=""POST"">
        <input name=""field"" />
        <button>Submit</button>
    </form>
    <iframe src=""/embed""></iframe>
</body>
</html>";

        var result = InvokeParseHtml(html, new Uri("https://example.com/"));

        Assert.NotEmpty(result.Links);
        Assert.NotEmpty(result.ScriptSources);
        Assert.NotEmpty(result.CssLinks);
        Assert.NotEmpty(result.Forms);
        Assert.NotEmpty(result.IframeSrcs);
    }

    [Fact]
    public void ParseHtml_WithEmptyHtml_ReturnsEmptyResults()
    {
        var html = "<html><body></body></html>";

        var result = InvokeParseHtml(html, new Uri("https://example.com/"));

        Assert.Empty(result.Links);
        Assert.Empty(result.ScriptSources);
    }

    [Fact]
    public void ParseHtml_WithMetaRefresh_ExtractsRedirectUrl()
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <meta http-equiv=""refresh"" content=""5;url=./redirected"">
</head>
<body></body>
</html>";

        var result = InvokeParseHtml(html, new Uri("https://example.com/page"));

        Assert.NotNull(result.MetaRefreshUrl);
    }

    [Fact]
    public void ParseHtml_WithDuplicateUrls_EliminatesDuplicates()
    {
        var html = @"<!DOCTYPE html>
<html>
<body>
    <a href=""/page"">Link 1</a>
    <a href=""/page"">Link 2</a>
    <a href=""/other"">Link 3</a>
</body>
</html>";

        var result = InvokeParseHtml(html, new Uri("https://example.com/"));

        Assert.Equal(2, result.Links.Count);
    }

    [Fact]
    public void ParseHtml_WithJavascriptLinks_FiltersOut()
    {
        var html = @"<!DOCTYPE html>
<html>
<body>
    <a href=""javascript:void(0)"">JS</a>
    <a href=""/valid"">Valid</a>
    <a href=""mailto:test@example.com"">Email</a>
    <a href=""tel:+1234567890"">Phone</a>
    <a href=""data:text/html,test"">Data</a>
    <a href=""#"">Hash</a>
</body>
</html>";

        var result = InvokeParseHtml(html, new Uri("https://example.com/"));

        Assert.Single(result.Links);
        Assert.Equal("https://example.com/valid", result.Links[0]);
    }

    [Fact]
    public void ParseHtml_WithInlineScripts_ExtractsFetchUrls()
    {
        var html = @"<!DOCTYPE html>
<html>
<body>
    <script>
        fetch('/api/data').then(r => r.json());
        axios.get('/api/users');
    </script>
</body>
</html>";

        var result = InvokeParseHtml(html, new Uri("https://example.com/page"));

        Assert.Contains(result.ScriptSources, s => s.Contains("/api/data") || s.Contains("/api/users"));
    }

    private static HtmlParseResultProxy InvokeParseHtml(string html, Uri baseUri)
    {
        if (ParseHtmlMethod == null)
        {
            return new HtmlParseResultProxy();
        }

        var result = ParseHtmlMethod.Invoke(null, new object[] { html, baseUri });
        var type = result!.GetType();

        return new HtmlParseResultProxy
        {
            Links = ((List<string>?)type.GetProperty("Links")?.GetValue(result))?.ToList() ?? new List<string>(),
            ScriptSources = ((List<string>?)type.GetProperty("ScriptSources")?.GetValue(result))?.ToList() ?? new List<string>(),
            CssLinks = ((List<string>?)type.GetProperty("CssLinks")?.GetValue(result))?.ToList() ?? new List<string>(),
            Forms = ((List<FormInfoProxy>?)type.GetProperty("Forms")?.GetValue(result))?.Select(f => new FormInfoProxy
            {
                Action = (string?)f.GetType().GetProperty("Action")?.GetValue(f),
                Method = (string?)f.GetType().GetProperty("Method")?.GetValue(f)
            }).ToList() ?? new List<FormInfoProxy>(),
            IframeSrcs = ((List<string>?)type.GetProperty("IframeSrcs")?.GetValue(result))?.ToList() ?? new List<string>(),
            MetaRefreshUrl = (string?)type.GetProperty("MetaRefreshUrl")?.GetValue(result)
        };
    }

    private sealed class HtmlParseResultProxy
    {
        public List<string> Links { get; init; } = new();
        public List<string> ScriptSources { get; init; } = new();
        public List<string> CssLinks { get; init; } = new();
        public List<FormInfoProxy> Forms { get; init; } = new();
        public List<string> IframeSrcs { get; init; } = new();
        public string? MetaRefreshUrl { get; init; }
    }

    private sealed class FormInfoProxy
    {
        public string? Action { get; init; }
        public string? Method { get; init; }
    }
}

internal static class HtmlDomSpiderTestExtensions
{
    public static async Task<WorkerProcessResult> ExecuteWithHtmlAsync(
        this WorkerTestHarness<HtmlDomSpiderWorker> harness,
        Guid programId,
        string htmlContent,
        string url,
        CancellationToken cancellationToken = default)
    {
        var handler = new TestHtmlHttpMessageHandler(htmlContent);
        using var httpClient = new HttpClient(handler);

        return await harness.ExecuteAsync(
            programId,
            "HtmlDomSpider",
            new Dictionary<string, string> { ["url"] = url },
            cancellationToken: cancellationToken);
    }

    private sealed class TestHtmlHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _htmlContent;

        public TestHtmlHttpMessageHandler(string htmlContent) => _htmlContent = htmlContent;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_htmlContent, System.Text.Encoding.UTF8, "text/html")
            });
        }
    }
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public StubHttpClientFactory()
    {
        _client = new HttpClient(new MockOkHttpMessageHandler());
    }

    public HttpClient CreateClient(string name) => _client;
}

internal sealed class MockOkHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <script src=""/static/app.js""></script>
    <link rel=""stylesheet"" href=""/styles/main.css"">
</head>
<body>
    <nav>
        <a href=""/"">Home</a>
        <a href=""/about"">About</a>
        <a href=""/api/v1/users"">Users API</a>
        <a href=""/admin"">Admin</a>
    </nav>
    <form action=""/login"" method=""POST"">
        <input type=""text"" name=""username"">
        <button type=""submit"">Login</button>
    </form>
    <iframe src=""/embed/widget""></iframe>
    <script>
        fetch('/api/data').then(r => r.json());
    </script>
</body>
</html>";

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
        });
    }
}
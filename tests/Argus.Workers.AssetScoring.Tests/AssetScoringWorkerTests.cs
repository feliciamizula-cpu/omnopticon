using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Tasks;
using Xunit;

namespace Argus.Workers.AssetScoring.Tests;

public sealed class AssetScoringWorkerTests
{
    private readonly AssetScoringWorker _worker = new();
    private readonly WorkerTestHarness<AssetScoringWorker> _harness = new(new AssetScoringWorker());

    [Theory]
    [InlineData("https://api.example.com/graphql", "Url", "graphql-detected", 85)]
    [InlineData("https://admin.example.com/dashboard", "Url", "admin-panel-detected", 80)]
    [InlineData("https://jenkins.example.com/job/test", "Url", "dev-tool-detected", 75)]
    [InlineData("https://internal.example.com/corp", "Url", "internal-endpoint-detected", 78)]
    [InlineData("https://debug.example.com/trace", "Url", "debug-info-detected", 88)]
    [InlineData("https://example.com/api-docs", "Url", "api-docs-detected", 70)]
    public async Task ProcessAsync_WithHighValueAsset_ReturnsObservationWithCorrectScore(
        string url, string assetType, string expectedSignal, int expectedScore)
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "AssetScoring",
            payload: new Dictionary<string, string>
            {
                ["value"] = url,
                ["assetType"] = assetType,
                ["targetId"] = Guid.NewGuid().ToString()
            },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        Assert.NotEmpty(result.ProducedAssets);
        var observation = result.ProducedAssets.First();
        Assert.Equal("Observation", observation.AssetType);
        Assert.Contains(observation.Tags ?? [], t => t == "interesting-asset" || t == "scored");
    }

    [Theory]
    [InlineData("https://example.com/page", "Url")]
    [InlineData("https://subdomain.example.com/", "Subdomain")]
    public async Task ProcessAsync_WithNormalAsset_ProducesMinimalObservations(string url, string assetType)
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "AssetScoring",
            payload: new Dictionary<string, string>
            {
                ["value"] = url,
                ["assetType"] = assetType
            },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_WithGraphQLEndpoint_ProducesGraphQLObservation()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "AssetScoring",
            payload: new Dictionary<string, string>
            {
                ["value"] = "https://api.example.com/graphql",
                ["assetType"] = "Url"
            },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        Assert.NotEmpty(result.ProducedAssets);
        var graphQLAsset = result.ProducedAssets.FirstOrDefault(a =>
            a.Metadata?["signal"] == "graphql-detected");
        Assert.NotNull(graphQLAsset);
    }

    [Fact]
    public async Task ProcessAsync_WithDebugEndpoint_ProducesHighScoreObservation()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "AssetScoring",
            payload: new Dictionary<string, string>
            {
                ["value"] = "https://example.com/.env",
                ["assetType"] = "Url"
            },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
        Assert.NotEmpty(result.ProducedAssets);
        var debugAsset = result.ProducedAssets.FirstOrDefault(a =>
            a.Metadata?["signal"] == "debug-info-detected");
        Assert.NotNull(debugAsset);
        Assert.Equal(88, int.Parse(debugAsset.Metadata?["signal"].Length.ToString()));
    }

    [Fact]
    public async Task ProcessAsync_WithJavaScriptFile_ProducesJsFileObservation()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "AssetScoring",
            payload: new Dictionary<string, string>
            {
                ["value"] = "https://example.com/static/app.js",
                ["assetType"] = "JavaScriptFile"
            },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
    }

    [Fact]
    public async Task ProcessAsync_WithMinifiedJavaScript_ProducesLowerScoreObservation()
    {
        var result = await _harness.ExecuteAsync(
            programId: Guid.NewGuid(),
            taskType: "AssetScoring",
            payload: new Dictionary<string, string>
            {
                ["value"] = "https://example.com/static/app.min.js",
                ["assetType"] = "JavaScriptFile"
            },
            workerId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.PartiallySucceeded);
    }
}

public sealed class AssetScoringWorkerScenarios
{
    [Fact]
    public async Task Scenario_HighValueAsset_GeneratesHighScoreObservation()
    {
        var worker = new AssetScoringWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<AssetScoringWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "AssetScoring",
                new Dictionary<string, string>
                {
                    ["value"] = "https://admin.example.com/internal/dashboard",
                    ["assetType"] = "Url"
                },
                TestContext.Current.CancellationToken);

        Assert.True(result.HasAssets);
        Assert.Contains(result.Result.ProducedAssets, a =>
            a.Metadata != null && a.Metadata.ContainsKey("signal"));
    }

    [Fact]
    public async Task Scenario_MultipleHighValueEndpoints_ProducesMultipleObservations()
    {
        var worker = new AssetScoringWorker();
        var scenarioBuilder = new WorkerScenarioBuilder<AssetScoringWorker>(worker);

        var result = await scenarioBuilder
            .ExecuteAsync(
                Guid.NewGuid(),
                "AssetScoring",
                new Dictionary<string, string>
                {
                    ["value"] = "https://api.example.com/graphql",
                    ["assetType"] = "Url"
                },
                TestContext.Current.CancellationToken);

        Assert.True(result.HasAssets);
    }
}
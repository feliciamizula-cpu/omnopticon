namespace Argus.AgentService.Tests;

using Argus.AgentService.Agents;
using Argus.AgentService.Stores;
using Argus.Contracts.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public sealed class AgentSelectionServiceTests
{
    private static AgentDto AgentFor(string name, string role, string[] caps, int sort = 1, string status = "active") =>
        new(Guid.NewGuid(), name, role, null, sort, status, [], null, "idle", null, null,
            "claude", "claude-sonnet-4-6", "Claude", "standard",
            caps, AgentCapabilities.DefaultRuntimeForCapabilities(caps),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SelectAsync_ReturnsLowestSortOrderActiveAgent_WhenNoRequirements()
    {
        var store = Substitute.For<IAgentStore>();
        store.ListAgentsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentDto>
        {
            AgentFor("b", "developer", new[]{ AgentCapabilities.ReadAppState }, sort: 2),
            AgentFor("a", "developer", new[]{ AgentCapabilities.ReadAppState }, sort: 1),
        });

        var svc = new AgentSelectionService(store, NullLogger<AgentSelectionService>.Instance);
        var ctx = await svc.SelectAsync("developer", requiredCapabilities: Array.Empty<string>());

        Assert.NotNull(ctx);
        Assert.Equal("a", ctx!.Agent.Name);
    }

    [Fact]
    public async Task SelectAsync_FiltersToAgentWithRequiredCapability()
    {
        var store = Substitute.For<IAgentStore>();
        store.ListAgentsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentDto>
        {
            AgentFor("readonly",  "developer", new[]{ AgentCapabilities.ReadAppState }, sort: 1),
            AgentFor("committer", "developer", new[]{ AgentCapabilities.ReadAppState, AgentCapabilities.GitCommit }, sort: 2),
        });

        var svc = new AgentSelectionService(store, NullLogger<AgentSelectionService>.Instance);
        var ctx = await svc.SelectAsync("developer", requiredCapabilities: new[]{ AgentCapabilities.GitCommit });

        Assert.NotNull(ctx);
        Assert.Equal("committer", ctx!.Agent.Name);
    }

    [Fact]
    public async Task SelectAsync_ReturnsNull_WhenNoAgentSatisfiesRequirements()
    {
        var store = Substitute.For<IAgentStore>();
        store.ListAgentsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentDto>
        {
            AgentFor("readonly", "developer", new[]{ AgentCapabilities.ReadAppState }, sort: 1),
        });

        var svc = new AgentSelectionService(store, NullLogger<AgentSelectionService>.Instance);
        var ctx = await svc.SelectAsync("developer", requiredCapabilities: new[]{ AgentCapabilities.GitPushMain });

        Assert.Null(ctx);
    }

    [Fact]
    public async Task SelectAsync_TreatsGitPushMainAsSatisfyingGitPush_ViaImplication()
    {
        var store = Substitute.For<IAgentStore>();
        store.ListAgentsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentDto>
        {
            AgentFor("rewriter", "developer", new[]{ AgentCapabilities.GitHistoryRewrite }, sort: 1),
        });

        var svc = new AgentSelectionService(store, NullLogger<AgentSelectionService>.Instance);
        var ctx = await svc.SelectAsync("developer", requiredCapabilities: new[]{ AgentCapabilities.GitCommit });

        Assert.NotNull(ctx);
        Assert.Equal("rewriter", ctx!.Agent.Name);
    }
}

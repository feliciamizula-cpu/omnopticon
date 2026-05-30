namespace Argus.AgentService.Tests;

using Argus.AgentService.Agents;
using Argus.AgentService.Stores;
using Argus.Contracts.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public sealed class AgentExecutionServiceTests
{
    private static AgentDto MakeAgent(
        string name = "devA",
        string role = "developer",
        string[]? caps = null,
        string defaultRuntime = AgentCapabilities.RuntimeWorkspace)
    {
        caps ??= new[] { AgentCapabilities.ReadAppState, AgentCapabilities.GitCommit };
        return new AgentDto(
            Guid.NewGuid(), name, role, null, 1, "active", [], null, "idle", null, null,
            "claude", "claude-sonnet-4-6", "Claude", "standard",
            caps, defaultRuntime,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ExecuteAsync_PicksWorkspaceRuntime_WhenTaskRuntimeIsDefault_AndAgentDefaultIsWorkspace()
    {
        var store = Substitute.For<IAgentStore>();
        var task = new AgentTaskDto(
            "T1", "do thing", "instr", "normal", "pending", null, "developer", "implementation",
            "default", new[]{ AgentCapabilities.GitCommit },
            null, null, null, DateTimeOffset.UtcNow, null, null, null, null,
            DateTimeOffset.UtcNow, null, null, 0);
        store.GetTaskAsync("T1", Arg.Any<CancellationToken>()).Returns(task);

        var agent = MakeAgent(defaultRuntime: AgentCapabilities.RuntimeWorkspace);
        store.ListAgentsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentDto> { agent });

        var inPod = Substitute.For<IRuntimeAdapter>();
        inPod.Kind.Returns(AgentCapabilities.RuntimeInPod);
        var workspace = Substitute.For<IRuntimeAdapter>();
        workspace.Kind.Returns(AgentCapabilities.RuntimeWorkspace);
        workspace.ExecuteAsync(Arg.Any<RuntimeExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RuntimeExecutionResult(true, "ok", null, "/tmp/ws"));

        store.CreateRunAsync(Arg.Any<AgentTaskRunDto>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<AgentTaskRunDto>());

        var completedRun = new AgentTaskRunDto(
            Guid.NewGuid(), "T1", null, null, agent.AgentId, "manual", "completed",
            AgentCapabilities.RuntimeWorkspace, [], DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ok", null, "/tmp/ws");
        store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(completedRun);

        var selection = new AgentSelectionService(store, NullLogger<AgentSelectionService>.Instance);
        var sut = new AgentExecutionService(store, selection,
            new[] { inPod, workspace },
            NullLogger<AgentExecutionService>.Instance);

        var run = await sut.ExecuteAsync("T1", scheduleId: null, triggerId: null, triggerSource: "manual", CancellationToken.None);

        Assert.NotNull(run);
        await workspace.Received(1).ExecuteAsync(Arg.Any<RuntimeExecutionRequest>(), Arg.Any<CancellationToken>());
        await inPod.DidNotReceive().ExecuteAsync(Arg.Any<RuntimeExecutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailedRun_WhenNoEligibleAgent()
    {
        var store = Substitute.For<IAgentStore>();
        var task = new AgentTaskDto(
            "T2", "restricted task", null, "normal", "pending", null, "developer", null,
            "default", new[]{ AgentCapabilities.GitPushMain },
            null, null, null, DateTimeOffset.UtcNow, null, null, null, null,
            DateTimeOffset.UtcNow, null, null, 0);
        store.GetTaskAsync("T2", Arg.Any<CancellationToken>()).Returns(task);

        // No agents available
        store.ListAgentsAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentDto>());

        var runId = Guid.NewGuid();
        var seedRun = new AgentTaskRunDto(runId, "T2", null, null, null, "manual", "pending",
            AgentCapabilities.RuntimeInPod, [], DateTimeOffset.UtcNow, null, null, null, null, null);
        store.CreateRunAsync(Arg.Any<AgentTaskRunDto>(), Arg.Any<CancellationToken>())
            .Returns(seedRun);

        var failedRun = seedRun with { Status = "failed", Error = "No agent available" };
        store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(failedRun);

        var inPod = Substitute.For<IRuntimeAdapter>();
        inPod.Kind.Returns(AgentCapabilities.RuntimeInPod);

        var selection = new AgentSelectionService(store, NullLogger<AgentSelectionService>.Instance);
        var sut = new AgentExecutionService(store, selection,
            new[] { inPod },
            NullLogger<AgentExecutionService>.Instance);

        var run = await sut.ExecuteAsync("T2", null, null, "manual", CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal("failed", run!.Status);
        await inPod.DidNotReceive().ExecuteAsync(Arg.Any<RuntimeExecutionRequest>(), Arg.Any<CancellationToken>());
    }
}

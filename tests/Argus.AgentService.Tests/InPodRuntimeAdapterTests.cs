namespace Argus.AgentService.Tests;

using Argus.AgentService.Agents;
using Argus.Contracts.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public sealed class InPodRuntimeAdapterTests
{
    private static AgentTaskRunDto Run(string status = "pending") =>
        new(Guid.NewGuid(), "T1", null, null, null, "manual", status,
            AgentCapabilities.RuntimeInPod, [], DateTimeOffset.UtcNow, null, null, null, null, null);

    private static AgentTaskDto Task() => new(
        "T1", "desc", "instructions", "normal", "pending", null, "developer", "implementation",
        AgentCapabilities.RuntimeInPod, [],
        null, null, null, DateTimeOffset.UtcNow, null, null, null, null,
        DateTimeOffset.UtcNow, null, null, 0);

    private static AgentDto Agent() => new(
        Guid.NewGuid(), "a", "developer", null, 1, "active", [], null, "idle", null, null,
        "claude", "claude-sonnet-4-6", "Claude", "standard",
        new[]{ AgentCapabilities.ReadAppState }, AgentCapabilities.RuntimeInPod,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ExecuteAsync_ReturnsOutput_WhenChatClientSucceeds()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, "HELLO")));

        var factory = (string tool, string model) => chat;
        var sut = new InPodRuntimeAdapter(factory, NullLogger<InPodRuntimeAdapter>.Instance);

        var result = await sut.ExecuteAsync(new RuntimeExecutionRequest(Run(), Task(), Agent(), "ping"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("HELLO", result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenChatClientThrows()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Microsoft.Extensions.AI.ChatResponse>>(_ => throw new InvalidOperationException("boom"));

        var sut = new InPodRuntimeAdapter((_, _) => chat, NullLogger<InPodRuntimeAdapter>.Instance);

        var result = await sut.ExecuteAsync(new RuntimeExecutionRequest(Run(), Task(), Agent(), "ping"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
    }
}

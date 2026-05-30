namespace Argus.AgentService.Tests;

using Argus.Contracts.Agents;
using Xunit;

public sealed class AgentCapabilitiesTests
{
    [Theory]
    [InlineData("junior_developer", AgentCapabilities.GitPush, true)]
    [InlineData("junior_developer", AgentCapabilities.GitPushMain, false)]
    [InlineData("developer", AgentCapabilities.GitPushMain, true)]
    [InlineData("senior_system_architect", AgentCapabilities.GitHistoryRewrite, true)]
    [InlineData("junior_devops", AgentCapabilities.KubectlApply, false)]
    [InlineData("senior_devops", AgentCapabilities.KubectlApply, true)]
    [InlineData("devops_architect", AgentCapabilities.GitPushMain, true)]
    public void RolePreset_GrantsExpectedCapability(string role, string capability, bool expected)
    {
        var preset = AgentCapabilities.PresetForRole(role);
        Assert.Equal(expected, preset.Contains(capability));
    }

    [Fact]
    public void GitPushMain_Implies_GitPush()
    {
        var caps = new[] { AgentCapabilities.GitPushMain };
        Assert.True(AgentCapabilities.Has(caps, AgentCapabilities.GitPush));
    }

    [Fact]
    public void GitHistoryRewrite_Implies_GitPushMain()
    {
        var caps = new[] { AgentCapabilities.GitHistoryRewrite };
        Assert.True(AgentCapabilities.Has(caps, AgentCapabilities.GitPushMain));
        Assert.True(AgentCapabilities.Has(caps, AgentCapabilities.GitPush));
        Assert.True(AgentCapabilities.Has(caps, AgentCapabilities.GitCommit));
    }

    [Theory]
    [InlineData(AgentCapabilities.GitCommit, "workspace")]
    [InlineData(AgentCapabilities.GitPushMain, "workspace")]
    [InlineData(AgentCapabilities.KubectlApply, "workspace")]
    [InlineData(AgentCapabilities.ReadAppState, "in_pod")]
    [InlineData(AgentCapabilities.WriteTodos, "in_pod")]
    [InlineData(AgentCapabilities.KubectlRead, "in_pod")]
    public void DefaultRuntimeForCapability_MatchesSpec(string capability, string expectedRuntime)
    {
        Assert.Equal(expectedRuntime, AgentCapabilities.DefaultRuntimeFor(capability));
    }

    [Fact]
    public void DefaultRuntimeForAgent_IsWorkspace_IfAnyCapIsWorkspace()
    {
        var caps = new[] { AgentCapabilities.ReadAppState, AgentCapabilities.GitCommit };
        Assert.Equal("workspace", AgentCapabilities.DefaultRuntimeForCapabilities(caps));
    }

    [Fact]
    public void DefaultRuntimeForAgent_IsInPod_IfAllInPod()
    {
        var caps = new[] { AgentCapabilities.ReadAppState, AgentCapabilities.WriteTodos };
        Assert.Equal("in_pod", AgentCapabilities.DefaultRuntimeForCapabilities(caps));
    }
}

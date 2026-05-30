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

    // Renamed from GitHistoryRewrite_Implies_GitPushMain to honestly describe all three assertions.
    [Fact]
    public void GitHistoryRewrite_TransitivelyImplies_GitPushMain_GitPush_GitCommit()
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

    // ── Expand() coverage ────────────────────────────────────────────────────

    [Fact]
    public void Expand_ReturnsInput_WhenNoImplications()
    {
        var result = AgentCapabilities.Expand(new[] { AgentCapabilities.ReadAppState, AgentCapabilities.WriteTodos });
        Assert.Contains(AgentCapabilities.ReadAppState, result);
        Assert.Contains(AgentCapabilities.WriteTodos, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Expand_WalksOneLevel()
    {
        var result = AgentCapabilities.Expand(new[] { AgentCapabilities.GitPush });
        Assert.Contains(AgentCapabilities.GitPush, result);
        Assert.Contains(AgentCapabilities.GitCommit, result);
    }

    [Fact]
    public void Expand_WalksTransitively_FromGitHistoryRewrite()
    {
        var result = AgentCapabilities.Expand(new[] { AgentCapabilities.GitHistoryRewrite });
        Assert.Contains(AgentCapabilities.GitHistoryRewrite, result);
        Assert.Contains(AgentCapabilities.GitPushMain, result);
        Assert.Contains(AgentCapabilities.GitPush, result);
        Assert.Contains(AgentCapabilities.GitCommit, result);
    }

    [Fact]
    public void Expand_OnEmptyInput_ReturnsEmpty()
    {
        var result = AgentCapabilities.Expand(Array.Empty<string>());
        Assert.Empty(result);
    }

    // ── Boundary tests ───────────────────────────────────────────────────────

    [Fact]
    public void PresetForRole_UnknownRole_FallsBackToReadAppStateOnly()
    {
        var preset = AgentCapabilities.PresetForRole("totally_made_up");
        Assert.Single(preset);
        Assert.Equal(AgentCapabilities.ReadAppState, preset[0]);
    }

    [Fact]
    public void DefaultRuntimeFor_UnknownCapability_FallsBackToInPod()
    {
        Assert.Equal(AgentCapabilities.RuntimeInPod, AgentCapabilities.DefaultRuntimeFor("nonexistent_cap"));
    }

    // ── AllCapabilities consistency ──────────────────────────────────────────

    [Fact]
    public void AllCapabilities_IsConsistentWith_RuntimeMap()
    {
        foreach (var cap in AgentCapabilities.AllCapabilities)
        {
            // Should not fall back to default; every known cap must be in the map.
            var runtime = AgentCapabilities.DefaultRuntimeFor(cap);
            Assert.True(runtime == AgentCapabilities.RuntimeInPod
                     || runtime == AgentCapabilities.RuntimeWorkspace,
                $"Capability {cap} returned unexpected runtime {runtime}");
        }
        Assert.Equal(13, AgentCapabilities.AllCapabilities.Count);
    }
}

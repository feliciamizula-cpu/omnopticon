namespace Argus.Contracts.Agents;

public static class AgentCapabilities
{
    // ── Capability identifiers (granular allow-list) ─────────────────────────
    public const string ReadAppState        = "read_app_state";
    public const string ReadExternal        = "read_external";
    public const string WriteTodos          = "write_todos";
    public const string WriteTasks          = "write_tasks";
    public const string WriteReports        = "write_reports";
    public const string GitRead             = "git_read";
    public const string GitCommit           = "git_commit";
    public const string GitPush             = "git_push";
    public const string GitPushMain         = "git_push_main";
    public const string GitHistoryRewrite   = "git_history_rewrite";
    public const string DotnetBuild         = "dotnet_build";
    public const string KubectlRead         = "kubectl_read";
    public const string KubectlApply        = "kubectl_apply";

    public static readonly IReadOnlyList<string> AllCapabilities = new[]
    {
        ReadAppState, ReadExternal, WriteTodos, WriteTasks, WriteReports,
        GitRead, GitCommit, GitPush, GitPushMain, GitHistoryRewrite,
        DotnetBuild, KubectlRead, KubectlApply
    };

    // ── Runtime sentinel values (not capabilities) ───────────────────────────
    public const string RuntimeInPod     = "in_pod";
    public const string RuntimeWorkspace = "workspace";
    public const string RuntimeDefault   = "default";

    private static readonly Dictionary<string, string> RuntimeMap = new(StringComparer.Ordinal)
    {
        [ReadAppState]      = RuntimeInPod,
        [ReadExternal]      = RuntimeInPod,
        [WriteTodos]        = RuntimeInPod,
        [WriteTasks]        = RuntimeInPod,
        [WriteReports]      = RuntimeInPod,
        [KubectlRead]       = RuntimeInPod,
        [GitRead]           = RuntimeWorkspace,
        [GitCommit]         = RuntimeWorkspace,
        [GitPush]           = RuntimeWorkspace,
        [GitPushMain]       = RuntimeWorkspace,
        [GitHistoryRewrite] = RuntimeWorkspace,
        [DotnetBuild]       = RuntimeWorkspace,
        [KubectlApply]      = RuntimeWorkspace,
    };

    // Direct implications only — Expand() performs the transitive BFS walk.
    private static readonly Dictionary<string, string[]> Implications = new(StringComparer.Ordinal)
    {
        [GitPush]           = new[] { GitCommit },
        [GitPushMain]       = new[] { GitPush },
        [GitHistoryRewrite] = new[] { GitPushMain },
    };

    public static string DefaultRuntimeFor(string capability) =>
        RuntimeMap.TryGetValue(capability, out var r) ? r : RuntimeInPod;

    public static string DefaultRuntimeForCapabilities(IEnumerable<string> capabilities) =>
        capabilities.Any(c => DefaultRuntimeFor(c) == RuntimeWorkspace) ? RuntimeWorkspace : RuntimeInPod;

    public static bool Has(IEnumerable<string> granted, string requested) =>
        Expand(granted).Contains(requested);

    public static IReadOnlySet<string> Expand(IEnumerable<string> capabilities)
    {
        var result = new HashSet<string>(capabilities, StringComparer.Ordinal);
        var frontier = new Queue<string>(result);
        while (frontier.Count > 0)
        {
            var c = frontier.Dequeue();
            foreach (var implied in ImpliedSet(c))
            {
                if (result.Add(implied)) frontier.Enqueue(implied);
            }
        }
        return result;
    }

    private static IEnumerable<string> ImpliedSet(string capability) =>
        Implications.TryGetValue(capability, out var imp) ? imp : Array.Empty<string>();

    public static IReadOnlyList<string> PresetForRole(string role) => role.ToLowerInvariant() switch
    {
        "junior_developer" => new[]
            { ReadAppState, WriteTodos, GitRead, GitCommit, GitPush, DotnetBuild },
        "developer" or "senior_developer" => new[]
            { ReadAppState, WriteTodos, GitRead, GitCommit, GitPush, GitPushMain, DotnetBuild },
        "junior_system_architect" => new[]
            { ReadAppState, ReadExternal, WriteReports, WriteTodos, WriteTasks },
        "senior_system_architect" => new[]
            { ReadAppState, ReadExternal, WriteReports, WriteTodos, WriteTasks,
              GitRead, GitHistoryRewrite },
        "junior_devops" => new[]
            { ReadAppState, KubectlRead, WriteReports, WriteTodos },
        "senior_devops" => new[]
            { ReadAppState, KubectlRead, KubectlApply, WriteReports, WriteTodos,
              GitRead, GitCommit, GitPush },
        "devops_architect" => new[]
            { ReadAppState, KubectlRead, KubectlApply, WriteReports, WriteTodos,
              GitRead, GitCommit, GitPush, GitPushMain },
        _ => new[] { ReadAppState }
    };
}

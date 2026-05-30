namespace Argus.AgentService.Agents;

using System.Diagnostics;
using Argus.Contracts.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

public sealed class WorkspaceRuntimeAdapter(
    Func<string, string, IChatClient> chatClientFactory,
    ILogger<WorkspaceRuntimeAdapter> logger) : IRuntimeAdapter
{
    public string Kind => AgentCapabilities.RuntimeWorkspace;

    private static readonly TimeSpan DefaultWallClock = TimeSpan.FromMinutes(15);

    public async Task<RuntimeExecutionResult> ExecuteAsync(
        RuntimeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var repoRoot = Environment.GetEnvironmentVariable("ARGUS_REPO_ROOT")
            ?? throw new InvalidOperationException(
                "WorkspaceRuntimeAdapter requires ARGUS_REPO_ROOT env var pointing to a checkout.");

        var workdir = Path.Combine(Path.GetTempPath(), $"agent-{request.Run.RunId:N}");
        try
        {
            // 1. Fetch origin/main to ensure we branch from the latest.
            await RunGitAsync(repoRoot, new[] { "fetch", "origin", "main" }, cancellationToken);

            // 2. Create the worktree off origin/main.
            await RunGitAsync(repoRoot, new[] { "worktree", "add", "-B", $"agent/{request.Run.RunId:N}",
                workdir, "origin/main" }, cancellationToken);

            // 3. Run the CLI inside the worktree.
            var chat = chatClientFactory(request.Agent.Tool, request.Agent.Model);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultWallClock);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, BuildSystemPrompt(request, workdir)),
                new(ChatRole.User, request.Prompt)
            };
            var completion = await chat.GetResponseAsync(messages, cancellationToken: timeoutCts.Token);
            var output = completion.Text ?? string.Empty;

            // 4. Build-gate if granted.
            if (AgentCapabilities.Has(request.Agent.Capabilities, AgentCapabilities.DotnetBuild))
            {
                var (buildExit, buildLog) = await RunProcessAsync("dotnet", new[] { "build" }, workdir, cancellationToken);
                if (buildExit != 0)
                {
                    return new RuntimeExecutionResult(false, output,
                        $"Build gate failed (exit {buildExit}):\n{buildLog}", workdir);
                }
            }

            // 5. Stage & commit if any changes and we have GitCommit.
            if (AgentCapabilities.Has(request.Agent.Capabilities, AgentCapabilities.GitCommit))
            {
                var status = await RunGitAsync(workdir, new[] { "status", "--porcelain" }, cancellationToken);
                if (!string.IsNullOrWhiteSpace(status.stdout))
                {
                    await RunGitAsync(workdir, new[] { "add", "-A" }, cancellationToken);
                    var msg = $"agent({request.Agent.Role}): {request.Task.Description}\n\nrunId: {request.Run.RunId}";
                    await RunGitAsync(workdir, new[] { "commit", "-m", msg }, cancellationToken);
                }
            }

            // 6. Push when authorized.
            if (AgentCapabilities.Has(request.Agent.Capabilities, AgentCapabilities.GitPushMain))
            {
                await RunGitAsync(workdir, new[] { "push", "origin",
                    $"HEAD:refs/heads/main" }, cancellationToken);
            }
            else if (AgentCapabilities.Has(request.Agent.Capabilities, AgentCapabilities.GitPush))
            {
                await RunGitAsync(workdir, new[] { "push", "origin",
                    $"HEAD:refs/heads/agent/{request.Run.RunId:N}" }, cancellationToken);
            }

            return new RuntimeExecutionResult(true, output, null, workdir);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeExecutionResult(false, null, $"Wall-clock timeout after {DefaultWallClock}: {ex.Message}", workdir);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WorkspaceRuntimeAdapter failed for task '{TaskId}'", request.Task.TaskId);
            return new RuntimeExecutionResult(false, null, ex.Message, workdir);
        }
        finally
        {
            // Best-effort worktree cleanup.
            try
            {
                await RunGitAsync(repoRoot, new[] { "worktree", "remove", "--force", workdir }, CancellationToken.None);
            }
            catch { /* swallow — leaving a stale worktree is recoverable */ }
        }
    }

    private static string BuildSystemPrompt(RuntimeExecutionRequest r, string workdir) =>
        $$"""
        You are an agent with role '{{r.Agent.Role}}' running in an isolated git worktree at {{workdir}}.
        Capabilities granted: {{string.Join(", ", r.Agent.Capabilities)}}.
        You may edit files in this worktree. Do NOT touch any path outside it.
        The build-gate (`dotnet build`) must pass before your changes are committed.
        Bare `git push --force` is blocked; use `--force-with-lease` only if `git_history_rewrite` is granted.
        Task type: {{r.Task.TaskType ?? "general"}}.
        """;

    private static Task<(int exit, string stdout)> RunGitAsync(string cwd, string[] args, CancellationToken ct) =>
        RunProcessAsync("git", args, cwd, ct);

    private static async Task<(int exit, string stdout)> RunProcessAsync(string fileName, string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (p.ExitCode, stdout + stderr);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.AgentCoordinator;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var workDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        var agentCoordinator = new AgentCoordinatorHost(workDir);
        return await agentCoordinator.RunAsync();
    }
}

public class AgentCoordinatorHost
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _workDir;
    private readonly string _toolsDir;
    private readonly string _stateFile;
    private readonly string _reviewMarkerFile;
    private readonly AgentCoordinatorConfig _config;
    private readonly ConcurrentDictionary<string, AgentProcess> _agents = new();
    private CancellationTokenSource? _cts;
    private Task? _supervisorTask;
    private Task? _devopsSweepTask;
    private Task? _reviewCycleTask;
    private Task? _watchdogTask;
    private readonly string _logFile;
    private int _supervisorPid;

    public AgentCoordinatorHost(string workDir)
    {
        _workDir = workDir;
        _toolsDir = Path.Combine(workDir, "tools");
        _stateFile = Path.Combine(_toolsDir, ".agent-tasks.json");
        _reviewMarkerFile = Path.Combine(_toolsDir, ".reviewed-commits");
        _logFile = Path.Combine(_toolsDir, "agent-coordinator.log");

        _config = new AgentCoordinatorConfig
        {
            MaxConcurrentDev = GetEnvInt("MAX_CONCURRENT", 5),
            MaxConcurrentDevops = GetEnvInt("MAX_CONCURRENT_DEVOPS", 2),
            MaxConcurrentReviewers = GetEnvInt("MAX_CONCURRENT_REVIEWERS", 2),
            HeartbeatIntervalSeconds = GetEnvInt("AGENT_HEARTBEAT_INTERVAL", 45),
            StaleTimeoutSeconds = GetEnvInt("AGENT_STALE_TIMEOUT", 120),
            DevopsSweepIntervalSeconds = GetEnvInt("DEVOPS_SWEEP_INTERVAL", 120),
            ReconcileIntervalSeconds = GetEnvInt("RECONCILE_INTERVAL", 30),
            SupervisorCheckIntervalSeconds = 30
        };
    }

    private int GetEnvInt(string name, int defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return int.TryParse(val, out var result) ? result : defaultValue;
    }

    private void Log(string message)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var logLine = $"[{timestamp}] {message}";
        Console.WriteLine(logLine);
        try
        {
            File.AppendAllText(_logFile, logLine + Environment.NewLine);
        }
        catch { }
    }

    public async Task<int> RunAsync()
    {
        _cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Log("SIGINT received, initiating graceful shutdown...");
            _ = StopAsync();
        };

        try
        {
            Log($"AgentCoordinator starting. Config: dev={_config.MaxConcurrentDev}, devops={_config.MaxConcurrentDevops}, reviewers={_config.MaxConcurrentReviewers}");
            Log($"Working directory: {_workDir}");
            Log($"Tools directory: {_toolsDir}");

            EnsureStateFileExists();

            _watchdogTask = WatchdogLoopAsync(_cts.Token);
            _supervisorTask = SupervisorLoopAsync(_cts.Token);
            _devopsSweepTask = DevopsSweepLoopAsync(_cts.Token);
            _reviewCycleTask = ReviewCycleLoopAsync(_cts.Token);

            await Task.WhenAll(_supervisorTask, _devopsSweepTask, _reviewCycleTask, _watchdogTask);

            return 0;
        }
        catch (OperationCanceledException)
        {
            Log("Coordinator shutdown requested");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Fatal error: {ex}");
            return 1;
        }
        finally
        {
            await StopAsync();
        }
    }

    private void EnsureStateFileExists()
    {
        if (!File.Exists(_stateFile))
        {
            var initialState = new TaskBoard
            {
                Version = "1.0",
                LastPullAt = DateTimeOffset.UtcNow,
                Agents = new List<AgentInfo>(),
                Tasks = new List<TaskInfo>()
            };
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(initialState, StateJsonOptions));
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        Log("Watchdog loop started");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.SupervisorCheckIntervalSeconds * 1000, ct);

            if (_supervisorTask == null || _supervisorTask.IsCompleted)
            {
                Log("WARNING: Supervisor task is not running. Restarting supervisor...");
                _supervisorTask = SupervisorLoopAsync(ct);
            }
        }
    }

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        Log("Supervisor loop started");
        long lastReconcileEpoch = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var state = ReadState();
                if (state == null) { await Task.Delay(5000, ct); continue; }

                var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (nowEpoch - lastReconcileEpoch >= _config.ReconcileIntervalSeconds)
                {
                    ReconcileState(state);
                    lastReconcileEpoch = nowEpoch;
                }

                var busyDevCount = CountBusyAgents(state.Agents, "development");
                Log($"Status: {state.Tasks.Count(t => t.Status == "pending")} pending, {state.Tasks.Count(t => t.Status == "in_progress")} in progress. Busy dev: {busyDevCount}/{_config.MaxConcurrentDev}");

                if (busyDevCount < _config.MaxConcurrentDev)
                {
                    var agentId = PickIdleDevAgent(state.Agents);
                    if (!string.IsNullOrEmpty(agentId))
                    {
                        var task = PickTaskForAgent(state.Tasks, agentId);
                        if (!string.IsNullOrEmpty(task?.Id))
                        {
                            SpawnAgent(agentId, task.Id, task.Description, "development", ct);
                        }
                    }
                }

                await Task.Delay(5000, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Supervisor error: {ex.Message}");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task DevopsSweepLoopAsync(CancellationToken ct)
    {
        Log("DevOps sweep loop started");
        var lastRun = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30_000, ct);

                if (DateTimeOffset.UtcNow - lastRun < TimeSpan.FromSeconds(_config.DevopsSweepIntervalSeconds))
                    continue;

                var state = ReadState();
                if (state == null) continue;

                var busyDevopsCount = CountBusyAgents(state.Agents, "devops");
                if (busyDevopsCount >= _config.MaxConcurrentDevops) continue;

                foreach (var devopsAgent in state.Agents.Where(a => a.Role == "devops" && a.Status == "active"))
                {
                    if (CountBusyAgents(state.Agents, "devops") >= _config.MaxConcurrentDevops) break;

                    var localState = ReadAgentState(devopsAgent.Id);
                    if (localState == null) continue;

                    var lastRunAge = DateTimeOffset.UtcNow - (localState.LastRunAt ?? DateTimeOffset.MinValue);
                    if (lastRunAge.TotalSeconds < _config.DevopsSweepIntervalSeconds) continue;

                    var monitoringTask = state.Tasks.FirstOrDefault(t =>
                        (t.Status == "monitoring" || t.Status == "pending") &&
                        (t.AssignedTo == null || t.AssignedTo == devopsAgent.Id));

                    if (monitoringTask != null)
                    {
                        SpawnAgent(devopsAgent.Id, monitoringTask.Id, monitoringTask.Description, "devops", ct);
                        lastRun = DateTimeOffset.UtcNow;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"DevOps sweep error: {ex.Message}");
            }
        }
    }

    private async Task ReviewCycleLoopAsync(CancellationToken ct)
    {
        Log("Review cycle loop started");
        var lastCommit = GetLastReviewedCommit();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60_000, ct);

                var modifiedFiles = GetModifiedFiles();
                if (string.IsNullOrEmpty(modifiedFiles)) continue;

                var newCommits = GetNewCommits(lastCommit);
                if (string.IsNullOrEmpty(newCommits)) continue;

                var state = ReadState();
                if (state == null) continue;

                var busyReviewerCount = CountBusyAgents(state.Agents, "reviewer");
                if (busyReviewerCount >= _config.MaxConcurrentReviewers) continue;

                var reviewerAgent = state.Agents.FirstOrDefault(a => a.Role == "reviewer" && a.Status == "active");
                if (reviewerAgent == null)
                {
                    reviewerAgent = new AgentInfo { Id = "reviewer-1", Role = "reviewer", Status = "active" };
                    state.Agents.Add(reviewerAgent);
                }

                Log($"New commits detected. Triggering code review...");
                SpawnAgent(reviewerAgent.Id, "code-review", $"Review new commits: {newCommits.Split('\n').FirstOrDefault() ?? ""}", "reviewer", ct);

                lastCommit = GetCurrentCommit();
                File.WriteAllText(_reviewMarkerFile, lastCommit);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Review cycle error: {ex.Message}");
            }
        }
    }

    private int CountBusyAgents(List<AgentInfo> agents, string role)
    {
        return agents.Count(a =>
        {
            var localState = ReadAgentState(a.Id);
            if (localState == null) return false;
            return a.Role == role && localState.Status == "working" && !IsProcessDead(localState.Pid);
        });
    }

    private string? PickIdleDevAgent(List<AgentInfo> agents)
    {
        var localStates = new Dictionary<string, AgentLocalState?>();
        foreach (var agent in agents.Where(a => a.Role == "development" && a.Status == "active"))
        {
            localStates[agent.Id] = ReadAgentState(agent.Id);
        }

        foreach (var kvp in localStates)
        {
            var local = kvp.Value;
            if (local == null) return kvp.Key;
            if (local.Status == "idle") return kvp.Key;
            if (local.Status == "crashed" || local.Status == "stalled" || local.Status == "unresponsive")
                return kvp.Key;
        }
        return null;
    }

    private TaskInfo? PickTaskForAgent(List<TaskInfo> tasks, string agentId)
    {
        var pendingTasks = tasks
            .Where(t => t.Status == "pending" && (t.AssignedTo == null || t.AssignedTo == agentId))
            .OrderBy(t => t.Priority == "critical" ? 0 : t.Priority == "high" ? 1 : t.Priority == "medium" ? 2 : 3)
            .ThenBy(t => t.Id)
            .ToList();

        if (pendingTasks.Count == 0) return null;

        var selected = pendingTasks.First();
        selected.Status = "in_progress";
        selected.AssignedTo = agentId;
        selected.ClaimedAt = DateTimeOffset.UtcNow;
        return selected;
    }

    private void SpawnAgent(string agentId, string taskId, string taskDescription, string role, CancellationToken ct)
    {
        var pid = Process.GetCurrentProcess().Id;
        _supervisorPid = pid;

        var promptFile = Path.Combine(_toolsDir, $".agent-prompt-{agentId}.txt");
        var prompt = BuildPrompt(agentId, taskId, taskDescription, role);
        File.WriteAllText(promptFile, prompt);

        var stateFile = Path.Combine(_toolsDir, $".agent-state-{agentId}.json");
        var now = DateTimeOffset.UtcNow;
        var localState = new AgentLocalState
        {
            AgentId = agentId,
            CurrentTaskId = taskId,
            CurrentTaskDescription = taskDescription,
            Status = "working",
            Pid = pid,
            StartedAt = now,
            LastRunAt = now,
            LastHeartbeatAt = now,
            LastError = null
        };
        File.WriteAllText(stateFile, JsonSerializer.Serialize(localState, new JsonSerializerOptions { WriteIndented = true }));

        UpdateCentralAgentStatus(agentId, "working", taskId, taskDescription, pid);

        var opencodeBin = Environment.GetEnvironmentVariable("OPENCODE_BIN") ?? "opencode";
        var startInfo = new ProcessStartInfo
        {
            FileName = opencodeBin,
            Arguments = $"run --dir {_workDir}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (s, e) => { if (e.Data != null) Log($"[{agentId}] {e.Data}"); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log($"[{agentId} ERROR] {e.Data}"); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.StandardInput.Write(prompt);
        process.StandardInput.Close();

        var agentProcess = new AgentProcess
        {
            AgentId = agentId,
            TaskId = taskId,
            Process = process,
            StartedAt = now,
            LastHeartbeat = now
        };

        _agents[agentId] = agentProcess;
        Log($"Agent {agentId} spawned for task {taskId} (role: {role})");

        _ = HeartbeatMonitorAsync(agentId, ct);
        _ = WaitForAgentExitAsync(agentId, ct);
    }

    private async Task HeartbeatMonitorAsync(string agentId, CancellationToken ct)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(heartbeatInterval, ct);

            if (!_agents.TryGetValue(agentId, out var agentProcess) || agentProcess.Process.HasExited)
                return;

            try
            {
                agentProcess.LastHeartbeat = DateTimeOffset.UtcNow;

                var stateFile = Path.Combine(_toolsDir, $".agent-state-{agentId}.json");
                var state = JsonSerializer.Deserialize<AgentLocalState>(File.ReadAllText(stateFile)) ?? new AgentLocalState();
                state.LastHeartbeatAt = DateTimeOffset.UtcNow;
                state.UpdatedAt = DateTimeOffset.UtcNow;
                File.WriteAllText(stateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

                UpdateCentralAgentStatus(agentId, "working", state.CurrentTaskId, state.CurrentTaskDescription, agentProcess.Process.Id);
            }
            catch { }
        }
    }

    private async Task WaitForAgentExitAsync(string agentId, CancellationToken ct)
    {
        if (!_agents.TryGetValue(agentId, out var agentProcess)) return;

        try
        {
            await agentProcess.Process.WaitForExitAsync(ct);
        }
        catch { }

        var exitCode = agentProcess.Process.ExitCode;
        var taskId = agentProcess.TaskId;

        _agents.TryRemove(agentId, out _);

        var stateFile = Path.Combine(_toolsDir, $".agent-state-{agentId}.json");
        var now = DateTimeOffset.UtcNow;
        var finalState = new AgentLocalState
        {
            AgentId = agentId,
            CurrentTaskId = null,
            CurrentTaskDescription = null,
            Status = exitCode == 0 ? "idle" : "stalled",
            Pid = null,
            StartedAt = null,
            LastRunAt = now,
            LastHeartbeatAt = now,
            LastError = exitCode != 0 ? $"Agent exited with code {exitCode}" : null
        };
        File.WriteAllText(stateFile, JsonSerializer.Serialize(finalState, StateJsonOptions));

        var promptFile = Path.Combine(_toolsDir, $".agent-prompt-{agentId}.txt");
        if (File.Exists(promptFile)) File.Delete(promptFile);

        UpdateCentralAgentStatus(agentId, "idle", null, null, null);

        Log($"Agent {agentId} finished task {taskId} (exit: {exitCode})");

        if (exitCode != 0)
        {
            RequeueTask(taskId);
        }
    }

    private string BuildPrompt(string agentId, string taskId, string taskDescription, string role)
    {
        var stateFile = Path.Combine(_toolsDir, $".agent-state-{agentId}.json");

        if (role == "devops")
        {
            return $@"You are {agentId}, a DevOps operations agent for the Argus workspace.

Your state file: {stateFile}
Standing task ID: {taskId}
Standing task: {taskDescription}

Scope:
- Keep the application and deployed components healthy.
- Keep the AI agent runner, reviewer agents, coordination state, and dashboard healthy.
- Prefer observation, reconciliation, and precise fixes over broad product work.

Operating checklist:
1. FIRST: cat {stateFile} and inspect the current coordination state
2. Check application health: dotnet build {_workDir}/src/Argus.AppHost/Argus.AppHost.csproj --configuration Release --verbosity quiet
3. Check deployment wiring: test -f {_workDir}/deploy/compose.yaml && docker compose -f {_workDir}/deploy/compose.yaml config
4. If agents or tasks are stale, run: cd {_toolsDir} && ./agent-coord.sh reconcile
5. Do not mark the standing monitoring task done; leave it available for the next sweep.

Make narrowly scoped operational fixes only when necessary to keep the app, deployment, or agent system running.";
        }

        if (role == "reviewer")
        {
            return $@"You are {agentId}, a code reviewer agent for the Argus workspace.

Your state file: {stateFile}
Task: {taskDescription}

Check the modified files in the repository and perform a thorough code review.
Focus on:
- Code quality and maintainability
- Security vulnerabilities
- Performance issues
- Best practices

Run: cd {_toolsDir} && ./review-agents.sh
Or: cd {_toolsDir} && ./generate-review-report.sh

Mark the review complete after examining all changes.";
        }

        return $@"You are {agentId}, a development agent for the Argus workspace.

Your state file: {stateFile}
Task ID: {taskId}
Task: {taskDescription}

Working directory: {_workDir}
Tools directory: {_toolsDir}

First: cat {stateFile}
Second: cat {_toolsDir}/agent-coord.sh status

Implement the task. When done, run:
cd {_toolsDir} && ./agent-coord.sh done -t {taskId} -a {agentId}

If you encounter blockers, run:
cd {_toolsDir} && ./agent-coord.sh checkpoint -a {agentId} -t {taskId} -s working -c ""<status message>""
";
    }

    private bool IsProcessDead(int? pid)
    {
        if (!pid.HasValue || pid == 0) return true;
        try
        {
            var proc = Process.GetProcessById(pid.Value);
            return proc.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private void ReconcileState(TaskBoard state)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var agent in state.Agents)
        {
            var localState = ReadAgentState(agent.Id);
            if (localState == null) continue;

            if (localState.Status == "working" && localState.Pid.HasValue && IsProcessDead(localState.Pid))
            {
                localState.Status = "crashed";
                localState.LastError = "Agent process exited unexpectedly";
                localState.Pid = null;
                File.WriteAllText(Path.Combine(_toolsDir, $".agent-state-{agent.Id}.json"),
                    JsonSerializer.Serialize(localState, StateJsonOptions));

                if (!string.IsNullOrEmpty(localState.CurrentTaskId))
                {
                    var task = state.Tasks.FirstOrDefault(t => t.Id == localState.CurrentTaskId);
                    if (task != null)
                    {
                        task.Status = "pending";
                        task.AssignedTo = null;
                        task.RequeuedAt = now;
                        task.RequeueReason = $"released from {agent.Id} after crashed";
                        Log($"Reconciled: {agent.Id} crashed on task {task.Id}, requeued");
                    }
                }

                agent.WorkStatus = "idle";
                agent.CurrentTask = null;
            }
            else if (localState.Status != "idle")
            {
                agent.WorkStatus = localState.Status == "working" ? "working" : "idle";
                agent.CurrentTask = localState.CurrentTaskId;
            }
        }

        foreach (var task in state.Tasks.Where(t => t.Status == "in_progress" && t.AssignedTo != null))
        {
            var assignedAgent = state.Agents.FirstOrDefault(a => a.Id == task.AssignedTo);
            if (assignedAgent?.CurrentTask != task.Id)
            {
                task.Status = "pending";
                task.AssignedTo = null;
                task.RequeuedAt = now;
                task.RequeueReason = "assigned agent is not currently working this task";
                Log($"Reconciled: task {task.Id} mismatch, requeued");
            }
        }

        WriteState(state);
        Log("Reconciled agent task board");
    }

    private void RequeueTask(string taskId)
    {
        var state = ReadState();
        if (state == null) return;

        var task = state.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task != null)
        {
            task.Status = "pending";
            task.AssignedTo = null;
            task.RequeuedAt = DateTimeOffset.UtcNow;
            task.RequeueReason = "agent exited with non-zero code";
            task.Attempts = (task.Attempts ?? 0) + 1;
            Log($"Task {taskId} requeued (attempt {task.Attempts})");
            WriteState(state);
        }
    }

    private void UpdateCentralAgentStatus(string agentId, string workStatus, string? taskId, string? taskDescription, int? pid)
    {
        var state = ReadState();
        if (state == null) return;

        var agent = state.Agents.FirstOrDefault(a => a.Id == agentId);
        if (agent == null)
        {
            agent = new AgentInfo { Id = agentId, Role = "development", Status = "active" };
            state.Agents.Add(agent);
        }

        agent.WorkStatus = workStatus;
        agent.CurrentTask = taskId;
        agent.CurrentTaskDescription = taskDescription;
        agent.Status = "active";
        agent.LastUpdated = DateTimeOffset.UtcNow;

        WriteState(state);
    }

    private TaskBoard? ReadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return null;
            var json = File.ReadAllText(_stateFile);
            return JsonSerializer.Deserialize<TaskBoard>(json, StateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteState(TaskBoard state)
    {
        File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, StateJsonOptions));
    }

    private AgentLocalState? ReadAgentState(string agentId)
    {
        var stateFile = Path.Combine(_toolsDir, $".agent-state-{agentId}.json");
        try
        {
            if (!File.Exists(stateFile)) return null;
            return JsonSerializer.Deserialize<AgentLocalState>(File.ReadAllText(stateFile), StateJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private string? GetLastReviewedCommit()
    {
        try
        {
            return File.Exists(_reviewMarkerFile) ? File.ReadAllText(_reviewMarkerFile).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private string GetCurrentCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "-C " + _workDir + " log -1 --format=%H")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            return proc?.StandardOutput.ReadToEnd().Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private string GetNewCommits(string? lastCommit)
    {
        try
        {
            var sinceArg = string.IsNullOrEmpty(lastCommit) ? "-10" : $"{lastCommit}..HEAD";
            var psi = new ProcessStartInfo("git", $"-C {_workDir} log --oneline {sinceArg}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            return proc?.StandardOutput.ReadToEnd().Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private string GetModifiedFiles()
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C {_workDir} status --porcelain")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            return proc?.StandardOutput.ReadToEnd().Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private async Task StopAsync()
    {
        Log("Shutting down coordinator...");

        _cts?.Cancel();

        foreach (var kvp in _agents)
        {
            try
            {
                if (!kvp.Value.Process.HasExited)
                {
                    kvp.Value.Process.Kill(entireProcessTree: true);
                    var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await kvp.Value.Process.WaitForExitAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) { }
                }
            }
            catch { }
        }

        _agents.Clear();
        Log("Coordinator shutdown complete");
    }
}

public class AgentCoordinatorConfig
{
    public int MaxConcurrentDev { get; set; } = 5;
    public int MaxConcurrentDevops { get; set; } = 2;
    public int MaxConcurrentReviewers { get; set; } = 2;
    public int HeartbeatIntervalSeconds { get; set; } = 45;
    public int StaleTimeoutSeconds { get; set; } = 120;
    public int DevopsSweepIntervalSeconds { get; set; } = 120;
    public int ReconcileIntervalSeconds { get; set; } = 30;
    public int SupervisorCheckIntervalSeconds { get; set; } = 30;
}

public class AgentProcess
{
    public string AgentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public Process Process { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
}

public class TaskBoard
{
    public string Version { get; set; } = "1.0";
    public DateTimeOffset LastPullAt { get; set; }
    public List<AgentInfo> Agents { get; set; } = new();
    public List<TaskInfo> Tasks { get; set; } = new();
}

public class AgentInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "development";
    public string Status { get; set; } = "active";
    public string WorkStatus { get; set; } = "idle";
    public string? CurrentTask { get; set; }
    public string? CurrentTaskDescription { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public int? Pid { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public string? LastError { get; set; }
}

public class TaskInfo
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Priority { get; set; } = "medium";
    public string? AssignedTo { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? Attempts { get; set; }
    public DateTimeOffset? RequeuedAt { get; set; }
    public string? RequeueReason { get; set; }
}

public class AgentLocalState
{
    public string? AgentId { get; set; }
    public string? CurrentTaskId { get; set; }
    public string? CurrentTaskDescription { get; set; }
    public string Status { get; set; } = "idle";
    public int? Pid { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastError { get; set; }
}

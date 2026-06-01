namespace Argus.AgentService.Stores;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.AgentService.Agents;
using Argus.AgentService.Data;
using Argus.Contracts.Agents;

internal static class AgentDevelopmentSeedData
{
    public static Guid GuidFromId(string id)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(id));
        return new Guid(hash);
    }

    public static IReadOnlyList<AgentRecord> CreateAgents(DateTimeOffset now)
    {
        static string Json(params string[] responsibilities) => JsonSerializer.Serialize(responsibilities);

        AgentRecord Agent(
            string id,
            string name,
            string role,
            string? roleDescription,
            int sortOrder,
            string tool,
            string model,
            string? provider,
            string[] responsibilities,
            string priority,
            string status = "active",
            string[]? capabilityOverride = null)
        {
            var caps = capabilityOverride ?? AgentCapabilities.PresetForRole(role).ToArray();
            return new()
            {
                AgentId = GuidFromId(id),
                Name = name,
                Role = role,
                RoleDescription = roleDescription,
                SortOrder = sortOrder,
                Status = status,
                WorkStatus = "idle",
                Tool = tool,
                Model = model,
                Provider = provider,
                Priority = priority,
                ResponsibilitiesJson = Json(responsibilities),
                CapabilitiesJson = JsonSerializer.Serialize(caps),
                DefaultRuntime = AgentCapabilities.DefaultRuntimeForCapabilities(caps),
                LastHeartbeatAt = now.AddHours(-1),
                CreatedAt = now.AddHours(-1),
                UpdatedAt = now.AddMinutes(-30)
            };
        }

        return
        [
            // ── Junior Developer ────────────────────────────────────────────
            Agent("jrdev-opencode-m3-free", "MiniMax M3 Free (OpenCode Zen)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 1,
                "opencode", "opencode/minimax-m3-free", "OpenCode Zen",
                ["small bug fixes", "unit tests", "well-scoped implementation"], "low"),

            Agent("jrdev-nvidia-gpt-oss-120b", "GPT-OSS 120B (NVIDIA)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 2,
                "opencode", "nvidia/openai/gpt-oss-120b", "NVIDIA",
                ["small bug fixes", "unit tests"], "low"),

            Agent("jrdev-openrouter-gpt-oss-120b", "GPT-OSS 120B (OpenRouter)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 3,
                "opencode", "openrouter/openai/gpt-oss-120b:free", "OpenRouter",
                ["small bug fixes", "unit tests"], "low"),

            Agent("jrdev-claude-haiku", "Claude Haiku 4.5", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 4,
                "claude", "claude-haiku-4-5-20251001", "Claude",
                ["small bug fixes", "unit tests", "docs"], "low"),

            Agent("jrdev-gemini-flash-preview", "Gemini 3.1 Flash Preview", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 5,
                "gemini", "gemini-3.1-flash-preview", "Gemini",
                ["small bug fixes", "unit tests"], "low"),

            Agent("jrdev-gemini-25-flash", "Gemini 2.5 Flash", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 6,
                "gemini", "gemini-2.5-flash", "Gemini",
                ["small bug fixes", "test cleanup"], "low"),

            Agent("jrdev-codex-mini", "GPT-5.4 Mini", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 7,
                "codex", "gpt-5.4-mini", "OpenAI",
                ["small bug fixes", "regression tests"], "low"),

            Agent("jrdev-openrouter-m25", "MiniMax M2.5 (OpenRouter)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 8,
                "opencode", "openrouter/minimax/minimax-m2.5:free", "OpenRouter",
                ["small bug fixes", "docs"], "low"),

            // ── Developer ───────────────────────────────────────────────────
            Agent("dev-claude-sonnet", "Claude Sonnet 4.6", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 1,
                "claude", "claude-sonnet-4-6", "Claude",
                ["feature implementation", "bug fixes", "integration tests"], "standard"),

            Agent("dev-codex-53", "GPT-5.3 Codex", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 2,
                "codex", "gpt-5.3-codex", "OpenAI",
                ["backend services", "worker reliability", "tests"], "standard"),

            Agent("dev-opencode-m3-free", "MiniMax M3 Free (OpenCode Zen)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 3,
                "opencode", "opencode/minimax-m3-free", "OpenCode Zen",
                ["feature implementation", "bug investigation"], "standard"),

            Agent("dev-openrouter-gpt-oss-120b", "GPT-OSS 120B (OpenRouter)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 4,
                "opencode", "openrouter/openai/gpt-oss-120b:free", "OpenRouter",
                ["feature implementation", "bug fixes"], "standard"),

            // ── Senior Developer ────────────────────────────────────────────
            Agent("srdev-claude-opus", "Claude Opus 4.7", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 1,
                "claude", "claude-opus-4-7", "Claude",
                ["complex implementation", "code review", "quality gates"], "standard"),

            Agent("srdev-codex-55", "GPT-5.5", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 2,
                "codex", "gpt-5.5", "OpenAI",
                ["complex implementation", "architecture-sensitive fixes"], "standard"),

            Agent("srdev-gemini-pro", "Gemini 3.1 Pro", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 3,
                "gemini", "gemini-3.1-pro", "Gemini",
                ["complex implementation", "code review"], "standard"),

            // ── Junior System Architect ─────────────────────────────────────
            Agent("jrarch-nvidia-mistral", "Mistral Large 3 675B (NVIDIA)", "junior_system_architect",
                "System design support, cross-service architecture review, and technical analysis", 1,
                "opencode", "nvidia/mistralai/mistral-large-3-675b-instruct-2512", "NVIDIA",
                ["system design", "architecture review", "technical analysis"], "standard"),

            Agent("jrarch-nvidia-qwen", "Qwen3 Coder 480B (NVIDIA)", "junior_system_architect",
                "System design support, cross-service architecture review, and technical analysis", 2,
                "opencode", "nvidia/qwen/qwen3-coder-480b-a35b-instruct", "NVIDIA",
                ["system design", "architecture review"], "standard"),

            Agent("jrarch-opencode-m3-free", "MiniMax M3 Free (OpenCode Zen)", "junior_system_architect",
                "System design support, cross-service architecture review, and technical analysis", 3,
                "opencode", "opencode/minimax-m3-free", "OpenCode Zen",
                ["architecture review", "technical analysis"], "standard"),

            // ── Senior System Architect ─────────────────────────────────────
            Agent("srarch-claude-opus", "Claude Opus 4.7", "senior_system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 1,
                "claude", "claude-opus-4-7", "Claude",
                ["system design", "cross-service architecture", "security review", "system reports"], "high"),

            Agent("srarch-codex-55", "GPT-5.5", "senior_system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 2,
                "codex", "gpt-5.5", "OpenAI",
                ["system design", "technical strategy", "system reports"], "high"),

            // ── Junior DevOps ───────────────────────────────────────────────
            Agent("jrdevops-nvidia-gpt-oss", "GPT-OSS 120B (NVIDIA)", "junior_devops",
                "Infrastructure automation, deployment pipelines, and routine system health checks", 1,
                "opencode", "nvidia/openai/gpt-oss-120b", "NVIDIA",
                ["deployment automation", "health checks"], "low"),

            Agent("jrdevops-openrouter-gpt-oss", "GPT-OSS 120B (OpenRouter)", "junior_devops",
                "Infrastructure automation, deployment pipelines, and routine system health checks", 2,
                "opencode", "openrouter/openai/gpt-oss-120b:free", "OpenRouter",
                ["deployment automation", "infrastructure checks"], "low"),

            // ── Senior DevOps ───────────────────────────────────────────────
            Agent("srdevops-nvidia-mistral", "Mistral Large 3 675B (NVIDIA)", "senior_devops",
                "Infrastructure strategy, complex deployment automation, system health monitoring, and recovery", 1,
                "opencode", "nvidia/mistralai/mistral-large-3-675b-instruct-2512", "NVIDIA",
                ["deployment automation", "system health", "worker recovery", "infrastructure checks"], "standard"),

            Agent("srdevops-nvidia-qwen", "Qwen3 Coder 480B (NVIDIA)", "senior_devops",
                "Infrastructure strategy, complex deployment automation, system health monitoring, and recovery", 2,
                "opencode", "nvidia/qwen/qwen3-coder-480b-a35b-instruct", "NVIDIA",
                ["deployment automation", "infrastructure checks"], "standard"),

            // ── DevOps Architect ────────────────────────────────────────────
            Agent("devopsarch-codex-55", "GPT-5.5", "devops_architect",
                "Infrastructure architecture, platform strategy, and DevOps systems design", 1,
                "codex", "gpt-5.5", "OpenAI",
                ["platform architecture", "infrastructure strategy", "systems design"], "high"),

            Agent("devopsarch-claude-opus", "Claude Opus 4.7", "devops_architect",
                "Infrastructure architecture, platform strategy, and DevOps systems design", 2,
                "claude", "claude-opus-4-7", "Claude",
                ["platform architecture", "infrastructure strategy", "release engineering"], "high"),
        ];
    }

    public static IReadOnlyList<AgentTaskRecord> CreateTasks(DateTimeOffset now)
    {
        string? AgentId(string? id) => id is null ? null : GuidFromId(id).ToString();

        AgentTaskRecord Task(
            string id,
            string description,
            string priority,
            string status,
            string? assignedTo,
            int attempts,
            int ageMinutes,
            string? recoveryContext = null,
            string? targetRole = null,
            string? taskType = null,
            string? scheduleExpression = null,
            string? triggerEvent = null)
        {
            var normalizedStatus = status.ToLowerInvariant();
            var createdAt = now.AddMinutes(-ageMinutes);
            var claimedAt = normalizedStatus is "pending" or "scheduled" ? (DateTimeOffset?)null
                : createdAt.AddMinutes(Math.Min(20, Math.Max(1, ageMinutes / 8)));
            var completedAt = normalizedStatus is "completed" or "failed"
                ? (DateTimeOffset?)now.AddMinutes(-Math.Max(2, ageMinutes / 12))
                : (DateTimeOffset?)null;

            return new AgentTaskRecord
            {
                TaskId = id,
                Description = description,
                Priority = priority,
                Status = normalizedStatus,
                AssignedTo = AgentId(assignedTo),
                TargetRole = targetRole,
                TaskType = taskType,
                ScheduleExpression = scheduleExpression,
                TriggerEvent = triggerEvent,
                Attempts = attempts,
                CreatedAt = createdAt,
                ClaimedAt = claimedAt,
                CompletedAt = completedAt,
                RecoveryContext = recoveryContext,
                NextRunAt = scheduleExpression is not null ? now : null
            };
        }

        return
        [
            Task("sched-log-review", """
                Review all application logs for all components and determine whether there are failures, errors, or problems in the last hour. For each error found, investigate and determine the likely root cause and appropriate fix. Add a HIGH-priority TODO item to the developer todo list with detailed implementation instructions, and assign the item to junior_developer for easy fixes, developer for moderate fixes, or senior_developer for complex fixes.
                """,
                "high", "scheduled", null, 0, 0,
                targetRole: "developer", taskType: "log_review",
                scheduleExpression: "0 * * * *"),

            Task("sched-junior-pick-task", """
                Review agent TODO tasks and determine whether there are available tasks to work on. Take the highest-priority task you are capable of completing, even if it is below your skill level. Prefer an easy high-priority task over a complex lower-priority task. Update task status, work output, and any follow-up TODOs.
                """,
                "high", "scheduled", null, 0, 0,
                targetRole: "junior_developer", taskType: "todo_worker",
                scheduleExpression: "* * * * *"),

            Task("sched-developer-pick-task", """
                Review agent TODO tasks and determine whether there are available tasks to work on. Take the highest-priority task you are capable of completing, even if it is below your skill level. Prefer an easy high-priority task over a complex lower-priority task. Update task status, work output, and any follow-up TODOs.
                """,
                "high", "scheduled", null, 0, 0,
                targetRole: "developer", taskType: "todo_worker",
                scheduleExpression: "* * * * *"),

            Task("sched-senior-pick-task", """
                Review agent TODO tasks and determine whether there are available tasks to work on. Take the highest-priority task you are capable of completing, even if it is below your skill level. Prefer an easy high-priority task over a complex lower-priority task. Update task status, work output, and any follow-up TODOs.
                """,
                "high", "scheduled", null, 0, 0,
                targetRole: "senior_developer", taskType: "todo_worker",
                scheduleExpression: "* * * * *"),

            Task("trig-review-committed-code", """
                Review committed code created by another agent. Prioritize correctness, regressions, security, performance, maintainability, and missing tests. Produce a concise code review suitable for display in the web UI.
                """,
                "high", "scheduled", null, 0, 0,
                targetRole: "senior_developer", taskType: "code_review",
                triggerEvent: "agent_task_completed"),

            Task("sched-system-architect-review", """
                Review all recent changes, logs, system health, TODO items, agent output, code reviews, deployment state, and provider usage. Report the current state of the system to the user, create TODO items for any problems, delegate required work, and flag important items for display in the web app.
                """,
                "high", "scheduled", null, 0, 0,
                targetRole: "senior_system_architect", taskType: "system_report",
                scheduleExpression: "0 */4 * * *")
        ];
    }

    public static IReadOnlyList<AgentTaskScheduleRecord> CreateSchedules(IEnumerable<AgentTaskRecord> tasks, DateTimeOffset now)
    {
        var schedules = new List<AgentTaskScheduleRecord>();
        foreach (var t in tasks)
        {
            if (string.IsNullOrWhiteSpace(t.ScheduleExpression)) continue;
            schedules.Add(new AgentTaskScheduleRecord
            {
                ScheduleId = Guid.NewGuid(),
                TaskId = t.TaskId,
                Cron = t.ScheduleExpression!,
                Enabled = true,
                NextRunAt = now,
                CreatedAt = now
            });
        }
        return schedules;
    }

    public static IReadOnlyList<AgentTaskTriggerRecord> CreateTriggers(IEnumerable<AgentTaskRecord> tasks, DateTimeOffset now)
    {
        var triggers = new List<AgentTaskTriggerRecord>();
        foreach (var t in tasks)
        {
            if (string.IsNullOrWhiteSpace(t.TriggerEvent)) continue;
            triggers.Add(new AgentTaskTriggerRecord
            {
                TriggerId = Guid.NewGuid(),
                TaskId = t.TaskId,
                EventName = t.TriggerEvent!,
                Enabled = true,
                CreatedAt = now
            });
        }
        return triggers;
    }

    public static int NextNumericTaskId(IEnumerable<string> taskIds)
    {
        var maxNumeric = taskIds
            .Select(id => int.TryParse(id, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();

        return maxNumeric + 1;
    }
}

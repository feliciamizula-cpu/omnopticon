namespace Argus.AgentService.Stores;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.AgentService.Agents;
using Argus.AgentService.Data;

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
            string[] responsibilities,
            string status = "active",
            string workStatus = "idle",
            int ageHours = 24) =>
            new()
            {
                AgentId = GuidFromId(id),
                Name = name,
                Role = role,
                RoleDescription = roleDescription,
                SortOrder = sortOrder,
                Status = status,
                WorkStatus = workStatus,
                Tool = tool,
                Model = model,
                ResponsibilitiesJson = Json(responsibilities),
                LastHeartbeatAt = now.AddHours(-ageHours),
                CreatedAt = now.AddHours(-ageHours),
                UpdatedAt = now.AddMinutes(-ageHours)
            };

        return
        [
            Agent("pm-opencode-m27", "Product Manager (Opencode M2.7)", "product_manager",
                "Strategy, roadmap, prioritization, requirements clarity, and acceptance criteria", 1,
                "opencode", "mightymax-m2.7",
                ["product strategy", "requirements", "prioritization", "acceptance criteria"]),
            Agent("pm-gemini-pro", "Product Manager (Gemini 3.1 Pro)", "product_manager",
                "Strategy, roadmap, prioritization, requirements clarity, and acceptance criteria", 2,
                "gemini", "gemini-3.1-pro",
                ["product strategy", "requirements", "stakeholder communication"]),
            Agent("pm-claude-haiku", "Product Manager (Claude Haiku)", "product_manager",
                "Strategy, roadmap, prioritization, requirements clarity, and acceptance criteria", 3,
                "claude", "claude-haiku-4-5-20251001",
                ["requirements", "acceptance criteria", "triage"]),

            Agent("cto-claude-opus", "Chief Technology Officer (Claude Opus)", "chief_technology_officer",
                "Technical direction, engineering strategy, risk management, and architectural governance", 1,
                "claude", "claude-opus-4-7",
                ["technical strategy", "architecture governance", "risk management"]),
            Agent("cto-openai-gpt55", "Chief Technology Officer (ChatGPT 5.5)", "chief_technology_officer",
                "Technical direction, engineering strategy, risk management, and architectural governance", 2,
                "openai", "chatgpt-5.5",
                ["technical strategy", "engineering leadership", "risk management"]),

            Agent("juniordev-opencode-m25", "Junior Developer (Opencode M2.5)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 1,
                "opencode", "mightymax-m2.5",
                ["small bug fixes", "unit tests", "well-scoped implementation"]),
            Agent("juniordev-gemini-flash-lite", "Junior Developer (Gemini Flash Lite)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 2,
                "gemini", "gemini-flash-lite",
                ["small bug fixes", "unit tests"]),
            Agent("juniordev-openai-mini", "Junior Developer (ChatGPT Mini)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 3,
                "openai", "chatgpt-mini",
                ["small bug fixes", "regression tests"]),
            Agent("juniordev-openai-nano", "Junior Developer (ChatGPT Nano)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 4,
                "openai", "chatgpt-nano",
                ["small bug fixes", "docs", "test cleanup"]),
            Agent("juniordev-claude-haiku", "Junior Developer (Claude Haiku)", "junior_developer",
                "Small, well-scoped implementation tasks, tests, and easy bug fixes", 5,
                "claude", "claude-haiku-4-5-20251001",
                ["small bug fixes", "unit tests"]),

            Agent("dev-opencode-m27", "Developer (Opencode M2.7)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 1,
                "opencode", "mightymax-m2.7",
                ["feature implementation", "bug fixes", "integration tests"]),
            Agent("dev-claude-haiku", "Developer (Claude Haiku)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 2,
                "claude", "claude-haiku-4-5-20251001",
                ["feature implementation", "bug fixes"]),
            Agent("dev-claude-sonnet", "Developer (Claude Sonnet)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 3,
                "claude", "claude-sonnet-4-6",
                ["feature implementation", "deep fixes", "integration tests"]),
            Agent("dev-openai-codex53", "Developer (Codex 5.3)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 4,
                "openai", "codex-5.3",
                ["backend services", "worker reliability", "tests"]),
            Agent("dev-gemini-pro", "Developer (Gemini 3.1 Pro)", "developer",
                "Moderate feature work, bug fixes, integration tests, and code review support", 5,
                "gemini", "gemini-3.1-pro",
                ["feature implementation", "bug investigation"]),

            Agent("senior-claude-sonnet", "Senior Developer (Claude Sonnet)", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 1,
                "claude", "claude-sonnet-4-6",
                ["complex implementation", "code review", "quality gates"]),
            Agent("senior-openai-gpt55", "Senior Developer (ChatGPT 5.5)", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 2,
                "openai", "chatgpt-5.5",
                ["complex implementation", "architecture-sensitive fixes", "quality gates"]),
            Agent("senior-openai-codex53", "Senior Developer (Codex 5.3)", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 3,
                "openai", "codex-5.3",
                ["code review", "refactoring", "durability work"]),
            Agent("senior-gemini-pro", "Senior Developer (Gemini 3.1 Pro)", "senior_developer",
                "Complex implementation, architecture-sensitive fixes, quality gates, and reviews", 4,
                "gemini", "gemini-3.1-pro",
                ["complex implementation", "code review"]),

            Agent("arch-claude-opus", "System Architect (Claude Opus)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 1,
                "claude", "claude-opus-4-7",
                ["system design", "cross-service architecture", "security review", "system reports"]),
            Agent("arch-openai-gpt55", "System Architect (ChatGPT 5.5)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 2,
                "openai", "chatgpt-5.5",
                ["system design", "technical strategy", "system reports"]),
            Agent("arch-opencode-m27", "System Architect (Opencode M2.7)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 3,
                "opencode", "mightymax-m2.7",
                ["system design", "architecture review"]),
            Agent("arch-gemini-pro", "System Architect (Gemini 3.1 Pro)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 4,
                "gemini", "gemini-3.1-pro",
                ["system design", "technical strategy"]),
            Agent("arch-claude-sonnet", "System Architect (Claude Sonnet)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review, and system reporting", 5,
                "claude", "claude-sonnet-4-6",
                ["architecture review", "technical strategy"]),

            Agent("designer-junior-claude-haiku", "Junior Designer (Claude Haiku)", "junior_designer",
                "UI/UX support, visual polish, interaction details, and simple design fixes", 1,
                "claude", "claude-haiku-4-5-20251001",
                ["ui polish", "interaction details", "simple design fixes"]),
            Agent("designer-senior-claude-opus", "Senior Designer (Claude Opus)", "senior_designer",
                "Product design direction, complex UX review, design systems, and user workflow quality", 1,
                "claude", "claude-opus-4-7",
                ["ux strategy", "design systems", "workflow review"]),

            Agent("devops-opencode-m25", "DevOps (Opencode M2.5)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring, and recovery", 1,
                "opencode", "mightymax-m2.5",
                ["deployment automation", "system health", "worker recovery", "infrastructure checks"]),
            Agent("devops-gemini-flash-lite", "DevOps (Gemini Flash Lite)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring, and recovery", 2,
                "gemini", "gemini-flash-lite",
                ["deployment automation", "infrastructure checks"]),
            Agent("devops-openai-mini", "DevOps (ChatGPT Mini)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring, and recovery", 3,
                "openai", "chatgpt-mini",
                ["deployment automation", "database maintenance"]),
            Agent("devops-openai-nano", "DevOps (ChatGPT Nano)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring, and recovery", 4,
                "openai", "chatgpt-nano",
                ["deployment checks", "health checks"]),
            Agent("devops-claude-haiku", "DevOps (Claude Haiku)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring, and recovery", 5,
                "claude", "claude-haiku-4-5-20251001",
                ["system health", "worker recovery", "release support"])
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
                targetRole: "system_architect", taskType: "system_report",
                scheduleExpression: "0 */4 * * *")
        ];
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

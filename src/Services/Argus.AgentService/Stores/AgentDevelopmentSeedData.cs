namespace Argus.AgentService.Stores;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                LastHeartbeatAt = now.AddMinutes(-ageHours),
                CreatedAt = now.AddHours(-ageHours),
                UpdatedAt = now.AddMinutes(-ageHours)
            };

        return
        [
            // DevOps — priority ordered; Opencode first (cheapest), then Gemini tiers, then Claude, then OpenAI
            Agent("devops-opencode-m25", "DevOps (Opencode M2.5)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring", 1,
                "opencode", "mightymax-m2.5",
                ["deployment automation", "system health", "worker recovery", "infrastructure checks"]),

            Agent("devops-gemini-flash", "DevOps (Gemini Flash)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring", 2,
                "gemini", "gemini-2.5-flash",
                ["deployment automation", "system health", "worker recovery"]),

            Agent("devops-gemini-flash-lite", "DevOps (Gemini Flash Lite)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring", 3,
                "gemini", "gemini-2.5-flash-lite",
                ["deployment automation", "infrastructure checks"]),

            Agent("devops-claude-haiku", "DevOps (Claude Haiku)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring", 4,
                "claude", "claude-haiku-4-5-20251001",
                ["system health", "worker recovery", "release support"]),

            Agent("devops-openai-mini", "DevOps (ChatGPT Mini)", "devops",
                "Infrastructure automation, deployment pipelines, system health monitoring", 5,
                "openai", "chatgpt-mini",
                ["deployment automation", "database maintenance"]),

            // Junior Developer
            Agent("juniordev-gemini-flash", "Junior Dev (Gemini Flash)", "junior_developer",
                "Implementation of well-scoped tasks, tests, small bug fixes", 1,
                "gemini", "gemini-2.5-flash",
                ["feature implementation", "unit tests", "small bug fixes"]),

            Agent("juniordev-opencode-m25", "Junior Dev (Opencode M2.5)", "junior_developer",
                "Implementation of well-scoped tasks, tests, small bug fixes", 2,
                "opencode", "mightymax-m2.5",
                ["feature implementation", "unit tests"]),

            Agent("juniordev-openai-mini", "Junior Dev (ChatGPT Mini)", "junior_developer",
                "Implementation of well-scoped tasks, tests, small bug fixes", 3,
                "openai", "chatgpt-mini",
                ["feature implementation", "regression tests"]),

            // Developer
            Agent("dev-claude-haiku", "Developer (Claude Haiku)", "developer",
                "Full-stack feature development, bug fixes, code review, integration tests", 1,
                "claude", "claude-haiku-4-5-20251001",
                ["application implementation", "bug fixes", "integration tests"]),

            Agent("dev-claude-sonnet", "Developer (Claude Sonnet)", "developer",
                "Full-stack feature development, bug fixes, code review, integration tests", 2,
                "claude", "claude-sonnet-4-6",
                ["application implementation", "code review", "deep fixes"]),

            Agent("dev-opencode-m27", "Developer (Opencode M2.7)", "developer",
                "Full-stack feature development, bug fixes, code review, integration tests", 3,
                "opencode", "mightymax-m2.7",
                ["backend services", "minimal diffs", "regression tests"]),

            Agent("dev-openai-codex53", "Developer (Codex 5.3)", "developer",
                "Full-stack feature development, bug fixes, code review, integration tests", 4,
                "openai", "codex-5.3",
                ["backend services", "worker reliability"]),

            Agent("dev-gemini-pro", "Developer (Gemini 3.1 Pro)", "developer",
                "Full-stack feature development, bug fixes, code review, integration tests", 5,
                "gemini", "gemini-3.1-pro",
                ["application implementation", "bug investigation"]),

            // Lead Developer
            Agent("lead-claude-sonnet", "Lead Dev (Claude Sonnet)", "lead_developer",
                "Complex feature design, architecture decisions, code quality, mentoring", 1,
                "claude", "claude-sonnet-4-6",
                ["complex feature design", "architecture decisions", "code review"]),

            Agent("lead-openai-gpt55", "Lead Dev (ChatGPT 5.5)", "lead_developer",
                "Complex feature design, architecture decisions, code quality, mentoring", 2,
                "openai", "chatgpt-5.5",
                ["complex feature design", "quality gates", "deep fixes"]),

            Agent("lead-openai-codex53", "Lead Dev (Codex 5.3)", "lead_developer",
                "Complex feature design, architecture decisions, code quality, mentoring", 3,
                "openai", "codex-5.3",
                ["code review", "refactoring", "durability work"]),

            Agent("lead-gemini-pro", "Lead Dev (Gemini 3.1 Pro)", "lead_developer",
                "Complex feature design, architecture decisions, code quality, mentoring", 4,
                "gemini", "gemini-3.1-pro",
                ["complex feature design", "code review"]),

            // System Architect
            Agent("arch-claude-opus", "System Architect (Claude Opus)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review", 1,
                "claude", "claude-opus-4-7",
                ["system design", "cross-service architecture", "security review", "technical strategy"]),

            Agent("arch-openai-gpt55", "System Architect (ChatGPT 5.5)", "system_architect",
                "System design, cross-service architecture, technical strategy, security review", 2,
                "openai", "chatgpt-5.5",
                ["system design", "technical strategy", "architecture review"])
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
                NextRunAt = scheduleExpression is not null ? now.AddHours(24) : null
            };
        }

        return
        [
            // Scheduled recurring tasks
            Task("sched-daily-review", "Daily code review: summarize all completed tasks and flag regressions",
                "normal", "scheduled", null, 0, 0,
                targetRole: "lead_developer", taskType: "code_review",
                scheduleExpression: "0 9 * * *"),

            Task("sched-weekly-report", "Weekly system health report: summarize service metrics and deployment status",
                "normal", "scheduled", null, 0, 0,
                targetRole: "devops", taskType: "system_report",
                scheduleExpression: "0 8 * * 1"),

            Task("sched-dep-check", "Check for dependency updates and security advisories",
                "low", "scheduled", null, 0, 0,
                targetRole: "devops", taskType: "maintenance",
                scheduleExpression: "0 10 * * 1"),

            Task("sched-arch-review", "Architecture review: assess new service boundaries and propose improvements",
                "low", "scheduled", null, 0, 0,
                targetRole: "system_architect", taskType: "system_report",
                scheduleExpression: "0 9 1 * *"),

            Task("sched-perf-bench", "Run performance benchmarks and document regressions against baseline",
                "normal", "scheduled", null, 0, 0,
                targetRole: "developer", taskType: "code_review",
                scheduleExpression: "0 22 * * 5"),

            Task("trig-post-deploy", "Post-deployment validation: verify all services healthy after deploy",
                "high", "scheduled", null, 0, 0,
                targetRole: "devops", taskType: "maintenance",
                triggerEvent: "deployment_completed"),

            // Active development tasks
            Task("052", "Enhancement #18: Program/scope export and import", "high", "in_progress", "dev-claude-haiku", 2, 420, targetRole: "developer"),
            Task("053", "Bug #43: Asset deduplication race in NormalizationWorker", "high", "claimed", "dev-claude-sonnet", 1, 390, targetRole: "developer"),
            Task("054", "Bug #41: HeadlessSpider OOM under heavy SPA load", "critical", "in_progress", "dev-opencode-m27", 1, 360, targetRole: "developer"),
            Task("055", "Enhancement #19: Per-target rate-limit policies", "medium", "pending", null, 0, 330, targetRole: "developer"),
            Task("056", "Enhancement #20: Subscription filter DSL", "medium", "pending", null, 0, 300, targetRole: "developer"),
            Task("057", "Refactor: extract WorkerLeaseService into shared lib", "low", "pending", null, 0, 270, targetRole: "lead_developer"),
            Task("058", "Bug #44: SSE event ordering jitter under burst", "high", "pending", null, 0, 240, targetRole: "developer"),
            Task("059", "Docs: producer/consumer matrix for worker types", "low", "pending", null, 0, 210, targetRole: "junior_developer"),
            Task("060", "Migrate JsExtractor to playwright-stealth", "medium", "pending", null, 0, 190, targetRole: "developer"),
            Task("061", "Add metrics: queue depth per worker capability", "medium", "pending", null, 0, 170, targetRole: "junior_developer"),
            Task("062", "Bug #45: Checkpoint resume drops cursor on retry > 3", "critical", "pending", null, 0, 150, targetRole: "developer"),
            Task("063", "Add agent feedback loop in code review comments", "low", "pending", null, 0, 130, targetRole: "lead_developer"),
            Task("064", "Snyk scan: 3 high CVEs in Argus.Web deps", "high", "blocked", "devops-opencode-m25", 1, 120,
                "Blocked pending breaking-change review for dependency upgrades.", targetRole: "devops"),
            Task("065", "Enhancement #21: Asset scoring weights configurable", "medium", "pending", null, 0, 110, targetRole: "junior_developer"),
            Task("066", "Bug #46: Subfinder rate-limited by Shodan above 30 rpm", "medium", "pending", null, 0, 100, targetRole: "developer"),

            // Completed / historical
            Task("051", "Enhancement #17: Bulk-tag editor in UI", "medium", "completed", "dev-claude-haiku", 2, 720, targetRole: "developer"),
            Task("050", "Bug #40: HttpProbe follows redirect into OOS host", "high", "completed", "dev-claude-sonnet", 2, 760, targetRole: "developer"),
            Task("049", "Refactor: switch outbox dispatcher to channels", "low", "completed", "dev-opencode-m27", 1, 800, targetRole: "developer"),
            Task("048", "Design #20: RealtimeService durability", "medium", "failed", "dev-gemini-pro", 4, 840,
                "Resume from service persistence design. Last failure occurred after in-memory ledger replay tests.", targetRole: "developer"),
            Task("047", "Bug #39: Asset confidence stuck at 50%", "high", "completed", "dev-openai-codex53", 1, 900, targetRole: "developer"),
            Task("046", "Add Prometheus exporters for worker metrics", "medium", "completed", "devops-opencode-m25", 1, 960, targetRole: "devops"),
            Task("045", "Bug #38: WordlistDiscovery respects 429 backoff", "high", "completed", "dev-claude-haiku", 1, 1020, targetRole: "developer"),
            Task("rev-08", "Review diff: HeadlessSpider playwright pool", "low", "in_progress", "lead-claude-sonnet", 0, 80,
                taskType: "code_review", targetRole: "lead_developer"),
            Task("rev-07", "Review diff: AssetScoring weight changes", "low", "completed", "lead-claude-sonnet", 1, 560,
                taskType: "code_review", targetRole: "lead_developer"),
            Task("ops-12", "Restart degraded HttpProbe instance w-httpprobe-002", "high", "in_progress", "devops-opencode-m25", 0, 60, targetRole: "devops"),
            Task("ops-11", "Rotate API gateway TLS cert", "medium", "pending", null, 0, 50, targetRole: "devops")
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

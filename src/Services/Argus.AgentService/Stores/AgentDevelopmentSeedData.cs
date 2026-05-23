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

        return
        [
            new AgentRecord
            {
                AgentId = GuidFromId("agent-1"),
                Name = "Agent 1",
                Role = "development",
                Status = "active",
                ResponsibilitiesJson = Json("application implementation", "tests", "code review"),
                CurrentTaskId = "052",
                WorkStatus = "working",
                LastHeartbeatAt = now.AddSeconds(-3),
                Tool = "claude",
                Model = "claude-sonnet-4-5",
                CreatedAt = now.AddHours(-6),
                UpdatedAt = now.AddSeconds(-3)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("agent-2"),
                Name = "Agent 2",
                Role = "development",
                Status = "active",
                ResponsibilitiesJson = Json("backend services", "tests", "minimal diffs"),
                WorkStatus = "idle",
                LastHeartbeatAt = now.AddSeconds(-9),
                Tool = "codex",
                Model = "gpt-5-codex",
                CreatedAt = now.AddHours(-4),
                UpdatedAt = now.AddSeconds(-9)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("agent-3"),
                Name = "Agent 3",
                Role = "development",
                Status = "active",
                ResponsibilitiesJson = Json("bug investigation", "worker reliability", "deep fixes"),
                CurrentTaskId = "054",
                WorkStatus = "working",
                LastHeartbeatAt = now.AddSeconds(-1),
                Tool = "claude",
                Model = "claude-opus-4-5",
                CreatedAt = now.AddHours(-3),
                UpdatedAt = now.AddSeconds(-1)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("agent-4"),
                Name = "Agent 4",
                Role = "development",
                Status = "active",
                ResponsibilitiesJson = Json("general implementation", "small commits", "regression tests"),
                WorkStatus = "idle",
                LastHeartbeatAt = now.AddSeconds(-6),
                Tool = "opencode",
                Model = "qwen3-coder-480b",
                CreatedAt = now.AddHours(-6),
                UpdatedAt = now.AddSeconds(-6)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("agent-5"),
                Name = "Agent 5",
                Role = "development",
                Status = "active",
                ResponsibilitiesJson = Json("durability work", "service implementation"),
                CurrentTaskId = "048",
                WorkStatus = "stalled",
                LastHeartbeatAt = now.AddMinutes(-4),
                LastError = "agent exited before task completion",
                Tool = "claude",
                Model = "claude-sonnet-4-5",
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddMinutes(-4)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("devops-1"),
                Name = "DevOps 1",
                Role = "devops",
                Status = "active",
                ResponsibilitiesJson = Json("system health", "deployment", "worker recovery"),
                CurrentTaskId = "ops-12",
                WorkStatus = "working",
                LastHeartbeatAt = now.AddSeconds(-2),
                Tool = "codex",
                Model = "gpt-5",
                CreatedAt = now.AddHours(-8),
                UpdatedAt = now.AddSeconds(-2)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("devops-2"),
                Name = "DevOps 2",
                Role = "devops",
                Status = "active",
                ResponsibilitiesJson = Json("infrastructure checks", "database maintenance", "release support"),
                WorkStatus = "idle",
                LastHeartbeatAt = now.AddMinutes(-1),
                Tool = "opencode",
                Model = "claude-sonnet-4-6",
                CreatedAt = now.AddHours(-5),
                UpdatedAt = now.AddMinutes(-1)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("reviewer-1"),
                Name = "Reviewer 1",
                Role = "reviewer",
                Status = "active",
                ResponsibilitiesJson = Json("code review", "security review", "quality gates"),
                CurrentTaskId = "rev-08",
                WorkStatus = "working",
                LastHeartbeatAt = now.AddSeconds(-4),
                Tool = "claude",
                Model = "claude-sonnet-4-5",
                CreatedAt = now.AddHours(-3),
                UpdatedAt = now.AddSeconds(-4)
            },
            new AgentRecord
            {
                AgentId = GuidFromId("reviewer-2"),
                Name = "Reviewer 2",
                Role = "reviewer",
                Status = "paused",
                ResponsibilitiesJson = Json("secondary review", "endpoint security"),
                WorkStatus = "idle",
                LastHeartbeatAt = now.AddHours(-6),
                Tool = "opencode",
                Model = "deepseek-v3.1",
                CreatedAt = now.AddHours(-7),
                UpdatedAt = now.AddHours(-6)
            }
        ];
    }

    public static IReadOnlyList<AgentTaskRecord> CreateTasks(DateTimeOffset now)
    {
        string? Agent(string? id) => id is null ? null : GuidFromId(id).ToString();

        AgentTaskRecord Task(
            string id,
            string description,
            string priority,
            string status,
            string? assignedTo,
            int attempts,
            int ageMinutes,
            string? recoveryContext = null)
        {
            var normalizedStatus = status.ToLowerInvariant();
            var createdAt = now.AddMinutes(-ageMinutes);
            var claimedAt = normalizedStatus is "pending" ? null : createdAt.AddMinutes(Math.Min(20, Math.Max(1, ageMinutes / 8)));
            var completedAt = normalizedStatus is "completed" or "failed" ? now.AddMinutes(-Math.Max(2, ageMinutes / 12)) : null;

            return new AgentTaskRecord
            {
                TaskId = id,
                Description = description,
                Priority = priority,
                Status = normalizedStatus,
                AssignedTo = Agent(assignedTo),
                Attempts = attempts,
                CreatedAt = createdAt,
                ClaimedAt = claimedAt,
                CompletedAt = completedAt,
                RecoveryContext = recoveryContext
            };
        }

        return
        [
            Task("052", "Enhancement #18: Program/scope export and import", "high", "in_progress", "agent-1", 2, 420),
            Task("053", "Bug #43: Asset deduplication race in NormalizationWorker", "high", "claimed", "agent-3", 1, 390),
            Task("054", "Bug #41: HeadlessSpider OOM under heavy SPA load", "critical", "in_progress", "agent-3", 1, 360),
            Task("055", "Enhancement #19: Per-target rate-limit policies", "medium", "pending", null, 0, 330),
            Task("056", "Enhancement #20: Subscription filter DSL", "medium", "pending", null, 0, 300),
            Task("057", "Refactor: extract WorkerLeaseService into shared lib", "low", "pending", null, 0, 270),
            Task("058", "Bug #44: SSE event ordering jitter under burst", "high", "pending", null, 0, 240),
            Task("059", "Docs: producer/consumer matrix for worker types", "low", "pending", null, 0, 210),
            Task("060", "Migrate JsExtractor to playwright-stealth", "medium", "pending", null, 0, 190),
            Task("061", "Add metrics: queue depth per worker capability", "medium", "pending", null, 0, 170),
            Task("062", "Bug #45: Checkpoint resume drops cursor on retry > 3", "critical", "pending", null, 0, 150),
            Task("063", "Add Reviewer agent feedback loop in PR comments", "low", "pending", null, 0, 130),
            Task("064", "Snyk scan: 3 high CVEs in Argus.Web deps", "high", "blocked", "devops-1", 1, 120, "Blocked pending breaking-change review for dependency upgrades."),
            Task("065", "Enhancement #21: Asset scoring weights configurable", "medium", "pending", null, 0, 110),
            Task("066", "Bug #46: Subfinder rate-limited by Shodan above 30 rpm", "medium", "pending", null, 0, 100),
            Task("051", "Enhancement #17: Bulk-tag editor in UI", "medium", "completed", "agent-1", 2, 720),
            Task("050", "Bug #40: HttpProbe follows redirect into OOS host", "high", "completed", "agent-3", 2, 760),
            Task("049", "Refactor: switch outbox dispatcher to channels", "low", "completed", "agent-2", 1, 800),
            Task("048", "Design #20: RealtimeService durability", "medium", "failed", "agent-5", 4, 840, "Resume from service persistence design. Last failure occurred after in-memory ledger replay tests."),
            Task("047", "Bug #39: Asset confidence stuck at 50%", "high", "completed", "agent-4", 1, 900),
            Task("046", "Add Prometheus exporters for worker metrics", "medium", "completed", "devops-1", 1, 960),
            Task("045", "Bug #38: WordlistDiscovery respects 429 backoff", "high", "completed", "agent-1", 1, 1020),
            Task("rev-08", "Review diff: HeadlessSpider playwright pool", "low", "in_progress", "reviewer-1", 0, 80),
            Task("rev-07", "Review diff: AssetScoring weight changes", "low", "completed", "reviewer-1", 1, 560),
            Task("ops-12", "Restart degraded HttpProbe instance w-httpprobe-002", "high", "in_progress", "devops-1", 0, 60),
            Task("ops-11", "Rotate API gateway TLS cert", "medium", "pending", null, 0, 50)
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

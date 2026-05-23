namespace Argus.AgentService.ProviderUsage;

using System.Text.Json;
using Argus.AgentService.Data;

internal static class ProviderUsageSeedData
{
    private static string Tools(params string[] tools) => JsonSerializer.Serialize(tools);
    private static string Models(params string[] models) => JsonSerializer.Serialize(models);

    public static IReadOnlyList<ProviderAccountRecord> CreateAccounts(DateTimeOffset now)
    {
        return
        [
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000001-0000-0000-0000-000000000001"),
                ProviderKey = "openai",
                DisplayName = "OpenAI API / Codex API mode",
                AccountType = "api",
                AuthMode = "env",
                SecretName = "OPENAI_ADMIN_API_KEY",
                RelatedToolsJson = Tools("codex"),
                RelatedModelsJson = Models("gpt-4o-mini", "gpt-4.1-nano", "gpt-5-codex", "gpt-chatgpt-mini", "gpt-chatgpt-5.5"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000002-0000-0000-0000-000000000002"),
                ProviderKey = "anthropic",
                DisplayName = "Anthropic API / Claude Code org mode",
                AccountType = "api",
                AuthMode = "env",
                SecretName = "ANTHROPIC_ADMIN_API_KEY",
                RelatedToolsJson = Tools("claude"),
                RelatedModelsJson = Models("claude-haiku-4-5", "claude-sonnet-4-6", "claude-opus-4-7"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000003-0000-0000-0000-000000000003"),
                ProviderKey = "openrouter",
                DisplayName = "OpenRouter",
                AccountType = "api",
                AuthMode = "env",
                SecretName = "OPENROUTER_API_KEY",
                RelatedToolsJson = Tools("opencode"),
                RelatedModelsJson = Models("mightymax-m2.5", "mightymax-m2.7"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000004-0000-0000-0000-000000000004"),
                ProviderKey = "deepseek",
                DisplayName = "DeepSeek",
                AccountType = "api",
                AuthMode = "env",
                SecretName = "DEEPSEEK_API_KEY",
                RelatedToolsJson = Tools("opencode"),
                RelatedModelsJson = Models("deepseek-v3.1"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000005-0000-0000-0000-000000000005"),
                ProviderKey = "gemini",
                DisplayName = "Google Gemini",
                AccountType = "api",
                AuthMode = "env",
                SecretName = "GOOGLE_APPLICATION_CREDENTIALS",
                RelatedToolsJson = Tools("opencode"),
                RelatedModelsJson = Models("gemini-flash", "gemini-flash-lite", "gemini-3.1-pro"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000006-0000-0000-0000-000000000006"),
                ProviderKey = "qwen_dashscope",
                DisplayName = "Qwen / DashScope",
                AccountType = "api",
                AuthMode = "env",
                SecretName = "DASHSCOPE_API_KEY",
                RelatedToolsJson = Tools("opencode"),
                RelatedModelsJson = Models("qwen3-coder-480b"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000007-0000-0000-0000-000000000007"),
                ProviderKey = "codex_cli",
                DisplayName = "Codex CLI subscription",
                AccountType = "subscription",
                AuthMode = "local",
                SecretName = null,
                RelatedToolsJson = Tools("codex"),
                RelatedModelsJson = Models("gpt-5-codex", "codex-5.3"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000008-0000-0000-0000-000000000008"),
                ProviderKey = "claude_code",
                DisplayName = "Claude Code subscription",
                AccountType = "subscription",
                AuthMode = "local",
                SecretName = null,
                RelatedToolsJson = Tools("claude"),
                RelatedModelsJson = Models("claude-haiku-4-5", "claude-sonnet-4-6", "claude-opus-4-7"),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                AccountId = new Guid("a1000009-0000-0000-0000-000000000009"),
                ProviderKey = "manual",
                DisplayName = "Manual/local provider usage",
                AccountType = "manual",
                AuthMode = "none",
                SecretName = null,
                RelatedToolsJson = Tools("claude", "codex", "opencode"),
                RelatedModelsJson = Models(),
                WarningThresholdPercent = 25,
                CriticalThresholdPercent = 10,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            }
        ];
    }
}

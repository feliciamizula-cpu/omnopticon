namespace Argus.AgentService.ProviderUsage;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Contracts.Agents;
using Microsoft.Extensions.Options;

internal sealed class AgentProviderUsageService(
    IOptions<AgentProviderUsageOptions> options,
    ILogger<AgentProviderUsageService> logger) : IAgentProviderUsageService
{
    private static readonly TimeSpan DefaultResetLead = TimeSpan.FromHours(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ProviderUsageDto> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public async Task<ProviderUsageOverviewDto> GetOverviewAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (forceRefresh || ShouldRefresh())
        {
            await RefreshAsync(forceRefresh, cancellationToken);
        }

        var providers = GetProviderDefinitions()
            .Select(definition => _snapshot.TryGetValue(definition.Id, out var provider)
                ? provider
                : CreateUnknownProvider(definition, "not checked yet"))
            .OrderByDescending(p => p.RoutingScore)
            .ThenBy(p => p.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProviderUsageOverviewDto(DateTimeOffset.UtcNow, providers, SelectProviderOnly(providers));
    }

    public async Task<ProviderUsageDto?> GetProviderAsync(string providerId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(forceRefresh, cancellationToken);
        return overview.Providers.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProviderLoginResponseDto?> LoginAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var definition = GetProviderDefinitions().FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return null;
        }

        var command = BuildCommand(definition.Executable, definition.LoginArguments);
        if (string.IsNullOrWhiteSpace(definition.LoginArguments))
        {
            var provider = await RefreshProviderAsync(definition, cancellationToken);
            return new ProviderLoginResponseDto(
                definition.Id,
                definition.Name,
                Started: false,
                Completed: false,
                ExitCode: null,
                Command: command,
                Output: string.Empty,
                Error: string.Empty,
                Message: "No login command is configured for this provider.",
                Provider: provider);
        }

        ProcessResult result;
        try
        {
            result = await RunCommandAsync(definition.Executable, definition.LoginArguments, LoginTimeout(definition), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Provider login command failed for {ProviderId}", definition.Id);
            var failedProvider = await RefreshProviderAsync(definition, cancellationToken);
            return new ProviderLoginResponseDto(
                definition.Id,
                definition.Name,
                Started: false,
                Completed: false,
                ExitCode: null,
                Command: command,
                Output: string.Empty,
                Error: ex.Message,
                Message: $"Unable to start login command: {ex.Message}",
                Provider: failedProvider);
        }

        var refreshedProvider = await RefreshProviderAsync(definition, cancellationToken);
        var message = result.TimedOut
            ? "Login command was started but timed out. If the CLI opened an interactive browser/device flow, complete it and refresh status."
            : result.ExitCode == 0
                ? "Login command completed. Refreshed authentication status."
                : "Login command completed with a non-zero exit code.";

        return new ProviderLoginResponseDto(
            definition.Id,
            definition.Name,
            Started: true,
            Completed: !result.TimedOut,
            ExitCode: result.TimedOut ? null : result.ExitCode,
            Command: command,
            Output: TrimOutput(result.Output),
            Error: TrimOutput(result.Error),
            Message: message,
            Provider: refreshedProvider);
    }

    public async Task<ProviderRouteSelectionDto> SelectRouteAsync(IReadOnlyList<AgentDto> agents, CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(forceRefresh: false, cancellationToken);
        var enabledAgents = agents
            .Where(IsEnabledAgent)
            .ToArray();

        if (enabledAgents.Length == 0)
        {
            return new ProviderRouteSelectionDto(null, null, null, null, null, false, 0, "No active agents are configured.");
        }

        foreach (var provider in overview.Providers.OrderByDescending(p => p.RoutingScore))
        {
            var agent = enabledAgents
                .Where(a => ToolMatchesProvider(a.Tool, provider))
                .OrderBy(a => IsIdleAgent(a) ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (agent is null)
            {
                continue;
            }

            if (provider.RoutingScore <= 0)
            {
                continue;
            }

            return new ProviderRouteSelectionDto(
                provider.ProviderId,
                provider.ProviderName,
                provider.ToolId,
                agent.AgentId.ToString(),
                agent.Name,
                true,
                provider.RoutingScore,
                $"Selected {provider.ProviderName} because it has the highest available usage among configured providers with an active matching agent.");
        }

        var fallback = enabledAgents.OrderBy(a => IsIdleAgent(a) ? 0 : 1).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).First();
        return new ProviderRouteSelectionDto(
            null,
            null,
            fallback.Tool,
            fallback.AgentId.ToString(),
            fallback.Name,
            true,
            0,
            "No provider had known available usage; selected the first active agent as a fallback.");
    }

    private bool ShouldRefresh()
    {
        var intervalSeconds = Math.Max(5, options.Value.RefreshIntervalSeconds);
        return DateTimeOffset.UtcNow - _lastRefresh > TimeSpan.FromSeconds(intervalSeconds);
    }

    private async Task RefreshAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && !ShouldRefresh())
            {
                return;
            }

            foreach (var definition in GetProviderDefinitions())
            {
                var provider = await RefreshProviderAsync(definition, cancellationToken);
                _snapshot[definition.Id] = provider;
            }

            _lastRefresh = DateTimeOffset.UtcNow;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ProviderUsageDto> RefreshProviderAsync(AgentProviderOptions definition, CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var toolStatus = await CheckToolStatusAsync(definition, checkedAt, cancellationToken);
        var (isAuthenticated, authStatus, authError) = await CheckAuthStatusAsync(definition, toolStatus, cancellationToken);
        var usageWindows = await ResolveUsageWindowsAsync(definition, cancellationToken);
        var lastError = string.Join(" ", new[] { toolStatus.Error, authError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(lastError))
        {
            lastError = null;
        }

        var routingScore = ComputeRoutingScore(toolStatus, isAuthenticated, usageWindows);
        var routingStatus = routingScore switch
        {
            <= 0 when !toolStatus.IsAvailable => "CLI missing",
            <= 0 when !isAuthenticated => "Not authenticated",
            <= 0 => "No available usage",
            _ => "Routable"
        };

        var provider = new ProviderUsageDto(
            definition.Id,
            definition.Name,
            definition.ToolId,
            definition.ToolAliases,
            definition.DisplayModelHint,
            toolStatus,
            isAuthenticated,
            authStatus,
            BuildCommand(definition.Executable, definition.LoginArguments),
            definition.LoginInstructions,
            usageWindows.FiveHour,
            usageWindows.TwentyFourHour,
            usageWindows.Weekly,
            usageWindows.Monthly,
            routingScore,
            routingStatus,
            checkedAt,
            lastError);

        _snapshot[definition.Id] = provider;
        return provider;
    }

    private async Task<CliToolStatusDto> CheckToolStatusAsync(AgentProviderOptions definition, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunCommandAsync(definition.Executable, definition.VersionArguments, CommandTimeout(definition), cancellationToken);
            if (result.TimedOut)
            {
                return new CliToolStatusDto(definition.ToolId, definition.Executable, false, "timeout", null, "Version check timed out.", checkedAt);
            }

            if (result.ExitCode != 0)
            {
                return new CliToolStatusDto(definition.ToolId, definition.Executable, false, "missing", null, TrimOutput(result.Error), checkedAt);
            }

            var version = FirstNonBlankLine(result.Output) ?? FirstNonBlankLine(result.Error) ?? "installed";
            return new CliToolStatusDto(definition.ToolId, definition.Executable, true, "available", version, null, checkedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CliToolStatusDto(
                definition.ToolId,
                definition.Executable,
                false,
                "missing",
                null,
                NormalizeCommandError(definition.Executable, ex),
                checkedAt);
        }
    }

    private async Task<(bool IsAuthenticated, string Status, string? Error)> CheckAuthStatusAsync(
        AgentProviderOptions definition,
        CliToolStatusDto toolStatus,
        CancellationToken cancellationToken)
    {
        var credentialVariable = definition.AuthEnvironmentVariables
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));
        if (!string.IsNullOrWhiteSpace(credentialVariable))
        {
            return (true, $"Credential present in {credentialVariable}", null);
        }

        if (!toolStatus.IsAvailable)
        {
            return (false, "CLI missing", null);
        }

        if (definition.Id.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            && TryGetGeminiCliCredentialStatus(out var geminiStatus))
        {
            return (true, geminiStatus, null);
        }

        if (definition.Id.Equals("claude", StringComparison.OrdinalIgnoreCase)
            && TryGetClaudeCliCredentialStatus(out var claudeStatus))
        {
            return (true, claudeStatus, null);
        }

        if (string.IsNullOrWhiteSpace(definition.AuthCheckArguments))
        {
            if (definition.Id.Equals("gemini", StringComparison.OrdinalIgnoreCase))
            {
                return (true, "Gemini CLI available; this CLI version does not expose a non-interactive auth status command.", null);
            }

            if (definition.Id.Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "No Claude Code credentials found", null);
            }

            return (false, "Auth check not configured", null);
        }

        try
        {
            var result = await RunCommandAsync(definition.Executable, definition.AuthCheckArguments, CommandTimeout(definition), cancellationToken);
            if (result.TimedOut)
            {
                return (false, "Auth check timed out", "Authentication check timed out.");
            }

            if (result.ExitCode == 0)
            {
                var detail = FirstNonBlankLine(result.Output) ?? "Logged in";
                return (true, detail, null);
            }

            var error = TrimOutput(result.Error);
            if (string.IsNullOrWhiteSpace(error))
            {
                error = TrimOutput(result.Output);
            }

            return (false, "Not logged in", error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, "Auth check failed", NormalizeCommandError(definition.Executable, ex));
        }
    }

    private static bool TryGetGeminiCliCredentialStatus(out string status)
    {
        foreach (var root in GeminiConfigRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var oauthCredentials = Path.Combine(root, "oauth_creds.json");
            if (FileExistsWithContent(oauthCredentials))
            {
                status = $"Gemini CLI OAuth credentials found in {root}";
                return true;
            }

            var googleAccounts = Path.Combine(root, "google_accounts.json");
            if (FileExistsWithContent(googleAccounts))
            {
                status = $"Gemini CLI account cache found in {root}";
                return true;
            }
        }

        status = string.Empty;
        return false;
    }

    private static IEnumerable<string> GeminiConfigRoots()
    {
        var explicitRoots = new[]
        {
            Environment.GetEnvironmentVariable("GEMINI_HOME"),
            Environment.GetEnvironmentVariable("GEMINI_CONFIG_DIR")
        };

        foreach (var root in explicitRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            yield return root!;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".gemini");
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig))
        {
            yield return Path.Combine(xdgConfig, "gemini");
        }
    }

    private static bool TryGetClaudeCliCredentialStatus(out string status)
    {
        foreach (var root in ClaudeConfigRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var credentialsFile = Path.Combine(root, ".credentials.json");
            if (FileExistsWithContent(credentialsFile))
            {
                status = $"Claude Code credentials found in {root}";
                return true;
            }
        }

        status = string.Empty;
        return false;
    }

    private static IEnumerable<string> ClaudeConfigRoots()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("CLAUDE_HOME");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            yield return explicitRoot;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".claude");
        }
    }

    private static bool FileExistsWithContent(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 2;
        }
        catch
        {
            return false;
        }
    }

    private async Task<UsageWindows> ResolveUsageWindowsAsync(AgentProviderOptions definition, CancellationToken cancellationToken)
    {
        UsageWindows? commandUsage = null;
        if (!string.IsNullOrWhiteSpace(definition.UsageExecutable) || !string.IsNullOrWhiteSpace(definition.UsageArguments))
        {
            var executable = string.IsNullOrWhiteSpace(definition.UsageExecutable)
                ? definition.Executable
                : definition.UsageExecutable;

            try
            {
                var result = await RunCommandAsync(executable, definition.UsageArguments, CommandTimeout(definition), cancellationToken);
                if (!result.TimedOut && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
                {
                    commandUsage = TryParseUsageJson(result.Output, definition);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Usage command failed for {ProviderId}", definition.Id);
            }
        }

        return new UsageWindows(
            commandUsage?.FiveHour ?? BuildWindow(definition.FiveHour, DefaultResetLead),
            commandUsage?.TwentyFourHour ?? BuildWindow(definition.TwentyFourHour, TimeSpan.FromHours(24)),
            commandUsage?.Weekly ?? BuildWindow(definition.Weekly, TimeSpan.FromDays(7)),
            commandUsage?.Monthly ?? BuildWindow(definition.Monthly, TimeSpan.FromDays(30)));
    }

    private static UsageWindows? TryParseUsageJson(string output, AgentProviderOptions definition)
    {
        try
        {
            var node = JsonNode.Parse(output);
            if (node is null)
            {
                return null;
            }

            var root = node["usage"] ?? node["windows"] ?? node;
            return new UsageWindows(
                ParseWindowNode(root["fiveHour"] ?? root["five_hour"] ?? root["5h"], definition.FiveHour, DefaultResetLead),
                ParseWindowNode(root["twentyFourHour"] ?? root["twenty_four_hour"] ?? root["daily"] ?? root["24h"], definition.TwentyFourHour, TimeSpan.FromHours(24)),
                ParseWindowNode(root["weekly"] ?? root["week"], definition.Weekly, TimeSpan.FromDays(7)),
                ParseWindowNode(root["monthly"] ?? root["month"], definition.Monthly, TimeSpan.FromDays(30)));
        }
        catch
        {
            return null;
        }
    }

    private static ProviderUsageWindowDto ParseWindowNode(JsonNode? node, AgentUsageWindowOptions fallback, TimeSpan resetLead)
    {
        if (node is null)
        {
            return BuildWindow(fallback, resetLead);
        }

        var limit = ReadDecimal(node, "limit") ?? fallback.Limit;
        var used = ReadDecimal(node, "used") ?? fallback.Used;
        var remaining = ReadDecimal(node, "remaining") ?? fallback.Remaining;
        var resetsAt = ReadDate(node, "resetsAt") ?? ReadDate(node, "resetAt") ?? fallback.ResetsAt;
        var source = ReadString(node, "source") ?? "usage-command";

        var merged = new AgentUsageWindowOptions
        {
            WindowId = fallback.WindowId,
            Label = fallback.Label,
            Limit = limit,
            Used = used,
            Remaining = remaining,
            ResetsAt = resetsAt,
            Source = source
        };

        return BuildWindow(merged, resetLead);
    }

    private static ProviderUsageWindowDto BuildWindow(AgentUsageWindowOptions option, TimeSpan resetLead)
    {
        var limit = option.Limit ?? 0;
        var used = option.Used;
        var remaining = option.Remaining;

        if (limit > 0)
        {
            if (remaining is null && used is not null)
            {
                remaining = Math.Max(0, limit - used.Value);
            }

            if (used is null && remaining is not null)
            {
                used = Math.Max(0, limit - remaining.Value);
            }
        }

        used ??= 0;
        remaining ??= limit > 0 ? Math.Max(0, limit - used.Value) : 0;

        var percent = limit <= 0 ? 0 : Math.Round(Math.Clamp(remaining.Value / limit * 100, 0, 100), 1);
        var isKnown = limit > 0 || option.Remaining is not null || option.Used is not null;
        var reset = option.ResetsAt ?? (isKnown ? DateTimeOffset.UtcNow.Add(resetLead) : null);

        return new ProviderUsageWindowDto(
            string.IsNullOrWhiteSpace(option.WindowId) ? "window" : option.WindowId,
            string.IsNullOrWhiteSpace(option.Label) ? option.WindowId : option.Label,
            limit,
            used.Value,
            remaining.Value,
            percent,
            reset,
            isKnown,
            isKnown ? option.Source : "not-configured");
    }

    private static decimal ComputeRoutingScore(CliToolStatusDto toolStatus, bool isAuthenticated, UsageWindows windows)
    {
        if (!toolStatus.IsAvailable || !isAuthenticated)
        {
            return 0;
        }

        var known = new[] { windows.FiveHour, windows.TwentyFourHour, windows.Weekly, windows.Monthly }
            .Where(window => window.IsKnown && !string.Equals(window.Source, "subscription", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (known.Length == 0)
        {
            return 1;
        }

        if (known.Any(window => window.Remaining <= 0 || window.RemainingPercent <= 0))
        {
            return 0;
        }

        return known.Min(window => window.RemainingPercent);
    }

    private static ProviderRouteSelectionDto? SelectProviderOnly(IReadOnlyList<ProviderUsageDto> providers)
    {
        var provider = providers.OrderByDescending(p => p.RoutingScore).FirstOrDefault(p => p.RoutingScore > 0);
        if (provider is null)
        {
            return new ProviderRouteSelectionDto(null, null, null, null, null, false, 0, "No provider currently has confirmed CLI, authentication, and available usage.");
        }

        return new ProviderRouteSelectionDto(
            provider.ProviderId,
            provider.ProviderName,
            provider.ToolId,
            null,
            null,
            true,
            provider.RoutingScore,
            $"{provider.ProviderName} has the highest available usage score.");
    }

    private AgentProviderOptions[] GetProviderDefinitions()
    {
        var configured = options.Value.Providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id))
            .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

        return DefaultProviders()
            .Select(definition => configured.TryGetValue(definition.Id, out var overrideDefinition)
                ? Merge(definition, overrideDefinition)
                : definition)
            .ToArray();
    }

    private AgentProviderOptions Merge(AgentProviderOptions fallback, AgentProviderOptions configured)
    {
        return new AgentProviderOptions
        {
            Id = ValueOrDefault(configured.Id, fallback.Id),
            Name = ValueOrDefault(configured.Name, fallback.Name),
            ToolId = ValueOrDefault(configured.ToolId, fallback.ToolId),
            ToolAliases = configured.ToolAliases.Length > 0 ? configured.ToolAliases : fallback.ToolAliases,
            Executable = ValueOrDefault(configured.Executable, fallback.Executable),
            VersionArguments = ValueOrDefault(configured.VersionArguments, fallback.VersionArguments),
            LoginArguments = ValueOrDefault(configured.LoginArguments, fallback.LoginArguments),
            AuthCheckArguments = ValueOrDefault(configured.AuthCheckArguments, fallback.AuthCheckArguments),
            AuthEnvironmentVariables = configured.AuthEnvironmentVariables.Length > 0 ? configured.AuthEnvironmentVariables : fallback.AuthEnvironmentVariables,
            UsageExecutable = ValueOrDefault(configured.UsageExecutable, fallback.UsageExecutable),
            UsageArguments = ValueOrDefault(configured.UsageArguments, fallback.UsageArguments),
            DisplayModelHint = ValueOrDefault(configured.DisplayModelHint, fallback.DisplayModelHint),
            LoginInstructions = ValueOrDefault(configured.LoginInstructions, fallback.LoginInstructions),
            FiveHour = MergeWindow(fallback.FiveHour, configured.FiveHour),
            TwentyFourHour = MergeWindow(fallback.TwentyFourHour, configured.TwentyFourHour),
            Weekly = MergeWindow(fallback.Weekly, configured.Weekly),
            Monthly = MergeWindow(fallback.Monthly, configured.Monthly)
        };
    }

    private static AgentUsageWindowOptions MergeWindow(AgentUsageWindowOptions fallback, AgentUsageWindowOptions configured)
    {
        return new AgentUsageWindowOptions
        {
            WindowId = ValueOrDefault(configured.WindowId, fallback.WindowId),
            Label = ValueOrDefault(configured.Label, fallback.Label),
            Limit = configured.Limit ?? fallback.Limit,
            Used = configured.Used ?? fallback.Used,
            Remaining = configured.Remaining ?? fallback.Remaining,
            ResetsAt = configured.ResetsAt ?? fallback.ResetsAt,
            Source = ValueOrDefault(configured.Source, fallback.Source)
        };
    }

    private static string ValueOrDefault(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string ScriptPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "scripts", name);

    private static AgentProviderOptions[] DefaultProviders() =>
    [
        new()
        {
            Id = "opencode",
            Name = "Opencode",
            ToolId = "opencode",
            ToolAliases = ["opencode"],
            Executable = "opencode",
            VersionArguments = "--version",
            LoginArguments = "auth login",
            AuthCheckArguments = "auth status",
            AuthEnvironmentVariables = ["OPENCODE_AUTH_TOKEN", "OPENROUTER_API_KEY", "OPENAI_API_KEY"],
            DisplayModelHint = "Mightymax / provider models",
            LoginInstructions = "Run the Opencode auth flow for the account that owns your development quota.",
            FiveHour = new() { WindowId = "fiveHour", Label = "5 hour" },
            TwentyFourHour = new() { WindowId = "twentyFourHour", Label = "24 hour" },
            Weekly = new() { WindowId = "weekly", Label = "weekly" },
            Monthly = new() { WindowId = "monthly", Label = "monthly" }
        },
        new()
        {
            Id = "gemini",
            Name = "Gemini",
            ToolId = "gemini",
            ToolAliases = ["gemini", "google"],
            Executable = "gemini",
            VersionArguments = "--version",
            LoginArguments = "auth login",
            // Gemini CLI has no auth subcommand — "gemini auth ..." passes the text as a prompt.
            // Auth is detected via environment variables only.
            AuthCheckArguments = "",
            AuthEnvironmentVariables = ["GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS"],
            UsageExecutable = "python3",
            UsageArguments = ScriptPath("gemini-usage.py"),
            DisplayModelHint = "Gemini Flash / Pro",
            LoginInstructions = "Run the Gemini CLI auth flow for the Google account used by the agent team.",
            FiveHour = new() { WindowId = "fiveHour", Label = "5 hour" },
            TwentyFourHour = new() { WindowId = "twentyFourHour", Label = "24 hour" },
            Weekly = new() { WindowId = "weekly", Label = "weekly" },
            Monthly = new() { WindowId = "monthly", Label = "monthly" }
        },
        new()
        {
            Id = "claude",
            Name = "Claude",
            ToolId = "claude",
            ToolAliases = ["claude", "anthropic"],
            Executable = "claude",
            VersionArguments = "--version",
            LoginArguments = "login",
            // claude whoami takes 12+ seconds (network call); use file-based credential check instead
            AuthCheckArguments = "",
            AuthEnvironmentVariables = ["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"],
            UsageExecutable = "python3",
            UsageArguments = ScriptPath("claude-usage.py"),
            DisplayModelHint = "Haiku / Sonnet",
            LoginInstructions = "Run the Claude CLI login flow for the Anthropic account that owns your Claude usage.",
            FiveHour = new() { WindowId = "fiveHour", Label = "5 hour" },
            TwentyFourHour = new() { WindowId = "twentyFourHour", Label = "24 hour" },
            Weekly = new() { WindowId = "weekly", Label = "weekly" },
            Monthly = new() { WindowId = "monthly", Label = "monthly" }
        },
        new()
        {
            Id = "openai",
            Name = "OpenAI",
            ToolId = "codex",
            ToolAliases = ["codex", "openai"],
            Executable = "codex",
            VersionArguments = "--version",
            LoginArguments = "login",
            AuthCheckArguments = "login status",
            AuthEnvironmentVariables = ["OPENAI_API_KEY", "CODEX_HOME"],
            UsageExecutable = "python3",
            UsageArguments = ScriptPath("codex-usage.py"),
            DisplayModelHint = "Codex / ChatGPT",
            LoginInstructions = "Run the Codex/OpenAI CLI login flow for the account that owns your OpenAI usage.",
            FiveHour = new() { WindowId = "fiveHour", Label = "5 hour" },
            TwentyFourHour = new() { WindowId = "twentyFourHour", Label = "24 hour" },
            Weekly = new() { WindowId = "weekly", Label = "weekly" },
            Monthly = new() { WindowId = "monthly", Label = "monthly" }
        }
    ];

    private static ProviderUsageDto CreateUnknownProvider(AgentProviderOptions definition, string reason)
    {
        var window = new ProviderUsageWindowDto("window", "unknown", 0, 0, 0, 0, null, false, "not-checked");
        return new ProviderUsageDto(
            definition.Id,
            definition.Name,
            definition.ToolId,
            definition.ToolAliases,
            definition.DisplayModelHint,
            new CliToolStatusDto(definition.ToolId, definition.Executable, false, "unknown", null, reason, null),
            false,
            "Unknown",
            BuildCommand(definition.Executable, definition.LoginArguments),
            definition.LoginInstructions,
            window with { WindowId = "fiveHour", Label = "5 hour" },
            window with { WindowId = "twentyFourHour", Label = "24 hour" },
            window with { WindowId = "weekly", Label = "weekly" },
            window with { WindowId = "monthly", Label = "monthly" },
            0,
            "Not checked",
            null,
            reason);
    }

    private static bool ToolMatchesProvider(string tool, ProviderUsageDto provider)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return false;
        }

        var normalized = tool.Trim().ToLowerInvariant();
        return string.Equals(normalized, provider.ToolId, StringComparison.OrdinalIgnoreCase)
            || provider.ToolAliases.Any(alias => string.Equals(normalized, alias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEnabledAgent(AgentDto agent) =>
        string.IsNullOrWhiteSpace(agent.Status)
        || agent.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
        || agent.Status.Equals("enabled", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdleAgent(AgentDto agent) =>
        string.IsNullOrWhiteSpace(agent.CurrentTaskId)
        && (string.IsNullOrWhiteSpace(agent.WorkStatus)
            || agent.WorkStatus.Equals("idle", StringComparison.OrdinalIgnoreCase));

    private int CommandTimeout(AgentProviderOptions _) => Math.Clamp(options.Value.CommandTimeoutSeconds, 2, 60);

    private int LoginTimeout(AgentProviderOptions _) => Math.Clamp(options.Value.CommandTimeoutSeconds, 2, 30);

    private static async Task<ProcessResult> RunCommandAsync(string fileName, string arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start command '{BuildCommand(fileName, arguments)}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await outputTask;
            var error = await errorTask;
            return new ProcessResult(process.ExitCode, output, error, TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup.
            }

            return new ProcessResult(null, string.Empty, $"Command timed out after {timeoutSeconds} seconds.", TimedOut: true);
        }
    }

    private static string BuildCommand(string executable, string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? executable : $"{executable} {arguments}";

    private static string? FirstNonBlankLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

    private static string TrimOutput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= 4_000 ? normalized : normalized[..4_000];
    }

    private static string NormalizeCommandError(string executable, Exception ex)
    {
        var message = TrimOutput(ex.Message);
        if (string.IsNullOrWhiteSpace(message))
        {
            return $"Executable '{executable}' is not available.";
        }

        if (message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase))
        {
            return $"Executable '{executable}' is not installed in this runtime environment.";
        }

        return message;
    }

    private static decimal? ReadDecimal(JsonNode node, string propertyName)
    {
        var value = node[propertyName];
        if (value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<decimal>(out var number))
        {
            return number;
        }

        return decimal.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadDate(JsonNode node, string propertyName)
    {
        var value = node[propertyName]?.ToString();
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? ReadString(JsonNode node, string propertyName)
    {
        var value = node[propertyName]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record UsageWindows(
        ProviderUsageWindowDto FiveHour,
        ProviderUsageWindowDto TwentyFourHour,
        ProviderUsageWindowDto Weekly,
        ProviderUsageWindowDto Monthly);

    private sealed record ProcessResult(int? ExitCode, string Output, string Error, bool TimedOut);
}

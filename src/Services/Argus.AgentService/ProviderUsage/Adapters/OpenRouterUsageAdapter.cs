namespace Argus.AgentService.ProviderUsage.Adapters;

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Argus.AgentService.Data;

public sealed class OpenRouterUsageAdapter(IHttpClientFactory httpClientFactory) : IProviderUsageAdapter
{
    public string ProviderKey => "openrouter";

    public async Task<ProviderUsageProbeResult> ProbeAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        var apiKey = account.SecretName is not null
            ? Environment.GetEnvironmentVariable(account.SecretName)
            : null;

        if (string.IsNullOrWhiteSpace(apiKey))
            return NotConfigured(account);

        try
        {
            var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(account.BaseUrl ?? "https://openrouter.ai");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.GetAsync("/api/v1/key", cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var status = ProviderLimitErrorClassifier.Classify(raw, (int)response.StatusCode);
                return ErrorSnapshot(account, "key_limit", status, $"HTTP {(int)response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}", raw);
            }

            var json = JsonNode.Parse(raw)?["data"];
            var limit = json?["limit"]?.GetValue<decimal?>();
            var usage = json?["usage"]?.GetValue<decimal?>();
            decimal? remaining = limit.HasValue ? limit - usage : null;
            decimal? remainingPct = limit.HasValue && limit > 0 ? remaining / limit * 100 : null;

            var snapshots = new List<ProviderUsageSnapshotRecord>
            {
                new()
                {
                    SnapshotId = Guid.NewGuid(),
                    AccountId = account.AccountId,
                    ProviderKey = account.ProviderKey,
                    ObservedAt = DateTimeOffset.UtcNow,
                    WindowKind = "key_limit",
                    Unit = "credits",
                    LimitAmount = limit,
                    UsedAmount = usage,
                    RemainingAmount = remaining,
                    RemainingPercent = remainingPct,
                    Source = "api",
                    Confidence = "high",
                    Status = "unknown",
                    RawJson = raw
                }
            };

            return new ProviderUsageProbeResult(snapshots, raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorSnapshot(account, "key_limit", "error", $"Network error: {ex.Message}");
        }
    }

    private static ProviderUsageProbeResult NotConfigured(ProviderAccountRecord account) =>
        new([new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "key_limit",
            Unit = "credits",
            Source = "not_configured",
            Confidence = "high",
            Status = "not_configured",
            Message = $"Set {account.SecretName} to enable OpenRouter usage monitoring."
        }]);

    private static ProviderUsageProbeResult ErrorSnapshot(
        ProviderAccountRecord account,
        string windowKind,
        string status,
        string message,
        string? rawJson = null) =>
        new([new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = windowKind,
            Unit = "credits",
            Source = "api",
            Confidence = "low",
            Status = status,
            Message = message,
            RawJson = rawJson
        }]);
}

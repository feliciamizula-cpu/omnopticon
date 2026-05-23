namespace Argus.AgentService.ProviderUsage.Adapters;

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.AgentService.Data;

public sealed class DeepSeekUsageAdapter(IHttpClientFactory httpClientFactory) : IProviderUsageAdapter
{
    public string ProviderKey => "deepseek";

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
            client.BaseAddress = new Uri(account.BaseUrl ?? "https://api.deepseek.com");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.GetAsync("/user/balance", cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var status = ProviderLimitErrorClassifier.Classify(raw, (int)response.StatusCode);
                return ErrorSnapshot(account, status, $"HTTP {(int)response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}", raw);
            }

            var json = JsonNode.Parse(raw);
            var isAvailable = json?["is_available"]?.GetValue<bool>() ?? true;
            var balanceInfos = json?["balance_infos"]?.AsArray();

            var snapshots = new List<ProviderUsageSnapshotRecord>();
            if (balanceInfos is not null)
            {
                foreach (var info in balanceInfos)
                {
                    var currency = info?["currency"]?.GetValue<string>() ?? "unknown";
                    var totalBalance = info?["total_balance"]?.GetValue<string>();
                    if (!decimal.TryParse(totalBalance, out var remaining)) remaining = 0;

                    snapshots.Add(new ProviderUsageSnapshotRecord
                    {
                        SnapshotId = Guid.NewGuid(),
                        AccountId = account.AccountId,
                        ProviderKey = account.ProviderKey,
                        ObservedAt = DateTimeOffset.UtcNow,
                        WindowKind = "balance",
                        Unit = currency.ToLowerInvariant(),
                        RemainingAmount = remaining,
                        Source = "api",
                        Confidence = "high",
                        Status = isAvailable ? (remaining > 0 ? "healthy" : "exhausted") : "exhausted",
                        Message = isAvailable ? null : "DeepSeek reported is_available=false",
                        RawJson = raw
                    });
                }
            }

            if (snapshots.Count == 0)
            {
                snapshots.Add(new ProviderUsageSnapshotRecord
                {
                    SnapshotId = Guid.NewGuid(),
                    AccountId = account.AccountId,
                    ProviderKey = account.ProviderKey,
                    ObservedAt = DateTimeOffset.UtcNow,
                    WindowKind = "balance",
                    Unit = "unknown",
                    Source = "api",
                    Confidence = "low",
                    Status = isAvailable ? "unknown" : "exhausted",
                    Message = isAvailable ? "No balance_infos returned." : "DeepSeek reported is_available=false",
                    RawJson = raw
                });
            }

            return new ProviderUsageProbeResult(snapshots, raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorSnapshot(account, "error", $"Network error: {ex.Message}");
        }
    }

    private static ProviderUsageProbeResult NotConfigured(ProviderAccountRecord account) =>
        new([new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "balance",
            Unit = "unknown",
            Source = "not_configured",
            Confidence = "high",
            Status = "not_configured",
            Message = $"Set {account.SecretName} to enable DeepSeek usage monitoring."
        }]);

    private static ProviderUsageProbeResult ErrorSnapshot(
        ProviderAccountRecord account,
        string status,
        string message,
        string? rawJson = null) =>
        new([new ProviderUsageSnapshotRecord
        {
            SnapshotId = Guid.NewGuid(),
            AccountId = account.AccountId,
            ProviderKey = account.ProviderKey,
            ObservedAt = DateTimeOffset.UtcNow,
            WindowKind = "balance",
            Unit = "unknown",
            Source = "api",
            Confidence = "low",
            Status = status,
            Message = message,
            RawJson = rawJson
        }]);
}

namespace Argus.AgentService.ProviderUsage.Adapters;

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Argus.AgentService.Data;

public sealed class OpenAiUsageAdapter(IHttpClientFactory httpClientFactory) : IProviderUsageAdapter
{
    public string ProviderKey => "openai";

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
            client.BaseAddress = new Uri(account.BaseUrl ?? "https://api.openai.com");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var today = DateTimeOffset.UtcNow.Date;
            var startOfMonth = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var startTs = startOfMonth.ToUnixTimeSeconds();
            var endTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var response = await client.GetAsync(
                $"/v1/organization/costs?start_time={startTs}&end_time={endTs}&limit=1",
                cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var classifiedStatus = ProviderLimitErrorClassifier.Classify(raw, (int)response.StatusCode);
                return ErrorSnapshot(account, classifiedStatus, $"HTTP {(int)response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}", raw);
            }

            var json = JsonNode.Parse(raw);
            var totalCost = json?["data"]?.AsArray()
                .Select(b => b?["amount"]?["value"]?.GetValue<decimal>() ?? 0)
                .Sum();

            var snapshots = new List<ProviderUsageSnapshotRecord>
            {
                new()
                {
                    SnapshotId = Guid.NewGuid(),
                    AccountId = account.AccountId,
                    ProviderKey = account.ProviderKey,
                    ObservedAt = DateTimeOffset.UtcNow,
                    WindowKind = "month",
                    Unit = "usd",
                    UsedAmount = totalCost,
                    RemainingAmount = null,
                    Source = "api",
                    Confidence = "high",
                    Status = "unknown",
                    Message = "No budget configured — showing spend only. Set a monthly budget to enable remaining/threshold calculation.",
                    RawJson = raw
                }
            };

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
            WindowKind = "month",
            Unit = "usd",
            Source = "not_configured",
            Confidence = "high",
            Status = "not_configured",
            Message = $"Set {account.SecretName} to enable OpenAI usage monitoring."
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
            WindowKind = "month",
            Unit = "usd",
            Source = "api",
            Confidence = "low",
            Status = status,
            Message = message,
            RawJson = rawJson
        }]);
}

namespace Argus.AgentService.ProviderUsage.Adapters;

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Argus.AgentService.Data;

public sealed class AnthropicUsageAdapter(IHttpClientFactory httpClientFactory) : IProviderUsageAdapter
{
    public string ProviderKey => "anthropic";

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
            client.BaseAddress = new Uri(account.BaseUrl ?? "https://api.anthropic.com");
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var today = DateTimeOffset.UtcNow.Date;
            var startOfMonth = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero);

            var response = await client.GetAsync(
                $"/v1/usage?start_time={startOfMonth:yyyy-MM-dd}&end_time={today:yyyy-MM-dd}",
                cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var classifiedStatus = ProviderLimitErrorClassifier.Classify(raw, (int)response.StatusCode);
                return ErrorSnapshot(account, classifiedStatus, $"HTTP {(int)response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}", raw);
            }

            var json = JsonNode.Parse(raw);
            var inputTokens = json?["data"]?.AsArray()
                .Select(d => d?["input_tokens"]?.GetValue<long>() ?? 0).Sum();
            var outputTokens = json?["data"]?.AsArray()
                .Select(d => d?["output_tokens"]?.GetValue<long>() ?? 0).Sum();
            var totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);

            var snapshots = new List<ProviderUsageSnapshotRecord>
            {
                new()
                {
                    SnapshotId = Guid.NewGuid(),
                    AccountId = account.AccountId,
                    ProviderKey = account.ProviderKey,
                    ObservedAt = DateTimeOffset.UtcNow,
                    WindowKind = "month",
                    Unit = "tokens",
                    UsedAmount = totalTokens,
                    RemainingAmount = null,
                    Source = "api",
                    Confidence = "high",
                    Status = "unknown",
                    Message = "No token limit configured — showing token usage only.",
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
            Unit = "tokens",
            Source = "not_configured",
            Confidence = "high",
            Status = "not_configured",
            Message = $"Set {account.SecretName} to enable Anthropic usage monitoring."
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
            Unit = "tokens",
            Source = "api",
            Confidence = "low",
            Status = status,
            Message = message,
            RawJson = rawJson
        }]);
}

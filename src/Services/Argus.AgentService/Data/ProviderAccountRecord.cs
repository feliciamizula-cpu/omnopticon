namespace Argus.AgentService.Data;

public sealed class ProviderAccountRecord
{
    public Guid AccountId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AccountType { get; set; } = "api";
    public string AuthMode { get; set; } = "env";
    public string? SecretName { get; set; }
    public string? BaseUrl { get; set; }
    public string RelatedToolsJson { get; set; } = "[]";
    public string RelatedModelsJson { get; set; } = "[]";
    public decimal WarningThresholdPercent { get; set; } = 25;
    public decimal CriticalThresholdPercent { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

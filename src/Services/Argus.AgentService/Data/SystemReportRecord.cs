namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class SystemReportRecord
{
    public Guid ReportId { get; set; }
    public Guid? AgentId { get; set; }
    public string ReportContent { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SystemReportDto ToDto() =>
        new(ReportId, AgentId, ReportContent, CreatedAt);
}

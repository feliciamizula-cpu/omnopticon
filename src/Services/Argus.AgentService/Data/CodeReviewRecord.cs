namespace Argus.AgentService.Data;

using Argus.Contracts.Agents;

public sealed class CodeReviewRecord
{
    public Guid ReviewId { get; set; }
    public string? SourceTaskId { get; set; }
    public Guid? AgentId { get; set; }
    public string ReviewContent { get; set; } = string.Empty;
    public string? CommitRef { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CodeReviewDto ToDto() =>
        new(ReviewId, SourceTaskId, AgentId, ReviewContent, CommitRef, Status, CreatedAt);
}

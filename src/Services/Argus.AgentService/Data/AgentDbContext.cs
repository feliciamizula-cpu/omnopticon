namespace Argus.AgentService.Data;

using Argus.BuildingBlocks.EventBus;
using Microsoft.EntityFrameworkCore;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<AgentRecord> Agents => Set<AgentRecord>();
    public DbSet<AgentTaskRecord> AgentTasks => Set<AgentTaskRecord>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<ProviderAccountRecord> ProviderAccounts => Set<ProviderAccountRecord>();
    public DbSet<ProviderUsageSnapshotRecord> ProviderUsageSnapshots => Set<ProviderUsageSnapshotRecord>();
    public DbSet<CodeReviewRecord> CodeReviews => Set<CodeReviewRecord>();
    public DbSet<SystemReportRecord> SystemReports => Set<SystemReportRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentRecord>(e =>
        {
            e.HasKey(a => a.AgentId);
            e.Property(a => a.ResponsibilitiesJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AgentTaskRecord>(e =>
        {
            e.HasKey(t => t.TaskId);
        });

        modelBuilder.Entity<ChatMessageRecord>(e =>
        {
            e.HasKey(c => c.MessageId);
        });

        modelBuilder.Entity<ProviderAccountRecord>(e =>
        {
            e.HasKey(x => x.AccountId);
            e.HasIndex(x => x.ProviderKey);
            e.Property(x => x.RelatedToolsJson).HasColumnType("jsonb");
            e.Property(x => x.RelatedModelsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ProviderUsageSnapshotRecord>(e =>
        {
            e.HasKey(x => x.SnapshotId);
            e.HasIndex(x => new { x.AccountId, x.ObservedAt });
            e.Property(x => x.RawJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CodeReviewRecord>(e =>
        {
            e.HasKey(x => x.ReviewId);
            e.HasIndex(x => x.SourceTaskId);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<SystemReportRecord>(e =>
        {
            e.HasKey(x => x.ReportId);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.ConfigureArgusOutbox();
    }
}

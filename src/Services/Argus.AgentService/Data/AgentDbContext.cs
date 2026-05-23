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

        modelBuilder.ConfigureArgusOutbox();
    }
}

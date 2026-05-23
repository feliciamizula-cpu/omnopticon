namespace Argus.AgentService.Data;

using Argus.BuildingBlocks.EventBus;
using Microsoft.EntityFrameworkCore;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<AgentRecord> Agents => Set<AgentRecord>();
    public DbSet<AgentTaskRecord> AgentTasks => Set<AgentTaskRecord>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();

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

        modelBuilder.ConfigureArgusOutbox();
    }
}

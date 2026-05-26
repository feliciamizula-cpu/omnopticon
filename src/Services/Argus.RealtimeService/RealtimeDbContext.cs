using Microsoft.EntityFrameworkCore;

namespace Argus.RealtimeService;

public sealed class RealtimeDbContext : DbContext
{
    public RealtimeDbContext(DbContextOptions<RealtimeDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<WorkerRecord> Workers => Set<WorkerRecord>();
    public DbSet<WorkerCapabilityRecord> WorkerCapabilities => Set<WorkerCapabilityRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var eventRecord = modelBuilder.Entity<EventRecord>(e =>
        {
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(256);
            e.Property(x => x.SourceService).HasMaxLength(256);
            e.Property(x => x.PayloadJson).HasColumnType("TEXT");
            e.HasIndex(x => x.RecordedAt);
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.CorrelationId);
        });

        var worker = modelBuilder.Entity<WorkerRecord>(e =>
        {
            e.HasKey(x => x.WorkerId);
            e.Property(x => x.WorkerType).IsRequired().HasMaxLength(128);
            e.HasIndex(x => x.LastSeenAt);
        });

        var capability = modelBuilder.Entity<WorkerCapabilityRecord>(e =>
        {
            e.HasKey(x => x.WorkerId);
            e.Property(x => x.WorkerType).IsRequired().HasMaxLength(128);
            e.Property(x => x.SubscribedAssetTypes).HasColumnType("TEXT");
            e.HasOne(x => x.Worker)
                .WithOne()
                .HasForeignKey<WorkerCapabilityRecord>(x => x.WorkerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class EventRecord
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? SourceService { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class WorkerRecord
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerType { get; set; } = string.Empty;
    public string? Version { get; set; }
    public int RunningTasks { get; set; }
    public int MaxConcurrency { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool IsOnline { get; set; }
}

public sealed class WorkerCapabilityRecord
{
    public string WorkerId { get; set; } = string.Empty;
    public string WorkerType { get; set; } = string.Empty;
    public int MaxConcurrency { get; set; }
    public string SubscribedAssetTypes { get; set; } = "[]";
    public WorkerRecord? Worker { get; set; }
}

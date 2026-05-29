using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Argus.RealtimeService;

public sealed class RealtimeDbContext : DbContext
{
    // SQLite cannot ORDER BY DateTimeOffset columns; store as Unix ms (long) instead.
    private static readonly ValueConverter<DateTimeOffset, long> _dateTimeOffsetConverter = new(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public RealtimeDbContext(DbContextOptions<RealtimeDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<WorkerRecord> Workers => Set<WorkerRecord>();
    public DbSet<WorkerCapabilityRecord> WorkerCapabilities => Set<WorkerCapabilityRecord>();
    public DbSet<WorkerScaleSettingRecord> WorkerScaleSettings => Set<WorkerScaleSettingRecord>();
    public DbSet<WorkerScaleCommandRecord> WorkerScaleCommands => Set<WorkerScaleCommandRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var eventRecord = modelBuilder.Entity<EventRecord>(e =>
        {
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(256);
            e.Property(x => x.SourceService).HasMaxLength(256);
            e.Property(x => x.PayloadJson).HasColumnType("TEXT");
            e.Property(x => x.RecordedAt).HasConversion(_dateTimeOffsetConverter);
            e.HasIndex(x => x.RecordedAt);
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.CorrelationId);
        });

        var worker = modelBuilder.Entity<WorkerRecord>(e =>
        {
            e.HasKey(x => x.WorkerId);
            e.Property(x => x.WorkerType).IsRequired().HasMaxLength(128);
            e.Property(x => x.LastSeenAt).HasConversion(_dateTimeOffsetConverter);
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
        
        var scaleSetting = modelBuilder.Entity<WorkerScaleSettingRecord>(e =>
        {
            e.HasKey(x => x.WorkerType);
            e.Property(x => x.WorkerType).IsRequired().HasMaxLength(128);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
            e.Property(x => x.RuntimeMode).IsRequired().HasMaxLength(64);
            e.Property(x => x.DeploymentName).HasMaxLength(256);
            e.Property(x => x.Namespace).HasMaxLength(128);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
            e.Property(x => x.CreatedAt).HasConversion(_dateTimeOffsetConverter);
            e.Property(x => x.UpdatedAt).HasConversion(_dateTimeOffsetConverter);
        });

        var scaleCommand = modelBuilder.Entity<WorkerScaleCommandRecord>(e =>
        {
            e.HasKey(x => x.CommandId);
            e.Property(x => x.WorkerType).IsRequired().HasMaxLength(128);
            e.Property(x => x.Action).IsRequired().HasMaxLength(64);
            e.Property(x => x.Status).IsRequired().HasMaxLength(64);
            e.Property(x => x.Message).HasMaxLength(2048);
            e.Property(x => x.ScalerKind).HasMaxLength(128);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.RequestedAt).HasConversion(_dateTimeOffsetConverter);
            e.Property(x => x.AppliedAt).HasConversion(_dateTimeOffsetConverter);
            e.HasIndex(x => x.WorkerType);
            e.HasIndex(x => x.RequestedAt);
            e.HasIndex(x => x.Status);
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

public sealed class WorkerScaleSettingRecord
{
    public string WorkerType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RuntimeMode { get; set; } = "continuous";
    public int DesiredReplicas { get; set; }
    public int MinReplicas { get; set; }
    public int MaxReplicas { get; set; }
    public bool IsPaused { get; set; }
    public string? DeploymentName { get; set; }
    public string? Namespace { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkerScaleCommandRecord
{
    public Guid CommandId { get; set; }
    public string WorkerType { get; set; } = string.Empty;
    public int PreviousDesiredReplicas { get; set; }
    public int RequestedDesiredReplicas { get; set; }
    public int? AppliedReplicas { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? ScalerKind { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

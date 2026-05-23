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
    public DbSet<WebhookConfig> WebhookConfigs => Set<WebhookConfig>();
    public DbSet<WebhookDeliveryLog> WebhookDeliveryLogs => Set<WebhookDeliveryLog>();

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

        var webhookConfig = modelBuilder.Entity<WebhookConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(256);
            e.Property(x => x.Url).IsRequired().HasMaxLength(2048);
            e.Property(x => x.EventTypes).HasColumnType("TEXT");
            e.Property(x => x.SecretHeader).HasMaxLength(256);
            e.Property(x => x.SecretValue).HasMaxLength(1024);
            e.HasIndex(x => x.IsActive);
        });

        var deliveryLog = modelBuilder.Entity<WebhookDeliveryLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(256);
            e.Property(x => x.RequestUrl).IsRequired().HasMaxLength(2048);
            e.Property(x => x.RequestBody).HasColumnType("TEXT");
            e.Property(x => x.ResponseBody).HasColumnType("TEXT");
            e.Property(x => x.ErrorMessage).HasMaxLength(2048);
            e.HasIndex(x => x.WebhookConfigId);
            e.HasIndex(x => x.AttemptedAt);
            e.HasOne(x => x.WebhookConfig)
                .WithMany(x => x.DeliveryLogs)
                .HasForeignKey(x => x.WebhookConfigId)
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

public sealed class WebhookConfig
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? EventTypes { get; set; }
    public Guid? ProgramId { get; set; }
    public int? MinInterestingScore { get; set; }
    public bool IsActive { get; set; } = true;
    public string? SecretHeader { get; set; }
    public string? SecretValue { get; set; }
    public int RetryCount { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WebhookDeliveryLog> DeliveryLogs { get; set; } = [];
}

public sealed class WebhookDeliveryLog
{
    public Guid Id { get; set; }
    public Guid WebhookConfigId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public string? RequestBody { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public int AttemptNumber { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public WebhookConfig? WebhookConfig { get; set; }
}
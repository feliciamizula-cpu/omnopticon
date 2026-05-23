using Microsoft.EntityFrameworkCore;

namespace Argus.RealtimeService;

public sealed class WebhookDbContext : DbContext
{
    public WebhookDbContext(DbContextOptions<WebhookDbContext> options) : base(options)
    {
    }

    public DbSet<WebhookConfig> WebhookConfigs => Set<WebhookConfig>();
    public DbSet<WebhookDeliveryLog> WebhookDeliveryLogs => Set<WebhookDeliveryLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(256);
            e.Property(x => x.Url).IsRequired().HasMaxLength(2048);
            e.Property(x => x.EventTypes).HasMaxLength(2048);
            e.Property(x => x.SecretHeader).HasMaxLength(256);
            e.Property(x => x.SecretValue).HasMaxLength(1024);
            e.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<WebhookDeliveryLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(256);
            e.Property(x => x.RequestUrl).HasMaxLength(2048);
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

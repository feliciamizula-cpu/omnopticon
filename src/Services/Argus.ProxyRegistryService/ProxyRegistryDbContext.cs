using Microsoft.EntityFrameworkCore;

namespace Argus.ProxyRegistryService;

public sealed class ProxyRegistryDbContext : DbContext
{
    public ProxyRegistryDbContext(DbContextOptions<ProxyRegistryDbContext> options) : base(options)
    {
    }

    public DbSet<ProxyRecord> Proxies => Set<ProxyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProxyRecord>(e =>
        {
            e.HasKey(x => x.ProxyId);
            e.Property(x => x.Url).IsRequired().HasMaxLength(2048);
            e.Property(x => x.Protocol).IsRequired().HasMaxLength(16);
            e.Property(x => x.Username).HasMaxLength(256);
            e.Property(x => x.Password).HasMaxLength(512);
            e.Property(x => x.Country).HasMaxLength(128);
            e.Property(x => x.City).HasMaxLength(256);
            e.Property(x => x.MaxRequestsPerSecond).HasDefaultValue(10);
            e.Property(x => x.MaxConcurrentRequests).HasDefaultValue(5);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.IsOnline).HasDefaultValue(true);
            e.HasIndex(x => x.Url).IsUnique();
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.Country);
        });
    }
}

public sealed class ProxyRecord
{
    public Guid ProxyId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsOnline { get; set; } = true;
    public int MaxRequestsPerSecond { get; set; } = 10;
    public int MaxConcurrentRequests { get; set; } = 5;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static Argus.Contracts.Proxies.ProxyDto ToDto(ProxyRecord r) =>
        new(
            r.ProxyId,
            r.Url,
            r.Protocol,
            r.Username,
            r.Country,
            r.City,
            r.IsActive,
            r.IsOnline,
            new Argus.Contracts.Proxies.ProxyRateLimitDto(
                r.MaxRequestsPerSecond,
                r.MaxConcurrentRequests,
                0,
                0,
                null),
            r.CreatedAt,
            r.UpdatedAt);
}

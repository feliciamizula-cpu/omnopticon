using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Argus.RequestToolService.Data;

public sealed class RequestToolDbContext(DbContextOptions<RequestToolDbContext> options) : DbContext(options)
{
    public DbSet<RequestToolSessionRecord> Sessions => Set<RequestToolSessionRecord>();
    public DbSet<HttpExchangeRecord> Exchanges => Set<HttpExchangeRecord>();
    public DbSet<HttpExchangeAuditRecord> AuditLogs => Set<HttpExchangeAuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSession(modelBuilder);
        ConfigureExchange(modelBuilder);
        ConfigureAudit(modelBuilder);
    }

    private static void ConfigureSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<RequestToolSessionRecord>();
        session.ToTable("request_tool_sessions");
        session.HasKey(record => record.SessionId);

        session.HasIndex(record => record.AssetId).IsUnique();
        session.HasIndex(record => record.ProgramId);

        session.Property(record => record.Title).HasMaxLength(512);
        session.Property(record => record.CreatedBy).HasMaxLength(256);
    }

    private static void ConfigureExchange(ModelBuilder modelBuilder)
    {
        var exchange = modelBuilder.Entity<HttpExchangeRecord>();
        exchange.ToTable("http_exchanges");
        exchange.HasKey(record => record.ExchangeId);

        exchange.HasIndex(record => record.SessionId);
        exchange.HasIndex(record => record.AssetId);
        exchange.HasIndex(record => record.ProgramId);
        exchange.HasIndex(record => record.RequestHost);
        exchange.HasIndex(record => record.ParentExchangeId);

        exchange.Property(record => record.Origin).HasConversion<string>().HasMaxLength(64);
        exchange.Property(record => record.Outcome).HasConversion<string>().HasMaxLength(64);
        exchange.Property(record => record.TabTitle).HasMaxLength(256);
        exchange.Property(record => record.RequestMethod).HasMaxLength(16);
        exchange.Property(record => record.RequestUrl).HasMaxLength(4096);
        exchange.Property(record => record.RequestScheme).HasMaxLength(16);
        exchange.Property(record => record.RequestHost).HasMaxLength(256);
        exchange.Property(record => record.RequestPath).HasMaxLength(2048);
        exchange.Property(record => record.RequestQuery).HasMaxLength(2048);
        exchange.Property(record => record.RequestHttpVersion).HasMaxLength(32);
        exchange.Property(record => record.RequestHeaders).HasColumnType("jsonb");
        exchange.Property(record => record.RequestCookies).HasColumnType("jsonb");
        exchange.Property(record => record.RequestBodyInline).HasColumnType("text");
        exchange.Property(record => record.RequestBodySha256).HasMaxLength(64);
        exchange.Property(record => record.RequestContentType).HasMaxLength(256);

        exchange.Property(record => record.ResponseStatusCode).HasMaxLength(3);
        exchange.Property(record => record.ResponseReasonPhrase).HasMaxLength(256);
        exchange.Property(record => record.ResponseHttpVersion).HasMaxLength(32);
        exchange.Property(record => record.ResponseHeaders).HasColumnType("jsonb");
        exchange.Property(record => record.ResponseCookies).HasColumnType("jsonb");
        exchange.Property(record => record.ResponseBodyInline).HasColumnType("text");
        exchange.Property(record => record.ResponseBodySha256).HasMaxLength(64);
        exchange.Property(record => record.ResponseContentType).HasMaxLength(256);

        exchange.Property(record => record.RedirectChain).HasColumnType("jsonb");
        exchange.Property(record => record.TlsInfo).HasColumnType("jsonb");
        exchange.Property(record => record.NetworkError).HasMaxLength(4096);

        exchange.Property(record => record.ScopeStatus).HasConversion<string>().HasMaxLength(32);
        exchange.Property(record => record.RateLimitKey).HasMaxLength(512);
        exchange.Property(record => record.ProxyId).HasMaxLength(256);
        exchange.Property(record => record.RequestSha256).HasMaxLength(64);
        exchange.Property(record => record.ResponseSha256).HasMaxLength(64);
        exchange.Property(record => record.CreatedBy).HasMaxLength(256);
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var audit = modelBuilder.Entity<HttpExchangeAuditRecord>();
        audit.ToTable("http_exchange_audit");
        audit.HasKey(record => record.AuditId);

        audit.HasIndex(record => record.ExchangeId);
        audit.HasIndex(record => record.SessionId);
        audit.HasIndex(record => record.AssetId);

        audit.Property(record => record.Action).HasMaxLength(64);
        audit.Property(record => record.Actor).HasMaxLength(256);
        audit.Property(record => record.TargetUrl).HasMaxLength(4096);
        audit.Property(record => record.RequestMethod).HasMaxLength(16);
        audit.Property(record => record.ScopeStatus).HasMaxLength(32);
        audit.Property(record => record.RequestSha256).HasMaxLength(64);
        audit.Property(record => record.ResponseSha256).HasMaxLength(64);
        audit.Property(record => record.Outcome).HasConversion<string>().HasMaxLength(64);
        audit.Property(record => record.Metadata).HasColumnType("jsonb");
    }
}

public sealed class RequestToolSessionRecord
{
    public Guid SessionId { get; set; }
    public Guid AssetId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? ScopeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class HttpExchangeRecord
{
    public Guid ExchangeId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AssetId { get; set; }
    public Guid ProgramId { get; set; }
    public Guid? ParentExchangeId { get; set; }

    public string Origin { get; set; } = "UserReplay";
    public string Outcome { get; set; } = "Draft";
    public string TabTitle { get; set; } = string.Empty;
    public bool IsPinned { get; set; }

    public string RequestMethod { get; set; } = "GET";
    public string RequestUrl { get; set; } = string.Empty;
    public string RequestScheme { get; set; } = "https";
    public string RequestHost { get; set; } = string.Empty;
    public int? RequestPort { get; set; }
    public string RequestPath { get; set; } = string.Empty;
    public string? RequestQuery { get; set; }
    public string? RequestHttpVersion { get; set; }
    public string RequestHeaders { get; set; } = "{}";
    public string RequestCookies { get; set; } = "{}";
    public string? RequestBodyInline { get; set; }
    public Guid? RequestBodyArtifactId { get; set; }
    public string? RequestBodySha256 { get; set; }
    public long? RequestBodySizeBytes { get; set; }
    public string? RequestContentType { get; set; }

    public int? ResponseStatusCode { get; set; }
    public string? ResponseReasonPhrase { get; set; }
    public string? ResponseHttpVersion { get; set; }
    public string? ResponseHeaders { get; set; }
    public string? ResponseCookies { get; set; }
    public string? ResponseBodyInline { get; set; }
    public Guid? ResponseBodyArtifactId { get; set; }
    public string? ResponseBodySha256 { get; set; }
    public long? ResponseBodySizeBytes { get; set; }
    public string? ResponseContentType { get; set; }

    public int? DurationMs { get; set; }
    public string RedirectChain { get; set; } = "[]";
    public string? TlsInfo { get; set; }
    public string? NetworkError { get; set; }

    public string ScopeStatus { get; set; } = "Unknown";
    public string? RateLimitKey { get; set; }
    public string? ProxyId { get; set; }

    public string? RequestSha256 { get; set; }
    public string? ResponseSha256 { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class HttpExchangeAuditRecord
{
    public Guid AuditId { get; set; }
    public Guid? ExchangeId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? ProgramId { get; set; }

    public string? Actor { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
    public string? RequestMethod { get; set; }
    public string? ScopeStatus { get; set; }
    public string? RequestSha256 { get; set; }
    public string? ResponseSha256 { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Metadata { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
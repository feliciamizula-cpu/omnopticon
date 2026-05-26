using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Assets;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<StorageDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddScoped<AssetConfirmedConsumer>();
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("argusdb")!, name: "argusdb", tags: ["db", "sql", "postgres"]);
}

builder.Services.AddSingleton<IPoisonMessageStore, InMemoryPoisonMessageStore>();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.StorageService");

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapHealthChecks("/health");

await app.RunAsync();

internal sealed class AssetDiscoveredConsumer : IIntegrationEventConsumer<AssetDiscovered>
{
    private readonly StorageDbContext _context;
    private readonly ILogger<AssetDiscoveredConsumer> _logger;

    public AssetDiscoveredConsumer(StorageDbContext context, ILogger<AssetDiscoveredConsumer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task ConsumeAsync(AssetDiscovered @event, CancellationToken cancellationToken)
    {
        try
        {
            var existingAsset = await _context.Assets.FirstOrDefaultAsync(
                a => a.AssetId == @event.AssetId, cancellationToken);

            if (existingAsset == null)
            {
                var asset = new AssetRecord
                {
                    AssetId = @event.AssetId,
                    ProgramId = @event.ProgramId,
                    Value = @event.Value,
                    AssetType = @event.AssetType,
                    DiscoveredAt = DateTimeOffset.UtcNow
                };

                _context.Assets.Add(asset);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Asset persisted: {AssetId} ({Value})", @event.AssetId, @event.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting asset {AssetId}", @event.AssetId);
            throw;
        }
    }
}

internal sealed class StorageDbContext(DbContextOptions<StorageDbContext> options) : DbContext(options)
{
    public DbSet<AssetRecord> Assets => Set<AssetRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var asset = modelBuilder.Entity<AssetRecord>();
        asset.ToTable("assets");
        asset.HasKey(a => a.AssetId);
        asset.HasIndex(a => a.ProgramId);
        asset.HasIndex(a => new { a.Value, a.AssetType });
        asset.Property(a => a.Value).HasMaxLength(2048);
        asset.Property(a => a.AssetType).HasMaxLength(64);
        asset.Property(a => a.Subtype).HasMaxLength(64);
    }
}

internal sealed class AssetRecord
{
    public Guid AssetId { get; set; }
    public Guid ProgramId { get; set; }
    public string Value { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public DateTimeOffset DiscoveredAt { get; set; }
}

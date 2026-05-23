using System.Text.Json;
using Argus.Contracts.Assets;
using Microsoft.EntityFrameworkCore;

namespace Argus.AssetService.Data;

public sealed class AssetDbContext(DbContextOptions<AssetDbContext> options) : DbContext(options)
{
    public DbSet<AssetRecord> Assets => Set<AssetRecord>();
    public DbSet<AssetRelationshipRecord> AssetRelationships => Set<AssetRelationshipRecord>();
    public DbSet<AssetObservationRecord> AssetObservations => Set<AssetObservationRecord>();
    public DbSet<AssetTypeDefinitionRecord> AssetTypeDefinitions => Set<AssetTypeDefinitionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAssetRecord(modelBuilder);
        ConfigureAssetRelationship(modelBuilder);
        ConfigureAssetObservation(modelBuilder);
        ConfigureAssetTypeDefinition(modelBuilder);
        modelBuilder.ConfigureArgusOutbox();
    }

    private static void ConfigureAssetRecord(ModelBuilder modelBuilder)
    {
        var asset = modelBuilder.Entity<AssetRecord>();
        asset.ToTable("assets");
        asset.HasKey(record => record.AssetId);

        asset.HasIndex(record => new { record.ProgramId, record.TypeKey, record.NaturalKey }).IsUnique();
        asset.HasIndex(record => new { record.ProgramId, record.Category });
        asset.HasIndex(record => new { record.ProgramId, record.ScopeStatus });
        asset.HasIndex(record => new { record.ProgramId, record.VerificationStatus });
        asset.HasIndex(record => new { record.ProgramId, record.HighValue });
        asset.HasIndex(record => new { record.ProgramId, record.LastSeenAt });
        asset.HasIndex(record => new { record.ProgramId, record.InterestingScore });
        asset.HasIndex(record => new { record.ProgramId, record.RiskScore });
        asset.HasIndex(record => record.CorrelationId);
        asset.HasIndex(record => new { record.ProgramId, record.Type, record.FirstSeenAt });
        asset.HasIndex(record => new { record.ProgramId, record.Type, record.LastSeenAt });
        asset.HasIndex(record => new { record.ProgramId, record.Status });

        asset.Property(record => record.Type).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.Status).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.Category).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.ScopeStatus).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.VerificationStatus).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.LifecycleStatus).HasConversion<string>().HasMaxLength(64);
        asset.Property(record => record.Subtype).HasMaxLength(128);
        asset.Property(record => record.TypeKey).HasMaxLength(64);
        asset.Property(record => record.Value).HasMaxLength(2048);
        asset.Property(record => record.NaturalKey).HasMaxLength(128);
        asset.Property(record => record.MetadataJson).HasColumnType("jsonb");
        asset.Property(record => record.TagsJson).HasColumnType("jsonb");
        asset.Property(record => record.SourceWorkerType).HasMaxLength(128);
        asset.Property(record => record.SourceWorkerId).HasMaxLength(256);
    }

    private static void ConfigureAssetRelationship(ModelBuilder modelBuilder)
    {
        var relationship = modelBuilder.Entity<AssetRelationshipRecord>();
        relationship.ToTable("asset_edges");
        relationship.HasKey(record => record.RelationshipId);
        relationship.HasIndex(record => record.FromAssetId);
        relationship.HasIndex(record => record.ToAssetId);
        relationship.HasIndex(record => record.EdgeType);
        relationship.HasIndex(record => new { record.FromAssetId, record.ToAssetId, record.EdgeType }).IsUnique();
        relationship.HasIndex(record => record.DiscoveredByTaskId);
        relationship.HasIndex(record => record.SourceWorkerType);
        relationship.HasIndex(record => record.SourceTaskRunId);
        relationship.HasIndex(record => record.SourceEventId);
        relationship.Property(record => record.EdgeType).HasMaxLength(128);
    }

    private static void ConfigureAssetObservation(ModelBuilder modelBuilder)
    {
        var observation = modelBuilder.Entity<AssetObservationRecord>();
        observation.ToTable("asset_observations");
        observation.HasKey(record => record.ObservationId);
        observation.HasIndex(record => record.AssetId);
        observation.HasIndex(record => record.TaskRunId);
        observation.HasIndex(record => record.ObservationType);
        observation.HasIndex(record => record.Status);
        observation.HasIndex(record => record.ObservedAt);
        observation.Property(record => record.ObservationType).HasConversion<string>().HasMaxLength(64);
        observation.Property(record => record.Status).HasConversion<string>().HasMaxLength(64);
        observation.Property(record => record.Summary).HasMaxLength(1024);
        observation.Property(record => record.DataJson).HasColumnType("jsonb");
    }

    private static void ConfigureAssetTypeDefinition(ModelBuilder modelBuilder)
    {
        var definition = modelBuilder.Entity<AssetTypeDefinitionRecord>();
        definition.ToTable("asset_type_definitions");
        definition.HasKey(record => record.TypeKey);
        definition.Property(record => record.TypeKey).HasMaxLength(64);
        definition.Property(record => record.DisplayName).HasMaxLength(128);
        definition.Property(record => record.Description).HasMaxLength(2048);
        definition.Property(record => record.Category).HasConversion<string>().HasMaxLength(64);
        definition.Property(record => record.Subtype).HasMaxLength(128);
        definition.Property(record => record.MetdataJson).HasColumnType("jsonb");
    }
}
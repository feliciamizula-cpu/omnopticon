namespace Argus.BuildingBlocks.Artifacts;

public sealed record ArtifactReference(
    string StoreType,
    string Key,
    string ContentType,
    long SizeBytes,
    ArtifactContentKind Kind,
    DateTimeOffset StoredAt);

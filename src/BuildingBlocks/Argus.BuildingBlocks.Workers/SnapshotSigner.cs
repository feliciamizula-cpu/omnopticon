using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.Contracts.Programs;

namespace Argus.BuildingBlocks.Workers;

public static class SnapshotSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ComputeSignature(ScopeSnapshot snapshot, string secretKey)
    {
        var canonical = FormatCanonical(snapshot);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSignature(ProgramExportDto export, string secretKey)
    {
        var canonical = FormatCanonical(export);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifySignature(ScopeSnapshot snapshot, string secretKey)
    {
        var expectedSignature = ComputeSignature(snapshot, secretKey);
        return string.Equals(snapshot.Signature, expectedSignature, StringComparison.OrdinalIgnoreCase);
    }

    public static ScopeSnapshot CreateSignedSnapshot(
        Guid programId,
        IReadOnlyCollection<ProgramScopeDto> scopes,
        IReadOnlyCollection<ScopeExclusionDto> exclusions,
        string secretKey)
    {
        var snapshot = new ScopeSnapshot(
            Guid.NewGuid(),
            programId,
            DateTimeOffset.UtcNow,
            scopes,
            exclusions,
            string.Empty);

        var signature = ComputeSignature(snapshot, secretKey);
        return snapshot with { Signature = signature };
    }

    public static string SerializeSnapshot(ScopeSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static ScopeSnapshot? DeserializeSnapshot(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScopeSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatCanonical(ScopeSnapshot snapshot)
    {
        var scopesJson = JsonSerializer.Serialize(snapshot.Scopes.OrderBy(s => s.ScopeId).Select(s => new
        {
            s.ScopeId,
            s.Pattern,
            s.ScopeType,
            s.Action
        }), JsonOptions);

        var exclusionsJson = JsonSerializer.Serialize(snapshot.Exclusions.OrderBy(e => e.ExclusionId).Select(e => new
        {
            e.ExclusionId,
            e.Pattern
        }), JsonOptions);

        return $"{snapshot.SnapshotId:N}:{snapshot.ProgramId:N}:{snapshot.CreatedAt:O}:{snapshot.Scopes.Count}:{snapshot.Exclusions.Count}:{scopesJson}:{exclusionsJson}";
    }

    private static string FormatCanonical(ProgramExportDto export)
    {
        var scopesJson = JsonSerializer.Serialize(export.Scopes.OrderBy(s => s.ScopeId).Select(s => new
        {
            s.ScopeId,
            s.Pattern,
            s.ScopeType,
            s.Action
        }), JsonOptions);

        var exclusionsJson = JsonSerializer.Serialize(export.Exclusions.OrderBy(e => e.ExclusionId).Select(e => new
        {
            e.ExclusionId,
            e.Pattern
        }), JsonOptions);

        var revisionsJson = JsonSerializer.Serialize(export.RuleRevisions.OrderBy(r => r.RevisionId).Select(r => new
        {
            r.RevisionId,
            r.Version,
            r.ChangeType
        }), JsonOptions);

        var policiesJson = JsonSerializer.Serialize(export.RateLimitPolicies.OrderBy(p => p.PolicyId).Select(p => new
        {
            p.PolicyId,
            p.BucketKey,
            p.Capacity,
            p.RefillRate
        }), JsonOptions);

        return $"{export.ProgramId:N}:{export.Name}:{export.Source}:{export.ExportedAt}:{export.Scopes.Count}:{export.Exclusions.Count}:{export.RuleRevisions.Count}:{export.RateLimitPolicies.Count}:{scopesJson}:{exclusionsJson}:{revisionsJson}:{policiesJson}";
    }
}
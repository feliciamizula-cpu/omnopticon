using Argus.Contracts.Assets;

namespace Argus.AssetService.Scoring;

public static class AssetScoringService
{
    public static int ScoreInitialInterestingness(AssetType type, string? subtype, string value) =>
        type switch
        {
            AssetType.ApiEndpoint => 40,
            AssetType.FindingCandidate => 70,
            AssetType.Finding => 85,
            AssetType.Url when value.Contains("admin", StringComparison.OrdinalIgnoreCase) => 35,
            AssetType.Url when value.Contains("login", StringComparison.OrdinalIgnoreCase) => 25,
            AssetType.JavaScriptFile => 20,
            _ when string.Equals(subtype, "graphql", StringComparison.OrdinalIgnoreCase) => 45,
            _ => 5
        };

    public static int ScoreRisk(AssetType type, string? subtype, IDictionary<string, object>? metadata)
    {
        var baseScore = type switch
        {
            AssetType.Finding => 70,
            AssetType.FindingCandidate => 50,
            AssetType.ApiEndpoint => 30,
            AssetType.Secret or AssetType.Vulnerability => 80,
            _ => 0
        };

        if (metadata is not null)
        {
            if (metadata.TryGetValue("cvss", out var cvssObj) && cvssObj is JsonElement cvssElement && cvssElement.TryGetDecimal(out var cvss))
            {
                baseScore = (int)(cvss * 10);
            }
        }

        return Math.Clamp(baseScore, 0, 100);
    }

    public static decimal AdjustConfidence(AssetType type, decimal baseConfidence, IReadOnlyCollection<string> tags)
    {
        var multiplier = 1.0m;

        if (tags.Contains("verified", StringComparer.OrdinalIgnoreCase))
            multiplier += 0.1m;

        if (tags.Contains("manual", StringComparer.OrdinalIgnoreCase))
            multiplier += 0.15m;

        if (tags.Contains("automated", StringComparer.OrdinalIgnoreCase))
            multiplier -= 0.05m;

        return Math.Clamp(baseConfidence * multiplier, 0.0m, 1.0m);
    }
}
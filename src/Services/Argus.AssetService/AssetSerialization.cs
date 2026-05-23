using Argus.Contracts.Assets;

namespace Argus.AssetService;

public static class AssetSerialization
{
    public static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
            return existing;

        var merged = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in incoming)
            merged[pair.Key] = pair.Value;

        return merged;
    }

    public static IReadOnlyCollection<string> MergeTags(
        IReadOnlyCollection<string> existing,
        IReadOnlyCollection<string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
            return existing;

        return existing
            .Concat(incoming)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
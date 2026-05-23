namespace Argus.AgentService.ProviderUsage;

public static class ProviderLimitErrorClassifier
{
    public static string ClassifyStatus(string? output, string? error, int exitCode)
    {
        var text = $"{output} {error}";
        return Classify(text, exitCode);
    }

    public static string Classify(string text, int? httpStatus = null)
    {
        if (string.IsNullOrWhiteSpace(text) && httpStatus is null)
            return "unknown";

        if (httpStatus is 401 or 403
            || ContainsAny(text, "invalid api key", "unauthorized", "401", "403"))
            return "error";

        if (httpStatus is 402
            || ContainsAny(text, "insufficient balance", "402", "quota exceeded",
                "usage limit reached", "weekly limit", "message limit"))
            return "exhausted";

        if (httpStatus is 429
            || ContainsAny(text, "rate_limit_error", "429", "rate limit", "too many requests"))
            return "critical";

        return "unknown";
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
}

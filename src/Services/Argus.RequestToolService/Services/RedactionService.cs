using System.Text.RegularExpressions;

namespace Argus.RequestToolService.Services;

public interface IRedactionService
{
    IReadOnlyDictionary<string, string[]> RedactHeaders(IReadOnlyDictionary<string, string[]> headers);
    IReadOnlyDictionary<string, string> RedactCookies(IReadOnlyDictionary<string, string> cookies);
    string? RedactText(string? value);
}

public sealed class RedactionService : IRedactionService
{
    private static readonly HashSet<string> SensitiveHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Amz-Security-Token",
        "X-Csrf-Token",
        "X-Auth-Token",
        "Api-Key",
        "Authentication"
    };

    private static readonly Regex BearerTokenRegex = new(@"Bearer\s+[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BasicAuthRegex = new(@"Basic\s+[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JwtRegex = new(@"[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+\.[A-Za-z0-9-_]+", RegexOptions.Compiled);
    private static readonly Regex AwsKeyRegex = new(@"(?<![A-Za-z0-9/+=])[A-Za-z0-9/+=]{20,}(?![A-Za-z0-9/+=])", RegexOptions.Compiled);
    private static readonly Regex SecretPatternRegex = new(@"(password|token|secret|apikey|api_key)[=:][^\s&]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyDictionary<string, string[]> RedactHeaders(IReadOnlyDictionary<string, string[]> headers)
    {
        var result = new Dictionary<string, string[]>();

        foreach (var kvp in headers)
        {
            if (SensitiveHeaderNames.Contains(kvp.Key))
            {
                result[kvp.Key] = kvp.Value.Select(_ => "[REDACTED]").ToArray();
            }
            else
            {
                result[kvp.Key] = kvp.Value.Select(RedactText).Select(v => v ?? "[REDACTED]").ToArray();
            }
        }

        return result;
    }

    public IReadOnlyDictionary<string, string> RedactCookies(IReadOnlyDictionary<string, string> cookies)
    {
        var result = new Dictionary<string, string>();

        foreach (var kvp in cookies)
        {
            result[kvp.Key] = "[REDACTED]";
        }

        return result;
    }

    public string? RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        value = BearerTokenRegex.Replace(value, "Bearer [REDACTED]");
        value = BasicAuthRegex.Replace(value, "Basic [REDACTED]");
        value = JwtRegex.Replace(value, "[JWT-REDACTED]");
        value = SecretPatternRegex.Replace(value, "$1=[REDACTED]");

        return value;
    }
}
using System.Text.Json;
using System.Web;

namespace Argus.BuildingBlocks.Workers;

public static class WorkerHelpers
{
    public static string? GetString(string? payloadJson, string propertyName)
    {
        if (!TryGetProperty(payloadJson, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static bool? GetBool(string? payloadJson, string propertyName)
    {
        if (!TryGetProperty(payloadJson, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            if (bool.TryParse(s, out var result)) return result;
            if (string.Equals(s, "1", StringComparison.Ordinal)) return true;
            if (string.Equals(s, "0", StringComparison.Ordinal)) return false;
        }

        return null;
    }

    public static int? GetInt(string? payloadJson, string propertyName)
    {
        if (!TryGetProperty(payloadJson, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var result)) return result;

        return null;
    }

    private static bool TryGetProperty(string? payloadJson, string propertyName, out JsonElement value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var element))
            {
                return false;
            }

            value = element.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? GetRegisteredDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length < 2 ? host : string.Join('.', parts[^2..]);
    }

    public static string? ExtractHost(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
        {
            return null;
        }

        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        if (Uri.TryCreate($"https://{urlOrHost}", UriKind.Absolute, out var uri2))
        {
            return uri2.Host;
        }

        return urlOrHost;
    }

    public static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = string.IsNullOrEmpty(uri.Query) ? null : uri.Query.TrimStart('?')
        };

        return builder.Uri.ToString();
    }

    public static TimeSpan GetDelayWithJitter(TimeSpan baseDelay, double jitterFactor = 0.1)
    {
        var random = Random.Shared;
        var jitter = baseDelay.TotalSeconds * jitterFactor * (2 * random.NextDouble() - 1);
        return TimeSpan.FromSeconds(Math.Max(0, baseDelay.TotalSeconds + jitter));
    }
}
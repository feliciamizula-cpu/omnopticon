using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace Argus.RequestToolService.Services;

/// <summary>
/// Generates the final payload sequence for a single variable config, applying the configured
/// transformation chain.  Built-in lists are small preset wordlists for common fuzzing scenarios.
/// </summary>
public static class FuzzPayloadGenerator
{
    // ── Built-in lists ────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string[]> BuiltIns =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["common-dirs"] = [
                "admin", "administrator", "api", "app", "assets", "backup", "bin",
                "cache", "cgi-bin", "config", "css", "data", "db", "debug", "dev",
                "docs", "downloads", "error", "files", "fonts", "img", "images",
                "includes", "index", "js", "lib", "login", "logs", "media", "old",
                "panel", "phpmyadmin", "private", "public", "scripts", "secure",
                "server-status", "setup", "sql", "static", "storage", "temp",
                "test", "tmp", "upload", "uploads", "user", "users", "vendor",
                "web", "wp-admin", "wp-content", "wp-login", ".git", ".env"
            ],
            ["common-files"] = [
                "index.php", "index.html", "config.php", "config.yml", "config.json",
                ".env", ".gitignore", "robots.txt", "sitemap.xml", "wp-config.php",
                "phpinfo.php", "info.php", "readme.md", "README.md", "CHANGELOG.md",
                "composer.json", "package.json", "Dockerfile", ".htaccess",
                "web.config", "server.xml", "backup.zip", "dump.sql", "db.sql"
            ],
            ["common-passwords"] = [
                "password", "123456", "password1", "admin", "letmein", "qwerty",
                "abc123", "monkey", "1234567890", "password123", "iloveyou",
                "admin123", "welcome", "login", "master", "pass", "test", "root",
                "toor", "changeme", "default", "guest", "secret", "dragon",
                "sunshine", "princess", "football", "shadow", "superman"
            ],
            ["common-usernames"] = [
                "admin", "administrator", "root", "user", "guest", "test",
                "demo", "operator", "manager", "superuser", "sysadmin", "sa",
                "postgres", "mysql", "oracle", "apache", "nginx", "web", "ftp",
                "mail", "email", "info", "support", "help", "service"
            ],
            ["http-methods"] = [
                "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
                "TRACE", "CONNECT", "PROPFIND", "PROPPATCH", "MKCOL", "COPY",
                "MOVE", "LOCK", "UNLOCK"
            ],
            ["sql-injection"] = [
                "'", "''", "' OR '1'='1", "' OR 1=1--", "\" OR 1=1--",
                "1' OR '1'='1", "1 OR 1=1", "' OR 'a'='a", "') OR ('1'='1",
                "' OR 1=1#", "admin'--", "' UNION SELECT null--",
                "1; DROP TABLE users--", "1' AND SLEEP(5)--", "1 WAITFOR DELAY '0:0:5'--"
            ],
            ["xss-payloads"] = [
                "<script>alert(1)</script>", "<img src=x onerror=alert(1)>",
                "javascript:alert(1)", "<svg onload=alert(1)>",
                "'><script>alert(1)</script>", "\"><script>alert(1)</script>",
                "<body onload=alert(1)>", "<iframe src=javascript:alert(1)>",
                "{{7*7}}", "${7*7}", "#{7*7}", "*{7*7}"
            ],
            ["path-traversal"] = [
                "../", "../../", "../../../", "../../../../", "../../../../../",
                "..%2F", "..%2F..%2F", "%2e%2e%2f", "%2e%2e/",
                "../etc/passwd", "../../etc/passwd", "../../../etc/passwd",
                "....//", "....\\\\", "%252e%252e%252f"
            ],
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns raw payloads for a single variable, capped at maxCount.</summary>
    public static IReadOnlyList<string> Generate(PayloadSource source, int maxCount = 10_000)
    {
        var raw = GenerateRaw(source, maxCount);
        return ApplyTransformations(raw, source.Transformations, maxCount);
    }

    /// <summary>Returns names of all built-in lists for display.</summary>
    public static IReadOnlyList<string> GetBuiltInNames() =>
        BuiltIns.Keys.Order().ToList();

    // ── Generation ────────────────────────────────────────────────────────────

    private static IEnumerable<string> GenerateRaw(PayloadSource source, int maxCount) =>
        source.SourceType switch
        {
            PayloadSourceType.List => (source.Values ?? []).Take(maxCount),
            PayloadSourceType.Counter => GenerateCounter(source, maxCount),
            PayloadSourceType.BuiltIn => BuiltIns.TryGetValue(source.BuiltInName ?? "", out var list)
                ? list.Take(maxCount)
                : [],
            _ => []
        };

    private static IEnumerable<string> GenerateCounter(PayloadSource source, int maxCount)
    {
        var start = source.CounterStart;
        var end   = source.CounterEnd;
        var step  = source.CounterStep == 0 ? 1 : source.CounterStep;
        var pad   = source.CounterPadding;
        var count = 0;

        if (step > 0)
        {
            for (var i = start; i <= end && count < maxCount; i += step, count++)
                yield return pad > 0 ? i.ToString().PadLeft(pad, '0') : i.ToString();
        }
        else
        {
            for (var i = start; i >= end && count < maxCount; i += step, count++)
                yield return pad > 0 ? i.ToString().PadLeft(pad, '0') : i.ToString();
        }
    }

    // ── Transformations ───────────────────────────────────────────────────────

    private static IReadOnlyList<string> ApplyTransformations(
        IEnumerable<string> values,
        IReadOnlyList<Transformation>? transforms,
        int maxCount)
    {
        if (transforms is null or { Count: 0 })
            return values.Take(maxCount).ToList();

        var result = new List<string>();
        foreach (var v in values.Take(maxCount))
        {
            var current = v;
            foreach (var t in transforms)
                current = Apply(current, t);
            result.Add(current);
        }
        return result;
    }

    private static string Apply(string value, Transformation t) => t.Type switch
    {
        TransformationType.Uppercase    => value.ToUpperInvariant(),
        TransformationType.Lowercase    => value.ToLowerInvariant(),
        TransformationType.Trim         => value.Trim(),
        TransformationType.Reverse      => new string(value.Reverse().ToArray()),
        TransformationType.Base64Encode => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
        TransformationType.Base64Decode => SafeBase64Decode(value),
        TransformationType.UrlEncode    => Uri.EscapeDataString(value),
        TransformationType.UrlDecode    => Uri.UnescapeDataString(value),
        TransformationType.HtmlEncode   => HttpUtility.HtmlEncode(value),
        TransformationType.HtmlDecode   => HttpUtility.HtmlDecode(value),
        TransformationType.Md5          => ComputeHash(MD5.HashData, value),
        TransformationType.Sha1         => ComputeHash(SHA1.HashData, value),
        TransformationType.Prefix       => (t.Value ?? "") + value,
        TransformationType.Suffix       => value + (t.Value ?? ""),
        TransformationType.RegexExtract => RegexExtract(value, t.Value ?? ".*"),
        _                               => value
    };

    private static string SafeBase64Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return value; }
    }

    private static string ComputeHash(Func<byte[], byte[]> hash, string value)
        => Convert.ToHexString(hash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RegexExtract(string value, string pattern)
    {
        try
        {
            var m = Regex.Match(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(500));
            return m.Success ? (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value) : value;
        }
        catch { return value; }
    }

    // ── Combination generators ────────────────────────────────────────────────

    /// <summary>
    /// Generates the full sequence of {variableName → value} dictionaries for the attack.
    /// Pitchfork: same index across all variables. ClusterBomb: cartesian product.
    /// </summary>
    public static IEnumerable<IReadOnlyDictionary<string, string>> GenerateCombinations(
        IReadOnlyList<FuzzVariableConfig> variables,
        FuzzAttackType attackType,
        int maxCount = 10_000)
    {
        if (variables.Count == 0) yield break;

        var payloadLists = variables
            .Select(v => (v.Name, Payloads: Generate(v.Source, maxCount)))
            .ToList();

        if (attackType == FuzzAttackType.Pitchfork)
        {
            var len = payloadLists.Min(p => p.Payloads.Count);
            len = Math.Min(len, maxCount);
            for (var i = 0; i < len; i++)
            {
                yield return payloadLists.ToDictionary(p => p.Name, p => p.Payloads[i]);
            }
        }
        else // ClusterBomb — cartesian product
        {
            var count = 0;
            foreach (var combo in CartesianProduct(payloadLists.Select(p => p.Payloads).ToList()))
            {
                if (count++ >= maxCount) yield break;
                var dict = new Dictionary<string, string>();
                for (var i = 0; i < payloadLists.Count; i++)
                    dict[payloadLists[i].Name] = combo[i];
                yield return dict;
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> CartesianProduct(IReadOnlyList<IReadOnlyList<string>> lists)
    {
        if (lists.Count == 0) yield break;

        static IEnumerable<IReadOnlyList<string>> Helper(IReadOnlyList<IReadOnlyList<string>> src, int idx)
        {
            if (idx == src.Count)
            {
                yield return [];
                yield break;
            }
            foreach (var tail in Helper(src, idx + 1))
            foreach (var head in src[idx])
            {
                var row = new string[tail.Count + 1];
                row[0] = head;
                for (var j = 0; j < tail.Count; j++) row[j + 1] = tail[j];
                yield return row;
            }
        }
        foreach (var combo in Helper(lists, 0)) yield return combo;
    }
}

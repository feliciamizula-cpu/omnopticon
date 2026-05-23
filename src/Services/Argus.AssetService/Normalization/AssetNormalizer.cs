using Argus.Contracts.Assets;

namespace Argus.AssetService.Normalization;

public static class AssetNormalizer
{
    public static string NormalizeValue(AssetType type, string value)
    {
        var trimmed = value.Trim();

        return type is AssetType.Domain or AssetType.Subdomain or AssetType.Url or AssetType.ApiEndpoint
            ? trimmed.ToLowerInvariant()
            : trimmed;
    }

    public static AssetCategory InferCategory(AssetType type) => type switch
    {
        AssetType.Domain => AssetCategory.Domain,
        AssetType.Subdomain => AssetCategory.Subdomain,
        AssetType.Ip => AssetCategory.Ip,
        AssetType.Cidr => AssetCategory.Network,
        AssetType.Url => AssetCategory.Url,
        AssetType.HttpResponse => AssetCategory.HttpResponse,
        AssetType.HtmlPage => AssetCategory.Document,
        AssetType.JavaScriptFile => AssetCategory.Script,
        AssetType.CssFile => AssetCategory.Style,
        AssetType.JsonDocument => AssetCategory.Document,
        AssetType.ApiEndpoint => AssetCategory.Api,
        AssetType.Technology => AssetCategory.Technology,
        AssetType.FindingCandidate or AssetType.Finding => AssetCategory.Finding,
        AssetType.Port => AssetCategory.Port,
        AssetType.DnsRecord => AssetCategory.Network,
        _ => AssetCategory.Other
    };

    public static string InferTypeKey(AssetType type) => type switch
    {
        AssetType.Domain => "domain",
        AssetType.Subdomain => "subdomain",
        AssetType.Ip => "ip",
        AssetType.Cidr => "cidr",
        AssetType.Url => "url",
        AssetType.HttpResponse => "http_response",
        AssetType.HtmlPage => "html_page",
        AssetType.JavaScriptFile => "javascript",
        AssetType.CssFile => "css",
        AssetType.JsonDocument => "json",
        AssetType.ApiEndpoint => "api_endpoint",
        AssetType.Technology => "technology",
        AssetType.FindingCandidate => "finding_candidate",
        AssetType.Finding => "finding",
        AssetType.Port => "port",
        AssetType.DnsRecord => "dns_record",
        _ => type.ToString().ToLowerInvariant()
    };

    public static AssetSubcategory InferSubcategory(AssetType type, string value)
    {
        if (value.Contains('/'))
        {
            return type switch
            {
                AssetType.Url when value.Contains("?") => AssetSubcategory.UrlQuery,
                AssetType.Url when value.Contains("#") => AssetSubcategory.UrlFragment,
                _ => AssetSubcategory.UrlRoot
            };
        }

        return type switch
        {
            AssetType.Subdomain when value.StartsWith("*.") => AssetSubcategory.DomainWildcard,
            AssetType.Domain when value.Contains(".") => AssetSubcategory.DomainRoot,
            AssetType.Subdomain => AssetSubcategory.DomainSubdomain,
            AssetType.Ip when value.Contains(":") => AssetSubcategory.IpV6,
            AssetType.Ip => AssetSubcategory.IpV4,
            _ => AssetSubcategory.Other
        };
    }
}
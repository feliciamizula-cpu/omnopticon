using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Argus.Contracts.Programs;

namespace Argus.ProgramScopeService.Providers;

public sealed class HackerOneScopeProvider : IScopeProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiToken;

    public string ProviderName => "HackerOne";

    public HackerOneScopeProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _apiToken = configuration["HACKERONE_API_TOKEN"] ?? string.Empty;
    }

    public async Task<IReadOnlyCollection<ProviderScopeItem>> FetchAsync(string programHandle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiToken))
            return [];

        var client = _httpClientFactory.CreateClient();
        var authBytes = Encoding.ASCII.GetBytes($"{_apiToken}:");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var items = new List<ProviderScopeItem>();
        var url = $"https://api.hackerone.com/v1/programs/{programHandle}/structured_scopes?page[size]=100";

        while (url is not null)
        {
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<HackerOneListResponse>(cancellationToken: cancellationToken);

            if (body?.Data is not null)
            {
                foreach (var entry in body.Data)
                {
                    items.Add(new ProviderScopeItem(
                        entry.Attributes.AssetIdentifier.Trim().ToLowerInvariant(),
                        NormalizeScopeType(entry.Attributes.AssetType),
                        entry.Attributes.EligibleForSubmission ? ScopeRuleAction.Include : ScopeRuleAction.Exclude));
                }
            }

            url = body?.Links?.Next;
        }

        return items;
    }

    private static string NormalizeScopeType(string assetType) => assetType switch
    {
        "url" => "url",
        "domain" => "domain",
        "ip_address" => "ip",
        "cidr_range" => "cidr",
        "wildcard" => "domain",
        _ => assetType
    };

    private sealed record HackerOneListResponse(
        [property: JsonPropertyName("data")] IReadOnlyCollection<HackerOneScopeEntry>? Data,
        [property: JsonPropertyName("links")] HackerOnePaginationLinks? Links);

    private sealed record HackerOneScopeEntry(
        [property: JsonPropertyName("attributes")] HackerOneScopeAttributes Attributes);

    private sealed record HackerOneScopeAttributes(
        [property: JsonPropertyName("asset_type")] string AssetType,
        [property: JsonPropertyName("asset_identifier")] string AssetIdentifier,
        [property: JsonPropertyName("eligible_for_submission")] bool EligibleForSubmission);

    private sealed record HackerOnePaginationLinks(
        [property: JsonPropertyName("next")] string? Next);
}

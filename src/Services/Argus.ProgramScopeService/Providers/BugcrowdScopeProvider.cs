using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Argus.Contracts.Programs;

namespace Argus.ProgramScopeService.Providers;

public sealed class BugcrowdScopeProvider : IScopeProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiToken;

    public string ProviderName => "Bugcrowd";

    public BugcrowdScopeProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _apiToken = configuration["BUGCROWD_API_TOKEN"] ?? string.Empty;
    }

    public async Task<IReadOnlyCollection<ProviderScopeItem>> FetchAsync(string programHandle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiToken))
            return [];

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var items = new List<ProviderScopeItem>();
        var url = $"https://api.bugcrowd.com/v3/programs/{programHandle}/target_groups?page[size]=100";

        while (url is not null)
        {
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<BugcrowdResponse>(cancellationToken: cancellationToken);

            if (body?.Included is not null)
            {
                foreach (var target in body.Included)
                {
                    if (target.Type != "target")
                        continue;

                    items.Add(new ProviderScopeItem(
                        target.Attributes.Name.Trim().ToLowerInvariant(),
                        NormalizeScopeType(target.Attributes.Category),
                        target.Attributes.Category is "in_scope" ? ScopeRuleAction.Include : ScopeRuleAction.Exclude));
                }
            }

            url = body?.Links?.Next;
        }

        return items;
    }

    private static string NormalizeScopeType(string category) => category switch
    {
        "in_scope" => "domain",
        "out_of_scope" => "domain",
        _ => "domain"
    };

    private sealed record BugcrowdResponse(
        [property: JsonPropertyName("data")] IReadOnlyCollection<BugcrowdResource>? Data,
        [property: JsonPropertyName("included")] IReadOnlyCollection<BugcrowdIncluded>? Included,
        [property: JsonPropertyName("links")] BugcrowdPaginationLinks? Links);

    private sealed record BugcrowdResource(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type);

    private sealed record BugcrowdIncluded(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("attributes")] BugcrowdTargetAttributes Attributes);

    private sealed record BugcrowdTargetAttributes(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("category")] string Category);

    private sealed record BugcrowdPaginationLinks(
        [property: JsonPropertyName("next")] string? Next);
}

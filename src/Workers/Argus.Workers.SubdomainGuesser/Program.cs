using System.Net;
using System.Text.Json;
using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.Configure<SubdomainGuesserOptions>(options =>
{
    options.MaxConcurrentQueries = int.TryParse(builder.Configuration["ARGUS_GUESSER_MAX_CONCURRENT"], out var mc) ? mc : 100;
    options.TimeoutMs = int.TryParse(builder.Configuration["ARGUS_GUESSER_TIMEOUT_MS"], out var to) ? to : 2000;
});
builder.AddArgusWorker<SubdomainGuesserWorker>();

await builder.Build().RunAsync();

internal sealed class SubdomainGuesserWorker : IReconWorker
{
    private static readonly string[] Top100Subdomains =
    [
        "www", "mail", "ftp", "localhost", "webmail", "smtp", "pop", "ns1", "webdisk",
        "ns2", "cpanel", "whm", "autodiscover", "autoconfig", "m", "imap", "test",
        "ns", "blog", "pop3", "dev", "www2", "admin", "forum", "news", "vpn",
        "ns3", "mail2", "new", "mysql", "old", "lists", "support", "mobile", "mx",
        "static", "docs", "beta", "shop", "sql", "secure", "demo", "loadbalancer",
        "api", "cdn", "stats", "ns4", "www1", "mail1", "server", "proxy", "router",
        "gateway", "git", "staging", "gitlab", "jenkins", "archiver", "gerrit",
        "mailman", "lists", "phpmyadmin", "vpn", "webmin", "svn", "mantis", "trac",
        "forum", "chat", "irc", "smtp2", "dc", "dc1", "dc2", "vcenter", "esxi",
        "backup", "snap", "restore", "office", "exchange", "outlook", "owa",
        "email", "corpsite", "internal", "intranet", "portal", "employee", "hr",
        "erp", "crm", "sales", "marketing", "support", "helpdesk", "asset",
        "files", "downloads", "upload", "media", "images", "img", "video", "stream"
    ];

    private readonly IOptions<SubdomainGuesserOptions> _options;
    private readonly ILogger<SubdomainGuesserWorker> _logger;
    private readonly SemaphoreSlim _throttle;

    public SubdomainGuesserWorker(IOptions<SubdomainGuesserOptions> options, ILogger<SubdomainGuesserWorker> logger)
    {
        _options = options;
        _logger = logger;
        _throttle = new SemaphoreSlim(options.Value.MaxConcurrentQueries, options.Value.MaxConcurrentQueries);
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "SubdomainGuesserWorker",
        ["Domain"],
        ["Subdomain"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 50);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var domain = WorkerHelpers.GetString(task.InputPayloadJson, "domain");
        if (string.IsNullOrWhiteSpace(domain))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "Missing domain in input payload" }));
        }

        domain = domain.Trim().TrimStart('*').TrimStart('.').TrimEnd('.').ToLowerInvariant();
        if (!IsValidRootDomain(domain))
        {
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { error = "Invalid domain in input payload", domain }));
        }

        var timeout = TimeSpan.FromMilliseconds(_options.Value.TimeoutMs);
        var maxConcurrent = _options.Value.MaxConcurrentQueries;

        if (Environment.GetEnvironmentVariable("GUESSER_DISABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            await context.ReportProgressAsync(100, "SubdomainGuesser disabled via configuration", null);
            return WorkerProcessResult.Empty(JsonSerializer.Serialize(new { domain, disabled = true }));
        }

        await context.ReportProgressAsync(5, $"Starting DNS-based subdomain guessing for {domain} with {Top100Subdomains.Length} entries", null);

        var foundSubdomains = new List<string>();
        var checkedCount = 0;
        var totalCount = Top100Subdomains.Length;

        var tasks = new List<Task<(string subdomain, bool found)>>();

        foreach (var prefix in Top100Subdomains)
        {
            var subdomain = $"{prefix}.{domain}";

            tasks.Add(Task.Run(async () =>
            {
                await _throttle.WaitAsync(cancellationToken);
                try
                {
                    var found = await CheckSubdomainAsync(subdomain, timeout, cancellationToken);

                    var current = Interlocked.Increment(ref checkedCount);
                    if (found && current % 10 == 0)
                    {
                        await context.ReportProgressAsync(
                            10 + (int)(50.0 * current / totalCount),
                            $"Checked {current}/{totalCount}, found {foundSubdomains.Count}",
                            null);
                    }

                    if (found)
                    {
                        lock (foundSubdomains)
                        {
                            foundSubdomains.Add(subdomain);
                        }
                    }

                    return (subdomain, found);
                }
                finally
                {
                    _throttle.Release();
                }
            }, cancellationToken));
        }

        await context.ReportProgressAsync(60, $"DNS queries complete, processing results...", null);

        var results = await Task.WhenAll(tasks);
        var foundCount = results.Count(r => r.found);

        await context.ReportProgressAsync(90, $"Found {foundCount} valid subdomains", null);

        foundSubdomains.Sort(StringComparer.OrdinalIgnoreCase);

        var payload = JsonSerializer.Serialize(new
        {
            domain,
            subdomains = foundSubdomains,
            count = foundSubdomains.Count,
            source = "dns-guess",
            wordlist = "top100"
        });

        var assets = foundSubdomains
            .Select(subdomain => new WorkerProducedAsset(
                "Subdomain",
                subdomain,
                null,
                0.70m,
                new Dictionary<string, string>
                {
                    ["source"] = "dns-guess",
                    ["domain"] = domain,
                    ["method"] = "wordlist-top100"
                },
                ["dns-guess", "wordlist"]))
            .ToArray();

        await context.ReportProgressAsync(100, $"Subdomain guessing complete: {foundSubdomains.Count} subdomains found", null);

        return new WorkerProcessResult(false, payload, assets);
    }

    private static async Task<bool> CheckSubdomainAsync(string subdomain, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            var addresses = await Dns.GetHostAddressesAsync(subdomain, cts.Token);
            return addresses.Length > 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidRootDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var parts = domain.Split('.');
        return parts.Length >= 2;
    }
}

internal sealed class SubdomainGuesserOptions
{
    public int MaxConcurrentQueries { get; set; } = 100;
    public int TimeoutMs { get; set; } = 2000;
}
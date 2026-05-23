using Argus.BuildingBlocks.Workers;
using Argus.Contracts.Workers;
using Argus.Contracts.Tasks;
using Argus.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.Configure<DnsResolverOptions>(options =>
{
    options.DnsServers = builder.Configuration["ARGUS_DNS_SERVERS"]?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        ?? ["8.8.8.8", "1.1.1.1"];
    if (int.TryParse(builder.Configuration["ARGUS_DNS_TIMEOUT_MS"], out var timeout))
    {
        options.TimeoutMs = timeout;
    }
    if (int.TryParse(builder.Configuration["ARGUS_DNS_RETRIES"], out var retries))
    {
        options.MaxRetries = retries;
    }
    if (bool.TryParse(builder.Configuration["ARGUS_DNS_WILDCARD_CHECK"], out var wcCheck))
    {
        options.EnableWildcardDetection = wcCheck;
    }
    if (int.TryParse(builder.Configuration["ARGUS_DNS_MAX_CONCURRENT"], out var maxConcurrent))
    {
        options.MaxConcurrentQueries = maxConcurrent;
    }
});
builder.AddArgusWorker<DnsResolverWorker>();

await builder.Build().RunAsync();

internal sealed class DnsResolverWorker : IReconWorker
{
    private readonly IOptions<DnsResolverOptions> _options;
    private readonly ILogger<DnsResolverWorker> _logger;
    private readonly SemaphoreSlim _queryThrottle;

    public DnsResolverWorker(IOptions<DnsResolverOptions> options, ILogger<DnsResolverWorker> logger)
    {
        _options = options;
        _logger = logger;
        _queryThrottle = new SemaphoreSlim(options.Value.MaxConcurrentQueries, options.Value.MaxConcurrentQueries);
    }

    public WorkerCapabilityDescriptor Capability { get; } = new(
        "DnsResolverWorker",
        ["Domain", "Subdomain"],
        ["Ip", "DnsRecord"],
        RequiresHttp: false,
        SupportsCheckpoint: true,
        MaxConcurrency: 50);

    public async Task<WorkerProcessResult> ProcessAsync(
        ReconTaskDto task,
        WorkerExecutionContext context,
        CancellationToken cancellationToken)
    {
        var host = WorkerHelpers.GetString(task.InputPayloadJson, "host")
            ?? WorkerHelpers.GetString(task.InputPayloadJson, "domain")
            ?? throw new InvalidOperationException("No host in task payload");

        host = WorkerHelpers.ExtractHost(host) ?? host;

        await context.ReportProgressAsync(5, $"Starting DNS resolution for {host}", null);

        var allowed = await context.RequestRateLimitTokenAsync(new RateLimitRequest(
            task.ProgramId,
            task.ScopeId,
            host,
            WorkerHelpers.GetRegisteredDomain(host),
            null,
            Capability.WorkerType,
            PermitCount: 1));

        if (!allowed)
        {
            await context.ReportProgressAsync(100, "Rate limited, will retry", null);
            return new WorkerProcessResult(true, JsonSerializer.Serialize(new { host, delayed = true }), []);
        }

        var producedAssets = new List<WorkerProducedAsset>();
        var dnsResults = new DnsQueryResult { Host = host };
        var errors = new List<string>();

        try
        {
            bool isWildcard = false;
            if (_options.Value.EnableWildcardDetection)
            {
                isWildcard = await DetectWildcardAsync(host, cancellationToken);
                dnsResults.IsWildcard = isWildcard;
                if (isWildcard)
                {
                    _logger.LogInformation("Wildcard DNS detected for {Host}", host);
                }
            }

            await context.ReportProgressAsync(20, $"Resolving A/AAAA records for {host}", null);

            var aRecords = await QueryWithRetryAsync(host, DnsRecordType.A, cancellationToken);
            dnsResults.ARecords = aRecords;
            
            var aaaaRecords = await QueryWithRetryAsync(host, DnsRecordType.AAAA, cancellationToken);
            dnsResults.AaaaRecords = aaaaRecords;

            foreach (var ip in aRecords.Concat(aaaaRecords))
            {
                producedAssets.Add(CreateIpAsset(ip, host, isWildcard));
            }

            var aRecordStrings = aRecords.Select(ip => $"A {ip}").ToList();
            var aaaaRecordStrings = aaaaRecords.Select(ip => $"AAAA {ip}").ToList();

            if (aRecordStrings.Count > 0)
            {
                producedAssets.Add(CreateDnsRecordAsset(host, "A", string.Join(", ", aRecordStrings), isWildcard));
            }
            if (aaaaRecordStrings.Count > 0)
            {
                producedAssets.Add(CreateDnsRecordAsset(host, "AAAA", string.Join(", ", aaaaRecordStrings), isWildcard));
            }

            await context.ReportProgressAsync(40, $"Resolving CNAME chain for {host}", null);
            
            var cnameChain = new List<string>();
            string? currentHost = host;
            
            for (int i = 0; i < 10 && currentHost != null; i++)
            {
                var cnames = await QueryWithRetryAsync(currentHost, DnsRecordType.CNAME, cancellationToken);
                if (cnames.Count == 0)
                    break;
                    
                var cname = cnames.First();
                cnameChain.Add(cname);
                
                if (cname == currentHost)
                    break;
                    
                currentHost = cname;
            }

            dnsResults.CnameChain = cnameChain;

            if (cnameChain.Count > 0)
            {
                producedAssets.Add(CreateDnsRecordAsset(host, "CNAME", string.Join(" -> ", cnameChain), isWildcard));
            }

            await context.ReportProgressAsync(60, $"Resolving additional records (MX, NS, TXT) for {host}", null);

            var mxRecords = await QueryWithRetryAsync(host, DnsRecordType.MX, cancellationToken);
            if (mxRecords.Count > 0)
            {
                dnsResults.MxRecords = mxRecords;
                producedAssets.Add(CreateDnsRecordAsset(host, "MX", string.Join(", ", mxRecords), isWildcard));
            }

            var nsRecords = await QueryWithRetryAsync(host, DnsRecordType.NS, cancellationToken);
            if (nsRecords.Count > 0)
            {
                dnsResults.NsRecords = nsRecords;
                producedAssets.Add(CreateDnsRecordAsset(host, "NS", string.Join(", ", nsRecords), isWildcard));
            }

            var txtRecords = await QueryWithRetryAsync(host, DnsRecordType.TXT, cancellationToken);
            if (txtRecords.Count > 0)
            {
                dnsResults.TxtRecords = txtRecords;
                foreach (var txt in txtRecords)
                {
                    producedAssets.Add(CreateDnsRecordAsset(host, "TXT", txt, isWildcard));
                }
            }

            var caaRecords = await QueryWithRetryAsync(host, DnsRecordType.CAA, cancellationToken);
            if (caaRecords.Count > 0)
            {
                dnsResults.CaaRecords = caaRecords;
                producedAssets.Add(CreateDnsRecordAsset(host, "CAA", string.Join(", ", caaRecords), isWildcard));
            }

            await context.ReportProgressAsync(90, $"Storing DNS observation artifact for {host}", null);

            var observation = new DnsObservation
            {
                Host = host,
                Timestamp = DateTimeOffset.UtcNow,
                QueryResult = dnsResults,
                Resolver = _options.Value.DnsServers.FirstOrDefault() ?? "system",
                RawResponses = dnsResults.SerializeRawResponses()
            };

            var artifact = new WorkerProducedArtifact(
                "DnsObservation",
                $"dns-{host}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(observation),
                new Dictionary<string, string>
                {
                    ["host"] = host,
                    ["is_wildcard"] = isWildcard.ToString(),
                    ["record_count"] = producedAssets.Count.ToString()
                });

            var outputSummary = JsonSerializer.Serialize(new
            {
                host,
                isWildcard,
                aCount = aRecords.Count,
                aaaaCount = aaaaRecords.Count,
                cnameChainLength = cnameChain.Count,
                produced = producedAssets.Count
            });

            await context.ReportProgressAsync(100, $"DNS resolution complete for {host}", null);

            return new WorkerProcessResult(false, outputSummary, producedAssets);
        }
        catch (DnsQueryException ex)
        {
            errors.Add(ex.Message);
            _logger.LogWarning("DNS query failed for {Host}: {Message}", host, ex.Message);

            var outputSummary = JsonSerializer.Serialize(new { host, error = ex.Code.ToString(), errors });
            return new WorkerProcessResult(false, outputSummary, producedAssets);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error resolving DNS for {Host}", host);
            var outputSummary = JsonSerializer.Serialize(new { host, error = ex.GetType().Name });
            return new WorkerProcessResult(false, outputSummary, producedAssets);
        }
    }

    private async Task<List<string>> QueryWithRetryAsync(string host, DnsRecordType recordType, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var attempt = 0;
        var maxAttempts = _options.Value.MaxRetries;

        while (attempt < maxAttempts)
        {
            try
            {
                await _queryThrottle.WaitAsync(cancellationToken);
                try
                {
                    results = recordType switch
                    {
                        DnsRecordType.A => await ResolveARecordsAsync(host, cancellationToken),
                        DnsRecordType.AAAA => await ResolveAaaaRecordsAsync(host, cancellationToken),
                        DnsRecordType.CNAME => await ResolveCnameRecordsAsync(host, cancellationToken),
                        DnsRecordType.MX => await ResolveMxRecordsAsync(host, cancellationToken),
                        DnsRecordType.NS => await ResolveNsRecordsAsync(host, cancellationToken),
                        DnsRecordType.TXT => await ResolveTxtRecordsAsync(host, cancellationToken),
                        DnsRecordType.CAA => await ResolveCaaRecordsAsync(host, cancellationToken),
                        _ => results
                    };
                    return results;
                }
                finally
                {
                    _queryThrottle.Release();
                }
            }
            catch (DnsQueryException ex) when (ex.IsTransient && attempt < maxAttempts - 1)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return results;
    }

    private async Task<List<string>> ResolveARecordsAsync(string host, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var configuredServers = _options.Value.DnsServers;

        foreach (var dnsServer in configuredServers)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.Value.TimeoutMs);

                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeoutCts.Token);
                results.AddRange(addresses.Select(a => a.ToString()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
            {
                return results;
            }
            catch (Exception)
            {
                continue;
            }
        }

        return results.Distinct().ToList();
    }

    private async Task<List<string>> ResolveAaaaRecordsAsync(string host, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var configuredServers = _options.Value.DnsServers;

        foreach (var dnsServer in configuredServers)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.Value.TimeoutMs);

                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetworkV6, timeoutCts.Token);
                results.AddRange(addresses.Select(a => a.ToString()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
            {
                return results;
            }
            catch (Exception)
            {
                continue;
            }
        }

        return results.Distinct().ToList();
    }

    private async Task<List<string>> ResolveCnameRecordsAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.Value.TimeoutMs);

            var aliases = await Dns.GetHostEntryAsync(host, timeoutCts.Token);
            var canonicalName = aliases.HostName;

            if (!string.Equals(canonicalName, host, StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { canonicalName };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
        {
            return new List<string>();
        }
        catch (Exception)
        {
        }

        return new List<string>();
    }

    private async Task<List<string>> ResolveMxRecordsAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.Value.TimeoutMs);

            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0)
            {
                return new List<string>();
            }

            using var client = new UdpClient();
            client.Client.ReceiveTimeout = _options.Value.TimeoutMs;
            client.Client.SendTimeout = _options.Value.TimeoutMs;

            foreach (var dnsServer in _options.Value.DnsServers)
            {
                try
                {
                    client.Connect(dnsServer, 53);
                    var query = BuildDnsQuery(host, 15);
                    await client.SendAsync(query, cancellationToken);

                    var buffer = new byte[512];
                    var result = await client.ReceiveAsync(timeoutCts.Token);
                    var mxRecords = ParseMxRecords(buffer[..result.Buffer.Length]);
                    if (mxRecords.Count > 0)
                    {
                        return mxRecords;
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return new List<string>();
    }

    private async Task<List<string>> ResolveNsRecordsAsync(string host, CancellationToken cancellationToken)
    {
        return await Task.FromResult(new List<string>());
    }

    private async Task<List<string>> ResolveTxtRecordsAsync(string host, CancellationToken cancellationToken)
    {
        return await Task.FromResult(new List<string>());
    }

    private async Task<List<string>> ResolveCaaRecordsAsync(string host, CancellationToken cancellationToken)
    {
        return await Task.FromResult(new List<string>());
    }

    private async Task<bool> DetectWildcardAsync(string host, CancellationToken cancellationToken)
    {
        var randomLabel = $"xn--{Guid.NewGuid().ToString("N")[..16]}";
        var testHost = $"{randomLabel}.{host}";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.Value.TimeoutMs);

            var addresses = await Dns.GetHostAddressesAsync(testHost, timeoutCts.Token);
            return addresses.Length > 0;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildDnsQuery(string domain, ushort queryType)
    {
        using var ms = new MemoryStream();
        var transactionId = (ushort)Random.Shared.Next(1, 65535);

        ms.WriteByte((byte)(transactionId >> 8));
        ms.WriteByte((byte)transactionId);
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00);

        foreach (var label in domain.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            var labelBytes = System.Text.Encoding.ASCII.GetBytes(label);
            ms.Write(labelBytes);
        }

        ms.WriteByte(0x00);

        ms.WriteByte(0x00);
        ms.WriteByte((byte)queryType);
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);

        return ms.ToArray();
    }

    private static List<string> ParseMxRecords(byte[] response)
    {
        var records = new List<string>();
        if (response.Length < 12)
            return records;

        return records;
    }

    private WorkerProducedAsset CreateIpAsset(string ip, string sourceHost, bool isWildcard)
    {
        var tags = new List<string> { "dns", "resolved" };
        if (isWildcard)
        {
            tags.Add("potential-wildcard");
        }

        return new WorkerProducedAsset(
            "Ip",
            ip,
            null,
            isWildcard ? 0.5m : 0.95m,
            new Dictionary<string, string>
            {
                ["source_host"] = sourceHost,
                ["source_type"] = "dns",
                ["is_wildcard"] = isWildcard.ToString().ToLowerInvariant()
            },
            tags);
    }

    private WorkerProducedAsset CreateDnsRecordAsset(string host, string recordType, string value, bool isWildcard)
    {
        var tags = new List<string> { "dns", recordType.ToLowerInvariant() };
        if (isWildcard)
        {
            tags.Add("potential-wildcard");
        }

        return new WorkerProducedAsset(
            "DnsRecord",
            $"{host} {recordType} {value}",
            recordType,
            isWildcard ? 0.5m : 0.9m,
            new Dictionary<string, string>
            {
                ["host"] = host,
                ["record_type"] = recordType,
                ["value"] = value,
                ["is_wildcard"] = isWildcard.ToString().ToLowerInvariant()
            },
            tags);
    }
}

internal sealed class DnsResolverOptions
{
    public string[] DnsServers { get; set; } = ["8.8.8.8", "1.1.1.1"];
    public int TimeoutMs { get; set; } = 5000;
    public int MaxRetries { get; set; } = 3;
    public bool EnableWildcardDetection { get; set; } = true;
    public int MaxConcurrentQueries { get; set; } = 20;
}

internal enum DnsRecordType : ushort
{
    A = 1,
    AAAA = 28,
    CNAME = 5,
    MX = 15,
    NS = 2,
    TXT = 16,
    CAA = 257
}

internal sealed class DnsQueryException : Exception
{
    public DnsQueryErrorCode Code { get; }

    public bool IsTransient => Code is DnsQueryErrorCode.Timeout or DnsQueryErrorCode.ServerFailure;

    public DnsQueryException(string message, DnsQueryErrorCode code) : base(message)
    {
        Code = code;
    }
}

internal enum DnsQueryErrorCode
{
    Success,
    Timeout,
    ServerFailure,
    FormatError,
    NameError,
    NotImplemented,
    Refused,
    Unknown
}

internal sealed class DnsQueryResult
{
    public string Host { get; set; } = string.Empty;
    public bool IsWildcard { get; set; }
    public List<string> ARecords { get; set; } = new();
    public List<string> AaaaRecords { get; set; } = new();
    public List<string> CnameChain { get; set; } = new();
    public List<string> MxRecords { get; set; } = new();
    public List<string> NsRecords { get; set; } = new();
    public List<string> TxtRecords { get; set; } = new();
    public List<string> CaaRecords { get; set; } = new();

    public List<string> SerializeRawResponses()
    {
        var responses = new List<string>();
        foreach (var a in ARecords)
            responses.Add($"A: {a}");
        foreach (var aaaa in AaaaRecords)
            responses.Add($"AAAA: {aaaa}");
        foreach (var cname in CnameChain)
            responses.Add($"CNAME: {cname}");
        foreach (var mx in MxRecords)
            responses.Add($"MX: {mx}");
        foreach (var ns in NsRecords)
            responses.Add($"NS: {ns}");
        foreach (var txt in TxtRecords)
            responses.Add($"TXT: {txt}");
        foreach (var caa in CaaRecords)
            responses.Add($"CAA: {caa}");
        return responses;
    }
}

internal sealed class DnsObservation
{
    public string Host { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public DnsQueryResult QueryResult { get; set; } = new();
    public string Resolver { get; set; } = string.Empty;
    public List<string> RawResponses { get; set; } = new();
}
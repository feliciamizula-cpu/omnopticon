using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Proxies;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

using Argus.ProxyRegistryService;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.ProxyRegistryService");
builder.Services.AddProblemDetails();

var dbPath = builder.Configuration.GetConnectionString("proxydb")
    ?? builder.Configuration.GetConnectionString("sqlite")
    ?? "proxy-registry.db";

builder.Services.AddDbContext<ProxyRegistryDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<ProxyRegistry>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ProxyRegistryDbContext>();
    await dbContext.Database.EnsureCreatedAsync();

    var registry = scope.ServiceProvider.GetRequiredService<ProxyRegistry>();
    await registry.LoadFromDatabaseAsync(dbContext);
}

app.MapDefaultEndpoints();

app.MapGet("/proxies", async (ProxyRegistry registry, ProxyRegistryDbContext db, CancellationToken ct) =>
{
    var records = await db.Proxies.OrderBy(p => p.Url).ToListAsync(ct);
    return Results.Ok(records.Select(ToDto).ToArray());
});

app.MapGet("/proxies/{id:guid}", async (Guid id, ProxyRegistryDbContext db, CancellationToken ct) =>
{
    var record = await db.Proxies.FindAsync([id], cancellationToken: ct);
    return record is null ? Results.NotFound() : Results.Ok(ToDto(record));
});

app.MapPost("/proxies", async (
    CreateProxyRequest request,
    ProxyRegistry registry,
    ProxyRegistryDbContext db,
    IIntegrationEventPublisher events,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest("Url is required.");
    if (string.IsNullOrWhiteSpace(request.Protocol))
        return Results.BadRequest("Protocol is required.");

    var existing = await db.Proxies.FirstOrDefaultAsync(p => p.Url == request.Url, ct);
    if (existing is not null)
        return Results.Conflict(new { message = "A proxy with this URL already exists." });

    var record = new ProxyRecord
    {
        ProxyId = Guid.NewGuid(),
        Url = request.Url,
        Protocol = request.Protocol,
        Username = request.Username,
        Password = request.Password,
        Country = request.Country,
        City = request.City,
        IsActive = true,
        IsOnline = true,
        MaxRequestsPerSecond = request.MaxRequestsPerSecond,
        MaxConcurrentRequests = request.MaxConcurrentRequests,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    db.Proxies.Add(record);
    await db.SaveChangesAsync(ct);
    registry.AddOrUpdate(ToDto(record));

    await events.PublishAsync(
        new ProxyAdded(record.ProxyId, record.Url, record.Protocol),
        nameof(ProxyAdded),
        "Argus.ProxyRegistryService",
        cancellationToken: ct);

    return Results.Created($"/proxies/{record.ProxyId}", ToDto(record));
});

app.MapPut("/proxies/{id:guid}", async (
    Guid id,
    UpdateProxyRequest request,
    ProxyRegistry registry,
    ProxyRegistryDbContext db,
    IIntegrationEventPublisher events,
    CancellationToken ct) =>
{
    var record = await db.Proxies.FindAsync([id], cancellationToken: ct);
    if (record is null)
        return Results.NotFound();

    if (request.Url is not null) record.Url = request.Url;
    if (request.Protocol is not null) record.Protocol = request.Protocol;
    if (request.Username is not null) record.Username = request.Username;
    if (request.Password is not null) record.Password = request.Password;
    if (request.Country is not null) record.Country = request.Country;
    if (request.City is not null) record.City = request.City;
    if (request.IsActive.HasValue) record.IsActive = request.IsActive.Value;
    if (request.MaxRequestsPerSecond.HasValue) record.MaxRequestsPerSecond = request.MaxRequestsPerSecond.Value;
    if (request.MaxConcurrentRequests.HasValue) record.MaxConcurrentRequests = request.MaxConcurrentRequests.Value;
    record.UpdatedAt = DateTimeOffset.UtcNow;

    await db.SaveChangesAsync(ct);
    registry.AddOrUpdate(ToDto(record));

    return Results.Ok(ToDto(record));
});

app.MapDelete("/proxies/{id:guid}", async (
    Guid id,
    ProxyRegistry registry,
    ProxyRegistryDbContext db,
    IIntegrationEventPublisher events,
    CancellationToken ct) =>
{
    var record = await db.Proxies.FindAsync([id], cancellationToken: ct);
    if (record is null)
        return Results.NotFound();

    db.Proxies.Remove(record);
    await db.SaveChangesAsync(ct);
    registry.Remove(id);

    await events.PublishAsync(
        new ProxyRemoved(record.ProxyId, record.Url),
        nameof(ProxyRemoved),
        "Argus.ProxyRegistryService",
        cancellationToken: ct);

    return Results.Ok(new { deleted = true });
});

app.MapPost("/proxies/{id:guid}/status", async (
    Guid id,
    ProxyStatusRequest request,
    ProxyRegistry registry,
    ProxyRegistryDbContext db,
    IIntegrationEventPublisher events,
    CancellationToken ct) =>
{
    var record = await db.Proxies.FindAsync([id], cancellationToken: ct);
    if (record is null)
        return Results.NotFound();

    var oldStatus = record.IsOnline ? "online" : "offline";
    record.IsOnline = request.IsOnline;
    record.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    registry.AddOrUpdate(ToDto(record));

    var newStatus = request.IsOnline ? "online" : "offline";
    if (oldStatus != newStatus)
    {
        await events.PublishAsync(
            new ProxyStatusChanged(record.ProxyId, record.Url, oldStatus, newStatus),
            nameof(ProxyStatusChanged),
            "Argus.ProxyRegistryService",
            cancellationToken: ct);
    }

    return Results.Ok(ToDto(record));
});

app.MapGet("/proxies/next", async (
    Guid? programId,
    string? workerType,
    string? country,
    string? protocol,
    ProxyRegistry registry,
    CancellationToken ct) =>
{
    var request = new ProxySelectionRequest(programId, workerType, country, protocol);
    var result = registry.SelectProxy(request);
    return result.Proxy is null
        ? Results.NotFound(new { message = "No available proxy found." })
        : Results.Ok(result);
});

app.MapGet("/proxies/{id:guid}/rate-limit", (Guid id, ProxyRegistry registry) =>
{
    var state = registry.GetRateLimitState(id);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

app.MapPost("/proxies/{id:guid}/consume", async (
    Guid id,
    ProxyRegistry registry,
    ProxyRegistryDbContext db,
    IIntegrationEventPublisher events,
    CancellationToken ct) =>
{
    var allowed = registry.TryConsume(id);
    if (!allowed)
    {
        var state = registry.GetRateLimitState(id);
        if (state is not null)
        {
            var proxy = await db.Proxies.FindAsync([id], ct);
            var proxyUrl = proxy?.Url ?? "";
            await events.PublishAsync(
                new ProxyRateLimitExceeded(id, proxyUrl, state.CurrentRequestsPerSecond),
                nameof(ProxyRateLimitExceeded),
                "Argus.ProxyRegistryService",
                cancellationToken: ct);
        }
        return Results.Conflict(new { message = "Rate limit exceeded for this proxy." });
    }

    return Results.Ok(new { consumed = true });
});

app.MapPost("/proxies/{id:guid}/release", (Guid id, ProxyRegistry registry) =>
{
    registry.Release(id);
    return Results.Ok(new { released = true });
});

app.Run();

static ProxyDto ToDto(ProxyRecord r) => ProxyRecord.ToDto(r);

internal sealed class ProxyRegistry
{
    private readonly ConcurrentDictionary<Guid, ProxyEntry> _proxies = new();
    private int _roundRobinIndex;

    public async Task LoadFromDatabaseAsync(ProxyRegistryDbContext db)
    {
        var records = await db.Proxies.ToListAsync();
        foreach (var record in records)
        {
            _proxies[record.ProxyId] = new ProxyEntry
            {
                Dto = ProxyRecord.ToDto(record),
                RateLimitBucket = new RateLimitBucket(
                    record.MaxRequestsPerSecond,
                    record.MaxConcurrentRequests),
                CurrentConcurrentRequests = 0
            };
        }
    }

    public void AddOrUpdate(ProxyDto dto)
    {
        var entry = _proxies.GetOrAdd(dto.ProxyId, _ => new ProxyEntry
        {
            Dto = dto,
            RateLimitBucket = new RateLimitBucket(
                dto.RateLimit.MaxRequestsPerSecond,
                dto.RateLimit.MaxConcurrentRequests),
            CurrentConcurrentRequests = 0
        });

        entry.Dto = dto;
        entry.RateLimitBucket.UpdateCapacity(
            dto.RateLimit.MaxRequestsPerSecond,
            dto.RateLimit.MaxConcurrentRequests);
    }

    public void Remove(Guid proxyId)
    {
        _proxies.TryRemove(proxyId, out _);
    }

    public ProxySelectionResult SelectProxy(ProxySelectionRequest request)
    {
        var candidates = _proxies.Values
            .Where(e => e.Dto.IsActive && e.Dto.IsOnline)
            .Where(e => e.RateLimitBucket.CanConsume())
            .Where(e => e.CurrentConcurrentRequests < e.Dto.RateLimit.MaxConcurrentRequests)
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(request.Country))
            candidates = candidates.Where(e =>
                string.Equals(e.Dto.Country, request.Country, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.Protocol))
            candidates = candidates.Where(e =>
                string.Equals(e.Dto.Protocol, request.Protocol, StringComparison.OrdinalIgnoreCase));

        var list = candidates
            .OrderBy(e => e.CurrentConcurrentRequests)
            .ThenBy(e => e.RateLimitBucket.CurrentRequestsPerSecond)
            .ToList();

        if (list.Count == 0)
        {
            return new ProxySelectionResult(
                null,
                _proxies.Values
                    .Where(e => e.Dto.IsActive && e.Dto.IsOnline)
                    .Select(e => e.Dto)
                    .ToArray());
        }

        var index = Interlocked.Increment(ref _roundRobinIndex);
        var selected = list[index % list.Count];

        selected.CurrentConcurrentRequests++;
        selected.RateLimitBucket.Consume();

        return new ProxySelectionResult(
            selected.Dto,
            list.Select(e => e.Dto).ToArray());
    }

    public bool TryConsume(Guid proxyId)
    {
        if (!_proxies.TryGetValue(proxyId, out var entry))
            return false;

        if (!entry.RateLimitBucket.CanConsume())
            return false;

        entry.RateLimitBucket.Consume();
        entry.CurrentConcurrentRequests++;
        return true;
    }

    public void Release(Guid proxyId)
    {
        if (_proxies.TryGetValue(proxyId, out var entry))
        {
            entry.CurrentConcurrentRequests = Math.Max(0, entry.CurrentConcurrentRequests - 1);
        }
    }

    public ProxyRateLimitDto? GetRateLimitState(Guid proxyId)
    {
        if (!_proxies.TryGetValue(proxyId, out var entry))
            return null;

        return new ProxyRateLimitDto(
            entry.Dto.RateLimit.MaxRequestsPerSecond,
            entry.Dto.RateLimit.MaxConcurrentRequests,
            entry.RateLimitBucket.CurrentRequestsPerSecond,
            entry.CurrentConcurrentRequests,
            entry.RateLimitBucket.ResetsAt);
    }

    private sealed class ProxyEntry
    {
        public ProxyDto Dto { get; set; } = null!;
        public RateLimitBucket RateLimitBucket { get; set; } = null!;
        public int CurrentConcurrentRequests { get; set; }
    }

    private sealed class RateLimitBucket
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
        private readonly object _gate = new();

        private int _maxRequestsPerSecond;
        private int _maxConcurrent;
        private int _remaining;
        private DateTimeOffset _resetsAt;

        public RateLimitBucket(int maxRequestsPerSecond, int maxConcurrent)
        {
            _maxRequestsPerSecond = maxRequestsPerSecond;
            _maxConcurrent = maxConcurrent;
            _remaining = maxRequestsPerSecond;
            _resetsAt = DateTimeOffset.UtcNow.Add(Window);
        }

        public int CurrentRequestsPerSecond
        {
            get
            {
                lock (_gate)
                {
                    RefreshIfExpired();
                    return _maxRequestsPerSecond - _remaining;
                }
            }
        }

        public DateTimeOffset? ResetsAt
        {
            get
            {
                lock (_gate)
                {
                    RefreshIfExpired();
                    return _resetsAt;
                }
            }
        }

        public void UpdateCapacity(int maxRequestsPerSecond, int maxConcurrent)
        {
            lock (_gate)
            {
                _maxRequestsPerSecond = maxRequestsPerSecond;
                _maxConcurrent = maxConcurrent;
                if (_remaining > maxRequestsPerSecond)
                    _remaining = maxRequestsPerSecond;
            }
        }

        public bool CanConsume()
        {
            lock (_gate)
            {
                RefreshIfExpired();
                return _remaining > 0;
            }
        }

        public void Consume()
        {
            lock (_gate)
            {
                RefreshIfExpired();
                if (_remaining > 0)
                    _remaining--;
            }
        }

        private void RefreshIfExpired()
        {
            var now = DateTimeOffset.UtcNow;
            if (_resetsAt <= now)
            {
                _remaining = _maxRequestsPerSecond;
                _resetsAt = now.Add(Window);
            }
        }
    }
}

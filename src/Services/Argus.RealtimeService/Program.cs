using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Argus.RealtimeService;
using Argus.RealtimeService.Workers;
using Argus.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

var realtimeDbConnStr = builder.Configuration.GetConnectionString("realtimedb")
    ?? builder.Configuration.GetConnectionString("argusdb");
var sqliteDbPath = builder.Configuration.GetConnectionString("sqlite") ?? "realtime.db";

if (!string.IsNullOrWhiteSpace(realtimeDbConnStr) && realtimeDbConnStr.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContextFactory<RealtimeDbContext>(options =>
        options.UseNpgsql(realtimeDbConnStr));
}
else
{
    builder.Services.AddDbContextFactory<RealtimeDbContext>(options =>
        options.UseSqlite($"Data Source={sqliteDbPath}"));
}

var webhookDbConnStr = builder.Configuration.GetConnectionString("argusdb");
if (!string.IsNullOrWhiteSpace(webhookDbConnStr))
{
    builder.Services.AddDbContext<WebhookDbContext>(options =>
        options.UseNpgsql(webhookDbConnStr));
}
else
{
    builder.Services.AddDbContext<WebhookDbContext>(options =>
        options.UseSqlite($"Data Source={sqliteDbPath}"));
}

builder.Services.AddSingleton<IPoisonMessageStore, InMemoryPoisonMessageStore>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connStr = config.GetConnectionString("messaging") ?? config.GetConnectionString("rabbitmq") ?? "";
    if (string.IsNullOrWhiteSpace(connStr)) return null!;
    var factory = new ConnectionFactory { Uri = new Uri(connStr) };
    return factory.CreateConnectionAsync().GetAwaiter().GetResult();
});
builder.Services.AddSingleton(sp =>
{
    var connection = sp.GetService<IConnection>();
    if (connection is null) return null!;
    return connection.CreateChannelAsync().GetAwaiter().GetResult();
});
builder.Services.AddSingleton<RealtimeStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<RealtimeStore>>();
    return new RealtimeStore(sp, logger);
});

builder.Services.AddHttpClient("webhook", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Argus.WebhookService/1.0");
    client.Timeout = TimeSpan.FromSeconds(60);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 10
});

builder.Services.AddHostedService<WebhookService>();

// Add worker services
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Workers"));
builder.Services.AddSingleton<IWorkerTypeCatalog, WorkerTypeCatalog>();
builder.Services.AddSingleton<IWorkerSummaryService, WorkerSummaryService>();
builder.Services.AddSingleton<IWorkerScaleSettingsService, WorkerScaleSettingsService>();
builder.Services.AddSingleton<IWorkerScaleCommandService, WorkerScaleCommandService>();
builder.Services.AddSingleton<IWorkerScaler, NoOpWorkerScaler>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

var app = builder.Build();

var dbContextFactory = app.Services.GetRequiredService<IDbContextFactory<RealtimeDbContext>>();
await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
{
    if (dbContext.Database.IsNpgsql())
    {
        await dbContext.EnsureRelationalSchemaCreatedAsync();
    }
    else
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var webhookDbContext = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();
    await webhookDbContext.Database.EnsureCreatedAsync();
}

var store = app.Services.GetRequiredService<RealtimeStore>();
await store.InitializeAsync(app.Services);

app.MapDefaultEndpoints();

app.MapGet("/events", (int? take, RealtimeStore store) => store.GetEvents(take ?? 200));

app.MapGet("/events/chain/{correlationId}", async (Guid correlationId, RealtimeStore store) =>
{
    var events = await store.GetEventsByCorrelationIdAsync(correlationId);
    return events.Count == 0 ? Results.NotFound() : Results.Ok(events);
});

app.MapGet("/events/stream", async (
    HttpContext context,
    RealtimeStore store,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    var subscription = store.Subscribe();

    try
    {
        foreach (var recentEvent in store.GetEvents(25).Reverse())
        {
            await SseWriter.WriteAsync(context, recentEvent, cancellationToken);
        }

        await foreach (var envelope in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            await SseWriter.WriteAsync(context, envelope, cancellationToken);
        }
    }
    finally
    {
        store.Unsubscribe(subscription.SubscriptionId);
    }
});

app.MapPost("/events", (EventIngestRequest request, RealtimeStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.EventType))
    {
        return Results.BadRequest("Event type is required.");
    }

    var envelope = store.RecordEvent(request);
    return Results.Accepted($"/events/{envelope.EventId}", envelope);
});

app.MapGet("/workers", (RealtimeStore store) => store.GetWorkers());

app.MapGet("/workers/capability/{assetType}", (string assetType, RealtimeStore store) =>
    store.GetWorkersForAssetType(assetType));

app.MapGet("/workers/{workerId}/capability", (string workerId, RealtimeStore store) =>
    store.GetWorkerCapability(workerId));

app.MapPost("/workers/register", (WorkerRegistrationRequest request, RealtimeStore store) =>
{
    var worker = store.Register(request);
    return Results.Created($"/workers/{worker.WorkerId}", worker);
});

app.MapPost("/workers/heartbeat", async (WorkerHeartbeatRequest request, RealtimeStore store, CancellationToken cancellationToken) =>
    await store.Heartbeat(request, cancellationToken));

var deadLetterConnection = app.Services.GetService<IConnection>();
var deadLetterChannel = app.Services.GetService<IChannel>();

if (deadLetterConnection is not null && deadLetterChannel is not null)
{
    app.MapGet("/admin/dead-letters", async (int? take, IPoisonMessageStore store, CancellationToken ct) =>
    {
        var messages = await store.GetMessagesAsync(take ?? 100, ct);
        return Results.Ok(messages);
    });

    app.MapGet("/admin/dead-letters/{eventId}", async (Guid eventId, IPoisonMessageStore store, CancellationToken ct) =>
    {
        var record = await store.GetMessageAsync(eventId, ct);
        return record is null ? Results.NotFound() : Results.Ok(record);
    });

    app.MapPost("/admin/dead-letters/{eventId}/replay", async (Guid eventId, IPoisonMessageStore store, IChannel channel, CancellationToken ct) =>
    {
        var record = await store.GetMessageAsync(eventId, ct);
        if (record is null) return Results.NotFound();

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = record.EventId.ToString(),
            CorrelationId = record.CorrelationId?.ToString() ?? "",
            Type = record.EventType
        };

        var body = Encoding.UTF8.GetBytes(record.PayloadJson);
        var routingKey = record.EventType;
        await channel.BasicPublishAsync("argus.integration.events", routingKey, false, properties, body, ct);

        await store.MarkReplayedAsync(eventId, ct);
        return Results.Ok(new { eventId, replayed = true });
    });

    app.MapDelete("/admin/dead-letters/{eventId}", async (Guid eventId, IPoisonMessageStore store, CancellationToken ct) =>
    {
        var removed = await store.RemoveMessageAsync(eventId, ct);
        return removed ? Results.Ok(new { removed = true }) : Results.NotFound();
    });
}

var webhooks = app.MapGroup("/webhooks").WithTags("Webhooks");

webhooks.MapGet("/", async (WebhookDbContext db) =>
{
    var configs = await db.WebhookConfigs.OrderBy(c => c.Name).ToListAsync();
    return Results.Ok(configs.Select(c => c.ToDto()).ToArray());
});

webhooks.MapGet("/{id:guid}", async (Guid id, WebhookDbContext db) =>
{
    var config = await db.WebhookConfigs.FindAsync(id);
    return config is null ? Results.NotFound() : Results.Ok(config.ToDto());
});

webhooks.MapPost("/", async (CreateWebhookRequest request, WebhookDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest("Name is required.");
    if (string.IsNullOrWhiteSpace(request.Url))
        return Results.BadRequest("Url is required.");
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "http" && uri.Scheme != "https"))
        return Results.BadRequest("Url must be a valid HTTP or HTTPS URL.");

    var config = request.ToEntity();
    db.WebhookConfigs.Add(config);
    await db.SaveChangesAsync();
    return Results.Created($"/webhooks/{config.Id}", config.ToDto());
});

webhooks.MapPut("/{id:guid}", async (Guid id, UpdateWebhookRequest request, WebhookDbContext db) =>
{
    var config = await db.WebhookConfigs.FindAsync(id);
    if (config is null)
        return Results.NotFound();

    if (request.Url is not null &&
        (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
         || (uri.Scheme != "http" && uri.Scheme != "https")))
    {
        return Results.BadRequest("Url must be a valid HTTP or HTTPS URL.");
    }

    config.ApplyUpdate(request);
    await db.SaveChangesAsync();
    return Results.Ok(config.ToDto());
});

webhooks.MapDelete("/{id:guid}", async (Guid id, WebhookDbContext db) =>
{
    var config = await db.WebhookConfigs.FindAsync(id);
    if (config is null)
        return Results.NotFound();

    db.WebhookConfigs.Remove(config);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = true });
});

webhooks.MapGet("/{id:guid}/logs", async (Guid id, int? take, int? skip, WebhookDbContext db) =>
{
    var query = db.WebhookDeliveryLogs
        .Where(l => l.WebhookConfigId == id)
        .OrderByDescending(l => l.AttemptedAt);

    var total = await query.CountAsync();
    var logs = await query
        .Skip(skip ?? 0)
        .Take(Math.Clamp(take ?? 50, 1, 500))
        .ToListAsync();

    return Results.Ok(new
    {
        total,
        logs = logs.Select(l => l.ToDto()).ToArray()
    });
});

// Worker endpoints
app.MapGet("/worker-types/summary", 
    async (IWorkerSummaryService service, CancellationToken ct) =>
        Results.Ok(await service.GetSummaryAsync(ct)));

app.MapGet("/worker-types/{workerType}", 
    async (string workerType, IWorkerSummaryService service, CancellationToken ct) =>
    {
        var row = await service.GetWorkerTypeAsync(workerType, ct);
        return row is null ? Results.NotFound() : Results.Ok(row);
    });

app.MapGet("/worker-types/{workerType}/instances", 
    async (string workerType, IWorkerSummaryService service, CancellationToken ct) =>
        Results.Ok(await service.GetInstancesAsync(workerType, ct)));

app.MapPut("/worker-types/{workerType}/scale-settings", 
    async (
        string workerType,
        UpdateWorkerScaleSettingsRequest request,
        IWorkerScaleSettingsService service,
        HttpContext http,
        CancellationToken ct) =>
    {
        var actor = http.User?.Identity?.Name;
        var result = await service.UpdateAsync(workerType, request, actor, ct);
        return Results.Ok(result);
    });

app.MapPost("/worker-types/{workerType}/scale", 
    async (
        string workerType,
        ScaleWorkerTypeRequest request,
        IWorkerScaleCommandService service,
        HttpContext http,
        CancellationToken ct) =>
    {
        var actor = http.User?.Identity?.Name;
        var result = await service.ScaleAsync(workerType, request.DesiredReplicas, "ScaleSet", request.Reason, actor, ct);
        return Results.Ok(result);
    });

app.MapPost("/worker-types/{workerType}/scale-up", 
    async (
        string workerType,
        IWorkerScaleSettingsService settings,
        IWorkerScaleCommandService commands,
        HttpContext http,
        CancellationToken ct) =>
    {
        var current = await settings.GetOrCreateAsync(workerType, ct);
        var target = Math.Min(current.DesiredReplicas + 1, current.MaxReplicas);
        var actor = http.User?.Identity?.Name;
        var result = await commands.ScaleAsync(workerType, target, "ScaleUp", null, actor, ct);
        return Results.Ok(result);
    });

app.MapPost("/worker-types/{workerType}/scale-down", 
    async (
        string workerType,
        IWorkerScaleSettingsService settings,
        IWorkerScaleCommandService commands,
        HttpContext http,
        CancellationToken ct) =>
    {
        var current = await settings.GetOrCreateAsync(workerType, ct);
        var target = Math.Max(current.DesiredReplicas - 1, current.MinReplicas);
        var actor = http.User?.Identity?.Name;
        var result = await commands.ScaleAsync(workerType, target, "ScaleDown", null, actor, ct);
        return Results.Ok(result);
    });

app.MapPost("/worker-types/{workerType}/pause", 
    async (
        string workerType,
        IWorkerScaleSettingsService settings,
        IWorkerScaleCommandService commands,
        HttpContext http,
        CancellationToken ct) =>
    {
        var current = await settings.GetOrCreateAsync(workerType, ct);
        if (current.MinReplicas > 0)
        {
            return Results.BadRequest(new
            {
                message = $"Cannot pause {workerType}; MinReplicas is {current.MinReplicas}."
            });
        }

        await settings.UpdateAsync(workerType, new UpdateWorkerScaleSettingsRequest(
            DesiredReplicas: 0,
            MinReplicas: current.MinReplicas,
            MaxReplicas: current.MaxReplicas,
            IsPaused: true,
            DeploymentName: current.DeploymentName,
            Namespace: current.Namespace), http.User?.Identity?.Name, ct);

        var result = await commands.ScaleAsync(workerType, 0, "Pause", null, http.User?.Identity?.Name, ct);
        return Results.Ok(result);
    });

app.MapPost("/worker-types/{workerType}/resume", 
    async (
        string workerType,
        IWorkerScaleSettingsService settings,
        IWorkerScaleCommandService commands,
        HttpContext http,
        CancellationToken ct) =>
    {
        var current = await settings.GetOrCreateAsync(workerType, ct);
        var target = current.DesiredReplicas <= 0 ? Math.Max(1, current.MinReplicas) : current.DesiredReplicas;

        await settings.UpdateAsync(workerType, new UpdateWorkerScaleSettingsRequest(
            DesiredReplicas: target,
            MinReplicas: current.MinReplicas,
            MaxReplicas: current.MaxReplicas,
            IsPaused: false,
            DeploymentName: current.DeploymentName,
            Namespace: current.Namespace), http.User?.Identity?.Name, ct);

        var result = await commands.ScaleAsync(workerType, target, "Resume", null, http.User?.Identity?.Name, ct);
        return Results.Ok(result);
    });

app.MapGet("/worker-scale-commands", 
    async (int? take, IWorkerScaleCommandService service, CancellationToken ct) =>
        Results.Ok(await service.GetRecentCommandsAsync(Math.Clamp(take ?? 100, 1, 500), ct)));

app.Run();

internal sealed class RealtimeStore
{
    private readonly ConcurrentQueue<IntegrationEventEnvelope<JsonNode>> _events = new();
    private readonly ConcurrentDictionary<string, WorkerStatusDto> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorkerCapabilityDescriptor> _workerCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Channel<IntegrationEventEnvelope<JsonNode>>> _subscriptions = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<RealtimeStore> _logger;
    private readonly ConcurrentQueue<WorkerRecord> _pendingWorkers = new();

    public RealtimeStore(IServiceProvider services, ILogger<RealtimeStore> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task InitializeAsync(IServiceProvider services)
    {
        var dbContextFactory = services.GetRequiredService<IDbContextFactory<RealtimeDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var recentEvents = await dbContext.Events
            .OrderByDescending(e => e.RecordedAt)
            .Take(5_000)
            .ToListAsync();

        foreach (var evt in recentEvents.AsEnumerable().Reverse())
        {
            var payload = string.IsNullOrWhiteSpace(evt.PayloadJson)
                ? new JsonObject()
                : JsonNode.Parse(evt.PayloadJson) ?? new JsonObject();

            var envelope = IntegrationEventEnvelope<JsonNode>.Create(
                payload,
                evt.EventType,
                evt.SourceService ?? "unknown",
                correlationId: evt.CorrelationId,
                causationId: evt.CausationId).WithEventId(evt.EventId);
            _events.Enqueue(envelope);
        }

        var workers = await dbContext.Workers.ToListAsync();
        foreach (var worker in workers)
        {
            _workers[worker.WorkerId] = new WorkerStatusDto(
                worker.WorkerId,
                worker.WorkerType,
                worker.Version,
                worker.RunningTasks,
                worker.MaxConcurrency,
                worker.LastSeenAt,
                worker.IsOnline);
        }

        var capabilities = await dbContext.WorkerCapabilities.ToListAsync();
        foreach (var cap in capabilities)
        {
            var types = string.IsNullOrWhiteSpace(cap.SubscribedAssetTypes)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(cap.SubscribedAssetTypes) ?? Array.Empty<string>();

            _workerCapabilities[cap.WorkerId] = new WorkerCapabilityDescriptor(
                cap.WorkerType,
                types,
                [],
                RequiresHttp: false,
                SupportsCheckpoint: false,
                cap.MaxConcurrency);
        }
    }

    public IReadOnlyCollection<IntegrationEventEnvelope<JsonNode>> GetEvents(int take) =>
        _events
            .Reverse()
            .Take(Math.Clamp(take, 1, 1_000))
            .ToArray();

    public IntegrationEventEnvelope<JsonNode> RecordEvent(EventIngestRequest request)
    {
        var payload = string.IsNullOrWhiteSpace(request.PayloadJson)
            ? new JsonObject()
            : JsonNode.Parse(request.PayloadJson) ?? new JsonObject();

        var envelope = IntegrationEventEnvelope<JsonNode>.Create(
            payload,
            request.EventType.Trim(),
            request.SourceService ?? "unknown",
            correlationId: request.CorrelationId,
            causationId: request.CausationId);

        var record = new EventRecord
        {
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            SourceService = envelope.SourceService,
            RecordedAt = envelope.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            PayloadJson = request.PayloadJson
        };

        try
        {
            using var scope = _services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();
            dbContext.Events.Add(record);
            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist event {EventId}", record.EventId);
        }

        _events.Enqueue(envelope);

        while (_events.Count > 5_000 && _events.TryDequeue(out _))
        {
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Writer.TryWrite(envelope);
        }

        return envelope;
    }

    public EventSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<IntegrationEventEnvelope<JsonNode>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Guid.NewGuid();

        _subscriptions[subscriptionId] = channel;

        return new EventSubscription(subscriptionId, channel.Reader);
    }

    public void Unsubscribe(Guid subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public WorkerStatusDto Register(WorkerRegistrationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var worker = new WorkerStatusDto(
            request.WorkerId,
            request.Capability.WorkerType,
            request.Version,
            RunningTasks: 0,
            request.Capability.MaxConcurrency,
            now,
            IsOnline: true);

        _workers[request.WorkerId] = worker;
        _workerCapabilities[request.WorkerId] = request.Capability;

        try
        {
            using var scope = _services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();

            var workerRecord = dbContext.Workers.FirstOrDefault(x => x.WorkerId == request.WorkerId);
            if (workerRecord is null)
            {
                dbContext.Workers.Add(new WorkerRecord
                {
                    WorkerId = worker.WorkerId,
                    WorkerType = worker.WorkerType,
                    Version = worker.Version,
                    RunningTasks = worker.RunningTasks,
                    MaxConcurrency = worker.MaxConcurrency,
                    LastSeenAt = now,
                    IsOnline = true
                });
            }
            else
            {
                workerRecord.WorkerType = worker.WorkerType;
                workerRecord.Version = worker.Version;
                workerRecord.RunningTasks = worker.RunningTasks;
                workerRecord.MaxConcurrency = worker.MaxConcurrency;
                workerRecord.LastSeenAt = now;
                workerRecord.IsOnline = true;
            }

            var capabilityRecord = dbContext.WorkerCapabilities.FirstOrDefault(x => x.WorkerId == request.WorkerId);
            if (capabilityRecord is null)
            {
                dbContext.WorkerCapabilities.Add(new WorkerCapabilityRecord
                {
                    WorkerId = request.WorkerId,
                    WorkerType = request.Capability.WorkerType,
                    MaxConcurrency = request.Capability.MaxConcurrency,
                    SubscribedAssetTypes = JsonSerializer.Serialize(request.Capability.SubscribedAssetTypes)
                });
            }
            else
            {
                capabilityRecord.WorkerType = request.Capability.WorkerType;
                capabilityRecord.MaxConcurrency = request.Capability.MaxConcurrency;
                capabilityRecord.SubscribedAssetTypes = JsonSerializer.Serialize(request.Capability.SubscribedAssetTypes);
            }

            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist worker {WorkerId}", request.WorkerId);
        }

        return worker;
    }

    public async Task<WorkerStatusDto> Heartbeat(WorkerHeartbeatRequest request, CancellationToken cancellationToken = default)
    {
        var worker = _workers.AddOrUpdate(
            request.WorkerId,
            _ => new WorkerStatusDto(
                request.WorkerId,
                request.WorkerType,
                Version: null,
                request.RunningTasks,
                request.MaxConcurrency,
                request.SeenAt,
                IsOnline: true),
            (_, existing) => existing with
            {
                RunningTasks = request.RunningTasks,
                MaxConcurrency = request.MaxConcurrency,
                LastSeenAt = request.SeenAt,
                IsOnline = true
            });

        try
        {
            using var scope = _services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();
            var workerRecord = dbContext.Workers.FirstOrDefault(x => x.WorkerId == request.WorkerId);

            if (workerRecord is null)
            {
                dbContext.Workers.Add(new WorkerRecord
                {
                    WorkerId = request.WorkerId,
                    WorkerType = request.WorkerType,
                    RunningTasks = request.RunningTasks,
                    MaxConcurrency = request.MaxConcurrency,
                    LastSeenAt = request.SeenAt,
                    IsOnline = true
                });
            }
            else
            {
                workerRecord.WorkerType = request.WorkerType;
                workerRecord.RunningTasks = request.RunningTasks;
                workerRecord.MaxConcurrency = request.MaxConcurrency;
                workerRecord.LastSeenAt = request.SeenAt;
                workerRecord.IsOnline = true;
            }

            dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist heartbeat for worker {WorkerId}", request.WorkerId);
        }

        RecordEvent(new EventIngestRequest(
            "WorkerHeartbeat",
            "Argus.RealtimeService",
            null,
            null,
            $"{{\"workerId\":\"{request.WorkerId}\",\"workerType\":\"{request.WorkerType}\",\"runningTasks\":{request.RunningTasks}}}"));

        return worker;
    }

    public IReadOnlyCollection<WorkerStatusDto> GetWorkers()
    {
        var now = DateTimeOffset.UtcNow;

        return _workers.Values
            .Select(worker => worker with { IsOnline = now - worker.LastSeenAt < TimeSpan.FromSeconds(45) })
            .OrderBy(worker => worker.WorkerType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(worker => worker.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<WorkerStatusDto> GetWorkersForAssetType(string assetType)
    {
        var now = DateTimeOffset.UtcNow;

        return _workerCapabilities
            .Where(kvp => kvp.Value.SubscribedAssetTypes.Contains(assetType, StringComparer.OrdinalIgnoreCase))
            .Select(kvp => _workers.TryGetValue(kvp.Key, out var worker)
                ? worker with { IsOnline = now - worker.LastSeenAt < TimeSpan.FromSeconds(45) }
                : null)
            .Where(worker => worker is not null)
            .OrderBy(worker => worker!.WorkerType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(worker => worker!.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public WorkerCapabilityDescriptor? GetWorkerCapability(string workerId)
    {
        return _workerCapabilities.TryGetValue(workerId, out var capability) ? capability : null;
    }

    public async Task<IReadOnlyList<IntegrationEventEnvelope<JsonNode>>> GetEventsByCorrelationIdAsync(Guid correlationId)
    {
        var dbContextFactory = _services.GetRequiredService<IDbContextFactory<RealtimeDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var events = await dbContext.Events
            .Where(e => e.CorrelationId == correlationId)
            .OrderBy(e => e.RecordedAt)
            .ToListAsync();

        return events
            .Select(evt =>
            {
                var payload = string.IsNullOrWhiteSpace(evt.PayloadJson)
                    ? new JsonObject()
                    : JsonNode.Parse(evt.PayloadJson) ?? new JsonObject();

                return IntegrationEventEnvelope<JsonNode>.Create(
                    payload,
                    evt.EventType,
                    evt.SourceService ?? "unknown",
                    correlationId: evt.CorrelationId,
                    causationId: evt.CausationId).WithEventId(evt.EventId);
            })
            .ToArray();
    }
}

internal sealed record EventSubscription(
    Guid SubscriptionId,
    ChannelReader<IntegrationEventEnvelope<JsonNode>> Reader);

internal static class SseWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        IntegrationEventEnvelope<JsonNode> envelope,
        CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync($"id: {envelope.EventId}\n", cancellationToken);
        var sanitizedEventType = envelope.EventType.Replace("\r", "").Replace("\n", " ");
        await context.Response.WriteAsync($"event: {sanitizedEventType}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(envelope)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}

internal sealed record EventIngestRequest(
    string EventType,
    string? SourceService,
    Guid? CorrelationId,
    Guid? CausationId,
    string? PayloadJson);
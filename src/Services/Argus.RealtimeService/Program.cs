using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Events;
using Argus.Contracts.Workers;
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

var dbPath = builder.Configuration.GetConnectionString("realtimedb")
    ?? builder.Configuration.GetConnectionString("sqlite")
    ?? "realtime.db";

builder.Services.AddDbContext<RealtimeDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

var store = app.Services.GetRequiredService<RealtimeStore>();
await store.InitializeAsync(app.Services);

app.MapDefaultEndpoints();

app.MapGet("/events", (int? take, RealtimeStore store) => store.GetEvents(take ?? 200));

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

app.MapPost("/workers/heartbeat", (WorkerHeartbeatRequest request, RealtimeStore store) =>
    store.Heartbeat(request));

var deadLetterConnection = app.Services.GetService<IConnection>();
var deadLetterChannel = app.Services.GetService<IChannel>();

if (deadLetterConnection is not null && deadLetterChannel is not null)
{
    app.MapGet("/admin/dead-letters", (int? take, IPoisonMessageStore store) =>
    {
        return Results.Ok(store.GetMessages(take ?? 100));
    });

    app.MapGet("/admin/dead-letters/{eventId}", (Guid eventId, IPoisonMessageStore store) =>
    {
        var record = store.GetMessage(eventId);
        return record is null ? Results.NotFound() : Results.Ok(record);
    });

    app.MapPost("/admin/dead-letters/{eventId}/replay", async (Guid eventId, IPoisonMessageStore store, IChannel channel) =>
    {
        var record = store.GetMessage(eventId);
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
        await channel.BasicPublishAsync("argus.integration.events", routingKey, false, properties, body);

        store.MarkReplayed(eventId);
        return Results.Ok(new { eventId, replayed = true });
    });

    app.MapDelete("/admin/dead-letters/{eventId}", (Guid eventId, IPoisonMessageStore store) =>
    {
        var removed = store.RemoveMessage(eventId);
        return removed ? Results.Ok(new { removed = true }) : Results.NotFound();
    });
}

app.Run();

internal sealed class RealtimeStore
{
    private readonly ConcurrentQueue<IntegrationEventEnvelope<JsonNode>> _events = new();
    private readonly ConcurrentDictionary<string, WorkerStatusDto> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorkerCapabilityDescriptor> _workerCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Channel<IntegrationEventEnvelope<JsonNode>>> _subscriptions = new();
    private readonly ConcurrentQueue<EventRecord> _pendingEvents = new();
    private readonly ConcurrentQueue<WorkerRecord> _pendingWorkers = new();
    private readonly IServiceProvider _services;
    private readonly ILogger<RealtimeStore> _logger;

    public RealtimeStore(IServiceProvider services, ILogger<RealtimeStore> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();

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
                evt.CorrelationId,
                evt.CausationId).WithEventId(evt.EventId);
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
                cap.MaxConcurrency,
                types);
        }

        _ = Task.Run(PersistWorkerLoop);
    }

    private async Task PersistWorkerLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(1_000);

                if (_pendingWorkers.IsEmpty && _pendingEvents.IsEmpty)
                    continue;

                using var scope = _services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<RealtimeDbContext>();

                var workersToSave = new List<WorkerRecord>();
                while (_pendingWorkers.TryDequeue(out var worker))
                {
                    workersToSave.Add(worker);
                }

                if (workersToSave.Count != 0)
                {
                    foreach (var worker in workersToSave)
                    {
                        var existing = await dbContext.Workers.FindAsync(worker.WorkerId);
                        if (existing is null)
                        {
                            dbContext.Workers.Add(worker);
                        }
                        else
                        {
                            existing.WorkerType = worker.WorkerType;
                            existing.Version = worker.Version;
                            existing.RunningTasks = worker.RunningTasks;
                            existing.MaxConcurrency = worker.MaxConcurrency;
                            existing.LastSeenAt = worker.LastSeenAt;
                            existing.IsOnline = worker.IsOnline;
                        }

                        var existingCap = await dbContext.WorkerCapabilities.FindAsync(worker.WorkerId);
                        if (existingCap is null && _workerCapabilities.TryGetValue(worker.WorkerId, out var cap))
                        {
                            dbContext.WorkerCapabilities.Add(new WorkerCapabilityRecord
                            {
                                WorkerId = worker.WorkerId,
                                WorkerType = cap.WorkerType,
                                MaxConcurrency = cap.MaxConcurrency,
                                SubscribedAssetTypes = JsonSerializer.Serialize(cap.SubscribedAssetTypes)
                            });
                        }
                    }
                    await dbContext.SaveChangesAsync();
                }

                var eventsToSave = new List<EventRecord>();
                while (_pendingEvents.TryDequeue(out var evt))
                {
                    eventsToSave.Add(evt);
                }

                if (eventsToSave.Count != 0)
                {
                    await dbContext.Events.AddRangeAsync(eventsToSave);
                    await dbContext.SaveChangesAsync();
                }

                while (_events.Count > 5_000 && _events.TryDequeue(out _))
                {
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in persist worker loop");
            }
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
            request.CorrelationId,
            request.CausationId);

        _events.Enqueue(envelope);

        _pendingEvents.Enqueue(new EventRecord
        {
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            SourceService = envelope.SourceService,
            RecordedAt = envelope.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            PayloadJson = request.PayloadJson
        });

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

        _pendingWorkers.Enqueue(new WorkerRecord
        {
            WorkerId = worker.WorkerId,
            WorkerType = worker.WorkerType,
            Version = worker.Version,
            RunningTasks = worker.RunningTasks,
            MaxConcurrency = worker.MaxConcurrency,
            LastSeenAt = now,
            IsOnline = true
        });

        return worker;
    }

    public WorkerStatusDto Heartbeat(WorkerHeartbeatRequest request)
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

        RecordEvent(new EventIngestRequest(
            "WorkerHeartbeat",
            "Argus.RealtimeService",
            null,
            null,
            $"{{\"workerId\":\"{request.WorkerId}\",\"workerType\":\"{request.WorkerType}\"}}"));

        _pendingWorkers.Enqueue(new WorkerRecord
        {
            WorkerId = request.WorkerId,
            WorkerType = request.WorkerType,
            Version = null,
            RunningTasks = request.RunningTasks,
            MaxConcurrency = request.MaxConcurrency,
            LastSeenAt = request.SeenAt,
            IsOnline = true
        });

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
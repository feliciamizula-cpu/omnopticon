using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<RealtimeStore>();

var app = builder.Build();

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

app.MapPost("/workers/register", (WorkerRegistrationRequest request, RealtimeStore store) =>
{
    var worker = store.Register(request);
    return Results.Created($"/workers/{worker.WorkerId}", worker);
});

app.MapPost("/workers/heartbeat", (WorkerHeartbeatRequest request, RealtimeStore store) =>
    store.Heartbeat(request));

app.Run();

internal sealed class RealtimeStore
{
    private readonly ConcurrentQueue<IntegrationEventEnvelope<JsonNode>> _events = new();
    private readonly ConcurrentDictionary<string, WorkerStatusDto> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Channel<IntegrationEventEnvelope<JsonNode>>> _subscriptions = new();

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
        await context.Response.WriteAsync($"event: {envelope.EventType}\n", cancellationToken);
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

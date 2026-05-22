using Argus.Contracts.Events;
using Argus.Contracts.Workers;
using Argus.ServiceDefaults;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<RealtimeStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/events", (int? take, RealtimeStore store) => store.GetEvents(take ?? 200));

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
    private readonly ConcurrentQueue<IntegrationEventEnvelope<object>> _events = new();
    private readonly ConcurrentDictionary<string, WorkerStatusDto> _workers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IntegrationEventEnvelope<object>> GetEvents(int take) =>
        _events
            .Reverse()
            .Take(Math.Clamp(take, 1, 1_000))
            .ToArray();

    public IntegrationEventEnvelope<object> RecordEvent(EventIngestRequest request)
    {
        var envelope = IntegrationEventEnvelope<object>.Create(
            request.PayloadJson is null ? new { } : new { request.PayloadJson },
            request.EventType.Trim(),
            request.SourceService ?? "unknown",
            request.CorrelationId,
            request.CausationId);

        _events.Enqueue(envelope);

        while (_events.Count > 5_000 && _events.TryDequeue(out _))
        {
        }

        return envelope;
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

internal sealed record EventIngestRequest(
    string EventType,
    string? SourceService,
    Guid? CorrelationId,
    Guid? CausationId,
    string? PayloadJson);

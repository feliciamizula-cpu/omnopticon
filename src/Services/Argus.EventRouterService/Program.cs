using System.Collections.Concurrent;
using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<EventRouteStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/event-router", () => Results.Ok(new
{
    Name = "Argus Event Router",
    Version = "1.0",
    Description = "Stores and exposes event routing configuration."
}));

app.MapGet("/event-routes", (EventRouteStore store) => Results.Ok(store.GetAll()));

app.MapGet("/event-routes/{routeId}", (string routeId, EventRouteStore store) =>
{
    var route = store.Find(routeId);
    return route is not null ? Results.Ok(route) : Results.NotFound();
});

app.MapPost("/event-routes", (UpsertEventRouteRequest request, EventRouteStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.EventType))
    {
        return Results.BadRequest("EventType is required.");
    }

    if (string.IsNullOrWhiteSpace(request.Destination))
    {
        return Results.BadRequest("Destination is required.");
    }

    var route = store.Upsert(request);
    return Results.Created($"/event-routes/{route.RouteId}", route);
});

app.MapDelete("/event-routes/{routeId}", (string routeId, EventRouteStore store) =>
    store.Delete(routeId) ? Results.NoContent() : Results.NotFound());

app.Run();

internal sealed class EventRouteStore
{
    private readonly ConcurrentDictionary<string, EventRouteDto> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<EventRouteDto> GetAll() =>
        _routes.Values
            .OrderBy(route => route.EventType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Destination, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public EventRouteDto? Find(string routeId) =>
        _routes.TryGetValue(routeId, out var route) ? route : null;

    public EventRouteDto Upsert(UpsertEventRouteRequest request)
    {
        var routeId = string.IsNullOrWhiteSpace(request.RouteId)
            ? $"{request.EventType}:{request.Destination}".ToLowerInvariant()
            : request.RouteId.Trim();

        var route = new EventRouteDto(
            RouteId: routeId,
            EventType: request.EventType.Trim(),
            Destination: request.Destination.Trim(),
            Enabled: request.Enabled ?? true,
            CreatedOrUpdatedAt: DateTimeOffset.UtcNow);

        _routes[route.RouteId] = route;
        return route;
    }

    public bool Delete(string routeId) => _routes.TryRemove(routeId, out _);
}

internal sealed record UpsertEventRouteRequest(
    string? RouteId,
    string EventType,
    string Destination,
    bool? Enabled);

internal sealed record EventRouteDto(
    string RouteId,
    string EventType,
    string Destination,
    bool Enabled,
    DateTimeOffset CreatedOrUpdatedAt);

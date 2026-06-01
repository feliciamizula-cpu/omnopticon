namespace Argus.Web;

using Microsoft.AspNetCore.SignalR;

public static class DevelopmentRealtime
{
    public const string GroupName = "development";
    public const string EventName = "DevelopmentDataChanged";
}

public sealed record DevelopmentDataChangedEvent(
    string Area,
    string Reason,
    DateTimeOffset ObservedAt,
    AssetDelta? Delta = null);

public sealed record AssetDelta(
    string Action,
    Guid AssetId,
    string Type,
    string Value,
    double? RiskScore,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    Guid ProgramId = default);

public sealed class ArgusHub : Hub
{
    public async Task JoinDevelopment()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, DevelopmentRealtime.GroupName);
    }
}

public sealed class DevelopmentRealtimeNotifier(IHubContext<ArgusHub> hubContext)
{
    public Task NotifyAsync(string area, string reason, CancellationToken cancellationToken = default)
    {
        var change = new DevelopmentDataChangedEvent(area, reason, DateTimeOffset.UtcNow);
        return hubContext.Clients
            .Group(DevelopmentRealtime.GroupName)
            .SendAsync(DevelopmentRealtime.EventName, change, cancellationToken);
    }

    public Task NotifyAssetDeltaAsync(AssetDelta delta, CancellationToken cancellationToken = default)
    {
        var change = new DevelopmentDataChangedEvent("assets", delta.Action, DateTimeOffset.UtcNow, delta);
        return hubContext.Clients
            .Group(DevelopmentRealtime.GroupName)
            .SendAsync(DevelopmentRealtime.EventName, change, cancellationToken);
    }
}

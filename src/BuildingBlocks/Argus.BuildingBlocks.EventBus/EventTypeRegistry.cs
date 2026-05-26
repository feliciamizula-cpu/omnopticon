using System.Text.Json;
using Argus.Contracts.Events;

namespace Argus.BuildingBlocks.EventBus;

public interface IEventTypeRegistry
{
    Type GetPayloadType(string eventType);
    string GetEventTypeName(Type payloadType);
    string GetVersionedEventTypeName(Type payloadType, int version);
    (string EventTypeName, int Version) ParseEventType(string versionedEventType);
}

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _eventTypeToPayload = new();
    private readonly Dictionary<Type, string> _payloadToEventType = new();
    private readonly Dictionary<string, int> _eventTypeToVersion = new();
    private readonly Dictionary<Type, int> _payloadToVersion = new();

    public EventTypeRegistry()
    {
        Register<AssetDiscovered>();
        Register<AssetConfirmed>();
        Register<AssetUpdated>();
        Register<AssetRelationshipDiscovered>();
        Register<TaskRequested>();
        Register<TaskLeased>();
        Register<TaskStarted>();
        Register<TaskProgressed>();
        Register<TaskCompleted>();
        Register<TaskFailed>();
        Register<ProgramCreated>();
        Register<ScopeCreated>();
        Register<RateLimitTokenGranted>();
        Register<RateLimitDelayed>();
        Register<RateLimitBackpressureSignaled>();
        Register<WorkerHeartbeat>();
        Register<ProgramScopeChanged>();
        Register<AssetPropertyChanged>();
        Register<FindingCandidateCreated>();
        Register<ProxyAdded>();
        Register<ProxyRemoved>();
        Register<ProxyStatusChanged>();
        Register<ProxyRateLimitExceeded>();
        Register<ArtifactCreated>();
        Register<EvidenceAdded>();
        Register<FindingCreated>();
        Register<FindingUpdated>();
        Register<FindingTriaged>();
    }

    private void Register<T>(int version = 1) where T : notnull
    {
        var type = typeof(T);
        var eventType = typeof(T).Name;
        var versionedEventType = version == 1 ? eventType : $"{eventType}/v{version}";
        _eventTypeToPayload[eventType] = type;
        _payloadToEventType[type] = eventType;
        if (version > 1)
        {
            _eventTypeToVersion[eventType] = version;
            _payloadToVersion[type] = version;
        }
    }

    public Type GetPayloadType(string eventType)
    {
        var (baseEventType, _) = ParseEventType(eventType);
        return _eventTypeToPayload.TryGetValue(baseEventType, out var type) ? type : typeof(JsonElement);
    }

    public string GetEventTypeName(Type payloadType)
    {
        return _payloadToEventType.TryGetValue(payloadType, out var name) ? name : throw new ArgumentException($"Unknown payload type: {payloadType}");
    }

    public string GetVersionedEventTypeName(Type payloadType, int version)
    {
        var baseEventType = GetEventTypeName(payloadType);
        return version == 1 ? baseEventType : $"{baseEventType}/v{version}";
    }

    public (string EventTypeName, int Version) ParseEventType(string versionedEventType)
    {
        if (versionedEventType.Contains("/v"))
        {
            var lastSlash = versionedEventType.LastIndexOf('/');
            var eventTypeName = versionedEventType[..lastSlash];
            var versionStr = versionedEventType[(lastSlash + 2)..];
            if (int.TryParse(versionStr, out var version))
            {
                return (eventTypeName, version);
            }
        }
        return (versionedEventType, 1);
    }
}
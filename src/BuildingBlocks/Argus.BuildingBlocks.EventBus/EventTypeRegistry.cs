namespace Argus.BuildingBlocks.EventBus;

public interface IEventTypeRegistry
{
    Type GetPayloadType(string eventType);
    string GetEventTypeName(Type payloadType);
}

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly Dictionary<string, Type> _eventTypeToPayload = new();
    private readonly Dictionary<Type, string> _payloadToEventType = new();

    public EventTypeRegistry()
    {
        Register<AssetDiscovered>();
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
        Register<WorkerHeartbeat>();
        Register<ProgramScopeChanged>();
    }

    private void Register<T>() where T : notnull
    {
        var type = typeof(T);
        var eventType = typeof(T).Name;
        _eventTypeToPayload[eventType] = type;
        _payloadToEventType[type] = eventType;
    }

    public Type GetPayloadType(string eventType)
    {
        return _eventTypeToPayload.TryGetValue(eventType, out var type) ? type : typeof(JsonElement);
    }

    public string GetEventTypeName(Type payloadType)
    {
        return _payloadToEventType.TryGetValue(payloadType, out var name) ? name : throw new ArgumentException($"Unknown payload type: {payloadType}");
    }
}
using Argus.Contracts.Events;
using System.Collections.Concurrent;

namespace Argus.BuildingBlocks.EventBus;

public sealed class InMemoryIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ConcurrentQueue<object> _events = new();

    public Task PublishAsync<T>(
        IntegrationEventEnvelope<T> envelope,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        _events.Enqueue(envelope);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<object> Snapshot() => _events.ToArray();
}

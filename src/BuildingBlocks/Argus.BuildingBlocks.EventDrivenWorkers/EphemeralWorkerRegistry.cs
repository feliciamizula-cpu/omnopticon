using Argus.Contracts.Workers;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Argus.BuildingBlocks.EventDrivenWorkers;

public sealed class EphemeralWorkerRegistry
{
    private readonly ConcurrentDictionary<string, WorkerRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;

    public EphemeralWorkerRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Register<TWorker>() where TWorker : class, IEphemeralWorker
    {
        var worker = _serviceProvider.GetRequiredService<TWorker>();
        var descriptor = worker.Descriptor;

        var registration = new WorkerRegistration(
            typeof(TWorker),
            descriptor);

        foreach (var eventType in descriptor.SubscribedEvents)
        {
            foreach (var assetType in descriptor.SubscribedAssetTypes)
            {
                var key = $"{eventType}:{assetType}";
                _registrations.AddOrUpdate(
                    key,
                    _ => new WorkerRegistration(typeof(TWorker), descriptor, new List<Type> { typeof(TWorker) }),
                    (_, existing) => existing.WithWorkerType(typeof(TWorker)));
            }
        }
    }

    public IReadOnlyCollection<Type> GetWorkerTypesForEvent(string eventType, string assetType)
    {
        var key = $"{eventType}:{assetType}";
        if (_registrations.TryGetValue(key, out var registration))
        {
            return registration.EffectiveWorkerTypes;
        }

        var wildcardKey = $"{eventType}:*";
        if (_registrations.TryGetValue(wildcardKey, out var wildcardRegistration))
        {
            return wildcardRegistration.EffectiveWorkerTypes;
        }

        return [];
    }

    public IReadOnlyCollection<WorkerRegistration> GetAllRegistrations()
    {
        return _registrations.Values
            .GroupBy(r => r.Descriptor.WorkerType)
            .Select(g => g.First())
            .ToArray();
    }

    public sealed record WorkerRegistration(
        Type WorkerType,
        EphemeralWorkerDescriptor Descriptor,
        IReadOnlyCollection<Type>? AdditionalWorkerTypes = null)
    {
        public IReadOnlyCollection<Type> EffectiveWorkerTypes => AdditionalWorkerTypes ?? new[] { WorkerType };

        public WorkerRegistration WithWorkerType(Type newWorkerType)
        {
            var existingTypes = EffectiveWorkerTypes.ToList();
            if (!existingTypes.Contains(newWorkerType))
            {
                existingTypes.Add(newWorkerType);
            }
            return this with { AdditionalWorkerTypes = existingTypes };
        }
    }
}

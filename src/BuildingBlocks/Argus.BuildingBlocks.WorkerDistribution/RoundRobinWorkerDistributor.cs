using System.Collections.Concurrent;

namespace Argus.BuildingBlocks.WorkerDistribution;

public sealed class RoundRobinWorkerDistributor
{
    private readonly ConcurrentDictionary<string, WorkerPool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _defaultPoolSize;

    public RoundRobinWorkerDistributor(int defaultPoolSize = 10)
    {
        _defaultPoolSize = defaultPoolSize;
    }

    public string GetNextWorker(string domain, string workerType)
    {
        var poolKey = $"{workerType}:{domain}";
        var pool = _pools.GetOrAdd(poolKey, _ => CreateWorkerPool(workerType));

        return pool.GetNextWorker(domain);
    }

    public IReadOnlyCollection<string> GetAllWorkers(string workerType)
    {
        return Enumerable.Range(0, _defaultPoolSize)
            .Select(i => $"{workerType}-{i:D2}")
            .ToArray();
    }

    public void ConfigurePool(string workerType, int size)
    {
        foreach (var key in _pools.Keys.Where(k => k.StartsWith($"{workerType}:")))
        {
            _pools.TryRemove(key, out _);
        }

        _pools[$"{workerType}:*"] = CreateWorkerPool(workerType, size);
    }

    private WorkerPool CreateWorkerPool(string workerType, int? size = null)
    {
        var poolSize = size ?? _defaultPoolSize;
        var workers = Enumerable.Range(0, poolSize)
            .Select(i => $"{workerType}-{i:D2}")
            .ToArray();

        return new WorkerPool(workers);
    }
}

public sealed class WorkerPool
{
    private readonly string[] _workers;
    private int _currentIndex = -1;

    public WorkerPool(string[] workers)
    {
        _workers = workers;
    }

    public string GetNextWorker(string domain)
    {
        var index = Interlocked.Increment(ref _currentIndex);
        return _workers[index % _workers.Length];
    }

    public IReadOnlyCollection<string> GetAllWorkers()
    {
        return _workers;
    }
}

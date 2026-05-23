using System.Text.Json;
using Argus.Contracts.Tasks;
using Argus.Contracts.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.BuildingBlocks.Workers;

public sealed class WorkerTestHarness<TWorker> where TWorker : class, IReconWorker
{
    private readonly TWorker _worker;
    private readonly List<ProgressReported> _progressReports = [];
    private readonly List<RateLimitRequest> _rateLimitRequests = [];
    private readonly List<RateLimitBackpressureSignal> _backpressureSignals = [];
    private bool _rateLimitAllowed = true;

    public WorkerTestHarness(TWorker worker)
    {
        _worker = worker;
    }

    public IReadOnlyList<ProgressReported> ProgressReports => _progressReports;
    public IReadOnlyList<RateLimitRequest> RateLimitRequests => _rateLimitRequests;
    public IReadOnlyList<RateLimitBackpressureSignal> BackpressureSignals => _backpressureSignals;

    public void SetRateLimitAllowed(bool allowed) => _rateLimitAllowed = allowed;

    public WorkerTestContext CreateContext(string? workerId = null)
    {
        return new WorkerTestContext(
            workerId ?? Guid.NewGuid().ToString(),
            percent => Task.CompletedTask,
            request =>
            {
                _rateLimitRequests.Add(request);
                return Task.FromResult(_rateLimitAllowed);
            },
            signal =>
            {
                _backpressureSignals.Add(signal);
                return Task.CompletedTask;
            });
    }

    public async Task<WorkerProcessResult> ExecuteAsync(
        ReconTaskDto task,
        WorkerExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = context ?? CreateContext();
        _progressReports.Clear();
        return await _worker.ProcessAsync(task, ctx, cancellationToken);
    }

    public async Task<WorkerProcessResult> ExecuteAsync(
        Guid programId,
        string taskType,
        Dictionary<string, string>? payload = null,
        string? workerId = null,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = payload != null ? JsonSerializer.Serialize(payload) : null;
        var task = new ReconTaskDto(
            TaskId: Guid.NewGuid(),
            TaskType: taskType,
            ProgramId: programId,
            ScopeId: null,
            InputAssetId: null,
            InputPayloadJson: payloadJson,
            WorkerCapability: _worker.Capability.WorkerType,
            RequiredAssetType: null,
            State: Contracts.Tasks.ReconTaskState.Running,
            Attempt: 1,
            MaxAttempts: 3,
            LeaseOwner: null,
            LeaseExpiresAt: null,
            StartedAt: null,
            CompletedAt: null,
            ProgressPercent: 0,
            ProgressMessage: null,
            CheckpointJson: null,
            OutputSummaryJson: null,
            ErrorCode: null,
            ErrorMessage: null,
            DedupeHash: null,
            ScopeSnapshotJson: null);

        return await ExecuteAsync(task, CreateContext(workerId), cancellationToken);
    }
}

public sealed record ProgressReported(int Percent, string Message, string? Checkpoint);

public sealed class WorkerTestContext(
    string WorkerId,
    Func<int, string, string?, Task> reportProgressAsync,
    Func<RateLimitRequest, Task<bool>> requestRateLimitTokenAsync,
    Func<RateLimitBackpressureSignal, Task> signalBackpressureAsync)
    : WorkerExecutionContext(WorkerId, reportProgressAsync, requestRateLimitTokenAsync, signalBackpressureAsync)
{
}

public sealed class WorkerTestFixtures
{
    private readonly Dictionary<Type, object> _fixtures = new();
    private readonly List<Action> _disposables = [];

    public WorkerTestFixtures Register<T>(T fixture) where T : class
    {
        _fixtures[typeof(T)] = fixture;
        return this;
    }

    public WorkerTestFixtures Register<T>(Func<T> factory) where T : class
    {
        _fixtures[typeof(T)] = factory();
        return this;
    }

    public T Get<T>() where T : class
    {
        if (_fixtures.TryGetValue(typeof(T), out var fixture))
        {
            return (T)fixture;
        }
        throw new InvalidOperationException($"Fixture of type {typeof(T).Name} not registered");
    }

    public bool TryGet<T>(out T? fixture) where T : class
    {
        if (_fixtures.TryGetValue(typeof(T), out var f))
        {
            fixture = (T)f;
            return true;
        }
        fixture = null;
        return false;
    }

    public void AddDisposable(Action dispose) => _disposables.Add(dispose);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable();
        }
        _disposables.Clear();
        _fixtures.Clear();
    }
}

public static class WorkerTestHarnessExtensions
{
    public static IServiceCollection AddWorkerTestFixtures(
        this IServiceCollection services,
        WorkerTestFixtures fixtures)
    {
        services.AddSingleton(fixtures);
        return services;
    }

    public static WorkerTestFixtures WithHttpClient(
        this WorkerTestFixtures fixtures,
        HttpClient httpClient)
    {
        fixtures.Register<IHttpClientFactory>(new FakeHttpClientFactory(httpClient));
        return fixtures;
    }

    public static WorkerTestFixtures WithHttpMessageHandler(
        this WorkerTestFixtures fixtures,
        HttpMessageHandler handler)
    {
        fixtures.Register<IHttpClientFactory>(new FakeHttpClientFactory(new HttpClient(handler)));
        return fixtures;
    }
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public FakeHttpClientFactory(HttpClient client)
    {
        _client = client;
    }

    public HttpClient CreateClient(string name)
    {
        return _client;
    }
}

public sealed class WorkerScenarioBuilder<TWorker> where TWorker : class, IReconWorker
{
    private readonly WorkerTestHarness<TWorker> _harness;
    private readonly WorkerTestFixtures _fixtures = new();

    public WorkerScenarioBuilder(TWorker worker)
    {
        _harness = new WorkerTestHarness<TWorker>(worker);
    }

    public WorkerScenarioBuilder<TWorker> WithRateLimitAllowed(bool allowed)
    {
        _harness.SetRateLimitAllowed(allowed);
        return this;
    }

    public WorkerScenarioBuilder<TWorker> WithFixtures(Action<WorkerTestFixtures> configure)
    {
        configure(_fixtures);
        return this;
    }

    public Task<WorkerScenarioResult> ExecuteAsync(
        Guid programId,
        string taskType,
        Dictionary<string, string>? payload = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(programId, taskType, payload, null, cancellationToken);
    }

    public async Task<WorkerScenarioResult> ExecuteAsync(
        Guid programId,
        string taskType,
        Dictionary<string, string>? payload,
        string? workerId,
        CancellationToken cancellationToken = default)
    {
        var progressWatcher = new List<(int Percent, string Message, string? Checkpoint)>();

        var context = new WorkerTestContext(
            workerId ?? Guid.NewGuid().ToString(),
            (percent, message, checkpoint) =>
            {
                progressWatcher.Add((percent, message, checkpoint));
                return Task.CompletedTask;
            },
            request => Task.FromResult(_harness.SetRateLimitAllowed(true)),
            signal => Task.CompletedTask);

        var result = await _harness.ExecuteAsync(programId, taskType, payload, workerId, cancellationToken);

        return new WorkerScenarioResult(
            result,
            _harness.ProgressReports,
            _harness.RateLimitRequests,
            _harness.BackpressureSignals,
            progressWatcher);
    }
}

public sealed record WorkerScenarioResult(
    WorkerProcessResult Result,
    IReadOnlyList<ProgressReported> ProgressReports,
    IReadOnlyList<RateLimitRequest> RateLimitRequests,
    IReadOnlyList<RateLimitBackpressureSignal> BackpressureSignals,
    IReadOnlyList<(int Percent, string Message, string? Checkpoint)> ProgressWatched)
{
    public bool IsSuccess => !Result.PartiallySucceeded;
    public bool HasAssets => Result.ProducedAssets.Count > 0;
    public int AssetCount => Result.ProducedAssets.Count;
}
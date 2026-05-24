namespace Argus.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

public sealed class DevelopmentRealtimeClient(NavigationManager navigationManager) : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private HubConnection? _connection;

    public event Func<DevelopmentDataChangedEvent, Task>? Changed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection?.State == HubConnectionState.Connected)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(navigationManager.ToAbsoluteUri("/hubs/argus"))
                    .WithAutomaticReconnect()
                    .Build();

                _connection.On<DevelopmentDataChangedEvent>(DevelopmentRealtime.EventName, DispatchAsync);
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken);
                await _connection.InvokeAsync("JoinDevelopment", cancellationToken);
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task DispatchAsync(DevelopmentDataChangedEvent change)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<DevelopmentDataChangedEvent, Task> handler in handlers.GetInvocationList())
        {
            await handler(change);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _startLock.Dispose();
    }
}

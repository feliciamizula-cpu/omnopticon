namespace Argus.Web;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;

public sealed class DevelopmentRealtimeClient(IConfiguration configuration) : IAsyncDisposable
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
                // This HubConnection runs server-side (Blazor Server circuit), so it must reach the
                // app's own in-container Kestrel address (http://localhost:8080), NOT the browser-facing
                // URL — that depends on hairpin NAT and fails entirely for localhost-mapped access.
                var port = configuration["ASPNETCORE_HTTP_PORTS"] ?? "8080";
                _connection = new HubConnectionBuilder()
                    .WithUrl($"http://localhost:{port}/hubs/argus")
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

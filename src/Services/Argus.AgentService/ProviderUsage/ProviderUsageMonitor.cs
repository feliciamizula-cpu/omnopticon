namespace Argus.AgentService.ProviderUsage;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ProviderUsageMonitor(
    IServiceScopeFactory scopeFactory,
    ILogger<ProviderUsageMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ProviderUsageMonitor started; polling every {Interval}", PollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProviderUsageService>();
                await service.RefreshAllAsync(stoppingToken);
                logger.LogInformation("Provider usage refresh completed");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provider usage refresh failed");
            }

            await Task.Delay(PollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

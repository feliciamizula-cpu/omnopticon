namespace Argus.Web;

public sealed class ProviderUsageBackgroundRefresher(
    ProviderUsageCacheWarmer warmer,
    ILogger<ProviderUsageBackgroundRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await warmer.WarmAsync(forceRefresh: false, stoppingToken);
        try
        {
            while (true)
            {
                await Task.Delay(Interval, stoppingToken);
                await warmer.WarmAsync(forceRefresh: true, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ProviderUsageBackgroundRefresher stopped.");
        }
    }
}

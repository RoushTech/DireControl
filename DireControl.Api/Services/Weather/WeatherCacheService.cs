namespace DireControl.Api.Services.Weather;

public sealed class WeatherCacheService(
    RainViewerRadarProvider rainViewerProvider,
    IemRadarProvider iemProvider,
    WindTileCache windTileCache,
    LightningCache lightningCache,
    ILogger<WeatherCacheService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief start-up delay so the DB is ready before the first refresh.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAll(
                RefreshRadarProvidersAsync(stoppingToken),
                RefreshLightningAsync(stoppingToken));

            windTileCache.EvictStale();

            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    private async Task RefreshRadarProvidersAsync(CancellationToken ct)
    {
        try
        {
            // Both providers are refreshed regardless of which is currently active so that
            // switching providers in Settings takes effect immediately without a cold-start
            // delay. IemRadarProvider.RefreshAsync is a no-op, so the cost is negligible.
            await Task.WhenAll(
                rainViewerProvider.RefreshAsync(ct),
                iemProvider.RefreshAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down — ignore
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh radar providers");
        }
    }

    private async Task RefreshLightningAsync(CancellationToken ct)
    {
        try
        {
            await lightningCache.RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down — ignore
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh lightning cache period");
        }
    }
}

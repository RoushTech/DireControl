namespace DireControl.Api.Services.Weather;

public sealed class WeatherCacheService(
    RadarCache radarCache,
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
                RefreshRadarAsync(stoppingToken),
                RefreshLightningAsync(stoppingToken));

            windTileCache.EvictStale();

            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    private async Task RefreshRadarAsync(CancellationToken ct)
    {
        try
        {
            await radarCache.RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down — ignore
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh RainViewer radar manifest");
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

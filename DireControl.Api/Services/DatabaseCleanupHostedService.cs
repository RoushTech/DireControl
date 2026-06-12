namespace DireControl.Api.Services;

/// <summary>
/// Periodically triggers <see cref="DatabaseMaintenanceService"/> on the configured
/// interval (see <see cref="StationSettings.DatabaseCleanupIntervalHours"/>).
/// Settings are re-read each cycle so UI changes apply without a restart;
/// a non-positive interval pauses the schedule (re-checked hourly).
/// </summary>
public sealed class DatabaseCleanupHostedService(
    DatabaseMaintenanceService maintenance,
    StationSettingsProvider settingsProvider,
    ILogger<DatabaseCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first run.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await settingsProvider.GetAsync(stoppingToken);
            var intervalHours = settings.DatabaseCleanupIntervalHours;

            if (intervalHours <= 0)
            {
                // Disabled — check again later in case the user re-enables it.
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                continue;
            }

            if (!maintenance.TryStart(settings.VacuumOnCleanup))
                logger.LogDebug("Skipped scheduled cleanup — a run is already in progress.");

            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
        }
    }
}

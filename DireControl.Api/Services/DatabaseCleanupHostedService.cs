using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Periodically triggers <see cref="DatabaseMaintenanceService"/> on the interval
/// configured by <see cref="DireControlOptions.DatabaseCleanupIntervalHours"/>.
/// A non-positive interval disables the schedule (cleanup then runs only on demand).
/// </summary>
public sealed class DatabaseCleanupHostedService(
    DatabaseMaintenanceService maintenance,
    IOptions<DireControlOptions> options,
    ILogger<DatabaseCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = options.Value.DatabaseCleanupIntervalHours;
        if (intervalHours <= 0)
        {
            logger.LogInformation("Scheduled database cleanup is disabled (DatabaseCleanupIntervalHours <= 0).");
            return;
        }

        // Let the app finish starting before the first run.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!maintenance.TryStart(options.Value.VacuumOnCleanup))
                logger.LogDebug("Skipped scheduled cleanup — a run is already in progress.");

            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
        }
    }
}

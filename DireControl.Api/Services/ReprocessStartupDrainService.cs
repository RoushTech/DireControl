using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Optionally kicks off a single packet-reprocessing drain shortly after startup when
/// <see cref="DireControlOptions.ReprocessStaleOnStartup"/> is enabled. This is the
/// "auto-drain on version bump" path: bump <see cref="ParserVersionInfo.Current"/>,
/// restart with the flag on, and stale rows are re-derived automatically. Disabled by
/// default; reprocessing is otherwise driven manually from the maintenance API.
/// </summary>
public sealed class ReprocessStartupDrainService(
    PacketReprocessingService reprocessor,
    IOptions<DireControlOptions> options,
    ILogger<ReprocessStartupDrainService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ReprocessStaleOnStartup)
            return;

        // Let live ingestion settle before competing for the database.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (reprocessor.TryStart(new ReprocessFilter()))
            logger.LogInformation("Startup reprocessing drain of stale packets started.");
        else
            logger.LogInformation("Startup reprocessing drain skipped — a run is already in progress.");
    }
}

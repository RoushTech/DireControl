using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Periodically drains strikes queued by <see cref="LightningStrikeBuffer"/> into the
/// database so the map can replay lightning in sync with historical radar frames, and
/// prunes rows older than <see cref="HistoryRetention"/>.
/// </summary>
public sealed class LightningPersistenceService(
    LightningStrikeBuffer buffer,
    IServiceScopeFactory scopeFactory,
    ILogger<LightningPersistenceService> logger) : BackgroundService
{
    /// <summary>Covers the longest radar history window (RainViewer's 2 h past) plus the fade window.</summary>
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(3);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);
    private const int MaxBatch = 20_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPrune = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var pending = buffer.DrainPending(MaxBatch);
                if (pending.Count == 0 && DateTime.UtcNow - lastPrune < PruneInterval)
                    continue;

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

                if (pending.Count > 0)
                {
                    db.LightningStrikes.AddRange(pending.Select(s => new LightningStrikeRecord
                    {
                        TimeUtc = s.TimeUtc,
                        Latitude = s.Latitude,
                        Longitude = s.Longitude,
                    }));
                    await db.SaveChangesAsync(stoppingToken);
                }

                if (DateTime.UtcNow - lastPrune >= PruneInterval)
                {
                    var cutoff = DateTime.UtcNow - HistoryRetention;
                    var removed = await db.LightningStrikes
                        .Where(s => s.TimeUtc < cutoff)
                        .ExecuteDeleteAsync(stoppingToken);
                    lastPrune = DateTime.UtcNow;
                    if (removed > 0)
                        logger.LogDebug("Pruned {Count} lightning strikes older than {Cutoff}", removed, cutoff);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist lightning strikes; will retry next flush");
            }
        }
    }
}

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

    /// <summary>
    /// Refills the in-memory buffer from persisted history before the feed starts, so a
    /// restart doesn't blank the map for an hour. Called from startup, ahead of the hosted
    /// services, so it can never interleave with live strikes arriving.
    /// </summary>
    public static async Task RehydrateBufferAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var buffer = scope.ServiceProvider.GetRequiredService<LightningStrikeBuffer>();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<LightningPersistenceService>>();

        try
        {
            var cutoff = DateTime.UtcNow - LightningStrikeBuffer.Retention;
            var strikes = await db.LightningStrikes
                .AsNoTracking()
                .Where(s => s.TimeUtc >= cutoff)
                .OrderBy(s => s.TimeUtc)
                .Select(s => new LightningStrike(s.TimeUtc, s.Latitude, s.Longitude))
                .ToListAsync(ct);

            buffer.Seed(strikes);
            if (strikes.Count > 0)
                logger.LogInformation("Restored {Count} lightning strikes from history", strikes.Count);
        }
        catch (Exception ex)
        {
            // A cold buffer is a degraded map, not a broken server — the feed refills it.
            logger.LogWarning(ex, "Failed to restore lightning strikes from history");
        }
    }

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

                await SaveAsync(db, pending, stoppingToken);

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

        await FlushOnShutdownAsync();
    }

    /// <summary>
    /// Persists whatever accumulated since the last flush, so the final seconds before a
    /// restart survive it. Runs with its own token — the stopping token is already cancelled.
    /// </summary>
    private async Task FlushOnShutdownAsync()
    {
        try
        {
            var pending = buffer.DrainPending(MaxBatch);
            if (pending.Count == 0)
                return;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            await SaveAsync(db, pending, CancellationToken.None);
            logger.LogInformation("Flushed {Count} lightning strikes on shutdown", pending.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to flush lightning strikes on shutdown");
        }
    }

    private static async Task SaveAsync(DireControlContext db, List<LightningStrike> strikes, CancellationToken ct)
    {
        if (strikes.Count == 0)
            return;

        db.LightningStrikes.AddRange(strikes.Select(s => new LightningStrikeRecord
        {
            TimeUtc = s.TimeUtc,
            Latitude = s.Latitude,
            Longitude = s.Longitude,
        }));
        await db.SaveChangesAsync(ct);
    }
}

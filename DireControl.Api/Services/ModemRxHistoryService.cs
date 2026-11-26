using DireControl.Api.Controllers.Models;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Records each sound modem's decoded, bad-CRC and bit-repaired frame counts
/// per minute in <see cref="ModemRxMinute"/>, so the Diagnostics page can chart
/// them over time and across restarts.  The modem's counters are sampled every
/// few seconds and the change is added to the current minute's row; rows older
/// than <see cref="ModemRxHistoryLogic.MaxHours"/> are pruned.
/// </summary>
public sealed class ModemRxHistoryService(
    SoundModemService modemService,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<ModemRxHistoryService> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tracker = new ModemRxCounterTracker();
        // Counts sampled but not yet written: a failed write (a locked database)
        // keeps them here and they go out with the next sample.
        var pending = new Dictionary<string, RxCounters>();
        var lastPrune = DateTime.MinValue;
        using var timer = new PeriodicTimer(SampleInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var s in modemService.Statuses)
            {
                if (s.State != ModemState.Running)
                    continue;
                var delta = tracker.Advance(
                    s.RadioId, s.RunningSince,
                    new RxCounters(s.DecodedFrames, s.InvalidFrames, s.RepairedFrames));
                if (!delta.IsZero)
                    pending[s.RadioId] = pending.GetValueOrDefault(s.RadioId) + delta;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var pruneDue = now - lastPrune >= PruneInterval;
            if (pending.Count == 0 && !pruneDue)
                continue;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
                if (pending.Count > 0)
                {
                    await RecordAsync(db, pending, now, stoppingToken);
                    pending.Clear();
                }
                if (pruneDue)
                {
                    await PruneAsync(db, now, stoppingToken);
                    lastPrune = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not record modem decode history; retrying with the next sample.");
            }
        }
    }

    /// <summary>Adds each radio's counts to its row for the minute holding <paramref name="utcNow"/>.</summary>
    public static async Task RecordAsync(
        DireControlContext db,
        IReadOnlyDictionary<string, RxCounters> deltas,
        DateTime utcNow,
        CancellationToken ct)
    {
        var minute = ModemRxHistoryLogic.TruncateToMinute(utcNow);
        var radioIds = deltas.Keys.ToList();
        var existing = await db.ModemRxMinutes
            .Where(r => r.Minute == minute && radioIds.Contains(r.RadioId))
            .ToDictionaryAsync(r => r.RadioId, ct);

        foreach (var (radioId, delta) in deltas)
        {
            if (!existing.TryGetValue(radioId, out var row))
            {
                row = new ModemRxMinute { RadioId = radioId, Minute = minute };
                db.ModemRxMinutes.Add(row);
            }
            row.DecodedFrames += (int)delta.Decoded;
            row.InvalidFrames += (int)delta.Invalid;
            row.RepairedFrames += (int)delta.Repaired;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>One radio's counts for the last <paramref name="hours"/> hours, bucketed for the chart.</summary>
    public static async Task<ModemRxHistoryDto> QueryAsync(
        DireControlContext db,
        string radioId,
        int hours,
        DateTime utcNow,
        CancellationToken ct)
    {
        var from = ModemRxHistoryLogic.RangeStart(utcNow, hours);
        var rows = await db.ModemRxMinutes
            .AsNoTracking()
            .Where(r => r.RadioId == radioId && r.Minute >= from)
            .Select(r => new { r.Minute, r.DecodedFrames, r.InvalidFrames, r.RepairedFrames })
            .ToListAsync(ct);

        return ModemRxHistoryLogic.Bucket(
            rows.Select(r => (r.Minute, r.DecodedFrames, r.InvalidFrames, r.RepairedFrames)),
            utcNow,
            hours);
    }

    /// <summary>Deletes rows older than the longest range the chart offers.</summary>
    public static Task<int> PruneAsync(DireControlContext db, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow.AddHours(-ModemRxHistoryLogic.MaxHours - 24);
        return db.ModemRxMinutes.Where(r => r.Minute < cutoff).ExecuteDeleteAsync(ct);
    }
}

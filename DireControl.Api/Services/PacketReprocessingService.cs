using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Re-derives the structured fields of stored <see cref="Packet"/> rows from their
/// preserved <c>RawPacket</c>, using <see cref="AprsPacketParsingService.ReprocessOneAsync"/>
/// (which skips all live side effects). The primary use is repairing rows after a
/// parser change: by default it drains every packet whose
/// <see cref="Packet.ParserVersion"/> is below <see cref="ParserVersionInfo.Current"/>,
/// so a version bump alone defines the work — no hand-picking of rows.
///
/// Rows are processed in keyset-paginated batches ordered by Id so a long run never
/// holds a large transaction and never blocks live ingestion for long. A single run
/// executes at a time (shared by the scheduler, startup drain, and manual triggers).
/// </summary>
public sealed class PacketReprocessingService(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    AprsPacketParsingService parser,
    DatabaseMaintenanceService maintenance,
    StationSettingsProvider settingsProvider,
    ILogger<PacketReprocessingService> logger)
{
    private const int BatchSize = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning { get; private set; }
    public long Processed { get; private set; }
    public long Total { get; private set; }
    public ReprocessResult? LastResult { get; private set; }

    /// <summary>
    /// Starts a reprocessing run in the background if one is not already in progress.
    /// Returns false (without starting another) when a run is already active.
    /// </summary>
    public bool TryStart(ReprocessFilter filter)
    {
        if (!_gate.Wait(0))
            return false;

        IsRunning = true;
        Processed = 0;
        Total = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                LastResult = await RunCoreAsync(filter, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — leave the previous LastResult in place.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Packet reprocessing failed.");
                LastResult = new ReprocessResult { CompletedAt = DateTime.UtcNow, Error = ex.Message };
            }
            finally
            {
                IsRunning = false;
                _gate.Release();
            }
        });

        return true;
    }

    /// <summary>
    /// Applies the reprocessing filter to a packet query. When <see cref="ReprocessFilter.Force"/>
    /// is false (the default) only rows below the current parser version are selected, which
    /// makes the run self-advancing — each processed row is stamped to the current version and
    /// drops out of the predicate. <c>Force</c> re-derives matching rows regardless of version.
    /// </summary>
    internal static IQueryable<Packet> BuildQuery(IQueryable<Packet> packets, ReprocessFilter filter)
    {
        if (!filter.Force)
            packets = packets.Where(p => p.ParserVersion < ParserVersionInfo.Current);

        if (filter.Source is { } source)
            packets = packets.Where(p => p.Source == source);

        if (filter.After is { } after)
            packets = packets.Where(p => p.ReceivedAt >= after);

        if (filter.Before is { } before)
            packets = packets.Where(p => p.ReceivedAt < before);

        return packets;
    }

    private async Task<ReprocessResult> RunCoreAsync(ReprocessFilter filter, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var ourCallsign = (await settingsProvider.GetAsync(ct)).OurCallsign.Trim();

        using (var countScope = scopeFactory.CreateScope())
        {
            var countDb = countScope.ServiceProvider.GetRequiredService<DireControlContext>();
            await SetBusyTimeoutAsync(countDb, ct);
            Total = await BuildQuery(countDb.Packets.AsNoTracking(), filter).LongCountAsync(ct);
        }

        logger.LogInformation(
            "Packet reprocessing started: {Total} rows match (force={Force}, source={Source}).",
            Total, filter.Force, filter.Source?.ToString() ?? "any");

        long processed = 0;
        long failed = 0;
        var lastId = 0;

        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

            // Wait (rather than fail) when live ingestion holds the SQLite write lock,
            // matching DatabaseMaintenanceService. Without this, SaveChanges would throw
            // "database is locked" the moment a batch contends with ingest.
            await SetBusyTimeoutAsync(db, ct);

            var batch = await BuildQuery(db.Packets, filter)
                .Where(p => p.Id > lastId)
                .OrderBy(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var packet in batch)
            {
                try
                {
                    await parser.ReprocessOneAsync(packet, db, ourCallsign, ct);
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogTrace(ex, "Reprocess failed for packet {Id} ({Raw}).", packet.Id, packet.RawPacket);
                    // Stamp the version so the row is not retried forever; mark unparseable
                    // to match the live parser's handling of frames it cannot decode.
                    packet.ParsedType = PacketType.Unparseable;
                    packet.ParserVersion = ParserVersionInfo.Current;
                }
            }

            await EnsureStationsExistAsync(db, batch, ct);
            await db.SaveChangesAsync(ct);

            lastId = batch[^1].Id;
            processed += batch.Count;
            Processed = processed;
        }

        // Reprocessing repairs corrupted StationCallsigns, which can leave the old
        // garbage station with no packets. Optionally sweep those away once the drain
        // has finished (only on a clean, non-cancelled completion).
        var orphansDeleted = 0;
        if (filter.DeleteOrphanStations && !ct.IsCancellationRequested)
            orphansDeleted = await maintenance.DeleteOrphanStationsAsync(ct);

        var result = new ReprocessResult
        {
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            Processed = processed,
            Failed = failed,
            OrphanStationsDeleted = orphansDeleted,
        };

        logger.LogInformation(
            "Packet reprocessing complete: {Processed} processed ({Failed} failed, {Orphans} orphan stations removed).",
            processed, failed, orphansDeleted);

        return result;
    }

    private static Task SetBusyTimeoutAsync(DireControlContext db, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=30000;", ct);

    /// <summary>
    /// Ensures every <see cref="Packet.StationCallsign"/> in the batch has a backing
    /// <see cref="Station"/> row. Re-derivation can change a packet's StationCallsign
    /// (notably repairing corrupted third-party rows to the real source), and the
    /// foreign key requires the target station to exist before the batch is saved.
    /// </summary>
    private static async Task EnsureStationsExistAsync(DireControlContext db, List<Packet> batch, CancellationToken ct)
    {
        var callsigns = batch.Select(p => p.StationCallsign).Distinct().ToList();

        var existing = (await db.Stations
            .Where(s => callsigns.Contains(s.Callsign))
            .Select(s => s.Callsign)
            .ToListAsync(ct)).ToHashSet();

        foreach (var packet in batch)
        {
            if (existing.Contains(packet.StationCallsign))
                continue;

            db.Stations.Add(new Station
            {
                Callsign = packet.StationCallsign,
                FirstSeen = packet.ReceivedAt,
                LastSeen = packet.ReceivedAt,
                Symbol = "/-",
            });
            existing.Add(packet.StationCallsign);
        }
    }
}

/// <summary>Selection criteria for a reprocessing run.</summary>
public sealed class ReprocessFilter
{
    /// <summary>
    /// When false (default), only rows below <see cref="ParserVersionInfo.Current"/> are
    /// reprocessed. When true, all matching rows are re-derived regardless of version.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>Optional restriction to a single packet source (RF / APRS-IS / Own).</summary>
    public PacketSource? Source { get; init; }

    /// <summary>Optional inclusive lower bound on <see cref="Packet.ReceivedAt"/> (UTC).</summary>
    public DateTime? After { get; init; }

    /// <summary>Optional exclusive upper bound on <see cref="Packet.ReceivedAt"/> (UTC).</summary>
    public DateTime? Before { get; init; }

    /// <summary>
    /// When true, after the drain completes, delete stations left with no packets
    /// (excluding watch-listed ones). Useful for clearing the garbage stations orphaned
    /// when reprocessing repairs corrupted callsigns.
    /// </summary>
    public bool DeleteOrphanStations { get; init; }
}

/// <summary>Outcome of a single reprocessing run.</summary>
public sealed class ReprocessResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public long Processed { get; init; }
    public long Failed { get; init; }
    public int OrphanStationsDeleted { get; init; }
    public string? Error { get; init; }
}

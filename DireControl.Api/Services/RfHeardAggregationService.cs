using DireControl.Api.Services.Weather;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.PathParsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Maintains <see cref="RfHeardDaily"/> — the day-by-day archive of how many stations each
/// radio heard directly (no digipeater in between), and how far away they were.
///
/// Everything here is derivable from <see cref="Packet"/> on demand, and the read endpoints
/// that can do so query packets live. This archive exists for the one thing a live query
/// cannot survive: <see cref="DatabaseMaintenanceService"/> prunes packets per the retention
/// settings, and a trend meant to be read over months or years must outlive them. It is
/// small — one row per radio per day.
///
/// Each row is recomputed as a pure function of the packets in that day, never accumulated,
/// so a day can be rebuilt any number of times and always converge on the same answer. That
/// is what makes the pass safe to interrupt, repeat, or run out of order.
/// </summary>
public sealed class RfHeardAggregationService(
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<RfHeardAggregationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Packets classified per backfill batch.</summary>
    public const int BackfillBatchSize = 2000;

    /// <summary>
    /// Cap on packets classified in a single pass. A first deploy against a large stored
    /// archive is spread over several passes rather than one long burst of writes competing
    /// with live ingestion for the database.
    /// </summary>
    public const int MaxClassifyPerPass = 200_000;

    /// <summary>
    /// Recent days rebuilt on every pass. Covers today (still filling in) plus enough slack
    /// for late-arriving or reclassified packets to be picked up.
    /// </summary>
    public const int RecomputeTrailingDays = 7;

    /// <summary>
    /// Older days without a row that are filled in per pass. A first run against years of
    /// stored packets is spread over a few passes rather than done in one long transaction.
    /// </summary>
    public const int MaxBackfillDaysPerPass = 120;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Match StatisticsAggregationService: let the app finish starting up first.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

                await AggregateAsync(
                    db,
                    options.Value.OurCallsign,
                    options.Value.HomeLat,
                    options.Value.HomeLon,
                    logger,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RF heard aggregation failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one full pass against <paramref name="db"/>. Exposed directly (rather than only
    /// through the hosted loop) so it can be driven against an in-memory database in tests.
    /// </summary>
    public static async Task AggregateAsync(
        DireControlContext db,
        string? ourCallsign,
        double? homeLat,
        double? homeLon,
        ILogger? logger,
        CancellationToken ct,
        int? maxClassifyPerPass = null)
    {
        var (classified, remaining) = await BackfillHeardViaAsync(
            db, maxClassifyPerPass ?? MaxClassifyPerPass, ct);
        if (classified > 0)
        {
            logger?.LogInformation(
                "RF heard: classified {Count} previously unclassified packets{More}.",
                classified,
                remaining ? " (more remain; continuing next pass)" : "");
        }

        // Do not build the archive from a half-classified table. Classification advances in
        // packet-id order, so while it is draining the boundary falls inside some day — and a
        // day summarised now from its classified half would be written, marked done, and never
        // revisited, silently under-reporting that day forever. Wait until the table is whole.
        if (remaining)
        {
            logger?.LogInformation(
                "RF heard: holding off the daily archive until packet classification finishes.");
            return;
        }

        var radios = await db.Radios.AsNoTracking().ToListAsync(ct);
        var days = await SelectDaysToRecomputeAsync(db, ct);
        if (days.Count == 0)
            return;

        // "First ever heard direct" is what makes a station new on a given day. Computed once
        // per pass and shared across every day being rebuilt, rather than re-scanned per day.
        var firstHeard = await LoadFirstHeardAsync(db, radios, ourCallsign, ct);

        foreach (var day in days)
            await RecomputeDayAsync(db, day, radios, ourCallsign, homeLat, homeLon, firstHeard, ct);

        logger?.LogDebug("RF heard: rebuilt {Count} day(s).", days.Count);
    }

    // -------------------------------------------------------------------------
    // Classify packets stored before Packet.HeardVia existed
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sweeps forward through unclassified packets, deriving <see cref="Packet.HeardVia"/>
    /// from the stored path. This is the one-time cost of deploying the reception feature
    /// onto an existing packet archive; new packets are classified at parse time and never
    /// reach here.
    ///
    /// Every classified row leaves the <see cref="HeardVia.Unknown"/> predicate for good —
    /// <see cref="AprsPathParser.ClassifyHeardVia"/> always returns a real value — so the
    /// sweep converges and then costs an empty index seek per pass. Work is capped at
    /// <see cref="MaxClassifyPerPass"/> so a large archive is drained over several passes
    /// instead of one long burst.
    /// </summary>
    /// <returns>How many packets were classified, and whether more are still waiting.</returns>
    private static async Task<(int Classified, bool More)> BackfillHeardViaAsync(
        DireControlContext db, int maxPerPass, CancellationToken ct)
    {
        var total = 0;

        while (total < maxPerPass && !ct.IsCancellationRequested)
        {
            var batch = await db.Packets
                .AsNoTracking()
                .Where(p => p.HeardVia == HeardVia.Unknown)
                .OrderBy(p => p.Id)
                .Take(Math.Min(BackfillBatchSize, maxPerPass - total))
                .Select(p => new { p.Id, p.Path })
                .ToListAsync(ct);

            if (batch.Count == 0)
                return (total, false);

            foreach (var group in batch.GroupBy(p => Classify(p.Path)))
            {
                var ids = group.Select(g => g.Id).ToList();
                await db.Packets
                    .Where(p => ids.Contains(p.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.HeardVia, group.Key), ct);
            }

            total += batch.Count;
        }

        var more = await db.Packets.AnyAsync(p => p.HeardVia == HeardVia.Unknown, ct);
        return (total, more);
    }

    private static HeardVia Classify(string? path) =>
        AprsPathParser.ClassifyHeardVia(
            string.IsNullOrEmpty(path)
                ? []
                : path.Split(',', StringSplitOptions.RemoveEmptyEntries));

    // -------------------------------------------------------------------------
    // Choosing which days to rebuild
    // -------------------------------------------------------------------------

    /// <summary>
    /// The trailing window (always rebuilt, because it is still changing) plus any older day
    /// that has packets but no row yet — the first-run backfill, bounded per pass.
    /// </summary>
    private static async Task<List<DateOnly>> SelectDaysToRecomputeAsync(
        DireControlContext db, CancellationToken ct)
    {
        var today = RfHeardLogic.LocalDay(DateTime.UtcNow);

        var days = new HashSet<DateOnly>();
        for (var i = 0; i < RecomputeTrailingDays; i++)
            days.Add(today.AddDays(-i));

        var oldestPacket = await db.Packets
            .Where(p => p.Source == PacketSource.Rf && p.HeardVia == HeardVia.Direct)
            .OrderBy(p => p.ReceivedAt)
            .Select(p => (DateTime?)p.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        if (oldestPacket is { } oldest)
        {
            var existing = await db.RfHeardDailies
                .AsNoTracking()
                .Select(r => r.Day)
                .Distinct()
                .ToListAsync(ct);

            var known = existing.ToHashSet();
            var backfilled = 0;

            // Newest-first, so the most recently interesting history appears first.
            for (var day = today.AddDays(-RecomputeTrailingDays);
                 day >= RfHeardLogic.LocalDay(oldest) && backfilled < MaxBackfillDaysPerPass;
                 day = day.AddDays(-1))
            {
                if (known.Add(day))
                {
                    days.Add(day);
                    backfilled++;
                }
            }
        }

        return days.OrderBy(d => d).ToList();
    }

    /// <summary>
    /// When each station was first heard direct on each channel, across all stored packets.
    /// </summary>
    private static async Task<Dictionary<(int Channel, string Callsign), DateTime>> LoadFirstHeardAsync(
        DireControlContext db,
        IReadOnlyList<Radio> radios,
        string? ourCallsign,
        CancellationToken ct)
    {
        var grouped = await db.Packets
            .AsNoTracking()
            .Where(p => p.Source == PacketSource.Rf && p.HeardVia == HeardVia.Direct)
            .GroupBy(p => new { p.KissChannel, p.StationCallsign })
            .Select(g => new
            {
                g.Key.KissChannel,
                g.Key.StationCallsign,
                First = g.Min(p => p.ReceivedAt),
            })
            .ToListAsync(ct);

        return grouped
            .Where(g => !RfHeardLogic.IsOwnStation(g.StationCallsign, radios, ourCallsign))
            .ToDictionary(g => (g.KissChannel, g.StationCallsign), g => g.First);
    }

    // -------------------------------------------------------------------------
    // Rebuilding one local day
    // -------------------------------------------------------------------------

    private static async Task RecomputeDayAsync(
        DireControlContext db,
        DateOnly day,
        IReadOnlyList<Radio> radios,
        string? ourCallsign,
        double? homeLat,
        double? homeLon,
        IReadOnlyDictionary<(int Channel, string Callsign), DateTime> firstHeard,
        CancellationToken ct)
    {
        var (startUtc, endUtc) = RfHeardLogic.LocalDayRangeUtc(day);

        var packets = await db.Packets
            .AsNoTracking()
            .Where(p => p.Source == PacketSource.Rf
                     && p.HeardVia == HeardVia.Direct
                     && p.ReceivedAt >= startUtc
                     && p.ReceivedAt < endUtc)
            .Select(p => new { p.KissChannel, p.StationCallsign, p.Latitude, p.Longitude })
            .ToListAsync(ct);

        var relevant = packets
            .Where(p => !RfHeardLogic.IsOwnStation(p.StationCallsign, radios, ourCallsign))
            .ToList();

        var stationPositions = await db.Stations
            .AsNoTracking()
            .Where(s => s.LastLat != null && s.LastLon != null)
            .Select(s => new { s.Callsign, s.LastLat, s.LastLon })
            .ToDictionaryAsync(s => s.Callsign, s => (s.LastLat, s.LastLon), ct);

        var existingRows = await db.RfHeardDailies
            .Where(r => r.Day == day)
            .ToDictionaryAsync(r => r.ChannelNumber, ct);

        var now = DateTime.UtcNow;
        var channelsSeen = new HashSet<int>();

        foreach (var channelGroup in relevant.GroupBy(p => p.KissChannel))
        {
            channelsSeen.Add(channelGroup.Key);

            // One distance per station — the farthest copy of it heard that day — so a chatty
            // nearby station cannot drag the median toward itself.
            var perStationBest = new Dictionary<string, double>();

            foreach (var pkt in channelGroup)
            {
                stationPositions.TryGetValue(pkt.StationCallsign, out var last);
                if (DistanceKm(pkt.Latitude, pkt.Longitude, last.LastLat, last.LastLon, homeLat, homeLon)
                    is not { } km)
                {
                    continue;
                }

                if (!perStationBest.TryGetValue(pkt.StationCallsign, out var best) || km > best)
                    perStationBest[pkt.StationCallsign] = km;
            }

            var distances = perStationBest.Values.ToList();
            var callsigns = channelGroup.Select(p => p.StationCallsign).Distinct().ToList();

            if (!existingRows.TryGetValue(channelGroup.Key, out var row))
            {
                row = new RfHeardDaily { ChannelNumber = channelGroup.Key, Day = day };
                db.RfHeardDailies.Add(row);
            }

            row.UniqueDirectStations = callsigns.Count;
            row.NewDirectStations = callsigns.Count(c =>
                firstHeard.TryGetValue((channelGroup.Key, c), out var first)
                && first >= startUtc && first < endUtc);
            row.DirectPackets = channelGroup.Count();
            row.DirectStationsWithPosition = distances.Count;
            row.MaxDirectDistanceKm = distances.Count > 0 ? distances.Max() : null;
            row.MedianDirectDistanceKm = RfHeardLogic.Median(distances);
            row.LastComputedAt = now;
        }

        // A channel that has a row but heard nothing this time — packets pruned, or a
        // reclassification — must not keep reporting stale counts.
        foreach (var (channel, row) in existingRows)
        {
            if (channelsSeen.Contains(channel))
                continue;

            row.UniqueDirectStations = 0;
            row.NewDirectStations = 0;
            row.DirectPackets = 0;
            row.DirectStationsWithPosition = 0;
            row.MaxDirectDistanceKm = null;
            row.MedianDirectDistanceKm = null;
            row.LastComputedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Distance from home to a direct-heard station, or <c>null</c> when either end has no
    /// known position.
    /// </summary>
    private static double? DistanceKm(
        double? packetLat, double? packetLon,
        double? stationLat, double? stationLon,
        double? homeLat, double? homeLon)
    {
        if (homeLat is not { } hlat || homeLon is not { } hlon)
            return null;

        if (RfHeardLogic.PickPosition(packetLat, packetLon, stationLat, stationLon) is not { } pos)
            return null;

        return LightningAlertLogic.HaversineKm(hlat, hlon, pos.Lat, pos.Lon);
    }
}

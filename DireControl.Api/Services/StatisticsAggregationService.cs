using DireControl.Api.Controllers.Models;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.PathParsing;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Periodically recomputes global statistics, digipeater analysis, and coverage
/// grid data, storing the results in the database so API endpoints can serve
/// pre-aggregated data without running expensive queries on the request path.
/// Digipeater totals and coverage grids are maintained incrementally from a
/// packet-id watermark; only the first run after startup reads the full table
/// (streamed, not materialized).
/// </summary>
public sealed class StatisticsAggregationService(
    IServiceScopeFactory scopeFactory,
    StatisticsService statisticsService,
    ILogger<StatisticsAggregationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Highest Packet.Id already folded into the incremental aggregates.
    // In-memory only: a restart re-seeds with one streamed full pass.
    private int _lastProcessedPacketId;
    private bool _initialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short initial delay to let the app finish starting up.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAggregationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Statistics aggregation failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunAggregationAsync(CancellationToken ct)
    {
        logger.LogDebug("Starting statistics aggregation.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        await AggregateIncrementalAsync(db, ct);
        await RecomputeDigipeaterLast24hAsync(db, ct);
        await AggregateGlobalStatisticsAsync(db, ct);

        logger.LogDebug("Statistics aggregation complete.");
    }

    /// <summary>
    /// Folds packets newer than the watermark into the digipeater totals and the
    /// coverage grid table. On a restart (or first ever run) this is a single
    /// streamed pass over the whole table; thereafter it touches only new rows.
    /// </summary>
    private async Task AggregateIncrementalAsync(DireControlContext db, CancellationToken ct)
    {
        var maxId = await db.Packets.MaxAsync(p => (int?)p.Id, ct);
        if (maxId is null)
        {
            _initialized = true;
            return;
        }

        if (_initialized && maxId.Value <= _lastProcessedPacketId)
            return;

        var fromId = _initialized ? _lastProcessedPacketId : 0;

        var digiTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var digiHopSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var digiHopCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coverage = new Dictionary<string, (int Count, double SumLat, double SumLon)>();

        var newPackets = db.Packets
            .AsNoTracking()
            .Where(p => p.Id > fromId && p.Id <= maxId.Value)
            .Select(p => new { p.Path, p.GridSquare, p.Latitude, p.Longitude })
            .AsAsyncEnumerable();

        await foreach (var pkt in newPackets.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(pkt.Path))
            {
                var entries = pkt.Path.Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < entries.Length; i++)
                {
                    var callsign = entries[i].TrimEnd('*').Trim();
                    if (string.IsNullOrWhiteSpace(callsign) || AprsPathParser.IsGenericAlias(callsign))
                        continue;

                    digiTotals[callsign] = digiTotals.GetValueOrDefault(callsign) + 1;
                    digiHopSums[callsign] = digiHopSums.GetValueOrDefault(callsign) + i + 1;
                    digiHopCounts[callsign] = digiHopCounts.GetValueOrDefault(callsign) + 1;
                }
            }

            if (!string.IsNullOrEmpty(pkt.GridSquare) && pkt.Latitude is { } lat && pkt.Longitude is { } lon)
            {
                var (count, sumLat, sumLon) = coverage.GetValueOrDefault(pkt.GridSquare);
                coverage[pkt.GridSquare] = (count + 1, sumLat + lat, sumLon + lon);
            }
        }

        var now = DateTime.UtcNow;

        if (digiTotals.Count > 0)
        {
            var digiCallsigns = digiTotals.Keys.ToList();
            var existingDigis = await db.DigipeaterStatistics
                .Where(d => digiCallsigns.Contains(d.Callsign))
                .ToDictionaryAsync(d => d.Callsign, ct);

            foreach (var (callsign, total) in digiTotals)
            {
                if (existingDigis.TryGetValue(callsign, out var existing))
                {
                    existing.TotalPacketsForwarded += total;
                    existing.HopPositionSum += digiHopSums.GetValueOrDefault(callsign);
                    existing.HopPositionCount += digiHopCounts.GetValueOrDefault(callsign);
                    existing.LastComputedAt = now;
                }
                else
                {
                    db.DigipeaterStatistics.Add(new DigipeaterStatistic
                    {
                        Callsign = callsign,
                        TotalPacketsForwarded = total,
                        HopPositionSum = digiHopSums.GetValueOrDefault(callsign),
                        HopPositionCount = digiHopCounts.GetValueOrDefault(callsign),
                        LastComputedAt = now,
                    });
                }
            }
        }

        if (coverage.Count > 0)
        {
            var grids = coverage.Keys.ToList();
            var existingGrids = await db.CoverageGridStatistics
                .Where(c => grids.Contains(c.GridSquare))
                .ToDictionaryAsync(c => c.GridSquare, ct);

            foreach (var (grid, delta) in coverage)
            {
                if (existingGrids.TryGetValue(grid, out var existing))
                {
                    var newCount = existing.PacketCount + delta.Count;
                    existing.AvgLat = (existing.AvgLat * existing.PacketCount + delta.SumLat) / newCount;
                    existing.AvgLon = (existing.AvgLon * existing.PacketCount + delta.SumLon) / newCount;
                    existing.PacketCount = newCount;
                    existing.LastComputedAt = now;
                }
                else
                {
                    db.CoverageGridStatistics.Add(new CoverageGridStatistic
                    {
                        GridSquare = grid,
                        PacketCount = delta.Count,
                        AvgLat = delta.SumLat / delta.Count,
                        AvgLon = delta.SumLon / delta.Count,
                        LastComputedAt = now,
                    });
                }
            }
        }

        await db.SaveChangesAsync(ct);

        _lastProcessedPacketId = maxId.Value;
        _initialized = true;
    }

    /// <summary>
    /// The 24-hour digipeater counts are a sliding window, so they are recomputed
    /// each run — but only over the last 24 hours of packets (ReceivedAt index).
    /// </summary>
    private async Task RecomputeDigipeaterLast24hAsync(DireControlContext db, CancellationToken ct)
    {
        var cutoff24h = DateTime.UtcNow.AddHours(-24);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var recentPaths = db.Packets
            .AsNoTracking()
            .Where(p => p.ReceivedAt >= cutoff24h && p.Path != null && p.Path != string.Empty)
            .Select(p => p.Path)
            .AsAsyncEnumerable();

        await foreach (var path in recentPaths.WithCancellation(ct))
        {
            foreach (var entry in path.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var callsign = entry.TrimEnd('*').Trim();
                if (string.IsNullOrWhiteSpace(callsign) || AprsPathParser.IsGenericAlias(callsign))
                    continue;
                counts[callsign] = counts.GetValueOrDefault(callsign) + 1;
            }
        }

        var rows = await db.DigipeaterStatistics.ToListAsync(ct);
        foreach (var row in rows)
            row.Last24hPackets = counts.GetValueOrDefault(row.Callsign);

        await db.SaveChangesAsync(ct);
    }

    private async Task AggregateGlobalStatisticsAsync(DireControlContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var todayStart = DateTime.Now.Date.ToUniversalTime();
        var weekStart = todayStart.AddDays(-7);
        var h24Start = now.AddHours(-24);

        var packetsToday = await db.Packets
            .CountAsync(p => p.ReceivedAt >= todayStart, ct);

        var uniqueToday = await db.Packets
            .Where(p => p.ReceivedAt >= todayStart)
            .Select(p => p.StationCallsign)
            .Distinct()
            .CountAsync(ct);

        var uniqueThisWeek = await db.Packets
            .Where(p => p.ReceivedAt >= weekStart)
            .Select(p => p.StationCallsign)
            .Distinct()
            .CountAsync(ct);

        var uniqueAllTime = await db.Stations.CountAsync(ct);

        // Packets per hour — use a grouped count instead of loading all timestamps.
        var hourBuckets = await db.Packets
            .Where(p => p.ReceivedAt >= h24Start)
            .GroupBy(p => (int)((now.Ticks - p.ReceivedAt.Ticks) / TimeSpan.TicksPerHour))
            .Select(g => new { HoursAgo = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var packetsPerHour = new int[24];
        foreach (var bucket in hourBuckets)
        {
            if (bucket.HoursAgo >= 0 && bucket.HoursAgo < 24)
                packetsPerHour[23 - bucket.HoursAgo] = bucket.Count;
        }

        // Busiest digipeaters from pre-aggregated table
        var topDigis = await db.DigipeaterStatistics
            .Where(d => d.TotalPacketsForwarded > 0)
            .OrderByDescending(d => d.TotalPacketsForwarded)
            .Take(10)
            .Select(d => new CallsignCountDto
            {
                Callsign = d.Callsign,
                Count = d.TotalPacketsForwarded,
            })
            .ToListAsync(ct);

        // Busiest stations by beacon rate
        var topBeacon = await db.StationStatistics
            .Where(ss => ss.AveragePacketsPerHour > 0)
            .OrderByDescending(ss => ss.AveragePacketsPerHour)
            .Take(10)
            .Select(ss => new CallsignCountDto
            {
                Callsign = ss.Callsign,
                Count = ss.PacketsToday,
                AveragePerHour = ss.AveragePacketsPerHour,
            })
            .ToListAsync(ct);

        // Recently first-heard
        var recentlyFirstHeard = await db.Stations
            .OrderByDescending(s => s.FirstSeen)
            .Take(10)
            .Select(s => new RecentlyHeardDto
            {
                Callsign = s.Callsign,
                FirstSeen = s.FirstSeen,
                StationType = s.StationType,
            })
            .ToListAsync(ct);

        // Grid squares — the coverage table already aggregates packet grids, so the
        // full-table distinct scan is unnecessary; union in station grids (small table).
        var packetGrids = await db.CoverageGridStatistics
            .Select(c => c.GridSquare)
            .ToListAsync(ct);

        var stationGrids = await db.Stations
            .Where(s => s.GridSquare != null && s.GridSquare != "")
            .Select(s => s.GridSquare!)
            .Distinct()
            .ToListAsync(ct);

        var gridSquares = packetGrids
            .Union(stationGrids)
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        var dto = new StatisticsDto
        {
            PacketsToday = packetsToday,
            UniqueStationsToday = uniqueToday,
            UniqueStationsThisWeek = uniqueThisWeek,
            UniqueStationsAllTime = uniqueAllTime,
            PacketsPerHour = packetsPerHour,
            BusiestDigipeaters = topDigis,
            BusiestStations = topBeacon,
            RecentlyFirstHeard = recentlyFirstHeard,
            GridSquares = gridSquares,
        };

        statisticsService.SetSnapshot(dto);
    }
}

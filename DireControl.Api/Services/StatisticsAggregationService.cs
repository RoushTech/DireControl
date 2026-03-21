using DireControl.Api.Controllers.Models;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.PathParsing;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Periodically recomputes global statistics, digipeater analysis, and coverage
/// grid data, storing the results in the database so API endpoints can serve
/// pre-aggregated data without running expensive queries on the request path.
/// </summary>
public sealed class StatisticsAggregationService(
    IServiceScopeFactory scopeFactory,
    StatisticsService statisticsService,
    ILogger<StatisticsAggregationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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

        await AggregateGlobalStatisticsAsync(db, ct);
        await AggregateDigipeaterStatisticsAsync(db, ct);
        await AggregateCoverageStatisticsAsync(db, ct);

        logger.LogDebug("Statistics aggregation complete.");
    }

    // -------------------------------------------------------------------------
    // Global statistics
    // -------------------------------------------------------------------------

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

        // Grid squares — union of packet and station grids
        var packetGrids = await db.Packets
            .Where(p => p.GridSquare != null && p.GridSquare != "")
            .Select(p => p.GridSquare!)
            .Distinct()
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

    // -------------------------------------------------------------------------
    // Digipeater statistics
    // -------------------------------------------------------------------------

    private async Task AggregateDigipeaterStatisticsAsync(DireControlContext db, CancellationToken ct)
    {
        var cutoff24h = DateTime.UtcNow.AddHours(-24);

        var packetPaths = await db.Packets
            .AsNoTracking()
            .Where(p => p.Path != null && p.Path != string.Empty)
            .Select(p => new { p.Path, p.ReceivedAt })
            .ToListAsync(ct);

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var last24h = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hopPositionSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hopPositionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkt in packetPaths)
        {
            var entries = pkt.Path.Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                var callsign = entries[i].TrimEnd('*').Trim();
                if (string.IsNullOrWhiteSpace(callsign) || AprsPathParser.IsGenericAlias(callsign))
                    continue;

                totals.TryAdd(callsign, 0);
                last24h.TryAdd(callsign, 0);
                hopPositionSums.TryAdd(callsign, 0.0);
                hopPositionCounts.TryAdd(callsign, 0);

                totals[callsign]++;
                if (pkt.ReceivedAt >= cutoff24h)
                    last24h[callsign]++;
                hopPositionSums[callsign] += i + 1;
                hopPositionCounts[callsign]++;
            }
        }

        var now = DateTime.UtcNow;

        // Load existing rows for bulk upsert
        var existingRows = await db.DigipeaterStatistics.ToDictionaryAsync(d => d.Callsign, ct);

        foreach (var (callsign, total) in totals)
        {
            if (existingRows.TryGetValue(callsign, out var existing))
            {
                existing.TotalPacketsForwarded = total;
                existing.Last24hPackets = last24h.GetValueOrDefault(callsign);
                existing.HopPositionSum = hopPositionSums.GetValueOrDefault(callsign);
                existing.HopPositionCount = hopPositionCounts.GetValueOrDefault(callsign);
                existing.LastComputedAt = now;
            }
            else
            {
                db.DigipeaterStatistics.Add(new DigipeaterStatistic
                {
                    Callsign = callsign,
                    TotalPacketsForwarded = total,
                    Last24hPackets = last24h.GetValueOrDefault(callsign),
                    HopPositionSum = hopPositionSums.GetValueOrDefault(callsign),
                    HopPositionCount = hopPositionCounts.GetValueOrDefault(callsign),
                    LastComputedAt = now,
                });
            }
        }

        // Remove rows for callsigns no longer in the data
        var staleRows = existingRows.Keys
            .Where(k => !totals.ContainsKey(k))
            .Select(k => existingRows[k]);
        db.DigipeaterStatistics.RemoveRange(staleRows);

        await db.SaveChangesAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Coverage grid statistics
    // -------------------------------------------------------------------------

    private async Task AggregateCoverageStatisticsAsync(DireControlContext db, CancellationToken ct)
    {
        var raw = await db.Packets
            .AsNoTracking()
            .Where(p => p.GridSquare != null && p.GridSquare != string.Empty &&
                        p.Latitude != null && p.Longitude != null)
            .GroupBy(p => p.GridSquare!)
            .Select(g => new
            {
                GridSquare = g.Key,
                PacketCount = g.Count(),
                AvgLat = g.Average(p => p.Latitude!.Value),
                AvgLon = g.Average(p => p.Longitude!.Value),
            })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var existingRows = await db.CoverageGridStatistics.ToDictionaryAsync(c => c.GridSquare, ct);

        foreach (var row in raw)
        {
            if (existingRows.TryGetValue(row.GridSquare, out var existing))
            {
                existing.PacketCount = row.PacketCount;
                existing.AvgLat = row.AvgLat;
                existing.AvgLon = row.AvgLon;
                existing.LastComputedAt = now;
            }
            else
            {
                db.CoverageGridStatistics.Add(new CoverageGridStatistic
                {
                    GridSquare = row.GridSquare,
                    PacketCount = row.PacketCount,
                    AvgLat = row.AvgLat,
                    AvgLon = row.AvgLon,
                    LastComputedAt = now,
                });
            }
        }

        var activeGrids = new HashSet<string>(raw.Select(r => r.GridSquare));
        var staleRows = existingRows.Keys
            .Where(k => !activeGrids.Contains(k))
            .Select(k => existingRows[k]);
        db.CoverageGridStatistics.RemoveRange(staleRows);

        await db.SaveChangesAsync(ct);
    }
}

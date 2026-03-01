using DireControl.Api.Contracts;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Computes global APRS statistics and caches the result for up to 5 minutes.
/// </summary>
public sealed class StatisticsService(
    IServiceScopeFactory scopeFactory,
    ILogger<StatisticsService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private StatisticsDto? _cache;
    private DateTime _cacheExpiresAt = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<StatisticsDto> GetStatisticsAsync(CancellationToken ct = default)
    {
        if (_cache is not null && DateTime.UtcNow < _cacheExpiresAt)
            return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTime.UtcNow < _cacheExpiresAt)
                return _cache;

            _cache = await ComputeAsync(ct);
            _cacheExpiresAt = DateTime.UtcNow.Add(CacheDuration);
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forces the cache to be invalidated on the next request.</summary>
    public void Invalidate() => _cacheExpiresAt = DateTime.MinValue;

    private async Task<StatisticsDto> ComputeAsync(CancellationToken ct)
    {
        logger.LogDebug("Recomputing global statistics.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var weekStart = todayStart.AddDays(-7);
        var h24Start = now.AddHours(-24);

        // ---- Packet counts ----
        var packetsToday = await db.Packets
            .CountAsync(p => p.ReceivedAt >= todayStart, ct);

        // ---- Unique station counts ----
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

        // ---- Packets per hour for last 24h (index 0 = oldest hour) ----
        var recentTimes = await db.Packets
            .Where(p => p.ReceivedAt >= h24Start)
            .Select(p => p.ReceivedAt)
            .ToListAsync(ct);

        var packetsPerHour = new int[24];
        foreach (var ts in recentTimes)
        {
            var hoursAgo = (int)(now - ts).TotalHours;
            if (hoursAgo >= 0 && hoursAgo < 24)
                packetsPerHour[23 - hoursAgo]++;
        }

        // ---- Busiest digipeaters: stations with type Digipeater ranked by packet count ----
        // We approximate "forwarded" packets by counting all packets whose path string
        // contains the digipeater's callsign.
        var digiCallsigns = await db.Stations
            .Where(s => s.StationType == StationType.Digipeater)
            .Select(s => s.Callsign)
            .ToListAsync(ct);

        var digiCounts = new List<CallsignCountDto>(digiCallsigns.Count);
        foreach (var digi in digiCallsigns)
        {
            // EF Core translates Contains to LIKE '%digi%' in SQLite
            var count = await db.Packets
                .CountAsync(p => p.Path.Contains(digi), ct);

            if (count > 0)
                digiCounts.Add(new CallsignCountDto { Callsign = digi, Count = count });
        }

        var topDigis = digiCounts
            .OrderByDescending(d => d.Count)
            .Take(10)
            .ToList();

        // ---- Busiest stations by average beacon rate (from StationStatistic) ----
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

        // ---- Recently first-heard stations (most recent first) ----
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

        // ---- Unique grid squares from packets + station grid squares ----
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

        return new StatisticsDto
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
    }
}

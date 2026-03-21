using DireControl.Api.Controllers.Models;

namespace DireControl.Api.Services;

/// <summary>
/// Holds the pre-computed global statistics snapshot. The snapshot is populated
/// by <see cref="StatisticsAggregationService"/> on a periodic schedule.
/// </summary>
public sealed class StatisticsService
{
    private volatile StatisticsDto? _snapshot;

    /// <summary>Returns the latest snapshot, or an empty DTO if none has been computed yet.</summary>
    public StatisticsDto GetStatistics() => _snapshot ?? Empty;

    /// <summary>Called by the aggregation service to push a freshly computed snapshot.</summary>
    public void SetSnapshot(StatisticsDto dto) => _snapshot = dto;

    private static readonly StatisticsDto Empty = new()
    {
        PacketsToday = 0,
        UniqueStationsToday = 0,
        UniqueStationsThisWeek = 0,
        UniqueStationsAllTime = 0,
        PacketsPerHour = new int[24],
        BusiestDigipeaters = [],
        BusiestStations = [],
        RecentlyFirstHeard = [],
        GridSquares = [],
    };
}

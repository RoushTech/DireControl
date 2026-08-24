using DireControl.Enums;

namespace DireControl.Api.Controllers.Models;

/// <summary>One radio's direct-RF reception for one local calendar day.</summary>
public sealed class RfHeardDailyDto
{
    /// <summary>Local calendar day, ISO-8601 date (no time component).</summary>
    public required DateOnly Day { get; init; }

    public required int ChannelNumber { get; init; }

    /// <summary>Radio currently configured on this channel, or null if none is.</summary>
    public string? RadioId { get; init; }

    /// <summary>Radio name, falling back to "Channel N" for history with no radio attached.</summary>
    public required string RadioName { get; init; }

    public int UniqueDirectStations { get; init; }
    public int NewDirectStations { get; init; }
    public int DirectPackets { get; init; }
    public double? MaxDirectDistanceKm { get; init; }
    public double? MedianDirectDistanceKm { get; init; }
    public int DirectStationsWithPosition { get; init; }
}

/// <summary>A station this radio has heard directly on RF.</summary>
public sealed class RfHeardStationDto
{
    public required string Callsign { get; init; }
    public required int ChannelNumber { get; init; }
    public string? RadioId { get; init; }
    public required string RadioName { get; init; }

    public DateTime FirstHeardDirect { get; init; }
    public DateTime LastHeardDirect { get; init; }
    public int DirectPacketCount { get; init; }

    /// <summary>
    /// Distance from home to the station's last known position, in km. This is where the
    /// station is <i>now</i>, not the farthest copy ever heard — for a mobile the two differ.
    /// Null when either end has no position.
    /// </summary>
    public double? DistanceKm { get; init; }

    /// <summary>APRS symbol, when the station is still known. Null once it has expired.</summary>
    public string? Symbol { get; init; }

    public StationType StationType { get; init; }

    /// <summary>Last seen by any source, when the station is still known.</summary>
    public DateTime? LastSeen { get; init; }
}

/// <summary>Headline direct-RF figures for one radio.</summary>
public sealed class RfHeardSummaryDto
{
    public required int ChannelNumber { get; init; }
    public string? RadioId { get; init; }
    public required string RadioName { get; init; }

    public int UniqueDirectToday { get; init; }
    public int UniqueDirect7d { get; init; }
    public int UniqueDirect30d { get; init; }

    /// <summary>Distinct stations heard direct on this channel across all retained packets.</summary>
    public int UniqueDirectAllTime { get; init; }

    /// <summary>
    /// Farthest station ever heard direct on this channel, km from home — read from the daily
    /// archive rather than from packets, so it does not shrink when old packets are pruned.
    /// </summary>
    public double? BestDistanceKm { get; init; }

    public DateTime? LastHeardDirect { get; init; }
}

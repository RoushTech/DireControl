using System.Linq.Expressions;
using DireControl.Data.Models;

namespace DireControl.Api.Controllers.Models;

public static class PacketProjections
{
    /// <summary>
    /// Server-side EF projection from <see cref="Packet"/> to <see cref="PacketDto"/>.
    /// </summary>
    public static readonly Expression<Func<Packet, PacketDto>> ToPacketDto = p => new PacketDto
    {
        Id = p.Id,
        StationCallsign = p.StationCallsign,
        ReceivedAt = p.ReceivedAt,
        RawPacket = p.RawPacket,
        ParsedType = p.ParsedType,
        Source = p.Source,
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        Path = p.Path,
        ResolvedPath = p.ResolvedPath,
        HopCount = p.HopCount,
        UnknownHopCount = p.UnknownHopCount,
        IsDirectHeard = p.HopCount == 0,
        Comment = p.Comment,
        WeatherData = p.WeatherData,
        TelemetryData = p.TelemetryData,
        MessageData = p.MessageData,
        SignalData = p.SignalData,
        GridSquare = p.GridSquare,
    };
}

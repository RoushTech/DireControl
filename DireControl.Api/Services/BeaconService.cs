using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Sends an immediate APRS position beacon via KISS and records it as an
/// <see cref="OwnBeacon"/> in the database so it always appears in the UI
/// regardless of whether it is heard back from RF (e.g. when a collision
/// prevents the normal KISS echo from arriving).
/// </summary>
public sealed class BeaconService(
    KissConnectionHolder connectionHolder,
    IHubContext<PacketHub> hubContext,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<BeaconService> logger)
{
    /// <summary>
    /// Transmits a position beacon for <paramref name="radio"/> and immediately
    /// records it as a <see cref="OwnBeacon"/> with <c>HopCount = -2</c> and
    /// <c>Heard = false</c>.  The record is upgraded to <c>HopCount = 0</c> /
    /// <c>Heard = true</c> when the KISS echo or a digi confirmation arrives.
    /// Returns the saved record, or <see langword="null"/> if the beacon could
    /// not be sent (no connection, or home position not configured).
    /// </summary>
    public async Task<OwnBeacon?> BeaconNowAsync(Radio radio, CancellationToken ct = default)
    {
        var opts = options.Value;

        if (opts.HomeLat is null || opts.HomeLon is null)
        {
            logger.LogWarning(
                "Cannot beacon for {Callsign}: home position not configured.",
                radio.FullCallsign);
            return null;
        }

        var lat = opts.HomeLat.Value;
        var lon = opts.HomeLon.Value;
        var path = radio.BeaconPath ?? string.Empty;

        var info = BuildPositionInfo(lat, lon, radio.BeaconSymbol ?? "/-", radio.BeaconComment);
        var frame = Ax25Frame.BuildUiFrame(radio.FullCallsign, info, path);

        if (!connectionHolder.TrySend(frame))
        {
            logger.LogWarning(
                "Cannot beacon for {Callsign}: no active Direwolf connection.",
                radio.FullCallsign);
            return null;
        }

        logger.LogInformation(
            "Sent beacon for {Callsign} (path={Path}).",
            radio.FullCallsign,
            string.IsNullOrEmpty(path) ? "(direct)" : path);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var beacon = new OwnBeacon
        {
            RadioId = radio.Id,
            BeaconedAt = DateTime.UtcNow,
            Latitude = lat,
            Longitude = lon,
            Comment = string.IsNullOrEmpty(radio.BeaconComment) ? null : radio.BeaconComment,
            PathUsed = string.IsNullOrEmpty(path) ? null : path,
            HopCount = -2,
            Heard = false,
        };

        db.OwnBeacons.Add(beacon);
        await db.SaveChangesAsync(ct);

        await hubContext.Clients.All.SendAsync(PacketHub.OwnBeaconReceivedMethod, new OwnBeaconBroadcastDto
        {
            RadioId = radio.Id,
            BeaconId = beacon.Id,
            FullCallsign = radio.FullCallsign,
            BeaconedAt = beacon.BeaconedAt,
            Lat = lat,
            Lon = lon,
            PathUsed = beacon.PathUsed,
            Heard = false,
        }, ct);

        return beacon;
    }

    private static string BuildPositionInfo(double lat, double lon, string symbol, string? comment)
    {
        var latAbs = Math.Abs(lat);
        var latDeg = (int)latAbs;
        var latMin = (latAbs - latDeg) * 60.0;
        var latDir = lat >= 0 ? 'N' : 'S';

        var lonAbs = Math.Abs(lon);
        var lonDeg = (int)lonAbs;
        var lonMin = (lonAbs - lonDeg) * 60.0;
        var lonDir = lon >= 0 ? 'E' : 'W';

        var symbolTable = symbol.Length >= 1 ? symbol[0] : '/';
        var symbolCode = symbol.Length >= 2 ? symbol[1] : '-';

        var commentPart = string.IsNullOrEmpty(comment) ? string.Empty : comment;

        return $"!{latDeg:D2}{latMin:00.00}{latDir}{symbolTable}{lonDeg:D3}{lonMin:00.00}{lonDir}{symbolCode}{commentPart}";
    }
}

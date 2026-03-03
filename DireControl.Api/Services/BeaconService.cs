using System.Text;
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
    /// records it as a <see cref="OwnBeacon"/> with <c>HopCount = 0</c>.
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

        var lat  = opts.HomeLat.Value;
        var lon  = opts.HomeLon.Value;
        var path = radio.BeaconPath ?? string.Empty;

        var info  = BuildPositionInfo(lat, lon, radio.BeaconSymbol ?? "/-", radio.BeaconComment);
        var frame = BuildAx25Frame(radio.FullCallsign, info, path);

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
            RadioId    = radio.Id,
            BeaconedAt = DateTime.UtcNow,
            Latitude   = lat,
            Longitude  = lon,
            Comment    = string.IsNullOrEmpty(radio.BeaconComment) ? null : radio.BeaconComment,
            PathUsed   = string.IsNullOrEmpty(path) ? null : path,
            HopCount   = 0,
        };

        db.OwnBeacons.Add(beacon);
        await db.SaveChangesAsync(ct);

        await hubContext.Clients.All.SendAsync(PacketHub.OwnBeaconReceivedMethod, new OwnBeaconBroadcastDto
        {
            RadioId      = radio.Id,
            BeaconId     = beacon.Id,
            FullCallsign = radio.FullCallsign,
            BeaconedAt   = beacon.BeaconedAt,
            Lat          = lat,
            Lon          = lon,
            PathUsed     = beacon.PathUsed,
        }, ct);

        return beacon;
    }

    // ── APRS position info field ───────────────────────────────────────────────

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
        var symbolCode  = symbol.Length >= 2 ? symbol[1] : '-';

        var commentPart = string.IsNullOrEmpty(comment) ? string.Empty : comment;

        return $"!{latDeg:D2}{latMin:00.00}{latDir}{symbolTable}{lonDeg:D3}{lonMin:00.00}{lonDir}{symbolCode}{commentPart}";
    }

    // ── AX.25 frame encoding (mirrors MessageSendingService) ──────────────────

    private static byte[] BuildAx25Frame(string sourceCallsign, string aprsInfo, string path)
    {
        const string destination = "APRS";

        var (destBase, destSsid) = SplitCallsign(destination);
        var (srcBase,  srcSsid)  = SplitCallsign(sourceCallsign);

        var pathItems = string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var frame = new List<byte>(128);

        frame.AddRange(EncodeAddress(destBase, destSsid, isLast: false));
        frame.AddRange(EncodeAddress(srcBase,  srcSsid,  isLast: pathItems.Length == 0));

        for (var i = 0; i < pathItems.Length; i++)
        {
            var (dBase, dSsid) = SplitCallsign(pathItems[i]);
            frame.AddRange(EncodeAddress(dBase, dSsid, isLast: i == pathItems.Length - 1));
        }

        frame.Add(0x03); // Control: Unnumbered Information (UI)
        frame.Add(0xF0); // PID: no layer-3 protocol

        frame.AddRange(Encoding.ASCII.GetBytes(aprsInfo));

        return [.. frame];
    }

    private static byte[] EncodeAddress(string callsign, int ssid, bool isLast)
    {
        var padded = callsign.ToUpperInvariant().PadRight(6)[..6];
        var bytes  = new byte[7];
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)((padded[i] & 0x7F) << 1);
        bytes[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (isLast ? 0x01 : 0x00));
        return bytes;
    }

    private static (string callsign, int ssid) SplitCallsign(string raw)
    {
        var parts = raw.Split('-', 2);
        var ssid  = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (parts[0], ssid);
    }
}

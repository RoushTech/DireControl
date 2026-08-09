using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Modem.Ax25;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace DireControl.Api.Services;

/// <summary>
/// Sends an immediate APRS position beacon over RF and records it as an
/// <see cref="OwnBeacon"/> in the database so it always appears in the UI
/// regardless of whether it is heard back from RF (e.g. when a collision
/// prevents the normal KISS echo from arriving).
/// </summary>
public sealed class BeaconService(
    IFrameTransmitter transmitter,
    IHubContext<PacketHub> hubContext,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<BeaconService> logger)
{
    /// <summary>Comment template used when the radio has none configured.</summary>
    internal const string DefaultCommentTemplate = "DireControl v{version}";

    /// <summary>
    /// App version substituted for <c>{version}</c> tokens, with any
    /// "+buildmetadata" suffix trimmed off the informational version.
    /// </summary>
    internal static readonly string Version = ResolveVersion(
        typeof(BeaconService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    internal static string ResolveVersion(string? informationalVersion)
    {
        var version = informationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>
    /// The comment actually transmitted for <paramref name="radio"/>: the
    /// configured comment (or <see cref="DefaultCommentTemplate"/> when blank)
    /// with <c>{version}</c> tokens expanded.
    /// </summary>
    internal static string EffectiveComment(Radio radio) =>
        EffectiveComment(radio, Version);

    internal static string EffectiveComment(Radio radio, string version)
    {
        var template = string.IsNullOrWhiteSpace(radio.BeaconComment)
            ? DefaultCommentTemplate
            : radio.BeaconComment;
        return template.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);
    }

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

        var comment = EffectiveComment(radio);
        var info = BuildPositionInfo(lat, lon, radio.BeaconSymbol ?? "/-", comment);
        var frame = Ax25Encoder.EncodeUiFrame(radio.FullCallsign, info, path);

        if (!transmitter.TrySend(frame, radio.ChannelNumber))
        {
            logger.LogWarning(
                "Cannot beacon for {Callsign}: no RF transmit backend available.",
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
            Comment = comment,
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
        var symbolCode = symbol.Length >= 2 ? symbol[1] : '-';

        var commentPart = string.IsNullOrEmpty(comment) ? string.Empty : comment;

        return $"!{latDeg:D2}{latMin:00.00}{latDir}{symbolTable}{lonDeg:D3}{lonMin:00.00}{lonDir}{symbolCode}{commentPart}";
    }
}

using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Background service that checks alert rules (WatchList, Proximity, Geofence) for
/// every station callsign pushed to <see cref="PendingAlertChannel"/> by the packet
/// parsing pipeline.
/// </summary>
public sealed class AlertingService(
    PendingAlertChannel channel,
    IServiceScopeFactory scopeFactory,
    IHubContext<PacketHub> hubContext,
    IOptions<DireControlOptions> options,
    ILogger<AlertingService> logger) : BackgroundService
{
    // WatchList: tracks the last time we processed an active packet for each callsign.
    private readonly Dictionary<string, DateTime> _lastSeenTimes = [];

    // Proximity: tracks whether a callsign was inside each rule (ruleId → inside).
    private readonly Dictionary<string, Dictionary<int, bool>> _insideRule = [];

    // Geofence: tracks whether a callsign was inside each geofence (fenceId → inside).
    private readonly Dictionary<string, Dictionary<int, bool>> _insideFence = [];

    private bool _seeded;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var callsign in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

                if (!_seeded)
                    await SeedAsync(db, stoppingToken);

                await CheckStationAsync(callsign, db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking alerts for station {Callsign}.", callsign);
            }
        }

        logger.LogInformation("AlertingService stopped.");
    }

    // -------------------------------------------------------------------------
    // Seeding — run once on first packet
    // -------------------------------------------------------------------------

    private async Task SeedAsync(DireControlContext db, CancellationToken ct)
    {
        _seeded = true;
        var now = DateTime.UtcNow;
        var opts = options.Value;

        // Seed _lastSeenTimes with watch-listed stations that are currently active so
        // we don't fire spurious alerts for stations already on air.
        var watched = await db.Stations
            .Where(s => s.IsOnWatchList)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var s in watched)
        {
            var threshold = opts.GetExpiryMinutes(s.StationType);
            if ((now - s.LastSeen).TotalMinutes < threshold)
                _lastSeenTimes[s.Callsign] = s.LastSeen;
        }

        logger.LogInformation("AlertingService seeded with {Count} active watched stations.", _lastSeenTimes.Count);
    }

    // -------------------------------------------------------------------------
    // Per-callsign rule evaluation
    // -------------------------------------------------------------------------

    private async Task CheckStationAsync(string callsign, DireControlContext db, CancellationToken ct)
    {
        var station = await db.Stations
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Callsign == callsign, ct);

        if (station is null) return;

        await CheckWatchListAsync(station, db, ct);

        if (station.LastLat.HasValue && station.LastLon.HasValue)
        {
            await CheckProximityAsync(station, db, ct);
            await CheckGeofencesAsync(station, db, ct);
        }
    }

    // -------------------------------------------------------------------------
    // WatchList
    // -------------------------------------------------------------------------

    private async Task CheckWatchListAsync(Station station, DireControlContext db, CancellationToken ct)
    {
        if (!station.IsOnWatchList) return;

        var opts = options.Value;
        var callsign = station.Callsign;
        var threshold = opts.GetExpiryMinutes(station.StationType);
        var now = DateTime.UtcNow;
        var isTransition = false;

        if (_lastSeenTimes.TryGetValue(callsign, out var prev))
        {
            // Was stale if we've not seen it within the threshold window since last tracking
            if ((now - prev).TotalMinutes >= threshold)
                isTransition = true;
        }
        else
        {
            // First time we've seen this station — only fire if it was stale before
            isTransition = (now - station.LastSeen).TotalMinutes < 0.5 &&
                           station.FirstSeen < now.AddMinutes(-threshold);
        }

        _lastSeenTimes[callsign] = now;

        if (!isTransition) return;

        var alert = new Alert
        {
            AlertType = AlertType.WatchList,
            Callsign = callsign,
            TriggeredAt = now,
            Detail = new AlertDetail
            {
                StationLat = station.LastLat,
                StationLon = station.LastLon,
            },
            IsAcknowledged = false,
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);

        await BroadcastAlertAsync(alert, ct);
        logger.LogInformation("WatchList alert fired for {Callsign}.", callsign);
    }

    // -------------------------------------------------------------------------
    // Proximity
    // -------------------------------------------------------------------------

    private async Task CheckProximityAsync(Station station, DireControlContext db, CancellationToken ct)
    {
        var rules = await db.ProximityRules
            .Where(r => r.IsActive &&
                        (r.TargetCallsign == null || r.TargetCallsign == station.Callsign))
            .AsNoTracking()
            .ToListAsync(ct);

        if (rules.Count == 0) return;

        var callsign = station.Callsign;
        var stationLat = station.LastLat!.Value;
        var stationLon = station.LastLon!.Value;

        if (!_insideRule.TryGetValue(callsign, out var ruleStates))
        {
            ruleStates = [];
            _insideRule[callsign] = ruleStates;
        }

        foreach (var rule in rules)
        {
            var dist = HaversineMeters(stationLat, stationLon, rule.CenterLat, rule.CenterLon);
            var isNowInside = dist <= rule.RadiusMetres;
            var wasInside = ruleStates.TryGetValue(rule.Id, out var p) && p;

            ruleStates[rule.Id] = isNowInside;

            if (!isNowInside || wasInside) continue; // only fire on entry

            var now = DateTime.UtcNow;
            var alert = new Alert
            {
                AlertType = AlertType.Proximity,
                Callsign = callsign,
                TriggeredAt = now,
                Detail = new AlertDetail
                {
                    DistanceMeters = dist,
                    StationLat = stationLat,
                    StationLon = stationLon,
                    RuleName = rule.Name,
                },
                IsAcknowledged = false,
            };
            db.Alerts.Add(alert);
            await db.SaveChangesAsync(ct);

            await BroadcastAlertAsync(alert, ct);
            logger.LogInformation("Proximity alert fired for {Callsign} against rule '{Rule}' (dist={Dist:F0}m).",
                callsign, rule.Name, dist);
        }
    }

    // -------------------------------------------------------------------------
    // Geofence
    // -------------------------------------------------------------------------

    private async Task CheckGeofencesAsync(Station station, DireControlContext db, CancellationToken ct)
    {
        var fences = await db.Geofences
            .Where(f => f.IsActive)
            .AsNoTracking()
            .ToListAsync(ct);

        if (fences.Count == 0) return;

        var callsign = station.Callsign;
        var stationLat = station.LastLat!.Value;
        var stationLon = station.LastLon!.Value;

        if (!_insideFence.TryGetValue(callsign, out var fenceStates))
        {
            fenceStates = [];
            _insideFence[callsign] = fenceStates;
        }

        foreach (var fence in fences)
        {
            var dist = HaversineMeters(stationLat, stationLon, fence.CenterLat, fence.CenterLon);
            var isNowInside = dist <= fence.RadiusMeters;
            var wasInside = fenceStates.TryGetValue(fence.Id, out var p) && p;

            fenceStates[fence.Id] = isNowInside;

            string? direction = null;
            if (isNowInside && !wasInside && fence.AlertOnEnter)
                direction = "entered";
            else if (!isNowInside && wasInside && fence.AlertOnExit)
                direction = "exited";

            if (direction is null) continue;

            var now = DateTime.UtcNow;
            var alert = new Alert
            {
                AlertType = AlertType.Geofence,
                Callsign = callsign,
                TriggeredAt = now,
                Detail = new AlertDetail
                {
                    GeofenceName = fence.Name,
                    Direction = direction,
                    StationLat = stationLat,
                    StationLon = stationLon,
                    DistanceMeters = dist,
                },
                IsAcknowledged = false,
            };
            db.Alerts.Add(alert);
            await db.SaveChangesAsync(ct);

            await BroadcastAlertAsync(alert, ct);
            logger.LogInformation("Geofence alert fired for {Callsign} {Direction} '{Fence}'.",
                callsign, direction, fence.Name);
        }
    }

    // -------------------------------------------------------------------------
    // Broadcast helper
    // -------------------------------------------------------------------------

    private async Task BroadcastAlertAsync(Alert alert, CancellationToken ct)
    {
        var dto = new AlertBroadcastDto
        {
            Id = alert.Id,
            AlertTypeName = alert.AlertType.ToString(),
            Callsign = alert.Callsign,
            TriggeredAt = alert.TriggeredAt,
            GeofenceName = alert.Detail.GeofenceName,
            Direction = alert.Detail.Direction,
            RuleName = alert.Detail.RuleName,
            DistanceMeters = alert.Detail.DistanceMeters,
        };

        await hubContext.Clients.All.SendAsync(PacketHub.AlertReceivedMethod, dto, ct);
    }

    // -------------------------------------------------------------------------
    // Haversine formula
    // -------------------------------------------------------------------------

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000; // Earth radius in metres
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

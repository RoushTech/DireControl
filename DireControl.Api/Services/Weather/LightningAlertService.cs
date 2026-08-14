using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Watches the in-memory strike buffer and broadcasts a SignalR alert when a strike
/// lands within the user-configured radius of the home position. Settings are re-read
/// from the database every cycle, so changes apply without a restart.
/// </summary>
public sealed class LightningAlertService(
    LightningStrikeBuffer buffer,
    IHubContext<PacketHub> hubContext,
    IOptions<DireControlOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<LightningAlertService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    // Strike timestamps come from Blitzortung and can lag arrival slightly, so
    // successive query windows overlap; the alert cooldown suppresses duplicates.
    private static readonly TimeSpan WindowOverlap = TimeSpan.FromSeconds(30);

    private const int MaxStrikesPerQuery = 5000;

    private DateTime _windowStartUtc = DateTime.UtcNow;
    private DateTime? _lastAlertUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lightning alert check failed; will retry next cycle");
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        // Advance the window even while disabled so enabling the alert doesn't
        // immediately fire on strikes that happened before it was turned on.
        var cutoff = _windowStartUtc - WindowOverlap;
        _windowStartUtc = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is not { LightningAlertEnabled: true })
            return;

        var opt = options.Value;
        if (opt.HomeLat is not { } homeLat || opt.HomeLon is not { } homeLon)
            return;

        var radiusKm = Math.Clamp(setting.LightningAlertRadiusKm, 1, 500);
        var (minLat, maxLat, minLon, maxLon) = LightningAlertLogic.BoundingBox(homeLat, homeLon, radiusKm);
        var strikes = buffer.Query(minLat, maxLat, minLon, maxLon, cutoff, MaxStrikesPerQuery);

        var closest = LightningAlertLogic.FindClosest(strikes, homeLat, homeLon, radiusKm);
        if (closest is null)
            return;

        var cooldown = TimeSpan.FromMinutes(Math.Clamp(setting.LightningAlertCooldownMinutes, 0, 120));
        var now = DateTime.UtcNow;
        if (!LightningAlertLogic.CooldownElapsed(now, _lastAlertUtc, cooldown))
            return;
        _lastAlertUtc = now;

        var (strike, distanceKm) = closest.Value;
        var dto = new LightningAlertDto
        {
            DistanceKm = distanceKm,
            BearingDegrees = LightningAlertLogic.BearingDegrees(homeLat, homeLon, strike.Latitude, strike.Longitude),
            Latitude = strike.Latitude,
            Longitude = strike.Longitude,
            StrikeTimeUtc = strike.TimeUtc,
            RadiusKm = radiusKm,
        };

        await hubContext.Clients.All.SendAsync(PacketHub.LightningAlertMethod, dto, ct);
        logger.LogInformation("Lightning alert: strike {DistanceKm:F1} km from home (alert radius {RadiusKm} km)",
            distanceKm, radiusKm);
    }
}

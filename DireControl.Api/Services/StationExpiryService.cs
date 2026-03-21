using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

public class StationExpiryService(
    IServiceScopeFactory scopeFactory,
    IHubContext<PacketHub> hubContext,
    IOptions<DireControlOptions> options,
    ILogger<StationExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    // Callsigns already known to be stale — used to identify newly stale stations each cycle.
    private readonly HashSet<string> _knownStale = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a short moment so the rest of the app finishes starting up.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckExpiryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during station expiry check");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckExpiryAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var now = DateTime.UtcNow;

        var mobileCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Mobile));
        var fixedCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Fixed));
        var weatherCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Weather));
        var digiCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Digipeater));
        var igateCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.IGate));
        var unknownCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Unknown));
        var gatewayCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Gateway));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var currentlyStale = await db.Stations
            .AsNoTracking()
            .Where(s =>
                (s.StationType == StationType.Mobile && s.LastSeen < mobileCutoff) ||
                (s.StationType == StationType.Fixed && s.LastSeen < fixedCutoff) ||
                (s.StationType == StationType.Weather && s.LastSeen < weatherCutoff) ||
                (s.StationType == StationType.Digipeater && s.LastSeen < digiCutoff) ||
                (s.StationType == StationType.IGate && s.LastSeen < igateCutoff) ||
                (s.StationType == StationType.Unknown && s.LastSeen < unknownCutoff) ||
                (s.StationType == StationType.Gateway && s.LastSeen < gatewayCutoff))
            .Select(s => s.Callsign)
            .ToListAsync(ct);

        var currentlyStaleSet = currentlyStale.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Stations that are now stale but were not stale in the previous cycle.
        var newlyStale = currentlyStaleSet
            .Where(c => !_knownStale.Contains(c))
            .ToList();

        // Update the known-stale set: add newly stale, remove any that are no longer stale
        // (e.g. a station sent a packet since last check and is active again).
        _knownStale.IntersectWith(currentlyStaleSet);
        foreach (var callsign in newlyStale)
            _knownStale.Add(callsign);

        if (newlyStale.Count == 0)
            return;

        logger.LogInformation(
            "Broadcasting {Count} newly stale stations: {Callsigns}",
            newlyStale.Count,
            string.Join(", ", newlyStale));

        await hubContext.Clients.All.SendAsync(
            PacketHub.StationsStaleMethod,
            newlyStale,
            ct);
    }
}

using DireControl.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace DireControl.Api.Services;

/// <summary>
/// Background service that automatically transmits position beacons for radios
/// with <see cref="Data.Models.Radio.AutoBeaconEnabled"/> set, on each radio's
/// configured <see cref="Data.Models.Radio.AutoBeaconIntervalSeconds"/>.  The
/// actual transmit + persistence is delegated to <see cref="BeaconService"/>,
/// the same path used by the manual "Beacon Now" button.
/// </summary>
public sealed class AutoBeaconService(
    IServiceScopeFactory scopeFactory,
    BeaconService beaconService,
    ILogger<AutoBeaconService> logger) : BackgroundService
{
    private const int PollIntervalMs = 15_000;

    /// <summary>
    /// Last attempt time per radio id, tracked in-memory so a radio that cannot
    /// beacon (no home position / no TX backend) is retried at most once per
    /// interval instead of on every poll.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastAttempt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueBeaconsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in AutoBeaconService.");
            }

            await Task.Delay(PollIntervalMs, stoppingToken);
        }

        logger.LogInformation("AutoBeaconService stopped.");
    }

    private async Task ProcessDueBeaconsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var radios = await db.Radios
            .AsNoTracking()
            .Where(r => r.IsActive && r.AutoBeaconEnabled)
            .ToListAsync(ct);

        if (radios.Count == 0)
            return;

        var ids = radios.Select(r => r.Id).ToList();

        var lastBeaconMap = (await db.OwnBeacons
            .AsNoTracking()
            .Where(b => ids.Contains(b.RadioId))
            .GroupBy(b => b.RadioId)
            .Select(g => new { RadioId = g.Key, Last = g.Max(b => b.BeaconedAt) })
            .ToListAsync(ct))
            .ToDictionary(x => x.RadioId, x => x.Last);

        var now = DateTime.UtcNow;

        foreach (var radio in radios)
        {
            lastBeaconMap.TryGetValue(radio.Id, out var lastBeacon);
            _lastAttempt.TryGetValue(radio.Id, out var lastAttempt);

            var due = AutoBeaconLogic.IsDue(
                now,
                lastBeaconMap.ContainsKey(radio.Id) ? lastBeacon : null,
                lastAttempt == default ? null : lastAttempt,
                radio.AutoBeaconIntervalSeconds);

            if (!due)
                continue;

            _lastAttempt[radio.Id] = now;
            await beaconService.BeaconNowAsync(radio, ct);
        }

        // Drop attempt records for radios that are no longer auto-beaconing so
        // the dictionary cannot grow without bound as radios come and go.
        foreach (var staleId in _lastAttempt.Keys.Where(k => !ids.Contains(k)).ToList())
            _lastAttempt.TryRemove(staleId, out _);
    }
}

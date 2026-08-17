using System.Threading.Channels;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Broadcasts a SignalR alert when a strike lands within the user-configured radius of the
/// home position. Strikes are judged the instant the feed delivers them rather than on a
/// poll, so the only delay left is the feed's own — that lag is reported on every alert.
/// Settings are refreshed from the database in the background, so changes apply without a
/// restart and without a database read on the alert path.
/// </summary>
public sealed class LightningAlertService(
    LightningStrikeBuffer buffer,
    IHubContext<PacketHub> hubContext,
    IOptions<DireControlOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<LightningAlertService> logger) : BackgroundService
{
    private static readonly TimeSpan ConfigRefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>Alert settings snapshot, read on the feed's thread — immutable by design.</summary>
    private sealed record AlertConfig(double HomeLat, double HomeLon, double RadiusKm, TimeSpan Cooldown);

    // A storm delivers strikes far faster than anyone needs alerting about, and the cooldown
    // discards the surplus anyway; a single-slot channel keeps the newest candidate waiting
    // and drops the rest instead of queueing work that will be thrown away.
    private readonly Channel<LightningStrike> _candidates = Channel.CreateBounded<LightningStrike>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private volatile AlertConfig? _config;
    private DateTime _configLoadedAt = DateTime.MinValue;
    private DateTime? _lastAlertUtc;
    private LightningStrike? _lastAlertedStrike;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshConfigAsync(stoppingToken);
        buffer.StrikeReceived += OnStrikeReceived;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow - _configLoadedAt >= ConfigRefreshInterval)
                    await RefreshConfigAsync(stoppingToken);

                // Times out on quiet skies so the settings refresh keeps ticking.
                var candidate = await WaitForCandidateAsync(ConfigRefreshInterval, stoppingToken);
                if (candidate is { } strike)
                    await AlertAsync(strike, stoppingToken);
            }
        }
        finally
        {
            buffer.StrikeReceived -= OnStrikeReceived;
        }
    }

    /// <summary>
    /// Runs on the feed's receive loop for every strike, so it does no more than a distance
    /// check — the cooldown and duplicate rules are applied by the alert loop.
    /// </summary>
    private void OnStrikeReceived(LightningStrike strike)
    {
        try
        {
            if (_config is not { } config)
                return;
            var distanceKm = LightningAlertLogic.HaversineKm(
                config.HomeLat, config.HomeLon, strike.Latitude, strike.Longitude);
            if (distanceKm <= config.RadiusKm)
                _candidates.Writer.TryWrite(strike);
        }
        catch (Exception ex)
        {
            // Never let alerting fault the feed's receive loop.
            logger.LogWarning(ex, "Lightning alert screening failed for a strike");
        }
    }

    private async Task<LightningStrike?> WaitForCandidateAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _candidates.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task AlertAsync(LightningStrike strike, CancellationToken ct)
    {
        if (_config is not { } config)
            return;

        var now = DateTime.UtcNow;
        var (shouldAlert, distanceKm) = LightningAlertLogic.Evaluate(
            strike, config.HomeLat, config.HomeLon, config.RadiusKm,
            now, _lastAlertUtc, config.Cooldown, _lastAlertedStrike);
        if (!shouldAlert)
            return;

        _lastAlertUtc = now;
        _lastAlertedStrike = strike;

        var feedLagSeconds = Math.Max(0, (now - strike.TimeUtc).TotalSeconds);
        var dto = new LightningAlertDto
        {
            DistanceKm = distanceKm,
            BearingDegrees = LightningAlertLogic.BearingDegrees(
                config.HomeLat, config.HomeLon, strike.Latitude, strike.Longitude),
            Latitude = strike.Latitude,
            Longitude = strike.Longitude,
            StrikeTimeUtc = strike.TimeUtc,
            RadiusKm = config.RadiusKm,
            FeedLagSeconds = feedLagSeconds,
        };

        try
        {
            await hubContext.Clients.All.SendAsync(PacketHub.LightningAlertMethod, dto, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to broadcast lightning alert");
            return;
        }

        logger.LogInformation(
            "Lightning alert: strike {DistanceKm:F1} km from home, {FeedLagSeconds:F1} s after it struck " +
            "(alert radius {RadiusKm} km)", distanceKm, feedLagSeconds, config.RadiusKm);
    }

    /// <summary>
    /// Re-reads the alert settings. A null config disables screening outright, so a disabled
    /// alert or an unknown home position costs the feed nothing.
    /// </summary>
    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        _configLoadedAt = DateTime.UtcNow;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            var setting = await db.UserSettings.FindAsync([1], ct);

            var opt = options.Value;
            if (setting is not { LightningAlertEnabled: true } ||
                opt.HomeLat is not { } homeLat || opt.HomeLon is not { } homeLon)
            {
                _config = null;
                return;
            }

            _config = new AlertConfig(
                homeLat,
                homeLon,
                Math.Clamp(setting.LightningAlertRadiusKm, 1, 500),
                TimeSpan.FromMinutes(Math.Clamp(setting.LightningAlertCooldownMinutes, 0, 120)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep screening on the previous settings rather than going deaf mid-storm.
            logger.LogWarning(ex, "Failed to refresh lightning alert settings; keeping previous values");
        }
    }
}

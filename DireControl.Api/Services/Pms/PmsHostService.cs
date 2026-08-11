using DireControl.Api.Services.Ax25;
using DireControl.Api.Services.Terminal;
using DireControl.Data;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services.Pms;

/// <summary>
/// Keeps the PMS listening on <c>BASE-{PmsSsid}</c> while the feature is
/// enabled, re-reading configuration whenever the packet settings change.
/// </summary>
public sealed class PmsHostService(
    Ax25SessionManager sessionManager,
    IPmsSessionServer handler,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    PacketServicesRestartTrigger restartTrigger,
    ILogger<PmsHostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, restartTrigger.Token);
            var ct = iterCts.Token;

            string? claimed = null;
            try
            {
                var (enabled, ssid) = await ReadConfigAsync(stoppingToken);
                if (enabled)
                {
                    var baseCall = StripSsid(options.Value.OurCallsign);
                    var pmsCallsign = $"{baseCall}-{ssid}";
                    if (sessionManager.RegisterListener(pmsCallsign, handler))
                    {
                        claimed = pmsCallsign;
                        logger.LogInformation("PMS listening on {Callsign}.", pmsCallsign);
                    }
                    else
                    {
                        logger.LogWarning("PMS could not claim {Callsign} — already registered.", pmsCallsign);
                    }
                }

                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Settings changed — loop and re-read.
            }
            finally
            {
                if (claimed is not null)
                    sessionManager.UnregisterListener(claimed);
            }
        }
    }

    private async Task<(bool Enabled, int Ssid)> ReadConfigAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var setting = await db.UserSettings.FindAsync([1], ct);
        return (setting?.PmsEnabled ?? false, setting?.PmsSsid ?? 1);
    }

    private static string StripSsid(string callsign)
    {
        var dash = callsign.IndexOf('-');
        return (dash > 0 ? callsign[..dash] : callsign).ToUpperInvariant();
    }
}

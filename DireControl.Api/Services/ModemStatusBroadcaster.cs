using DireControl.Api.Controllers;
using DireControl.Api.Hubs;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Services;

/// <summary>Fast, tiny per-radio telemetry payload for live RX/TX meters.</summary>
public sealed record ModemLevelDto(
    string RadioId, int Channel, float AudioLevel, bool CarrierDetected, bool Transmitting);

/// <summary>Per-radio spectrum row for the waterfalls.</summary>
public sealed record ModemSpectrumDto(string RadioId, byte[] Bins);

/// <summary>
/// Pushes native modem telemetry for every radio instance to browsers over
/// SignalR at three cadences while any modem runs: audio level / DCD / TX at
/// 10 Hz (live meters), the waterfall spectra at 5 Hz, and the full status
/// snapshots at 1 Hz.  While idle or disabled only status changes are sent,
/// so a quiet modem costs no traffic.
/// </summary>
public sealed class ModemStatusBroadcaster(
    SoundModemService modemService,
    IHubContext<PacketHub> hubContext) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private const int SpectrumEveryTicks = 2;
    private const int StatusEveryTicks = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<ModemStatusSnapshot>? lastSent = null;
        var tick = 0;
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            tick++;
            var statuses = modemService.Statuses.ToList();
            var anyRunning = statuses.Any(s => s.State == ModemState.Running);

            if (anyRunning)
            {
                await hubContext.Clients.All.SendAsync(
                    PacketHub.ModemLevelMethod,
                    statuses
                        .Where(s => s.State == ModemState.Running)
                        .Select(s => new ModemLevelDto(
                            s.RadioId, s.Channel, s.AudioLevel, s.CarrierDetected, s.Transmitting))
                        .ToList(),
                    stoppingToken);

                if (tick % SpectrumEveryTicks == 0)
                {
                    var spectra = modemService.GetSpectra();
                    if (spectra.Count > 0)
                    {
                        await hubContext.Clients.All.SendAsync(
                            PacketHub.ModemSpectrumMethod,
                            spectra.Select(x => new ModemSpectrumDto(x.RadioId, x.Bins)).ToList(),
                            stoppingToken);
                    }
                }
            }

            if (tick % StatusEveryTicks != 0)
                continue;

            // While running the counters change constantly — send every status
            // tick.  Otherwise only send when something actually changed.
            if (!anyRunning && lastSent is not null && statuses.SequenceEqual(lastSent))
                continue;
            lastSent = statuses;

            await hubContext.Clients.All.SendAsync(
                PacketHub.ModemStatusChangedMethod,
                statuses.Select(ModemController.ToDto).ToList(),
                stoppingToken);
        }
    }
}

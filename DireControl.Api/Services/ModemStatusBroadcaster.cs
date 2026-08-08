using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Services;

/// <summary>Fast, tiny telemetry payload for live RX/TX meters.</summary>
public sealed record ModemLevelDto(float AudioLevel, bool CarrierDetected, bool Transmitting);

/// <summary>
/// Pushes native modem telemetry to browsers over SignalR at three cadences
/// while the modem runs: audio level / DCD / TX at 10 Hz (live meters), the
/// waterfall spectrum at 5 Hz, and the full status snapshot at 1 Hz.  While
/// idle or disabled only status changes are sent, so a quiet modem costs no
/// traffic.
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
        ModemStatusSnapshot? lastSent = null;
        var tick = 0;
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            tick++;
            var status = modemService.Status;

            if (status.State == ModemState.Running)
            {
                await hubContext.Clients.All.SendAsync(
                    PacketHub.ModemLevelMethod,
                    new ModemLevelDto(status.AudioLevel, status.CarrierDetected, status.Transmitting),
                    stoppingToken);

                if (tick % SpectrumEveryTicks == 0 && modemService.GetSpectrum() is { } spectrum)
                {
                    await hubContext.Clients.All.SendAsync(
                        PacketHub.ModemSpectrumMethod, spectrum, stoppingToken);
                }
            }

            if (tick % StatusEveryTicks != 0)
                continue;

            // While running the counters change constantly — send every status
            // tick.  Otherwise only send when something actually changed.
            if (status.State != ModemState.Running && status == lastSent)
                continue;
            lastSent = status;

            await hubContext.Clients.All.SendAsync(
                PacketHub.ModemStatusChangedMethod,
                new ModemStatusDto
                {
                    State = status.State,
                    CaptureDevice = status.CaptureDevice,
                    ErrorMessage = status.ErrorMessage,
                    AudioLevel = status.AudioLevel,
                    CarrierDetected = status.CarrierDetected,
                    DecodedFrames = status.DecodedFrames,
                    InvalidFrames = status.InvalidFrames,
                    TxEnabled = status.TxEnabled,
                    Transmitting = status.Transmitting,
                    TransmittedFrames = status.TransmittedFrames,
                    RigFrequencyHz = status.RigFrequencyHz,
                    DecodedByProfile = status.DecodedByProfile,
                },
                stoppingToken);
        }
    }
}

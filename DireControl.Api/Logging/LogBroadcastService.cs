using DireControl.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Logging;

/// <summary>
/// Drains the <see cref="LogStreamBroadcaster"/> channel and pushes each entry to
/// all connected <see cref="LogHub"/> clients. Runs on its own thread so the
/// synchronous <see cref="SignalRLogger"/> never has to await SignalR I/O.
/// </summary>
public sealed class LogBroadcastService(
    LogStreamBroadcaster broadcaster,
    IHubContext<LogHub> hubContext) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in broadcaster.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await hubContext.Clients.All.SendAsync(LogHub.LogReceivedMethod, entry, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A failed broadcast must never tear down the drain loop, and we must
                // not log here — a logging failure would feed straight back into the stream.
            }
        }
    }
}

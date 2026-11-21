using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Services;

/// <summary>
/// Pushes every <see cref="TxFrameRegistry"/> transition to browsers as
/// <c>txFrameStateChanged</c>. Reports come from the transmit thread, so the
/// send is fire-and-forget: a slow socket must never hold up a keyup.
/// </summary>
public sealed class TxFrameBroadcaster(
    TxFrameRegistry registry,
    IHubContext<PacketHub> hubContext,
    ILogger<TxFrameBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(TxFrameDto dto) =>
        _ = hubContext.Clients.All
            .SendAsync(PacketHub.TxFrameStateChangedMethod, dto)
            .ContinueWith(
                t => logger.LogDebug(t.Exception, "txFrameStateChanged push failed."),
                TaskContinuationOptions.OnlyOnFaulted);
}

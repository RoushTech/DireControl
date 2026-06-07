using DireControl.Api.Logging;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Hubs;

/// <summary>
/// Streams application logs to the frontend log viewer. On connect, the caller is
/// immediately seeded with the recent backlog from the <see cref="LogStreamBroadcaster"/>
/// ring buffer; subsequent entries arrive live via <see cref="LogReceivedMethod"/>.
/// </summary>
public sealed class LogHub(LogStreamBroadcaster broadcaster) : Hub
{
    public const string HubPath = "/hubs/logs";
    public const string LogReceivedMethod = "logReceived";
    public const string LogBacklogMethod = "logBacklog";

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync(LogBacklogMethod, broadcaster.Snapshot());
        await base.OnConnectedAsync();
    }
}

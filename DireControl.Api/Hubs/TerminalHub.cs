using DireControl.Api.Services.Terminal;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Hubs;

/// <summary>
/// Live terminal transport.  Clients join a per-session group to receive its
/// output stream (seeded with the scrollback backlog on join, LogHub-style)
/// and send keystrokes back through <see cref="SendInput"/> — input rides the
/// open WebSocket rather than per-keystroke HTTP requests.  Session lifecycle
/// (open/close) stays on the REST controller.
/// </summary>
public sealed class TerminalHub(TerminalSessionService terminals) : Hub
{
    public const string HubPath = "/hubs/terminal";
    public const string OutputMethod = "terminalOutput";
    public const string BacklogMethod = "terminalBacklog";
    public const string StateChangedMethod = "terminalStateChanged";
    public const string StatsMethod = "terminalStats";
    public const string SessionsChangedMethod = "terminalSessionsChanged";

    public static string GroupName(string sessionId) => $"term:{sessionId}";

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));

        // Seed the caller with everything the ring still holds; live output
        // overlaps are deduplicated client-side by sequence number.
        if (terminals.SnapshotBuffer(sessionId) is { } snapshot)
        {
            await Clients.Caller.SendAsync(BacklogMethod, new
            {
                SessionId = sessionId,
                StartSeq = snapshot.StartSeq,
                DataBase64 = Convert.ToBase64String(snapshot.Data),
            });
        }
    }

    public Task LeaveSession(string sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId));

    public Task SendInput(string sessionId, string dataBase64) =>
        terminals.SendInputAsync(
            sessionId, Convert.FromBase64String(dataBase64), Context.ConnectionAborted);
}

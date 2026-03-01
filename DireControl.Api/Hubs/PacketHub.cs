namespace DireControl.Api.Hubs;

public class PacketHub : Microsoft.AspNetCore.SignalR.Hub
{
    public const string HubPath = "/hubs/packets";
    public const string PacketReceivedMethod = "packetReceived";
    public const string StationsStaleMethod = "stationsStale";
    public const string MessageReceivedMethod = "messageReceived";
    public const string MessageAckedMethod = "messageAcked";
    public const string AlertReceivedMethod = "alertReceived";
}

namespace DireControl.Enums;

/// <summary>
/// Connection state of the APRS-IS TCP client.
/// </summary>
public enum AprsIsConnectionState
{
    Disabled = 0,
    Connecting = 1,
    Connected = 2,
    AuthFailed = 3,
    Disconnected = 4,
}

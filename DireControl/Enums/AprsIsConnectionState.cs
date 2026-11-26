namespace DireControl.Enums;

/// <summary>
/// Connection state of the APRS-IS TCP client.
/// </summary>
public enum AprsIsConnectionState
{
    Unknown = 0,
    Disabled = 1,
    Connecting = 2,
    Connected = 3,
    AuthFailed = 4,
    Disconnected = 5,
}

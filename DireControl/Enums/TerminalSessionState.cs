namespace DireControl.Enums;

/// <summary>UI-facing session state (mirror of the LAPB link state).</summary>
public enum TerminalSessionState
{
    Unknown = 0,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Error,
}

namespace DireControl.Enums;

/// <summary>How a terminal-visible AX.25 session came to exist.</summary>
public enum TerminalSessionOrigin
{
    Unknown = 0,

    /// <summary>Opened from the web terminal toward a remote station.</summary>
    Outbound,

    /// <summary>A remote station connected to one of our callsigns.</summary>
    Inbound,

    /// <summary>Opened by a third-party app through the AGWPE server.</summary>
    Agwpe,

    /// <summary>In-process PMS preview session — no RF involved.</summary>
    LocalPms,
}

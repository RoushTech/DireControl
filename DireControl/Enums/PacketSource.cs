namespace DireControl.Enums;

/// <summary>
/// Indicates how a packet reached DireControl.
/// </summary>
public enum PacketSource
{
    /// <summary>Received via Direwolf KISS TCP (over the air).</summary>
    Rf = 0,

    /// <summary>Received from the APRS-IS network over TCP.</summary>
    AprsIs = 1,

    /// <summary>Transmitted by our own station (Direwolf KISS echo of own beacon).</summary>
    Own = 2,
}

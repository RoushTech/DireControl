namespace DireControl.Enums;

/// <summary>
/// What the modem does with a keyup's frames when the CSMA wait for a clear
/// channel exceeds its cap.
/// </summary>
public enum CsmaTimeoutAction
{
    Unknown = 0,

    /// <summary>Drop the frames and log a warning (Dire Wolf behaviour).</summary>
    Discard = 1,

    /// <summary>Key up anyway, ignoring the busy channel.</summary>
    TransmitAnyway = 2,
}

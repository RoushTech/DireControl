namespace DireControl.Enums;

/// <summary>A TX calibration test tone the native sound modem can transmit.</summary>
public enum TestToneKind
{
    Unknown = 0,

    /// <summary>Steady Bell 202 mark tone (1200 Hz).</summary>
    Mark = 1,

    /// <summary>Steady Bell 202 space tone (2200 Hz).</summary>
    Space = 2,

    /// <summary>Alternating mark/space warble — a distinctive "this is a test" tone.</summary>
    Alternating = 3,
}

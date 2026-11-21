namespace DireControl.Enums;

/// <summary>What produced an outbound RF frame (TX lifecycle tracking).</summary>
public enum TxFrameKind
{
    Unknown = 0,
    Beacon = 1,
    Message = 2,
    Ack = 3,
    Digipeat = 4,
    /// <summary>Connected-mode (LAPB) session frame.</summary>
    Session = 5,
    /// <summary>Frame from a KISS TCP client.</summary>
    Kiss = 6,
    /// <summary>Frame from an AGWPE client.</summary>
    Agwpe = 7,
    /// <summary>APRS-IS traffic gated to RF.</summary>
    IsToRf = 8,
    /// <summary>Calibration tone or PTT test.</summary>
    TestTone = 9,
}

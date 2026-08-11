namespace DireControl.Modem.Ax25.Lapb;

/// <summary>
/// Tunables for one LAPB link.  <see cref="T1"/> should already include any
/// digipeater-path scaling — the machine uses these values verbatim.
/// </summary>
public sealed record LapbConfig
{
    /// <summary>Try SABME (modulo-128) first, falling back to SABM if the peer answers DM.</summary>
    public bool RequestExtended { get; init; }

    /// <summary>Window size k: 1–7 in modulo-8, 1–63 in modulo-128.</summary>
    public int WindowSize { get; init; } = 4;

    /// <summary>Maximum I-field bytes per I frame (16–256).</summary>
    public int PacLen { get; init; } = 128;

    /// <summary>Retransmission timer.</summary>
    public TimeSpan T1 { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Delayed-acknowledgement timer.</summary>
    public TimeSpan T2 { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Idle link-check timer; <see cref="TimeSpan.Zero"/> disables it.</summary>
    public TimeSpan T3 { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum retries before the link is declared dead.</summary>
    public int N2 { get; init; } = 10;
}

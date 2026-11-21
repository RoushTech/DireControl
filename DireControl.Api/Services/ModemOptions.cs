using DireControl.Enums;

namespace DireControl.Api.Services;

/// <summary>
/// Soundcard-modem transmit policy that is not per-radio configuration.
/// </summary>
public class ModemOptions
{
    public const string Section = "DireControl:Modem";

    /// <summary>
    /// Longest the transmitter waits for carrier detect to clear before giving
    /// up on a keyup.  Without a cap, a carrier detect stuck on noise stalled
    /// every transmission indefinitely.  Dire Wolf waits 60 s.
    /// </summary>
    public int CsmaMaxWaitSeconds { get; set; } = 20;

    /// <summary>
    /// What happens when <see cref="CsmaMaxWaitSeconds"/> expires.  Defaults to
    /// discarding the frames (Dire Wolf behaviour) — flagged for operator review.
    /// </summary>
    public CsmaTimeoutAction CsmaTimeoutAction { get; set; } = CsmaTimeoutAction.Discard;

    /// <summary>
    /// Carrier held continuously this long without a single preamble is treated
    /// as stuck (noise or a mis-set squelch, not a transmission) and ignored by
    /// CSMA.  Reported as <c>CarrierStuck</c> in the modem status.
    /// </summary>
    public int CarrierStuckSeconds { get; set; } = 5;
}

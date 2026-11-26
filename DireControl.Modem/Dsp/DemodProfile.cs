namespace DireControl.Modem.Dsp;

/// <summary>
/// Tuning parameters for one <see cref="AfskDemodulator"/> instance.  Multiple
/// profiles with different filter/AGC characteristics can run in parallel on
/// the same audio, with duplicate decodes collapsed by the frame deduper.
/// </summary>
public sealed record DemodProfile
{
    public required string Name { get; init; }
    public required int SampleRate { get; init; }
    public float Baud { get; init; } = 1200f;
    public float MarkFreq { get; init; } = 1200f;
    public float SpaceFreq { get; init; } = 2200f;

    /// <summary>
    /// Input bandpass pre-filter (Kaiser window); 0 taps disables it.  The
    /// rectangular correlators have only −13 dB sidelobes, so this filter is
    /// what keeps out-of-band noise — which on flat discriminator audio keeps
    /// rising above the tones — away from the weaker tone's arm.
    /// </summary>
    public int PreFilterTaps { get; init; }
    public float PreFilterLowHz { get; init; } = 800f;
    public float PreFilterHighHz { get; init; } = 2600f;

    /// <summary>Tone correlator window length in samples.</summary>
    public required int CorrelatorTaps { get; init; }

    /// <summary>Post-comparator lowpass on the mark−space difference; 0 taps disables it.</summary>
    public int PostFilterTaps { get; init; }
    public float PostFilterCutoffHz { get; init; } = 1440f;

    public float AgcAttack { get; init; } = 0.130f;
    public float AgcDecay { get; init; } = 0.00013f;

    public float PllLockedInertia { get; init; } = 0.74f;
    public float PllSearchingInertia { get; init; } = 0.50f;

    /// <summary>
    /// Bit decisions the deframer may invert to rescue a frame whose FCS fails
    /// (0 = off, 1 or 2).  See <see cref="Hdlc.HdlcDeframer.MaxRepairBits"/>.
    /// </summary>
    public int MaxRepairBits { get; init; }

    /// <summary>Repair depth used when the caller does not choose one.</summary>
    public const int DefaultMaxRepairBits = 1;

    /// <summary>
    /// The standard profile: bandpass pre-filter, ~1.2-symbol correlator
    /// window (rectangular), per-tone AGC, and a gentle post-filter.  Starting point modelled
    /// on well-known AFSK demodulator practice; tuned via the round-trip tests.
    /// </summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="maxRepairBits">
    /// FCS-guided bit repair: 0 off, 1 (default) or 2.  One bit is worth about
    /// 0.6–0.9 dB of sensitivity with no measurable rate of wrong frames; two
    /// adds a few tenths more but about 1 in 400 repaired frames is wrong.
    /// </param>
    public static DemodProfile Standard(int sampleRate, int maxRepairBits = DefaultMaxRepairBits)
    {
        var samplesPerSymbol = sampleRate / 1200f;
        return new DemodProfile
        {
            Name = "A",
            SampleRate = sampleRate,
            // 3.2 symbols (129 taps at 48 kHz), 800–2600 Hz.  Against the
            // earlier 65-tap 900–2500 Hz Blackman design this measured the
            // same in white noise and 0.1–0.4 dB better with FM-shaped
            // noise and mark/space twist; narrower or longer gained nothing.
            PreFilterTaps = (int)(samplesPerSymbol * 3.2f) | 1,
            // Exactly 1.2 symbols (48 taps at 48 kHz) — the rectangular
            // correlator window only nulls the opposite tone at this length,
            // so it must not be rounded up to an odd count.
            CorrelatorTaps = (int)MathF.Round(samplesPerSymbol * 1.2f),
            PostFilterTaps = (int)(samplesPerSymbol * 1.0f) | 1,
            MaxRepairBits = Math.Clamp(maxRepairBits, 0, 2),
        };
    }
}

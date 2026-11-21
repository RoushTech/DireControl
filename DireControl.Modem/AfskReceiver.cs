using DireControl.Modem.Dsp;

namespace DireControl.Modem;

/// <summary>
/// The complete AFSK receive pipeline: N parallel demodulator profiles over
/// the same audio, with duplicate decodes collapsed by a <see cref="FrameDeduper"/>.
/// Raises <see cref="FrameReceived"/> once per unique CRC-valid AX.25 frame.
/// </summary>
public sealed class AfskReceiver
{
    private readonly AfskDemodulator[] _demodulators;
    private readonly FrameDeduper _deduper = new();

    /// <summary>Rolling FFT of the input audio for the waterfall display.</summary>
    public SpectrumAnalyzer Spectrum { get; }

    private float _peakLevel;
    private long _samplesSincePeakReset;
    private readonly int _peakWindowSamples;

    /// <summary>Raised with the frame bytes and how the first profile to decode it got there.</summary>
    public event Action<byte[], DecodeInfo>? FrameReceived;

    public AfskReceiver(params IReadOnlyList<DemodProfile> profiles)
    {
        if (profiles.Count == 0)
            throw new ArgumentException("At least one demodulator profile is required.", nameof(profiles));

        _peakWindowSamples = profiles[0].SampleRate / 10; // 100 ms peak meter window — lively UI meters
        Spectrum = new SpectrumAnalyzer(profiles[0].SampleRate);

        _demodulators = new AfskDemodulator[profiles.Count];
        for (var i = 0; i < profiles.Count; i++)
        {
            var demod = new AfskDemodulator(profiles[i]);
            var profile = profiles[i];
            demod.FrameDemodulated += (frame, repairedBits) =>
            {
                var now = DateTime.UtcNow;
                if (_deduper.IsNewFrame(frame, now))
                {
                    ValidFrameCount++;
                    if (repairedBits > 0)
                        RepairedFrameCount++;
                    LastFrameAt = now;
                    FrameReceived?.Invoke(frame, new DecodeInfo(profile, repairedBits));
                }
            };
            _demodulators[i] = demod;
        }
    }

    /// <summary>Creates a receiver with the standard single profile.</summary>
    /// <param name="sampleRate">Audio sample rate in Hz.</param>
    /// <param name="maxRepairBits">FCS-guided bit repair depth: 0 off, 1 (default) or 2.</param>
    public static AfskReceiver CreateStandard(
        int sampleRate, int maxRepairBits = DemodProfile.DefaultMaxRepairBits) =>
        new(DemodProfile.Standard(sampleRate, maxRepairBits));

    /// <summary>True while any demodulator profile reports carrier.</summary>
    public bool CarrierDetected => _demodulators.Any(d => d.CarrierDetected);

    /// <summary>Total unique valid frames emitted.</summary>
    public long ValidFrameCount { get; private set; }

    /// <summary>Unique valid frames that needed a bit repair to pass the FCS.</summary>
    public long RepairedFrameCount { get; private set; }

    /// <summary>UTC time of the last unique valid frame, or null before the first.</summary>
    public DateTime? LastFrameAt { get; private set; }

    /// <summary>
    /// Peak absolute audio level (0–1) over roughly the last 100 ms — drives
    /// the input-level meter in the UI.
    /// </summary>
    public float PeakAudioLevel { get; private set; }

    /// <summary>
    /// Processes a block of mono float samples through every profile.
    /// </summary>
    public void ProcessSamples(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            var abs = MathF.Abs(s);
            if (abs > _peakLevel)
                _peakLevel = abs;
        }

        Spectrum.AddSamples(samples);

        _samplesSincePeakReset += samples.Length;
        if (_samplesSincePeakReset >= _peakWindowSamples)
        {
            PeakAudioLevel = _peakLevel;
            _peakLevel = 0;
            _samplesSincePeakReset = 0;
        }

        foreach (var demod in _demodulators)
            demod.ProcessSamples(samples);
    }

    public IReadOnlyList<AfskDemodulator> Demodulators => _demodulators;
}

/// <summary>How a received frame was decoded.</summary>
/// <param name="Profile">The demodulator profile that decoded it first.</param>
/// <param name="RepairedBits">
/// Bit decisions inverted to make the FCS pass — 0 for a frame that was valid
/// as received.  A repaired frame is very likely right but is not guaranteed.
/// </param>
public readonly record struct DecodeInfo(DemodProfile Profile, int RepairedBits);

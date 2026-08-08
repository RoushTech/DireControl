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

    /// <summary>Raised with the frame bytes and the profile that decoded it first.</summary>
    public event Action<byte[], DemodProfile>? FrameReceived;

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
            demod.FrameDemodulated += frame =>
            {
                if (_deduper.IsNewFrame(frame, DateTime.UtcNow))
                {
                    ValidFrameCount++;
                    FrameReceived?.Invoke(frame, profile);
                }
            };
            _demodulators[i] = demod;
        }
    }

    /// <summary>Creates a receiver with the standard single profile.</summary>
    public static AfskReceiver CreateStandard(int sampleRate) =>
        new(DemodProfile.Standard(sampleRate));

    /// <summary>True while any demodulator profile reports carrier.</summary>
    public bool CarrierDetected => _demodulators.Any(d => d.CarrierDetected);

    /// <summary>Total unique valid frames emitted.</summary>
    public long ValidFrameCount { get; private set; }

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

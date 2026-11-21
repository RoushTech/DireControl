using DireControl.Modem.Hdlc;

namespace DireControl.Modem.Dsp;

/// <summary>
/// One complete AFSK receive chain: bandpass pre-filter → mark/space tone
/// correlators → per-tone AGC → comparator → PLL bit-clock recovery → NRZI →
/// HDLC deframing.  Feed it float audio samples; it raises
/// <see cref="FrameDemodulated"/> for every CRC-valid AX.25 frame.
/// </summary>
public sealed class AfskDemodulator
{
    private readonly FirFilter? _preFilter;
    private readonly ToneCorrelator _mark;
    private readonly ToneCorrelator _space;
    private readonly Agc _markAgc;
    private readonly Agc _spaceAgc;
    private readonly FirFilter? _postFilter;
    private readonly BitClockPll _pll;
    private readonly NrziDecoder _nrzi = new();
    private readonly HdlcDeframer _deframer;

    private readonly int _dcdHoldSamples;
    private readonly int _zeroPreambleWindowSamples;
    private long _zeroPreambleSampleIndex = long.MinValue;
    private long _sampleIndex;
    private long _dcdArmedSampleIndex = long.MinValue;
    private bool _frameQualified;
    private bool _lastFrameValid;

    /// <summary>
    /// Raised with the AX.25 frame bytes (FCS stripped) on each valid decode,
    /// and the number of bit decisions repaired to make the FCS pass (0 for a
    /// frame that was valid as received).
    /// </summary>
    public event Action<byte[], int>? FrameDemodulated;

    public DemodProfile Profile { get; }

    public AfskDemodulator(DemodProfile profile)
    {
        Profile = profile;
        _deframer = new HdlcDeframer { MaxRepairBits = profile.MaxRepairBits };

        if (profile.PreFilterTaps > 0)
            _preFilter = new FirFilter(FirFilter.KaiserBandpassTaps(
                profile.SampleRate, profile.PreFilterLowHz, profile.PreFilterHighHz, profile.PreFilterTaps));

        _mark = new ToneCorrelator(profile.SampleRate, profile.MarkFreq, profile.CorrelatorTaps);
        _space = new ToneCorrelator(profile.SampleRate, profile.SpaceFreq, profile.CorrelatorTaps);
        _markAgc = new Agc(profile.AgcAttack, profile.AgcDecay);
        _spaceAgc = new Agc(profile.AgcAttack, profile.AgcDecay);

        if (profile.PostFilterTaps > 0)
            _postFilter = new FirFilter(FirFilter.LowpassTaps(
                profile.SampleRate, profile.PostFilterCutoffHz, profile.PostFilterTaps));

        _pll = new BitClockPll(
            profile.SampleRate, profile.Baud, profile.PllLockedInertia, profile.PllSearchingInertia);

        // DCD holds for half a second after arming.
        _dcdHoldSamples = profile.SampleRate / 2;
        _zeroPreambleWindowSamples = (int)(ZeroPreambleFlagWindowBits * profile.SampleRate / profile.Baud);
        _deframer.FrameReceived += frame =>
        {
            _lastFrameValid = true;
            FrameDemodulated?.Invoke(frame, _deframer.LastFrameRepairedBits);
        };
        _deframer.FlagDetected += OnFlag;
    }

    /// <summary>
    /// Consecutive flags that arm DCD.  Random bits produce a 0x7E roughly
    /// 4–5 times a second and a second flag lands within a few octets often
    /// enough that a two-flag rule held DCD ~20% of the time on pure noise,
    /// stalling CSMA.  Three back-to-back flags (2^-16 per noise flag) costs
    /// ~13 ms of acquisition against a 300 ms TXDelay preamble.
    /// </summary>
    internal const int DcdArmFlagRun = 3;

    /// <summary>
    /// Bit periods after a zero-fill preamble within which a flag still counts
    /// as opening a real transmission.  Stations that fill TXDelay with zeros
    /// follow it with only one or two flags — never the three-flag run — so
    /// the zero-fill itself has to qualify the frame those flags open.
    /// </summary>
    internal const int ZeroPreambleFlagWindowBits = 40;

    private void OnFlag()
    {
        // A frame opened behind a real preamble — or immediately following a
        // CRC-valid frame of the same transmission — is "qualified": DCD stays
        // armed for as long as the deframer remains inside it, so carrier does
        // not drop mid-frame on long packets once the preamble's hold expires.
        // A lone noise flag never qualifies.
        var followsValidFrame = _lastFrameValid && _frameQualified;
        _lastFrameValid = false;

        var followsZeroPreamble = _zeroPreambleSampleIndex != long.MinValue
            && _sampleIndex - _zeroPreambleSampleIndex <= _zeroPreambleWindowSamples;

        if (_deframer.CurrentFlagRun >= DcdArmFlagRun || followsValidFrame || followsZeroPreamble)
        {
            _frameQualified = true;
            _dcdArmedSampleIndex = _sampleIndex;
        }
        else
        {
            _frameQualified = false;
        }
    }

    /// <summary>Valid frames decoded by this demodulator.</summary>
    public long ValidFrameCount => _deframer.ValidFrameCount;

    /// <summary>Frames that failed the FCS/length checks.</summary>
    public long InvalidFrameCount => _deframer.InvalidFrameCount;

    /// <summary>Valid frames that needed a bit repair to pass the FCS.</summary>
    public long RepairedFrameCount => _deframer.RepairedFrameCount;

    /// <summary>Transmission preambles seen, whether or not a frame resulted.</summary>
    public long PreambleCount => _deframer.PreambleCount;

    /// <summary>Longest run of consecutive opening flags seen.</summary>
    public int LongestFlagRun => _deframer.LongestFlagRun;

    /// <summary>
    /// Data-carrier detect: true while a run of consecutive HDLC flags or a
    /// zero-fill run (a real preamble, not a lone noise-decoded flag) has been
    /// seen recently, and for
    /// as long as the deframer stays inside the frame that preamble opened.
    /// The sentinel check
    /// matters: subtracting long.MinValue wraps negative, which made DCD read
    /// "detected" from startup until the first flag ever arrived.
    /// </summary>
    public bool CarrierDetected =>
        _dcdArmedSampleIndex != long.MinValue && _sampleIndex - _dcdArmedSampleIndex < _dcdHoldSamples;

    /// <summary>
    /// Processes a block of mono float samples (any nominal level; AGC adapts).
    /// </summary>
    public void ProcessSamples(ReadOnlySpan<float> samples)
    {
        foreach (var raw in samples)
        {
            var sample = _preFilter?.Process(raw) ?? raw;

            var markNorm = _markAgc.Process(_mark.Process(sample));
            var spaceNorm = _spaceAgc.Process(_space.Process(sample));

            var diff = markNorm - spaceNorm;
            if (_postFilter is not null)
                diff = _postFilter.Process(diff);

            var level = diff > 0;

            if (_pll.Advance(level))
            {
                // |diff| at the sampling instant is how decisively this bit
                // cell was sliced — the ranking the deframer repairs by.
                _deframer.ProcessBit(_nrzi.Decode(level), MathF.Abs(diff));
                _pll.Locked = _deframer.InFrame;

                // A zero-fill preamble is carrier in its own right, and
                // qualifies the frame opened by the flag(s) that follow it.
                if (_deframer.InZeroPreamble)
                {
                    _dcdArmedSampleIndex = _sampleIndex;
                    _zeroPreambleSampleIndex = _sampleIndex;
                }

                // Hold DCD through a qualified frame; an abort or runaway
                // (InFrame dropping) ends the qualification.
                if (_frameQualified)
                {
                    if (_deframer.InFrame)
                        _dcdArmedSampleIndex = _sampleIndex;
                    else
                        _frameQualified = false;
                }
            }

            _sampleIndex++;
        }
    }

    public void Reset()
    {
        _preFilter?.Reset();
        _mark.Reset();
        _space.Reset();
        _markAgc.Reset();
        _spaceAgc.Reset();
        _postFilter?.Reset();
        _pll.Reset();
        _nrzi.Reset();
        _deframer.Reset();
        _dcdArmedSampleIndex = long.MinValue;
        _zeroPreambleSampleIndex = long.MinValue;
        _frameQualified = false;
        _lastFrameValid = false;
    }
}

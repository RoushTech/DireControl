namespace DireControl.Modem.Hdlc;

/// <summary>
/// HDLC deframer: consumes NRZI-decoded data bits, detects 0x7E flags,
/// removes stuffed zero bits, assembles octets (LSB first per AX.25), and
/// emits frames whose CRC-CCITT FCS validates.  Emitted frames exclude the FCS.
/// </summary>
/// <remarks>
/// Bits are matured through an 8-bit delay line so that the bits belonging to
/// a closing flag are never committed to the frame buffer: when the flag
/// pattern is recognised, its 8 bits are exactly the ones still in the delay
/// line and are simply discarded.
/// <para>
/// With <see cref="MaxRepairBits"/> set, a frame that fails its FCS gets a
/// second chance: the least-confident bit decisions (per the confidence passed
/// to <see cref="ProcessBit"/>) are inverted one at a time — or two at a time —
/// and the frame is accepted if the FCS then validates and the result has a
/// well-formed AX.25 address field.  Worth roughly 0.6–0.9 dB of sensitivity at
/// one bit, about 1 dB at two.
/// </para>
/// </remarks>
public sealed class HdlcDeframer
{
    /// <summary>Smallest valid AX.25 frame: 14 address + 1 control + 2 FCS bytes.</summary>
    private const int MinFrameLengthWithFcs = 17;

    /// <summary>
    /// Sanity cap on a frame being assembled.  Well above the AX.25 maximum
    /// (256-byte info field), because some stacks send oversized I-frames —
    /// dropping those at 400 bytes cost connected-mode data for no APRS benefit.
    /// </summary>
    private const int MaxFrameLengthWithFcs = 2048;

    private const byte FlagPattern = 0x7E;

    /// <summary>Raw (still bit-stuffed) bits kept for repair: the frame cap plus stuffing.</summary>
    private const int MaxRawBits = MaxFrameLengthWithFcs * 8 * 6 / 5 + 16;

    /// <summary>
    /// Least-confident decisions considered for a one-bit repair.  Eight
    /// candidates recovered nearly everything a wider search did while keeping
    /// the chance of a wrong frame passing the FCS at 8 in 65536 per attempt.
    /// </summary>
    internal const int SingleRepairCandidates = 8;

    /// <summary>Candidates considered when two-bit repair is enabled (singles, then all pairs).</summary>
    internal const int DoubleRepairCandidates = 12;

    /// <summary>
    /// Consecutive zero bits that mark a zero-fill preamble.  Many TNCs and
    /// digipeaters fill TXDelay with 0x00 (alternating tones) and send only one
    /// or two flags, so a flag run alone never recognises them.  Eight hours of
    /// simulated channel noise produced no run this long (and a single run of
    /// 24); real zero-fill preambles run 150 bits or more.
    /// </summary>
    internal const int ZeroPreambleBits = 32;

    /// <summary>
    /// Bits within which a second preamble indication (flag run after a
    /// zero-fill, or the reverse) belongs to the transmission already counted.
    /// </summary>
    private const int PreambleRecountGuardBits = 256;

    /// <summary>Raised with the frame contents (FCS stripped) when a valid frame is decoded.</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>
    /// How many bit decisions may be inverted to rescue a frame whose FCS
    /// fails: 0 disables repair, 1 or 2 enable it.  Two-bit repair decodes
    /// more but roughly 1 in 400 repaired frames is then wrong.
    /// </summary>
    public int MaxRepairBits { get; init; }

    /// <summary>
    /// Bit decisions inverted to make the frame most recently raised through
    /// <see cref="FrameReceived"/> pass its FCS — 0 for a frame that was valid
    /// as received.  Read it from inside the event handler.
    /// </summary>
    public int LastFrameRepairedBits { get; private set; }

    /// <summary>Valid frames that needed a repair to pass the FCS.</summary>
    public long RepairedFrameCount { get; private set; }

    /// <summary>True while the most recent bits form a zero-fill preamble.</summary>
    public bool InZeroPreamble => _zeroRun >= ZeroPreambleBits;

    /// <summary>Raised whenever an HDLC flag byte is recognised — useful for DCD.</summary>
    public event Action? FlagDetected;

    /// <summary>True after an opening flag until the frame ends or aborts.</summary>
    public bool InFrame => _inFrame;

    /// <summary>
    /// Consecutive flags immediately preceding the current position — 1 for a
    /// lone flag (including a closing flag after frame content).  Carrier
    /// detect arms on a run, since a single flag turns up in noise constantly.
    /// </summary>
    public int CurrentFlagRun => _flagRun;

    /// <summary>Total frames emitted with a valid FCS.</summary>
    public long ValidFrameCount { get; private set; }

    /// <summary>Frames dropped due to bad FCS, bad length, or overflow.</summary>
    public long InvalidFrameCount { get; private set; }

    /// <summary>
    /// Preambles seen (a run of consecutive opening flags, or a zero-fill
    /// run) — one per transmission whose
    /// preamble arrived, whether or not a frame came out of it.  Unlike audio
    /// level this distinguishes a real transmission from noise on any channel,
    /// including an open squelch where noise is as loud as a signal.
    /// </summary>
    public long PreambleCount { get; private set; }

    /// <summary>
    /// Longest run of consecutive opening flags seen.  For a transmitter that
    /// fills TXDelay with flags (32-45 of them) a much smaller number means the
    /// start of every transmission is being lost before the demodulator sees
    /// it.  It says nothing about transmitters that fill TXDelay with zeros
    /// and send only one or two flags — a run of 2 is normal for those.
    /// </summary>
    public int LongestFlagRun { get; private set; }

    /// <summary>
    /// Opening flags required before a bad-CRC candidate counts as a damaged
    /// packet.  Real transmissions start with a TXDelay preamble of many
    /// flags; random noise essentially never produces two in a row, so this
    /// keeps the damaged-packet counter meaningful on a noisy channel.
    /// </summary>
    private const int MinOpeningFlagsForDamageCount = 2;

    /// <summary>
    /// Consecutive opening flags that mark a genuine transmission for
    /// <see cref="PreambleCount"/>.  Two in a row — enough to qualify a
    /// bad-CRC frame as damaged, since that also requires a plausible frame to
    /// have assembled — turns up in random noise every few seconds on its own,
    /// which is far too often to drive automatic capture.  A real TXDelay
    /// preamble runs 32–45 flags, so eight is well under any true preamble and
    /// astronomically unlikely from noise.
    /// </summary>
    private const int MinFlagsForTransmission = 8;

    // 8-bit delay line; newest bit enters at bit 7, the matured bit leaves from bit 0.
    private byte _delayLine;
    private int _delayCount;

    // Consecutive flags immediately preceding the current frame's content.
    private int _flagRun;

    // Whether any bit matured since the last flag — aborts clear the assembly
    // buffers, so this is tracked explicitly to keep the flag run honest.
    private bool _dataSinceFlag;

    // Destuffing / octet assembly state for matured bits.
    private bool _inFrame;
    private int _onesCount;
    private byte _currentByte;
    private int _bitPosition;
    private readonly List<byte> _frame = new(512);

    // Confidence of each bit in the delay line, and the raw matured bits of the
    // candidate frame (kept through aborts) for repair.  Allocated on first use.
    private readonly float[] _delayConfidence = new float[8];
    private int _delayConfidencePosition;
    private bool[]? _rawBits;
    private float[]? _rawConfidence;
    private byte[]? _repairScratch;
    private int _rawCount;
    private bool _collecting;

    // Zero-fill preamble tracking.
    private int _zeroRun;
    private int _bitsSincePreambleCounted = PreambleRecountGuardBits;

    /// <summary>
    /// Processes one NRZI-decoded data bit.
    /// </summary>
    /// <param name="bit">The decoded data bit.</param>
    /// <param name="confidence">
    /// How sure the demodulator was of the bit-cell decision behind this bit
    /// (any monotonic scale; larger is surer).  Only used to rank repair
    /// candidates.
    /// </param>
    public void ProcessBit(bool bit, float confidence = 1f)
    {
        // Capture the oldest bit before it is shifted out, but only consume it
        // once we know the newest 8 bits are not a flag.
        var maturedBit = (_delayLine & 0x01) != 0;
        var delayWasFull = _delayCount >= 8;
        var maturedConfidence = _delayConfidence[_delayConfidencePosition];
        _delayConfidence[_delayConfidencePosition] = confidence;
        _delayConfidencePosition = (_delayConfidencePosition + 1) & 7;

        if (_bitsSincePreambleCounted < PreambleRecountGuardBits)
            _bitsSincePreambleCounted++;
        _zeroRun = bit ? 0 : _zeroRun + 1;
        if (_zeroRun == ZeroPreambleBits)
            CountPreamble();

        _delayLine = (byte)((_delayLine >> 1) | (bit ? 0x80 : 0x00));
        if (_delayCount < 8)
            _delayCount++;

        if (_delayCount == 8 && _delayLine == FlagPattern)
        {
            // Flag: the bit that matured on this push still belongs to the
            // frame — consume it, then everything matured is the candidate
            // frame and the 8 flag bits in the delay line are discarded.
            if (delayWasFull)
                ConsumeMaturedBit(maturedBit, maturedConfidence);
            EndFrame();
            _flagRun = _dataSinceFlag ? 1 : _flagRun + 1;
            // Counted once as the run crosses the threshold, so a long preamble
            // of many flags still registers as one transmission.
            if (_flagRun == MinFlagsForTransmission)
                CountPreamble();
            if (_flagRun > LongestFlagRun)
                LongestFlagRun = _flagRun;
            _dataSinceFlag = false;
            _delayCount = 0;
            _inFrame = true;
            ResetAssembly();
            _rawCount = 0;
            _collecting = MaxRepairBits > 0;
            FlagDetected?.Invoke();
            return;
        }

        if (delayWasFull)
            ConsumeMaturedBit(maturedBit, maturedConfidence);
    }

    /// <summary>
    /// Counts one transmission preamble, unless one was counted moments ago —
    /// a zero-fill followed by a long flag run is still a single transmission.
    /// </summary>
    private void CountPreamble()
    {
        if (_bitsSincePreambleCounted < PreambleRecountGuardBits)
            return;
        PreambleCount++;
        _bitsSincePreambleCounted = 0;
    }

    /// <summary>
    /// Drops any partial frame and resets all state (e.g. on DCD loss).
    /// </summary>
    public void Reset()
    {
        _inFrame = false;
        _delayLine = 0;
        _delayCount = 0;
        _flagRun = 0;
        _dataSinceFlag = false;
        _rawCount = 0;
        _collecting = false;
        _zeroRun = 0;
        _bitsSincePreambleCounted = PreambleRecountGuardBits;
        ResetAssembly();
    }

    private void ConsumeMaturedBit(bool bit, float confidence)
    {
        _dataSinceFlag = true;

        // Raw bits are kept even after an abort: a single bad decision can
        // fake the seven-ones abort sequence, and repair can undo that.
        if (_collecting)
        {
            _rawBits ??= new bool[MaxRawBits];
            _rawConfidence ??= new float[MaxRawBits];
            if (_rawCount < MaxRawBits)
            {
                _rawBits[_rawCount] = bit;
                _rawConfidence[_rawCount++] = confidence;
            }
            else
            {
                _collecting = false;
            }
        }

        if (!_inFrame)
            return;

        if (bit)
        {
            _onesCount++;
            if (_onesCount >= 7)
            {
                // Abort sequence — discard the partial frame and hunt for a flag.
                _inFrame = false;
                ResetAssembly();
                return;
            }

            AppendBit(true);
            return;
        }

        if (_onesCount == 5)
        {
            // Stuffed zero — discard it.
            _onesCount = 0;
            return;
        }

        _onesCount = 0;
        AppendBit(false);
    }

    private void AppendBit(bool bit)
    {
        if (bit)
            _currentByte |= (byte)(1 << _bitPosition);

        if (++_bitPosition == 8)
        {
            if (_frame.Count >= MaxFrameLengthWithFcs)
            {
                // Runaway "frame" (noise with no closing flag) — give up on it
                // silently; this is channel noise, not a damaged packet.
                _inFrame = false;
                ResetAssembly();
                return;
            }

            _frame.Add(_currentByte);
            _currentByte = 0;
            _bitPosition = 0;
        }
    }

    private void EndFrame()
    {
        // A valid frame is a whole number of octets within AX.25 size limits
        // and must pass the FCS check.
        var plausible = _inFrame && _bitPosition == 0 &&
            _frame.Count is >= MinFrameLengthWithFcs and <= MaxFrameLengthWithFcs;

        if (plausible &&
            Crc16Ccitt.IsFrameValid(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frame)))
        {
            ValidFrameCount++;
            LastFrameRepairedBits = 0;
            FrameReceived?.Invoke(_frame[..^2].ToArray());
        }
        else if (TryRepair(out var repaired, out var repairedBits))
        {
            ValidFrameCount++;
            RepairedFrameCount++;
            LastFrameRepairedBits = repairedBits;
            FrameReceived?.Invoke(repaired);
        }
        else if (plausible && _flagRun >= MinOpeningFlagsForDamageCount)
        {
            // Right shape, wrong checksum, arrived behind a real preamble —
            // a genuinely damaged packet.  Anything short, misaligned, or
            // without consecutive opening flags is just channel noise between
            // spurious flag patterns and is not worth counting.
            InvalidFrameCount++;
        }
    }

    // ── FCS-guided repair ─────────────────────────────────────────────────────

    /// <summary>
    /// Tries inverting the least-confident bit-cell decisions of the candidate
    /// frame until the FCS validates.  A wrong tone decision in one bit cell
    /// inverts two adjacent NRZI-decoded bits, so each candidate flips a pair.
    /// </summary>
    private bool TryRepair(out byte[] frame, out int repairedBits)
    {
        frame = [];
        repairedBits = 0;
        if (MaxRepairBits <= 0 || !_collecting || _rawCount < MinFrameLengthWithFcs * 8)
            return false;

        var bits = _rawBits.AsSpan(0, _rawCount);
        var confidence = _rawConfidence.AsSpan(0, _rawCount);

        // The K least-confident cells, most doubtful first.  The final bit is
        // excluded: its partner is the first bit of the closing flag.
        var wanted = MaxRepairBits >= 2 ? DoubleRepairCandidates : SingleRepairCandidates;
        Span<int> candidates = stackalloc int[wanted];
        Span<float> candidateConfidence = stackalloc float[wanted];
        var count = 0;
        for (var i = 0; i < bits.Length - 1; i++)
        {
            var c = confidence[i];
            if (count == wanted && c >= candidateConfidence[count - 1])
                continue;
            var slot = count < wanted ? count++ : wanted - 1;
            while (slot > 0 && candidateConfidence[slot - 1] > c)
            {
                candidateConfidence[slot] = candidateConfidence[slot - 1];
                candidates[slot] = candidates[slot - 1];
                slot--;
            }
            candidateConfidence[slot] = c;
            candidates[slot] = i;
        }

        for (var i = 0; i < count; i++)
        {
            if (TryDecodeRaw(bits, candidates[i], -1, out frame))
            {
                repairedBits = 1;
                return true;
            }
        }

        if (MaxRepairBits < 2)
            return false;

        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                if (TryDecodeRaw(bits, candidates[i], candidates[j], out frame))
                {
                    repairedBits = 2;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Destuffs and checks the raw candidate frame with the bit cells at
    /// <paramref name="flipA"/> / <paramref name="flipB"/> inverted (−1 for none).
    /// </summary>
    private bool TryDecodeRaw(ReadOnlySpan<bool> bits, int flipA, int flipB, out byte[] frame)
    {
        frame = [];
        var buffer = _repairScratch ??= new byte[MaxFrameLengthWithFcs];
        var length = 0;
        var ones = 0;
        var current = 0;
        var position = 0;

        for (var i = 0; i < bits.Length; i++)
        {
            // A cell error inverts the bit at its own index and the next one;
            // XOR so two adjacent cell flips cancel on the shared bit.
            var bit = bits[i] ^ (i == flipA || i == flipA + 1) ^ (flipB >= 0 && (i == flipB || i == flipB + 1));
            if (bit)
            {
                if (++ones >= 7)
                    return false;
                current |= 1 << position;
            }
            else
            {
                if (ones == 5)
                {
                    ones = 0;
                    continue;
                }
                ones = 0;
            }

            if (++position == 8)
            {
                if (length >= buffer.Length)
                    return false;
                buffer[length++] = (byte)current;
                current = 0;
                position = 0;
            }
        }

        if (position != 0 || length < MinFrameLengthWithFcs)
            return false;

        var candidate = buffer.AsSpan(0, length);
        if (!Crc16Ccitt.IsFrameValid(candidate) || !HasPlausibleAddressField(candidate[..^2]))
            return false;

        frame = candidate[..^2].ToArray();
        return true;
    }

    /// <summary>
    /// Whether <paramref name="frame"/> starts with a well-formed AX.25 address
    /// field: two to ten 7-octet addresses of shifted A–Z / 0–9 / space, with
    /// the extension bit set only on the last.  A repaired frame must pass
    /// this as well as the FCS — trying several bit patterns multiplies the
    /// odds of a chance FCS match, and random bytes essentially never look
    /// like callsigns.
    /// </summary>
    public static bool HasPlausibleAddressField(ReadOnlySpan<byte> frame)
    {
        var end = -1;
        for (var i = 0; i < frame.Length && i < 70; i++)
        {
            if ((frame[i] & 1) != 0)
            {
                end = i;
                break;
            }
        }

        if (end < 13 || (end + 1) % 7 != 0 || frame.Length < end + 2)
            return false;

        for (var i = 0; i <= end; i++)
        {
            if (i % 7 == 6)
                continue; // SSID octet
            var c = frame[i] >> 1;
            if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' '))
                return false;
        }

        return true;
    }

    private void ResetAssembly()
    {
        _onesCount = 0;
        _currentByte = 0;
        _bitPosition = 0;
        _frame.Clear();
    }
}

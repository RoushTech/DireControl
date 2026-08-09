using System.Threading.Channels;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem;
using DireControl.Modem.Audio;
using DireControl.Modem.Dsp;
using DireControl.Modem.Ptt;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Point-in-time snapshot of one radio's modem instance for the status API
/// and SignalR.
/// </summary>
public sealed record ModemStatusSnapshot
{
    public required string RadioId { get; init; }
    public required string RadioName { get; init; }
    public required string FullCallsign { get; init; }
    public int Channel { get; init; }
    public required ModemState State { get; init; }
    public string? CaptureDevice { get; init; }
    public string? ErrorMessage { get; init; }
    public float AudioLevel { get; init; }
    public bool CarrierDetected { get; init; }
    public long DecodedFrames { get; init; }
    public long InvalidFrames { get; init; }
    public bool TxEnabled { get; init; }
    public bool Transmitting { get; init; }
    public long TransmittedFrames { get; init; }
    public long? RigFrequencyHz { get; init; }
    public IReadOnlyDictionary<string, long>? DecodedByProfile { get; init; }
}

/// <summary>
/// The native soundcard modem backend, one instance per active radio with
/// modem audio configured — each radio gets its own capture device, DSP
/// pipeline, transmit queue, and PTT controller, all identified by the
/// radio's KISS channel number.  Decoded AX.25 frames flow into the shared
/// <see cref="RfFrameIngestService"/>; outbound frames are routed to the
/// instance whose channel matches (or any TX-capable instance as fallback).
/// One radio's device error never disturbs the others — each instance retries
/// independently.  Reconfigures live via <see cref="ModemRestartTrigger"/>.
/// </summary>
public sealed class SoundModemService(
    IServiceScopeFactory scopeFactory,
    RfFrameIngestService ingestService,
    ModemRestartTrigger restartTrigger,
    ILogger<SoundModemService> logger) : BackgroundService
{
    private const int SampleRate = 48000;
    private const int RetryDelaySeconds = 10;
    private const int TxQueueCapacity = 64;

    /// <summary>A queued TX calibration test tone.</summary>
    private sealed record ToneRequest(TestToneKind Kind, int DurationMs);

    /// <summary>Per-radio runtime state.</summary>
    private sealed class ModemInstance(Radio radio)
    {
        public Radio Radio { get; } = radio;
        public volatile ModemStatusSnapshot Status = new()
        {
            RadioId = radio.Id,
            RadioName = radio.Name,
            FullCallsign = radio.FullCallsign,
            Channel = radio.ChannelNumber,
            State = ModemState.Disabled,
        };
        public volatile AfskReceiver? Receiver;
        public Channel<byte[]>? TxChannel;
        public Channel<ToneRequest>? ToneChannel;
        public volatile bool Transmitting;
        public long TransmittedFrames;
        public long RigFrequencyHz = -1;

        // TX audio level (gain), adjustable live from the UI without a modem
        // restart. Read on the transmit thread, written from a controller thread.
        private int _txAudioLevelPct = Math.Clamp(radio.TxAudioLevelPct, 1, 100);
        public int TxAudioLevelPct
        {
            get => Volatile.Read(ref _txAudioLevelPct);
            set => Volatile.Write(ref _txAudioLevelPct, Math.Clamp(value, 1, 100));
        }

        public ModemStatusSnapshot LiveStatus()
        {
            var receiver = Receiver;
            var status = Status;
            if (receiver is null || status.State != ModemState.Running)
                return status;

            var rigHz = Interlocked.Read(ref RigFrequencyHz);
            return status with
            {
                AudioLevel = receiver.PeakAudioLevel,
                CarrierDetected = receiver.CarrierDetected,
                DecodedFrames = receiver.ValidFrameCount,
                InvalidFrames = receiver.Demodulators.Sum(d => d.InvalidFrameCount),
                TxEnabled = TxChannel is not null,
                Transmitting = Transmitting,
                TransmittedFrames = Interlocked.Read(ref TransmittedFrames),
                RigFrequencyHz = rigHz >= 0 ? rigHz : null,
                DecodedByProfile = receiver.Demodulators.ToDictionary(
                    d => d.Profile.Name, d => d.ValidFrameCount),
            };
        }
    }

    private volatile List<ModemInstance> _instances = [];

    /// <summary>Current status of every modem instance, ordered by channel.</summary>
    public IReadOnlyList<ModemStatusSnapshot> Statuses =>
        _instances.Select(i => i.LiveStatus()).OrderBy(s => s.Channel).ToList();

    /// <summary>Rolled-up state for the app-level status line.</summary>
    public ModemState AggregateState
    {
        get
        {
            var statuses = _instances;
            if (statuses.Count == 0)
                return ModemState.Disabled;
            if (statuses.Any(i => i.Status.State == ModemState.Running))
                return ModemState.Running;
            if (statuses.Any(i => i.Status.State == ModemState.Error))
                return ModemState.Error;
            return ModemState.Disabled;
        }
    }

    /// <summary>True while any instance reports carrier.</summary>
    public bool AnyCarrierDetected => _instances.Any(i => i.Receiver?.CarrierDetected == true);

    /// <summary>
    /// Current spectra for the waterfalls — one entry per running instance.
    /// </summary>
    public IReadOnlyList<(string RadioId, byte[] Bins)> GetSpectra() =>
        _instances
            .Where(i => i.Status.State == ModemState.Running)
            .Select(i => (i.Radio.Id, Bins: i.Receiver?.Spectrum.ComputeSpectrum()))
            .Where(x => x.Bins is not null)
            .Select(x => (x.Id, x.Bins!))
            .ToList();

    /// <summary>
    /// Applies a new TX audio level (gain) to the running modem for
    /// <paramref name="radioId"/> immediately, without a restart.  Returns
    /// <see langword="false"/> when no running instance exists for the radio
    /// (the persisted value still takes effect on the next modem start).
    /// </summary>
    public bool SetTxAudioLevel(string radioId, int pct)
    {
        var instance = _instances.FirstOrDefault(i => i.Radio.Id == radioId);
        if (instance is null)
            return false;
        instance.TxAudioLevelPct = pct;
        return true;
    }

    /// <summary>
    /// Queues a TX calibration test tone for <paramref name="radioId"/>.  Returns
    /// <see langword="false"/> when the radio has no running TX-capable modem.
    /// The duration is clamped to a safe range so PTT is never held indefinitely.
    /// </summary>
    public bool TryEnqueueTestTone(string radioId, TestToneKind kind, int durationMs)
    {
        var tone = _instances.FirstOrDefault(i => i.Radio.Id == radioId)?.ToneChannel;
        return tone is not null
            && tone.Writer.TryWrite(new ToneRequest(kind, Math.Clamp(durationMs, 200, 10_000)));
    }

    /// <summary>
    /// Queues an AX.25 frame for transmission on the radio whose KISS channel
    /// matches, falling back to any TX-capable instance so single-radio
    /// stations never care about channel numbers.  Returns
    /// <see langword="false"/> when no instance can transmit.
    /// </summary>
    public bool TryEnqueueTransmit(byte[] ax25Frame, int channel = 0)
    {
        var instances = _instances;

        var exact = instances.FirstOrDefault(i => i.Radio.ChannelNumber == channel)?.TxChannel;
        if (exact is not null && exact.Writer.TryWrite(ax25Frame))
            return true;

        foreach (var instance in instances)
        {
            if (instance.TxChannel is { } tx && tx.Writer.TryWrite(ax25Frame))
                return true;
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Per-iteration linked token so either app shutdown or a settings
            // change can tear down every instance and re-read configuration.
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, restartTrigger.Token);
            var ct = iterCts.Token;

            try
            {
                var radios = await GetModemRadiosAsync(stoppingToken);
                if (radios.Count == 0)
                {
                    _instances = [];
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                var instances = radios.Select(r => new ModemInstance(r)).ToList();
                _instances = instances;

                await Task.WhenAll(instances.Select(i => RunInstanceAsync(i, ct)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                continue; // settings changed — reload radios
            }
            catch (Exception ex)
            {
                // Should be unreachable — instances contain their own retry
                // loops — but never let the manager die.
                logger.LogError(ex, "Sound modem manager error; restarting in {Delay}s.", RetryDelaySeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
            }
            finally
            {
                _instances = [];
            }
        }

        _instances = [];
        logger.LogInformation("SoundModemService stopped.");
    }

    /// <summary>
    /// Runs one radio's modem with its own retry loop, so a broken device on
    /// one radio never disturbs the others.
    /// </summary>
    private async Task RunInstanceAsync(ModemInstance instance, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(instance, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                instance.Status = instance.Status with
                {
                    State = ModemState.Error,
                    ErrorMessage = ex.Message,
                };
                logger.LogError(
                    ex, "Modem for {Radio} failed; retrying in {Delay}s.",
                    instance.Radio.FullCallsign, RetryDelaySeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunSessionAsync(ModemInstance instance, CancellationToken ct)
    {
        var radio = instance.Radio;
        var captureDevice = string.IsNullOrWhiteSpace(radio.ModemCaptureDevice)
            ? "default"
            : radio.ModemCaptureDevice;

        logger.LogInformation(
            "Starting modem for {Radio} on ALSA device \"{Device}\" (channel {Channel}, TX {Tx}).",
            radio.FullCallsign, captureDevice, radio.ChannelNumber,
            radio.TxEnabled ? "enabled" : "disabled");

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.FrameReceived += (frame, profile) =>
        {
            var signalData = new SignalData
            {
                AudioLevel = Math.Round(receiver.PeakAudioLevel, 3),
                DemodProfile = profile.Name,
            };
            _ = ingestService.IngestAsync(frame, radio.ChannelNumber, signalData, ct).ContinueWith(
                t => logger.LogError(t.Exception, "Unhandled error processing modem frame."),
                TaskContinuationOptions.OnlyOnFaulted);
        };

        IPttController? ptt = null;
        Channel<byte[]>? txChannel = null;
        Channel<ToneRequest>? toneChannel = null;
        if (radio.TxEnabled)
        {
            ptt = CreatePttController(radio);
            txChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(TxQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            toneChannel = Channel.CreateBounded<ToneRequest>(new BoundedChannelOptions(4)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

            if (ptt is RigctldPtt rig)
                Interlocked.Exchange(ref instance.RigFrequencyHz, rig.TryGetFrequencyHz() ?? -1);
        }

        instance.Receiver = receiver;
        instance.TxChannel = txChannel;
        instance.ToneChannel = toneChannel;

        // Either loop faulting (device unplugged, PTT failure) must tear down
        // this instance's session so the retry loop reopens everything.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sct = sessionCts.Token;

        try
        {
            // The blocking ALSA reads run on a dedicated thread so the thread
            // pool is never blocked and the DSP keeps up with real time.
            var captureTask = Task.Factory.StartNew(
                () => CaptureLoop(instance, captureDevice, receiver, sct),
                sct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var txTask = txChannel is null
                ? Task.Delay(Timeout.Infinite, sct)
                : TransmitLoopAsync(instance, receiver, ptt, txChannel, toneChannel!, sct);

            var first = await Task.WhenAny(captureTask, txTask);
            sessionCts.Cancel();

            var other = first == captureTask ? txTask : captureTask;
            try { await other; }
            catch (OperationCanceledException) { /* wound down by session cancel */ }

            await first; // propagate the original fault (or cancellation)
        }
        finally
        {
            instance.TxChannel = null;
            instance.ToneChannel = null;
            instance.Receiver = null;
            instance.Transmitting = false;
            ptt?.Dispose();
        }
    }

    private void CaptureLoop(ModemInstance instance, string deviceName, AfskReceiver receiver, CancellationToken ct)
    {
        using var device = new AlsaCaptureDevice(deviceName, SampleRate);

        instance.Status = instance.Status with
        {
            State = ModemState.Running,
            CaptureDevice = deviceName,
            ErrorMessage = null,
        };
        logger.LogInformation(
            "Modem for {Radio} running on \"{Device}\".", instance.Radio.FullCallsign, deviceName);

        var buffer = new float[1024];
        while (!ct.IsCancellationRequested)
        {
            var read = device.Read(buffer);
            if (read > 0)
                receiver.ProcessSamples(buffer.AsSpan(0, read));
        }

        ct.ThrowIfCancellationRequested();
    }

    // ── Transmit ──────────────────────────────────────────────────────────────

    private async Task TransmitLoopAsync(
        ModemInstance instance,
        AfskReceiver receiver,
        IPttController? ptt,
        Channel<byte[]> txChannel,
        Channel<ToneRequest> toneChannel,
        CancellationToken ct)
    {
        var radio = instance.Radio;
        var playbackDevice = string.IsNullOrWhiteSpace(radio.ModemPlaybackDevice)
            ? "default"
            : radio.ModemPlaybackDevice;
        var modulator = new AfskModulator(SampleRate);
        var leadFlags = modulator.FlagsForMilliseconds(radio.TxDelayMs);
        var tailFlags = modulator.FlagsForMilliseconds(radio.TxTailMs);
        var slotTimeMs = Math.Max(10, radio.TxSlotTimeMs);
        var persistence = Math.Clamp(radio.TxPersistence, 0, 255);

        while (!ct.IsCancellationRequested)
        {
            // Wake on either a queued frame or a test-tone request.
            var frameReady = txChannel.Reader.WaitToReadAsync(ct).AsTask();
            var toneReady = toneChannel.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(frameReady, toneReady);

            // A test tone is a deliberate operator action for calibration —
            // transmit it immediately, bypassing CSMA.
            if (toneChannel.Reader.TryRead(out var tone))
            {
                await SendTestToneAsync(instance, ptt, playbackDevice, modulator, tone, ct);
                continue;
            }

            // p-persistence CSMA: wait for a clear channel, then transmit with
            // probability (P+1)/256 per slot, re-checking carrier each slot.
            while (true)
            {
                while (receiver.CarrierDetected)
                    await Task.Delay(10, ct);
                if (Random.Shared.Next(256) <= persistence)
                    break;
                await Task.Delay(slotTimeMs, ct);
            }

            // Send everything queued in one keyup (including any that arrived
            // while we waited out CSMA).
            var frames = new List<byte[]>();
            while (txChannel.Reader.TryRead(out var frame))
                frames.Add(frame);
            if (frames.Count == 0)
                continue;

            var amplitude = instance.TxAudioLevelPct / 100f;
            var audio = modulator.GenerateTransmission(frames, leadFlags, tailFlags, amplitude);

            instance.Transmitting = true;
            try
            {
                ptt?.SetPtt(true);
                using var playback = new AlsaPlaybackDevice(playbackDevice, SampleRate);
                playback.Write(audio);
                playback.Drain();
            }
            finally
            {
                try { ptt?.SetPtt(false); }
                catch (Exception ex) { logger.LogError(ex, "Failed to unkey PTT for {Radio}.", radio.FullCallsign); }
                instance.Transmitting = false;
            }

            Interlocked.Add(ref instance.TransmittedFrames, frames.Count);
            logger.LogInformation(
                "{Radio} transmitted {Count} frame(s), {Ms} ms of audio.",
                radio.FullCallsign, frames.Count, audio.Length * 1000 / SampleRate);

            // Loop our own transmissions back into the ingest pipeline — the
            // equivalent of the Direwolf KISS echo that OwnBeacon matching and
            // packet history rely on (the demodulator cannot hear our own TX).
            foreach (var frame in frames)
            {
                _ = ingestService
                    .IngestAsync(frame, radio.ChannelNumber, signalData: null, ct, isOwnTransmission: true)
                    .ContinueWith(
                        t => logger.LogError(t.Exception, "Unhandled error ingesting own transmission."),
                        TaskContinuationOptions.OnlyOnFaulted);
            }

            if (ptt is RigctldPtt rig)
                Interlocked.Exchange(ref instance.RigFrequencyHz, rig.TryGetFrequencyHz() ?? -1);
        }
    }

    /// <summary>
    /// Keys PTT and plays a TX calibration test tone through the radio's playback
    /// device at the current TX audio level, then unkeys.  Uses the same PTT and
    /// playback path as a normal transmission so there is no device contention.
    /// </summary>
    private async Task SendTestToneAsync(
        ModemInstance instance,
        IPttController? ptt,
        string playbackDevice,
        AfskModulator modulator,
        ToneRequest tone,
        CancellationToken ct)
    {
        var amplitude = instance.TxAudioLevelPct / 100f;
        var (frequencies, segmentMs) = tone.Kind switch
        {
            TestToneKind.Space => (new double[] { 2200 }, (double)tone.DurationMs),
            TestToneKind.Alternating => (new double[] { 1200, 2200 }, 100.0),
            _ => (new double[] { 1200 }, (double)tone.DurationMs), // Mark (and default)
        };
        var audio = modulator.GenerateTestTone(frequencies, tone.DurationMs, segmentMs, amplitude);
        if (audio.Length == 0)
            return;

        instance.Transmitting = true;
        try
        {
            ptt?.SetPtt(true);
            using var playback = new AlsaPlaybackDevice(playbackDevice, SampleRate);
            playback.Write(audio);
            playback.Drain();
        }
        finally
        {
            try { ptt?.SetPtt(false); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to unkey PTT after test tone for {Radio}.", instance.Radio.FullCallsign);
            }
            instance.Transmitting = false;
        }

        // Cancellation requested mid-tone should still surface to the loop.
        ct.ThrowIfCancellationRequested();

        logger.LogInformation(
            "{Radio} sent {Ms} ms {Kind} test tone.",
            instance.Radio.FullCallsign, tone.DurationMs, tone.Kind);
    }

    /// <summary>
    /// Builds the radio's configured PTT controller, or <see langword="null"/>
    /// for VOX operation.  Throws when the configuration is incomplete or the
    /// device cannot be opened — surfacing as that instance's Error state.
    /// </summary>
    private static IPttController? CreatePttController(Radio radio) =>
        radio.PttMethod switch
        {
            PttMethod.None or PttMethod.Unknown => null,
            PttMethod.SerialRtsDtr => new SerialPtt(
                radio.PttSerialPort is { Length: > 0 } port
                    ? port
                    : throw new InvalidOperationException("Serial PTT selected but no serial port configured."),
                radio.PttSerialUseRts,
                radio.PttSerialUseDtr),
            PttMethod.Cm108 => new Cm108Ptt(
                radio.PttHidDevice is { Length: > 0 } hid
                    ? hid
                    : throw new InvalidOperationException("CM108 PTT selected but no hidraw device configured."),
                radio.PttHidPin),
            PttMethod.Gpio => new GpioPtt(
                radio.PttGpioChip,
                radio.PttGpioLine,
                radio.PttGpioActiveLow),
            PttMethod.Rigctld => new RigctldPtt(
                radio.PttRigctldHost,
                radio.PttRigctldPort),
            _ => throw new InvalidOperationException($"Unsupported PTT method {radio.PttMethod}."),
        };

    private async Task<List<Radio>> GetModemRadiosAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.Radios
            .AsNoTracking()
            .Where(r => r.IsActive && r.ModemEnabled)
            .OrderBy(r => r.ChannelNumber)
            .ToListAsync(ct);
    }
}

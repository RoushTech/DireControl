using System.Threading.Channels;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem;
using DireControl.Modem.Audio;
using DireControl.Modem.Dsp;
using DireControl.Modem.Ptt;

namespace DireControl.Api.Services;

/// <summary>
/// Point-in-time snapshot of the native modem for the status API and SignalR.
/// </summary>
public sealed record ModemStatusSnapshot
{
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
/// The native soundcard modem backend: captures audio from an ALSA device on a
/// dedicated thread, runs the <see cref="AfskReceiver"/> DSP pipeline, and
/// hands decoded AX.25 frames to the shared <see cref="RfFrameIngestService"/> —
/// no external TNC involved.  When TX is enabled it also drains a transmit
/// queue: p-persistence CSMA channel access, PTT keying, AFSK playback, and a
/// loopback ingest of its own transmissions (standing in for the KISS echo the
/// rest of the pipeline expects).  Reconfigures live via
/// <see cref="ModemRestartTrigger"/> and recovers from device errors automatically.
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

    private volatile ModemStatusSnapshot _status = new() { State = ModemState.Disabled };
    private AfskReceiver? _receiver;
    private Channel<byte[]>? _txChannel;
    private volatile bool _transmitting;
    private long _transmittedFrames;
    private long _rigFrequencyHz = -1;

    /// <summary>Current modem status for the API/SignalR layer.</summary>
    public ModemStatusSnapshot Status
    {
        get
        {
            // Merge live DSP/TX counters into the last lifecycle snapshot.
            var receiver = _receiver;
            var status = _status;
            if (receiver is null || status.State != ModemState.Running)
                return status;

            var rigHz = Interlocked.Read(ref _rigFrequencyHz);
            return status with
            {
                AudioLevel = receiver.PeakAudioLevel,
                CarrierDetected = receiver.CarrierDetected,
                DecodedFrames = receiver.ValidFrameCount,
                InvalidFrames = receiver.Demodulators.Sum(d => d.InvalidFrameCount),
                TxEnabled = _txChannel is not null,
                Transmitting = _transmitting,
                TransmittedFrames = Interlocked.Read(ref _transmittedFrames),
                RigFrequencyHz = rigHz >= 0 ? rigHz : null,
                DecodedByProfile = receiver.Demodulators.ToDictionary(
                    d => d.Profile.Name, d => d.ValidFrameCount),
            };
        }
    }

    /// <summary>
    /// Current audio spectrum (0–4 kHz, byte-quantised dB bins) for the
    /// waterfall, or <see langword="null"/> when the modem is not running.
    /// </summary>
    public byte[]? GetSpectrum() =>
        _status.State == ModemState.Running ? _receiver?.Spectrum.ComputeSpectrum() : null;

    /// <summary>
    /// Queues an AX.25 frame for native-modem transmission.  Returns
    /// <see langword="false"/> when the modem is not running with TX enabled
    /// (callers then fall back to the KISS TNC).
    /// </summary>
    public bool TryEnqueueTransmit(byte[] ax25Frame)
    {
        var channel = _txChannel;
        return channel is not null && channel.Writer.TryWrite(ax25Frame);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Per-iteration linked token so either app shutdown or a settings
            // change can interrupt the capture loop.
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, restartTrigger.Token);
            var ct = iterCts.Token;

            try
            {
                var settings = await GetSettingsAsync(stoppingToken);

                if (settings is not { ModemEnabled: true })
                {
                    _status = new ModemStatusSnapshot { State = ModemState.Disabled };
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                await RunSessionAsync(settings, ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Settings changed — loop immediately to re-read them.
                continue;
            }
            catch (Exception ex)
            {
                _status = new ModemStatusSnapshot
                {
                    State = ModemState.Error,
                    ErrorMessage = ex.Message,
                };
                logger.LogError(ex, "Sound modem error; retrying in {Delay}s.", RetryDelaySeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* settings changed */ }
            }
        }

        _status = new ModemStatusSnapshot { State = ModemState.Disabled };
        logger.LogInformation("SoundModemService stopped.");
    }

    /// <summary>
    /// One capture (+ optional transmit) session — runs until cancellation or
    /// a device error tears it down.
    /// </summary>
    private async Task RunSessionAsync(UserSetting settings, CancellationToken ct)
    {
        var captureDevice = string.IsNullOrWhiteSpace(settings.ModemCaptureDevice)
            ? "default"
            : settings.ModemCaptureDevice;
        var kissChannel = settings.ModemKissChannel;

        logger.LogInformation(
            "Starting sound modem on ALSA device \"{Device}\" at {Rate} Hz (KISS channel {Channel}, TX {Tx}).",
            captureDevice, SampleRate, kissChannel, settings.ModemTxEnabled ? "enabled" : "disabled");

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.FrameReceived += (frame, profile) =>
        {
            var signalData = new SignalData
            {
                AudioLevel = Math.Round(receiver.PeakAudioLevel, 3),
                DemodProfile = profile.Name,
            };
            _ = ingestService.IngestAsync(frame, kissChannel, signalData, ct).ContinueWith(
                t => logger.LogError(t.Exception, "Unhandled error processing modem frame."),
                TaskContinuationOptions.OnlyOnFaulted);
        };

        IPttController? ptt = null;
        Channel<byte[]>? txChannel = null;
        if (settings.ModemTxEnabled)
        {
            ptt = CreatePttController(settings);
            txChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(TxQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

            if (ptt is RigctldPtt rig)
                Interlocked.Exchange(ref _rigFrequencyHz, rig.TryGetFrequencyHz() ?? -1);
        }

        _receiver = receiver;
        _txChannel = txChannel;

        // Either loop faulting (device unplugged, PTT failure) must tear down
        // the whole session so the outer retry logic reopens everything.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sct = sessionCts.Token;

        try
        {
            // The blocking ALSA reads run on a dedicated thread so the thread
            // pool is never blocked and the DSP keeps up with real time.
            var captureTask = Task.Factory.StartNew(
                () => CaptureLoop(captureDevice, receiver, sct),
                sct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var txTask = txChannel is null
                ? Task.Delay(Timeout.Infinite, sct)
                : TransmitLoopAsync(settings, receiver, ptt, txChannel, kissChannel, sct);

            var first = await Task.WhenAny(captureTask, txTask);
            sessionCts.Cancel();

            var other = first == captureTask ? txTask : captureTask;
            try { await other; }
            catch (OperationCanceledException) { /* wound down by session cancel */ }

            await first; // propagate the original fault (or cancellation)
        }
        finally
        {
            _txChannel = null;
            _receiver = null;
            _transmitting = false;
            ptt?.Dispose();
        }
    }

    private void CaptureLoop(string deviceName, AfskReceiver receiver, CancellationToken ct)
    {
        using var device = new AlsaCaptureDevice(deviceName, SampleRate);

        _status = new ModemStatusSnapshot
        {
            State = ModemState.Running,
            CaptureDevice = deviceName,
        };
        logger.LogInformation("Sound modem running on \"{Device}\".", deviceName);

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
        UserSetting settings,
        AfskReceiver receiver,
        IPttController? ptt,
        Channel<byte[]> txChannel,
        int kissChannel,
        CancellationToken ct)
    {
        var playbackDevice = string.IsNullOrWhiteSpace(settings.ModemPlaybackDevice)
            ? "default"
            : settings.ModemPlaybackDevice;
        var modulator = new AfskModulator(SampleRate);
        var amplitude = Math.Clamp(settings.ModemTxAudioLevelPct, 1, 100) / 100f;
        var leadFlags = modulator.FlagsForMilliseconds(settings.ModemTxDelayMs);
        var tailFlags = modulator.FlagsForMilliseconds(settings.ModemTxTailMs);
        var slotTimeMs = Math.Max(10, settings.ModemSlotTimeMs);
        var persistence = Math.Clamp(settings.ModemPersistence, 0, 255);

        while (await txChannel.Reader.WaitToReadAsync(ct))
        {
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

            // Send everything queued in one keyup.
            var frames = new List<byte[]>();
            while (txChannel.Reader.TryRead(out var frame))
                frames.Add(frame);
            if (frames.Count == 0)
                continue;

            var audio = modulator.GenerateTransmission(frames, leadFlags, tailFlags, amplitude);

            _transmitting = true;
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
                catch (Exception ex) { logger.LogError(ex, "Failed to unkey PTT."); }
                _transmitting = false;
            }

            Interlocked.Add(ref _transmittedFrames, frames.Count);
            logger.LogInformation(
                "Transmitted {Count} frame(s), {Ms} ms of audio.",
                frames.Count, audio.Length * 1000 / SampleRate);

            // Loop our own transmissions back into the ingest pipeline — the
            // equivalent of the Direwolf KISS echo that OwnBeacon matching and
            // packet history rely on (the demodulator cannot hear our own TX).
            foreach (var frame in frames)
            {
                _ = ingestService.IngestAsync(frame, kissChannel, signalData: null, ct, isOwnTransmission: true)
                    .ContinueWith(
                        t => logger.LogError(t.Exception, "Unhandled error ingesting own transmission."),
                        TaskContinuationOptions.OnlyOnFaulted);
            }

            if (ptt is RigctldPtt rig)
                Interlocked.Exchange(ref _rigFrequencyHz, rig.TryGetFrequencyHz() ?? -1);
        }
    }

    /// <summary>
    /// Builds the configured PTT controller, or <see langword="null"/> for
    /// VOX operation.  Throws when the configuration is incomplete or the
    /// device cannot be opened — surfacing as the modem Error state.
    /// </summary>
    private static IPttController? CreatePttController(UserSetting settings) =>
        settings.ModemPttMethod switch
        {
            PttMethod.None or PttMethod.Unknown => null,
            PttMethod.SerialRtsDtr => new SerialPtt(
                settings.ModemPttSerialPort is { Length: > 0 } port
                    ? port
                    : throw new InvalidOperationException("Serial PTT selected but no serial port configured."),
                settings.ModemPttSerialUseRts,
                settings.ModemPttSerialUseDtr),
            PttMethod.Cm108 => new Cm108Ptt(
                settings.ModemPttHidDevice is { Length: > 0 } hid
                    ? hid
                    : throw new InvalidOperationException("CM108 PTT selected but no hidraw device configured."),
                settings.ModemPttHidPin),
            PttMethod.Gpio => new GpioPtt(
                settings.ModemPttGpioChip,
                settings.ModemPttGpioLine,
                settings.ModemPttGpioActiveLow),
            PttMethod.Rigctld => new RigctldPtt(
                settings.ModemPttRigctldHost,
                settings.ModemPttRigctldPort),
            _ => throw new InvalidOperationException($"Unsupported PTT method {settings.ModemPttMethod}."),
        };

    private async Task<UserSetting?> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.FindAsync([1], ct);
    }
}

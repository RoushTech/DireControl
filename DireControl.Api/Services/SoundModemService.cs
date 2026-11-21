using DireControl.Api.Controllers.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using DireControl.Api.Logging;
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
/// <summary>Outcome of <see cref="SoundModemService.ForceUnkey"/>.</summary>
public enum ForceUnkeyResult
{
    Unknown = 0,
    Released,
    NotFound,
    SessionRunning,
    NoPtt,
    Failed,
}

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

    /// <summary>UTC start of the current carrier-detect burst; null while the channel is clear.</summary>
    public DateTime? CarrierSince { get; init; }

    /// <summary>Preambles seen since <see cref="CarrierSince"/>.</summary>
    public long PreamblesSinceCarrier { get; init; }

    /// <summary>UTC time the capture thread last delivered audio (when <see cref="AudioLevel"/> was measured).</summary>
    public DateTime? LevelUpdatedAt { get; init; }

    /// <summary>
    /// Carrier detect has been held for <see cref="ModemOptions.CarrierStuckSeconds"/>
    /// or more without a single preamble — noise or squelch, not a transmission.
    /// CSMA ignores it while this is set.
    /// </summary>
    public bool CarrierStuck { get; init; }

    /// <summary>Keyups abandoned because the channel never cleared within the CSMA cap.</summary>
    public long CsmaTimeouts { get; init; }

    /// <summary>Decoded frames dropped because ingest fell behind the capture thread.</summary>
    public long IngestDropped { get; init; }

    /// <summary>Why the last keyup failed (cleared by the next good one); RX is unaffected.</summary>
    public string? TxError { get; init; }

    /// <summary>PTT could not be released after retries — the radio may be stuck transmitting.</summary>
    public bool PttStuck { get; init; }

    /// <summary>Live PTT line: Released / Keyed / Stuck (Unknown without a TX path).</summary>
    public PttState PttState => PttStuck ? PttState.Stuck : Transmitting ? PttState.Keyed : TxEnabled ? PttState.Released : PttState.Unknown;

    /// <summary>Keyups the PTT watchdog had to cut short.</summary>
    public long PttWatchdogTrips { get; init; }

    /// <summary>Queued frames discarded because a TX lane overflowed.</summary>
    public long TxDropped { get; init; }

    /// <summary>Frames waiting across every TX lane.</summary>
    public int TxQueueDepth { get; init; }

    /// <summary>ALSA capture overruns this session — audio the demodulator never saw.</summary>
    public long CaptureOverruns { get; init; }

    public long DecodedFrames { get; init; }

    /// <summary>UTC time of the last frame this modem decoded off the air.</summary>
    public DateTime? LastDecodedAt { get; init; }

    /// <summary>UTC time the capture device last came up (state became Running).</summary>
    public DateTime? RunningSince { get; init; }
    public long InvalidFrames { get; init; }

    /// <summary>Decoded frames that needed a bit repair to pass their checksum.</summary>
    public long RepairedFrames { get; init; }
    public bool TxEnabled { get; init; }
    public bool Transmitting { get; init; }
    public long TransmittedFrames { get; init; }

    /// <summary>UTC end of the last keyup that actually went to air.</summary>
    public DateTime? LastTxAt { get; init; }

    /// <summary>Set while frames wait for carrier detect to clear (CSMA), else null.</summary>
    public DateTime? CsmaWaitingSince { get; init; }
    public long? RigFrequencyHz { get; init; }
    public IReadOnlyDictionary<string, long>? DecodedByProfile { get; init; }
}

/// <summary>
/// An outbound frame plus its optional airtime completion — completed after
/// the keyup that carried it, cancelled if the modem tears down first.
/// </summary>
public readonly record struct TxItem(byte[] Frame, TaskCompletionSource<bool>? Completion, long FrameId = 0);

/// <summary>
/// The native soundcard modem backend, one instance per active radio with
/// modem audio configured — each radio gets its own capture device, DSP
/// pipeline, transmit queue, and PTT controller, all identified by the
/// radio's KISS channel number.  Decoded AX.25 frames flow into the shared
/// <see cref="RfFrameIngestService"/>; outbound frames are routed to the
/// instance whose channel matches (or any TX-capable instance as fallback).
/// One radio's device error never disturbs the others — each instance retries
/// independently.  Reconfigures live via the <c>Modem</c> restart signal.
/// </summary>
public sealed class SoundModemService(
    IServiceScopeFactory scopeFactory,
    RfFrameIngestService ingestService,
    [FromKeyedServices(RestartSignals.Modem)] RestartSignal restartTrigger,
    RadioAudioBroker audioBroker,
    ModemAudioCaptureService captureService,
    TxFrameRegistry txFrames,
    Microsoft.Extensions.Options.IOptions<ModemOptions> modemOptions,
    ILoggerFactory loggerFactory,
    ILogger<SoundModemService> logger) : BackgroundService
{
    /// <summary>
    /// Per-burst channel activity logs under its own category so it can be
    /// silenced on its own.  Sharing this class's category would mean turning
    /// it off also turned off modem start-up and transmit logging.
    /// </summary>
    private readonly ILogger _channelActivity =
        loggerFactory.CreateLogger(LogCategories.ChannelActivity);

    /// <summary>
    /// The rate every modem instance captures and transmits at.  Public so
    /// audio capture and replay use exactly the rate the demodulator saw.
    /// </summary>
    public const int SampleRate = 48000;

    /// <summary>
    /// Peak sample level transmit audio is normalised to for monitoring, chosen
    /// to sit in the same range as typical receive audio off a data jack.
    /// </summary>
    private const float TxMonitorPeak = 0.2f;

    private const int RetryDelaySeconds = 10;
    private const int TxQueueCapacity = 64;
    private const int PriorityTxQueueCapacity = 32;
    private const int DigipeatTxQueueCapacity = 16;

    /// <summary>Digipeats per keyup — one, ahead of beacons (Dire Wolf behaviour).</summary>
    internal const int MaxDigipeatsPerKeyup = 1;

    /// <summary>
    /// Longest airtime one keyup may take, preamble included.  Without a budget
    /// a full session lane plus a normal-lane slice keyed for over 40 s.
    /// </summary>
    internal const double KeyupBudgetSeconds = 6.0;

    /// <summary>
    /// Cap on normal-lane frames per keyup.  Bounds keyup length so a beacon
    /// or digipeat backlog cannot hold the channel for seconds in front of a
    /// waiting session ack; the remainder waits for the next CSMA cycle.
    /// </summary>
    private const int MaxNormalFramesPerKeyup = 5;

    /// <summary>A queued TX calibration test tone.</summary>
    private sealed record ToneRequest(TestToneKind Kind, int DurationMs, long FrameId = 0);

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
        public volatile RfIngestDispatcher? Ingest;
        public volatile AlsaCaptureDevice? CaptureDevice;
        public Channel<TxItem>? TxChannel;
        public Channel<TxItem>? PriorityTxChannel;
        public Channel<TxItem>? DigipeatTxChannel;
        public long TxDropped;
        public Channel<ToneRequest>? ToneChannel;
        public volatile bool Transmitting;
        public volatile bool CarrierStuck;
        public volatile bool PttStuck;
        public volatile string? TxError;
        public long PttWatchdogTrips;
        public long TransmittedFrames;
        public long LastTxAtTicks;
        public long CsmaWaitingSinceTicks;
        public long CsmaTimeouts;
        public readonly CarrierBurstTracker Burst = new();
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
                CarrierSince = Burst.CarrierSince,
                PreamblesSinceCarrier = Burst.PreamblesSinceCarrier(
                    receiver.Demodulators.Sum(d => d.PreambleCount)),
                LevelUpdatedAt = Burst.LevelUpdatedAt,
                CarrierStuck = CarrierStuck,
                CsmaTimeouts = Interlocked.Read(ref CsmaTimeouts),
                IngestDropped = Ingest?.Dropped ?? 0,
                TxError = TxError,
                PttStuck = PttStuck,
                PttWatchdogTrips = Interlocked.Read(ref PttWatchdogTrips),
                TxDropped = Interlocked.Read(ref TxDropped),
                CaptureOverruns = CaptureDevice?.Overruns ?? 0,
                DecodedFrames = receiver.ValidFrameCount,
                LastDecodedAt = receiver.LastFrameAt,
                InvalidFrames = receiver.Demodulators.Sum(d => d.InvalidFrameCount),
                RepairedFrames = receiver.RepairedFrameCount,
                TxEnabled = TxChannel is not null,
                TxQueueDepth = (TxChannel?.Reader.Count ?? 0)
                    + (PriorityTxChannel?.Reader.Count ?? 0)
                    + (DigipeatTxChannel?.Reader.Count ?? 0),
                Transmitting = Transmitting,
                TransmittedFrames = Interlocked.Read(ref TransmittedFrames),
                LastTxAt = Interlocked.Read(ref LastTxAtTicks) is > 0 and var tx
                    ? new DateTime(tx, DateTimeKind.Utc) : null,
                CsmaWaitingSince = Interlocked.Read(ref CsmaWaitingSinceTicks) is > 0 and var wait
                    ? new DateTime(wait, DateTimeKind.Utc) : null,
                RigFrequencyHz = rigHz >= 0 ? rigHz : null,
                DecodedByProfile = receiver.Demodulators.ToDictionary(
                    d => d.Profile.Name, d => d.ValidFrameCount),
            };
        }
    }

    private volatile List<ModemInstance> _instances = [];

    /// <summary>Receive/transmit internals of every modem instance, for the Diagnostics page.</summary>
    public IReadOnlyList<ModemDiagnosticsDto> GetDiagnostics()
    {
        var opts = modemOptions.Value;
        var now = DateTime.UtcNow;
        return _instances
            .OrderBy(i => i.Radio.ChannelNumber)
            .Select(i =>
            {
                var s = i.LiveStatus();
                return new ModemDiagnosticsDto
                {
                    RadioId = s.RadioId,
                    RadioName = s.RadioName,
                    State = s.State,
                    CarrierDutyLastMinute = s.State == ModemState.Running ? i.Burst.CarrierDutyLastMinute(now) : null,
                    CarrierDetected = s.CarrierDetected,
                    CarrierSince = s.CarrierSince,
                    PreamblesSinceCarrier = s.PreamblesSinceCarrier,
                    CarrierStuck = s.CarrierStuck,
                    CarrierStuckSeconds = Math.Max(1, opts.CarrierStuckSeconds),
                    DecodedFrames = s.DecodedFrames,
                    InvalidFrames = s.InvalidFrames,
                    RepairedFrames = s.RepairedFrames,
                    DecodedByProfile = s.DecodedByProfile,
                    IngestDropped = s.IngestDropped,
                    CaptureDevice = s.CaptureDevice,
                    CapturePeriodMs = opts.CapturePeriodMs,
                    CaptureExplicitPeriod = i.CaptureDevice?.ExplicitPeriod ?? false,
                    CaptureOverruns = s.CaptureOverruns,
                    LevelUpdatedAt = s.LevelUpdatedAt,
                    TxEnabled = s.TxEnabled,
                    NormalLaneDepth = i.TxChannel?.Reader.Count ?? 0,
                    DigipeatLaneDepth = i.DigipeatTxChannel?.Reader.Count ?? 0,
                    SessionLaneDepth = i.PriorityTxChannel?.Reader.Count ?? 0,
                    TxDropped = s.TxDropped,
                    CsmaWaitingSince = s.CsmaWaitingSince,
                    CsmaTimeouts = s.CsmaTimeouts,
                    CsmaMaxWaitSeconds = Math.Max(1, opts.CsmaMaxWaitSeconds),
                    CsmaTimeoutAction = opts.CsmaTimeoutAction,
                    PttMethod = i.Radio.PttMethod,
                    PttState = s.PttState,
                    PttWatchdogTrips = s.PttWatchdogTrips,
                    TxError = s.TxError,
                    TransmittedFrames = s.TransmittedFrames,
                    LastTxAt = s.LastTxAt,
                };
            })
            .ToList();
    }

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
    /// Last-resort release of a PTT that would not unkey: once the session has
    /// faulted (its own controller disposed), open a fresh controller for the
    /// radio's PTT config, release PTT and dispose it.  Refused while a session
    /// is running — that session owns the PTT device.
    /// </summary>
    public ForceUnkeyResult ForceUnkey(string radioId)
    {
        var instance = _instances.FirstOrDefault(i => i.Radio.Id == radioId);
        if (instance is null)
            return ForceUnkeyResult.NotFound;
        var result = TryForceUnkey(instance.Status.State, () => PttControllerFactory(instance.Radio), out var error);
        switch (result)
        {
            case ForceUnkeyResult.Released:
                logger.LogWarning("PTT for {Radio} force-released by the operator.", instance.Radio.FullCallsign);
                instance.PttStuck = false;
                instance.Status = instance.Status with { PttStuck = false };
                break;
            case ForceUnkeyResult.Failed:
                logger.LogError(error, "Force unkey for {Radio} failed.", instance.Radio.FullCallsign);
                instance.TxError = $"force unkey failed: {error?.Message}";
                instance.Status = instance.Status with { TxError = instance.TxError };
                break;
        }
        return result;
    }

    /// <summary>The decision and I/O behind <see cref="ForceUnkey"/>, without the instance bookkeeping.</summary>
    internal static ForceUnkeyResult TryForceUnkey(
        ModemState sessionState, Func<IPttController?> createPtt, out Exception? error)
    {
        error = null;
        if (sessionState == ModemState.Running)
            return ForceUnkeyResult.SessionRunning;
        try
        {
            using var ptt = createPtt();
            if (ptt is null)
                return ForceUnkeyResult.NoPtt;
            ptt.SetPtt(false);
            return ForceUnkeyResult.Released;
        }
        catch (Exception ex)
        {
            error = ex;
            return ForceUnkeyResult.Failed;
        }
    }

    /// <summary>Builds a radio's PTT controller; replaceable for tests.</summary>
    internal Func<Radio, IPttController?> PttControllerFactory { get; set; } = CreatePttController;

    /// <summary>
    /// Queues a TX calibration test tone for <paramref name="radioId"/>.  Returns
    /// <see langword="false"/> when the radio has no running TX-capable modem.
    /// The duration is clamped to a safe range so PTT is never held indefinitely.
    /// </summary>
    public long? TryEnqueueTestTone(string radioId, TestToneKind kind, int durationMs)
    {
        var instance = _instances.FirstOrDefault(i => i.Radio.Id == radioId);
        if (instance?.ToneChannel is not { } tone)
            return null;
        var title = kind == TestToneKind.PttOnly ? "PTT test" : $"{kind} test tone";
        var id = txFrames.Begin(new TxFrameInfo(TxFrameKind.TestTone, title));
        if (!tone.Writer.TryWrite(new ToneRequest(kind, Math.Clamp(durationMs, 200, 10_000), id)))
        {
            txFrames.Abandon(id);
            return null;
        }
        txFrames.Queued(id, instance.Radio.Id, instance.Radio.Name);
        return id;
    }

    /// <summary>
    /// Queues an AX.25 frame for transmission on the radio whose KISS channel
    /// matches, falling back to any TX-capable instance so single-radio
    /// stations never care about channel numbers.  Returns
    /// <see langword="false"/> when no instance can transmit.
    /// </summary>
    public bool TryEnqueueTransmit(byte[] ax25Frame, int channel = 0) =>
        TryEnqueueTransmit(ax25Frame, channel, TxPriority.Normal, txCompletion: null, exactChannelOnly: false);

    /// <summary>
    /// Queues an AX.25 frame with an explicit priority lane.
    /// <paramref name="txCompletion"/> (if any) completes after the frame's
    /// keyup finishes — the LAPB layer starts T1 there — and is cancelled when
    /// the modem tears down before transmitting.  Session traffic passes
    /// <paramref name="exactChannelOnly"/> because a session is bound to one
    /// frequency; the any-radio fallback remains for order-tolerant traffic.
    /// </summary>
    public bool TryEnqueueTransmit(
        byte[] ax25Frame,
        int channel,
        TxPriority priority,
        TaskCompletionSource<bool>? txCompletion,
        bool exactChannelOnly = false,
        long frameId = 0)
    {
        var instances = _instances;
        var item = new TxItem(ax25Frame, txCompletion, frameId);

        var exact = instances.FirstOrDefault(i => i.Radio.ChannelNumber == channel);
        if (exact is not null && TryWrite(exact, item, priority))
            return true;

        if (exactChannelOnly)
            return false;

        foreach (var instance in instances)
        {
            if (instance != exact && TryWrite(instance, item, priority))
                return true;
        }

        return false;

        bool TryWrite(ModemInstance instance, TxItem item, TxPriority priority)
        {
            var channel = priority switch
            {
                TxPriority.Session => instance.PriorityTxChannel,
                TxPriority.Digipeat => instance.DigipeatTxChannel,
                _ => instance.TxChannel,
            };
            if (channel is null || !channel.Writer.TryWrite(item))
                return false;
            txFrames.Queued(item.FrameId, instance.Radio.Id, instance.Radio.Name);
            return true;
        }
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
                txFrames.FailPending(instance.Radio.Id, "The modem restarted before this frame went to air.");
                return;
            }
            catch (Exception ex)
            {
                txFrames.FailPending(instance.Radio.Id, $"The modem stopped before this frame went to air: {ex.Message}");
                instance.Status = instance.Status with
                {
                    State = ModemState.Error,
                    ErrorMessage = ex.Message,
                    // A session torn down by a failed unkey must keep saying so —
                    // the radio may still be transmitting.
                    PttStuck = instance.PttStuck,
                    TxError = instance.TxError,
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

        // Decoded frames are handed off to their own consumer: this callback
        // runs on the ALSA capture thread, which must never wait on the database.
        var ingest = new RfIngestDispatcher();
        var receiver = AfskReceiver.CreateStandard(
            SampleRate, Math.Clamp(radio.RxBitRepair, 0, 2));
        receiver.FrameReceived += (frame, decode) =>
        {
            var signalData = new SignalData
            {
                AudioLevel = Math.Round(receiver.PeakAudioLevel, 3),
                DemodProfile = decode.Profile.Name,
                RepairedBits = decode.RepairedBits > 0 ? decode.RepairedBits : null,
            };
            ingest.Post(new RfIngestItem(frame, radio.ChannelNumber, signalData));
        };

        IPttController? ptt = null;
        Channel<TxItem>? txChannel = null;
        Channel<TxItem>? priorityTxChannel = null;
        Channel<TxItem>? digipeatTxChannel = null;
        Channel<ToneRequest>? toneChannel = null;
        if (radio.TxEnabled)
        {
            ptt = PttControllerFactory(radio);
            txChannel = CreateDroppingLane(TxQueueCapacity, instance, "normal");
            digipeatTxChannel = CreateDroppingLane(DigipeatTxQueueCapacity, instance, "digipeat");
            // Session frames must never be silently dropped (their completions
            // drive LAPB timers): a full lane fails the write instead, and the
            // session layer handles the failed send.
            priorityTxChannel = Channel.CreateBounded<TxItem>(new BoundedChannelOptions(PriorityTxQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
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
        instance.Ingest = ingest;
        instance.TxChannel = txChannel;
        instance.PriorityTxChannel = priorityTxChannel;
        instance.DigipeatTxChannel = digipeatTxChannel;
        instance.ToneChannel = toneChannel;

        // Either loop faulting (device unplugged, PTT failure) must tear down
        // this instance's session so the retry loop reopens everything.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sct = sessionCts.Token;

        var ingestTask = ingest.RunAsync(
            (item, token) => ingestService.IngestAsync(
                item.Frame, item.Channel, item.Signal, token, item.IsOwnTransmission),
            logger,
            sct);

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
                : TransmitLoopAsync(
                instance, receiver, ingest, ptt, txChannel, priorityTxChannel!, digipeatTxChannel!, toneChannel!, sct);

            var first = await Task.WhenAny(captureTask, txTask);
            sessionCts.Cancel();

            var other = first == captureTask ? txTask : captureTask;
            try { await other; }
            catch (OperationCanceledException) { /* wound down by session cancel */ }

            await first; // propagate the original fault (or cancellation)
        }
        finally
        {
            sessionCts.Cancel();
            try { await ingestTask; }
            catch (OperationCanceledException) { /* session over */ }

            instance.TxChannel = null;
            instance.PriorityTxChannel = null;
            instance.DigipeatTxChannel = null;
            instance.ToneChannel = null;
            instance.Receiver = null;
            instance.Ingest = null;
            instance.CaptureDevice = null;
            instance.Transmitting = false;
            ptt?.Dispose();

            // Frames stuck in a dead session's queues will never transmit —
            // cancel their completions so LAPB timers are not left hanging.
            CancelPending(txChannel);
            CancelPending(priorityTxChannel);
            CancelPending(digipeatTxChannel);
        }
    }

    /// <summary>
    /// Closes a dead session's lane and cancels everything still in it.  The
    /// writer is completed first: a producer holding the old channel reference
    /// would otherwise enqueue a frame after the drain, stranding it and its
    /// LAPB completion forever.  Completed, its TryWrite fails and the sender
    /// handles the failure instead.
    /// </summary>
    internal static void CancelPending(Channel<TxItem>? channel)
    {
        if (channel is null)
            return;
        channel.Writer.TryComplete();
        while (channel.Reader.TryRead(out var item))
            item.Completion?.TrySetCanceled();
    }

    /// <summary>
    /// A lane that drops its oldest frame when full — counted, logged, and the
    /// dropped frame's completion cancelled, so producers never believe a
    /// frame was sent when it was silently discarded.
    /// </summary>
    private Channel<TxItem> CreateDroppingLane(int capacity, ModemInstance instance, string lane) =>
        CreateDroppingLane(capacity, txFrames, lane, dropped =>
        {
            Interlocked.Increment(ref instance.TxDropped);
            logger.LogWarning(
                "{Radio} {Lane} TX queue full; dropped the oldest frame ({Bytes} bytes).",
                instance.Radio.FullCallsign, lane, dropped.Frame.Length);
        });

    /// <summary>A dropping lane whose overflow is reported as a Dropped frame.</summary>
    internal static Channel<TxItem> CreateDroppingLane(
        int capacity, TxFrameRegistry registry, string lane, Action<TxItem> onDropped) =>
        CreateDroppingLane(capacity, dropped =>
        {
            registry.Report(
                dropped.FrameId, TxFrameState.Dropped,
                $"The {lane} TX queue was full, so the oldest frame was dropped.");
            onDropped(dropped);
        });

    internal static Channel<TxItem> CreateDroppingLane(int capacity, Action<TxItem> onDropped) =>
        Channel.CreateBounded<TxItem>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            dropped =>
            {
                dropped.Completion?.TrySetCanceled();
                onDropped(dropped);
            });

    private void CaptureLoop(ModemInstance instance, string deviceName, AfskReceiver receiver, CancellationToken ct)
    {
        var periodMs = modemOptions.Value.CapturePeriodMs;
        using var device = new AlsaCaptureDevice(
            deviceName, SampleRate, periodUs: periodMs > 0 ? (uint)periodMs * 1000 : null);
        instance.CaptureDevice = device;
        if (periodMs > 0 && !device.ExplicitPeriod)
            logger.LogWarning(
                "{Radio} capture device \"{Device}\" refused a {Period} ms period; using the ALSA default.",
                instance.Radio.FullCallsign, deviceName, periodMs);

        instance.Status = instance.Status with
        {
            State = ModemState.Running,
            CaptureDevice = deviceName,
            ErrorMessage = null,
            RunningSince = DateTime.UtcNow,
        };
        logger.LogInformation(
            "Modem for {Radio} running on \"{Device}\".", instance.Radio.FullCallsign, deviceName);

        var buffer = new float[1024];
        var monitor = new AudioMonitorResampler(SampleRate);
        var radioId = instance.Radio.Id;
        var wasPublishing = false;
        var wasCarrier = false;
        instance.Burst.Reset();
        var validAtCarrierStart = 0L;
        var invalidAtCarrierStart = 0L;
        var preamblesAtCarrierStart = 0L;
        var burstStartSample = 0L;
        var burstPeakLevel = 0f;
        var samplesSeen = 0L;
        var stuckSamples = (long)Math.Max(1, modemOptions.Value.CarrierStuckSeconds) * SampleRate;

        while (!ct.IsCancellationRequested)
        {
            var read = device.Read(buffer);
            if (read <= 0)
                continue;

            var samples = buffer.AsSpan(0, read);
            receiver.ProcessSamples(samples);
            captureService.Append(radioId, samples, SampleRate);
            samplesSeen += read;

            // Every carrier burst is reported once it ends — what the channel
            // did, how strong it was, and whether anything came of it.  This is
            // the only way to tell "nothing was transmitted" apart from "a
            // transmission arrived and we failed to decode it", which look
            // identical from the packet log alone.
            var carrierNow = receiver.CarrierDetected;
            instance.Burst.OnBlock(
                carrierNow,
                carrierNow ? receiver.Demodulators.Sum(d => d.PreambleCount) : 0,
                DateTime.UtcNow,
                read);
            if (carrierNow)
            {
                if (!wasCarrier)
                {
                    validAtCarrierStart = receiver.ValidFrameCount;
                    invalidAtCarrierStart = receiver.Demodulators.Sum(d => d.InvalidFrameCount);
                    preamblesAtCarrierStart = receiver.Demodulators.Sum(d => d.PreambleCount);
                    burstStartSample = samplesSeen;
                    burstPeakLevel = 0f;
                }

                // Peak is a ~100 ms rolling window, so it has to be tracked
                // across the burst — by the time carrier drops it reflects the
                // silence afterwards, not the transmission.
                burstPeakLevel = MathF.Max(burstPeakLevel, receiver.PeakAudioLevel);

                // Carrier held for seconds with no preamble at all is not a
                // transmission — stop letting it hold off CSMA.
                if (!instance.CarrierStuck
                    && samplesSeen - burstStartSample >= stuckSamples
                    && receiver.Demodulators.Sum(d => d.PreambleCount) == preamblesAtCarrierStart)
                {
                    instance.CarrierStuck = true;
                    logger.LogWarning(
                        "{Radio} carrier detect held {Seconds} s with no preamble — treating it as stuck "
                        + "(check squelch and audio level); CSMA will ignore it until it clears.",
                        instance.Radio.FullCallsign, stuckSamples / SampleRate);
                }
            }
            else if (wasCarrier)
            {
                instance.CarrierStuck = false;
                var decoded = receiver.ValidFrameCount - validAtCarrierStart;
                var failed = receiver.Demodulators.Sum(d => d.InvalidFrameCount)
                    - invalidAtCarrierStart;
                var preambles = receiver.Demodulators.Sum(d => d.PreambleCount)
                    - preamblesAtCarrierStart;

                // Burst length includes the demodulator's carrier hold, so a
                // very short transmission still reports at least that long.
                // The longest flag run is a running high-water mark, not a
                // per-burst figure.  It is only meaningful for transmitters
                // that fill TXDelay with flags (32-45 of them); ones that fill
                // it with zeros send just one or two, and are recognised by
                // the zero-fill run instead.
                _channelActivity.LogInformation(
                    "{Radio} channel activity: {Ms} ms, peak {Peak:F3}, {Preambles} preamble(s), "
                    + "{Decoded} decoded, {Failed} CRC failure(s), longest flag run {FlagRun} "
                    + "— {Verdict}.",
                    instance.Radio.FullCallsign,
                    (samplesSeen - burstStartSample) * 1000 / SampleRate,
                    burstPeakLevel,
                    preambles,
                    decoded,
                    failed,
                    receiver.Demodulators.Max(d => d.LongestFlagRun),
                    BurstVerdict(decoded, failed, preambles));

                // Captured on evidence a transmission actually happened — a
                // TXDelay preamble — rather than on audio level.  Level cannot
                // tell signal from noise on an open squelch, where the two sit
                // at similar levels, and carrier detect alone arms on noise.
                if (decoded == 0 && preambles > 0)
                    captureService.CaptureMissedDecode(
                        radioId, instance.Radio.FullCallsign, SampleRate);
            }
            wasCarrier = carrierNow;

            // Demodulation always runs; the monitor stream only costs anything
            // while somebody is actually listening.  Receive audio also stands
            // down while the radio is keyed: this is a half-duplex radio, so
            // there is nothing real to hear, and TX monitoring publishes to the
            // same listener — interleaving both would land two unrelated audio
            // sources in one playback cursor.
            var publishing = audioBroker.HasListeners(radioId) && !instance.Transmitting;
            if (publishing)
            {
                // A newly arrived listener — or one resuming after a keyup —
                // starts from clean filter history rather than stale samples.
                if (!wasPublishing)
                    monitor.Reset();

                var carrier = receiver.CarrierDetected;
                monitor.Process(samples, frame => audioBroker.PublishReceive(radioId, frame, carrier));
            }

            wasPublishing = publishing;
        }

        ct.ThrowIfCancellationRequested();
    }

    // ── Transmit ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Collects the frames for one keyup with no digipeat lane and no airtime
    /// budget — see the full overload.
    /// </summary>
    internal static List<TxItem> DrainForKeyup(
        ChannelReader<TxItem> priority, ChannelReader<TxItem> normal, int maxNormal) =>
        DrainForKeyup(priority, digipeat: null, normal, maxNormal, long.MaxValue);

    /// <summary>
    /// Collects the frames for one keyup, in order: the priority (session)
    /// lane, then up to <see cref="MaxDigipeatsPerKeyup"/> digipeat, then the
    /// normal lane up to <paramref name="maxNormal"/> frames — all within
    /// <paramref name="bitBudget"/> stuffed bits of airtime.  Whatever does not
    /// fit stays queued, in order, for the next CSMA cycle.  The first frame is
    /// always taken so an oversized frame can never wedge the queue.
    /// </summary>
    internal static List<TxItem> DrainForKeyup(
        ChannelReader<TxItem> priority,
        ChannelReader<TxItem>? digipeat,
        ChannelReader<TxItem> normal,
        int maxNormal,
        long bitBudget)
    {
        var items = new List<TxItem>();
        var used = 0L;

        bool TakeIfFits(ChannelReader<TxItem> lane)
        {
            if (!lane.TryPeek(out var next))
                return false;
            var bits = EstimateKeyupBits(next.Frame);
            if (items.Count > 0 && used + bits > bitBudget)
                return false;
            if (!lane.TryRead(out var taken))
                return false;
            items.Add(taken);
            used += bits;
            return true;
        }

        while (TakeIfFits(priority)) { }
        if (digipeat is not null)
            for (var i = 0; i < MaxDigipeatsPerKeyup && TakeIfFits(digipeat); i++) { }
        for (var i = 0; i < maxNormal && TakeIfFits(normal); i++) { }
        return items;
    }

    /// <summary>Stuffed bits one frame adds to a keyup, inter-frame flags included.</summary>
    internal static long EstimateKeyupBits(byte[] frame) =>
        AfskModulator.BuildHdlcBitStream(frame, leadFlags: 0, tailFlags: 0).Count + 2 * 8;

    /// <summary>Outcome of one CSMA wait.</summary>
    internal enum CsmaResult
    {
        Unknown = 0,
        Clear = 1,
        TimedOut = 2,
    }

    /// <summary>
    /// p-persistence CSMA: wait for <paramref name="busy"/> to clear, then
    /// transmit with probability (P+1)/256 per slot, re-checking carrier each
    /// slot.  Gives up once the channel has stayed busy past
    /// <paramref name="maxWait"/> — persistence slots on a clear channel never
    /// time out, only a channel that will not clear.
    /// </summary>
    internal static async Task<CsmaResult> WaitForClearChannelAsync(
        Func<bool> busy,
        int persistence,
        int slotMs,
        TimeSpan maxWait,
        Random rng,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var start = timeProvider.GetTimestamp();
        var poll = TimeSpan.FromMilliseconds(10);
        var slot = TimeSpan.FromMilliseconds(Math.Max(1, slotMs));

        while (true)
        {
            while (busy())
            {
                if (timeProvider.GetElapsedTime(start) >= maxWait)
                    return CsmaResult.TimedOut;
                await Task.Delay(poll, timeProvider, ct);
            }

            if (rng.Next(256) <= persistence)
                return CsmaResult.Clear;
            await Task.Delay(slot, timeProvider, ct);
        }
    }

    private async Task TransmitLoopAsync(
        ModemInstance instance,
        AfskReceiver receiver,
        RfIngestDispatcher ingest,
        IPttController? ptt,
        Channel<TxItem> txChannel,
        Channel<TxItem> priorityTxChannel,
        Channel<TxItem> digipeatTxChannel,
        Channel<ToneRequest> toneChannel,
        CancellationToken ct)
    {
        var radio = instance.Radio;
        var playbackDevice = string.IsNullOrWhiteSpace(radio.ModemPlaybackDevice)
            ? "default"
            : radio.ModemPlaybackDevice;
        var modulator = new AfskModulator(SampleRate) { SpaceBoostDb = radio.TxSpaceBoostDb };
        var leadFlags = modulator.FlagsForMilliseconds(radio.TxDelayMs);
        var tailFlags = modulator.FlagsForMilliseconds(radio.TxTailMs);
        var slotTimeMs = Math.Max(10, radio.TxSlotTimeMs);
        var persistence = Math.Clamp(radio.TxPersistence, 0, 255);
        var csmaMaxWait = TimeSpan.FromSeconds(Math.Max(1, modemOptions.Value.CsmaMaxWaitSeconds));

        // Validate the playback device up front so a bad name shows as a TX
        // error at once, rather than on the first keyup.  Never fatal: RX runs.
        try
        {
            PlaybackSinkFactory(playbackDevice, SampleRate).Dispose();
        }
        catch (Exception ex)
        {
            instance.TxError = $"playback device could not be opened: {ex.Message}";
            logger.LogWarning(ex, "{Radio} playback device \"{Device}\" is not usable; TX will fail until it is.",
                radio.FullCallsign, playbackDevice);
        }
        var csmaTimeoutAction = modemOptions.Value.CsmaTimeoutAction;

        // Airtime per keyup: the budget less the preamble and tail it always carries.
        var bitBudget = Math.Max(1L, (long)(KeyupBudgetSeconds * 1200) - (leadFlags + tailFlags) * 8L);

        // Pending wake-ups survive across iterations: creating fresh
        // WaitToReadAsync tasks every pass left the losers registered on their
        // channels for good — a slow leak of ~0.5 KB per keyup.
        Task<bool>? frameReady = null, priorityReady = null, digipeatReady = null, toneReady = null;

        while (!ct.IsCancellationRequested)
        {
            // Wake on a queued frame (any lane) or a test-tone request.
            frameReady ??= txChannel.Reader.WaitToReadAsync(ct).AsTask();
            priorityReady ??= priorityTxChannel.Reader.WaitToReadAsync(ct).AsTask();
            digipeatReady ??= digipeatTxChannel.Reader.WaitToReadAsync(ct).AsTask();
            toneReady ??= toneChannel.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(frameReady, priorityReady, digipeatReady, toneReady);
            if (frameReady.IsCompleted) frameReady = null;
            if (priorityReady.IsCompleted) priorityReady = null;
            if (digipeatReady.IsCompleted) digipeatReady = null;
            if (toneReady.IsCompleted) toneReady = null;
            ct.ThrowIfCancellationRequested();

            // A test tone is a deliberate operator action for calibration —
            // transmit it immediately, bypassing CSMA.
            if (toneChannel.Reader.TryRead(out var tone))
            {
                await SendTestToneAsync(instance, ptt, playbackDevice, modulator, tone, ct);
                continue;
            }

            Func<bool> busy = () => receiver.CarrierDetected && !instance.CarrierStuck;
            if (busy())
            {
                Interlocked.Exchange(ref instance.CsmaWaitingSinceTicks, DateTime.UtcNow.Ticks);
                txFrames.WaitingForChannel(radio.Id);
            }
            CsmaResult csma;
            try
            {
                csma = await WaitForClearChannelAsync(
                    busy, persistence, slotTimeMs, csmaMaxWait, Random.Shared, TimeProvider.System, ct);
            }
            finally
            {
                Interlocked.Exchange(ref instance.CsmaWaitingSinceTicks, 0);
            }

            // One keyup: session lane first, then a bounded slice of the
            // normal lane (including frames that arrived during CSMA).
            var items = DrainForKeyup(
                priorityTxChannel.Reader, digipeatTxChannel.Reader, txChannel.Reader,
                MaxNormalFramesPerKeyup, bitBudget);
            if (items.Count == 0)
                continue;

            if (csma == CsmaResult.TimedOut)
            {
                Interlocked.Increment(ref instance.CsmaTimeouts);
                if (csmaTimeoutAction != CsmaTimeoutAction.TransmitAnyway)
                {
                    // Dire Wolf: "Waited too long for clear channel."  The
                    // frames count as never sent, so LAPB T1 still runs.
                    logger.LogWarning(
                        "{Radio} waited {Seconds} s for a clear channel; discarding {Count} frame(s).",
                        radio.FullCallsign, csmaMaxWait.TotalSeconds, items.Count);
                    var why = CsmaDiscardReason(csmaMaxWait);
                    foreach (var item in items)
                    {
                        item.Completion?.TrySetCanceled();
                        txFrames.Report(item.FrameId, TxFrameState.Failed, why);
                    }
                    continue;
                }

                logger.LogWarning(
                    "{Radio} waited {Seconds} s for a clear channel; transmitting anyway.",
                    radio.FullCallsign, csmaMaxWait.TotalSeconds);
            }

            var frames = items.Select(i => i.Frame).ToList();
            var amplitude = instance.TxAudioLevelPct / 100f;
            var audio = modulator.GenerateTransmission(frames, leadFlags, tailFlags, amplitude);

            var result = RunKeyup(instance, ptt, playbackDevice, audio, onKeyed: () =>
            {
                foreach (var item in items)
                    txFrames.Report(item.FrameId, TxFrameState.OnAir);
            });
            var transmitted = result.Outcome == KeyupOutcome.Transmitted;
            var failure = transmitted ? null : KeyupFailureReason(result);

            // Airtime completions fire only after the audio fully drained and
            // PTT dropped — this is where LAPB starts T1. A failed keyup cancels
            // instead so the frames count as never sent.
            foreach (var item in items)
            {
                if (transmitted)
                    item.Completion?.TrySetResult(true);
                else
                    item.Completion?.TrySetCanceled();
                txFrames.Report(
                    item.FrameId, transmitted ? TxFrameState.Sent : TxFrameState.Failed, failure);
            }

            if (!transmitted)
            {
                await HandleFailedKeyupAsync(instance, result, items.Count, ct);
                continue;
            }

            Interlocked.Add(ref instance.TransmittedFrames, frames.Count);
            Interlocked.Exchange(ref instance.LastTxAtTicks, DateTime.UtcNow.Ticks);
            logger.LogInformation(
                "{Radio} transmitted {Count} frame(s), {Ms} ms of audio.",
                radio.FullCallsign, frames.Count, audio.Length * 1000 / SampleRate);

            // Loop our own transmissions back into the ingest pipeline — the
            // equivalent of the Direwolf KISS echo that OwnBeacon matching and
            // packet history rely on (the demodulator cannot hear our own TX).
            foreach (var frame in frames)
                ingest.Post(new RfIngestItem(frame, radio.ChannelNumber, Signal: null, IsOwnTransmission: true));

            if (ptt is RigctldPtt rig)
                Interlocked.Exchange(ref instance.RigFrequencyHz, rig.TryGetFrequencyHz() ?? -1);
        }
    }

    /// <summary>How long TX stands down after a failed keyup; RX keeps running.</summary>
    private static readonly TimeSpan TxFaultBackoff = TimeSpan.FromSeconds(RetryDelaySeconds);

    /// <summary>
    /// Opens the playback device, then keys, plays and unkeys under the PTT
    /// watchdog — see <see cref="KeyupRunner"/>.
    /// </summary>
    private KeyupResult RunKeyup(
        ModemInstance instance, IPttController? ptt, string playbackDevice, float[] audio, Action? onKeyed = null)
    {
        var radio = instance.Radio;
        var limit = KeyupRunner.WatchdogLimit(TimeSpan.FromSeconds(audio.Length / (double)SampleRate));
        try
        {
            var result = KeyupRunner.Run(
                ptt,
                () => PlaybackSinkFactory(playbackDevice, SampleRate),
                audio,
                limit,
                logger,
                radio.FullCallsign,
                onKeyed: () =>
                {
                    instance.Transmitting = true;
                    onKeyed?.Invoke();
                    PublishTransmitAudio(radio.Id, audio);
                });
            if (result.WatchdogTripped)
                Interlocked.Increment(ref instance.PttWatchdogTrips);
            if (result.Outcome == KeyupOutcome.Transmitted)
            {
                instance.TxError = null;
                instance.PttStuck = false;
            }
            return result;
        }
        finally
        {
            instance.Transmitting = false;
        }
    }

    /// <summary>
    /// A keyup that never made it to air is a transmit-side problem only:
    /// report it, stand TX down for a while and keep receiving.  The one
    /// exception is PTT that would not release — that faults the session so
    /// the PTT controller is disposed (and its own unkey attempted) at once.
    /// </summary>
    private async Task HandleFailedKeyupAsync(ModemInstance instance, KeyupResult result, int frameCount, CancellationToken ct)
    {
        var radio = instance.Radio;
        var reason = KeyupOutcomeText(result.Outcome);
        instance.TxError = $"{reason}: {result.Error?.Message}";

        if (result.Outcome == KeyupOutcome.UnkeyFailed)
        {
            instance.PttStuck = true;
            throw new InvalidOperationException(
                $"PTT for {radio.FullCallsign} could not be released — tearing the modem session down.",
                result.Error);
        }

        logger.LogError(
            result.Error,
            "{Radio} TX failed ({Reason}); {Count} frame(s) not sent. Receive continues; TX retries in {Delay} s.",
            radio.FullCallsign, reason, frameCount, TxFaultBackoff.TotalSeconds);
        await Task.Delay(TxFaultBackoff, ct);
    }

    internal static string KeyupOutcomeText(KeyupOutcome outcome) => outcome switch
    {
        KeyupOutcome.OpenFailed => "playback device could not be opened",
        KeyupOutcome.KeyFailed => "PTT could not be keyed",
        KeyupOutcome.PlaybackFailed => "playback failed",
        KeyupOutcome.UnkeyFailed => "PTT could not be released",
        _ => "keyup failed",
    };

    /// <summary>Operator-facing reason a keyup's frames never made it to air.</summary>
    internal static string KeyupFailureReason(KeyupResult result)
    {
        var text = KeyupOutcomeText(result.Outcome);
        var why = char.ToUpperInvariant(text[0]) + text[1..];
        if (result.Error?.Message is { Length: > 0 } msg)
            why += $" ({msg})";
        return result.Outcome == KeyupOutcome.UnkeyFailed
            ? why + ". The radio may still be transmitting."
            : why + ". Frame not sent.";
    }

    /// <summary>Reason shown for frames discarded by the CSMA cap.</summary>
    internal static string CsmaDiscardReason(TimeSpan maxWait) =>
        $"Waited {maxWait.TotalSeconds:0} s for a clear channel; carrier detect never released. Frame discarded.";

    /// <summary>Creates the playback sink for a keyup; replaceable for tests.</summary>
    internal Func<string, int, IPlaybackSink> PlaybackSinkFactory { get; set; } =
        (device, rate) => new AlsaPlaybackDevice(device, rate);

    /// <summary>
    /// Mirrors a rendered transmission into the audio monitor so an operator
    /// can hear their own keyups.  The burst is already fully rendered, so it
    /// is published in one go and reaches the client faster than real time —
    /// client-side buffering is what plays it back at the correct rate.
    /// </summary>
    /// <summary>
    /// Plain-language reading of one carrier burst.  The counters alone cannot
    /// distinguish the three failure modes that matter — too weak to be real, a
    /// frame that arrived corrupt, and a preamble that never produced a frame —
    /// and those point at different fixes.
    /// </summary>
    internal static string BurstVerdict(long decoded, long failed, long preambles)
    {
        // Judged on preambles rather than level: with the squelch open, channel
        // noise is as loud as a signal, so level says nothing about whether a
        // transmission occurred.  A run of opening flags does.
        if (preambles == 0)
            return "no preamble — carrier detect tripped on noise, not a transmission";
        if (decoded > 0)
            return failed == 0 ? "decoded cleanly" : "partially decoded";
        if (failed > 0)
            return "a frame arrived but failed CRC — audio quality or level";
        return "preamble arrived but no frame formed — cut short, or too damaged to assemble";
    }

    private void PublishTransmitAudio(string radioId, float[] audio)
    {
        if (!audioBroker.HasTxListeners(radioId))
            return;

        // Transmit audio is rendered at the radio's TX drive level, which exists
        // to set deviation — not loudness — so it arrives far hotter than
        // receive audio off the data jack.  Normalising to a fixed monitor peak
        // keeps the two at a comparable volume, and means changing TX drive no
        // longer changes how loud the monitor is.
        var peak = 0f;
        foreach (var sample in audio)
            peak = MathF.Max(peak, MathF.Abs(sample));
        if (peak <= 0f)
            return;

        // A fresh resampler per burst: transmissions are discontinuous, so
        // carrying filter history between them would be meaningless.
        var monitor = new AudioMonitorResampler(SampleRate);
        monitor.Process(
            audio, frame => audioBroker.PublishTransmit(radioId, frame), TxMonitorPeak / peak);
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
        var audio = tone.Kind == TestToneKind.PttOnly
            ? new float[SampleRate * tone.DurationMs / 1000]
            : modulator.GenerateTestTone(frequencies, tone.DurationMs, segmentMs, amplitude);
        if (audio.Length == 0)
        {
            txFrames.Report(tone.FrameId, TxFrameState.Failed, "Empty tone.");
            return;
        }

        var result = RunKeyup(instance, ptt, playbackDevice, audio,
            onKeyed: () => txFrames.Report(tone.FrameId, TxFrameState.OnAir));
        txFrames.Report(
            tone.FrameId,
            result.Outcome == KeyupOutcome.Transmitted ? TxFrameState.Sent : TxFrameState.Failed,
            result.Outcome == KeyupOutcome.Transmitted ? null : KeyupFailureReason(result));

        // Cancellation requested mid-tone should still surface to the loop.
        ct.ThrowIfCancellationRequested();

        if (result.Outcome != KeyupOutcome.Transmitted)
        {
            await HandleFailedKeyupAsync(instance, result, frameCount: 0, ct);
            return;
        }

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

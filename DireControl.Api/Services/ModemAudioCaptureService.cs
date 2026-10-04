using System.Collections.Concurrent;
using System.Threading.Channels;
using DireControl.Api.Logging;
using DireControl.Data;
using DireControl.Modem.Audio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>One captured audio file.</summary>
public sealed record AudioCaptureInfo(
    string Name, long SizeBytes, DateTime CapturedAtUtc, double DurationSeconds, string Reason);

/// <summary>
/// Records raw, full-rate modem audio so transmissions can be replayed through
/// the demodulator offline.
///
/// Captures are deliberately not taken from the browser monitor stream: that is
/// downsampled to 8 kHz and is useless for validating a 1200 baud demodulator.
/// This keeps a rolling in-memory buffer of the original 48 kHz samples per
/// radio, because a failed decode is only recognisable once the transmission is
/// already over.
///
/// Every file write happens on this service's own loop.  Nothing on the audio
/// capture thread is allowed to touch the disk — a few megabytes of synchronous
/// IO there would stall the DSP and cause the very decode failures we are
/// trying to study.
/// </summary>
public sealed class ModemAudioCaptureService(
    IOptionsMonitor<AudioCaptureOptions> options,
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory) : BackgroundService
{
    // Read on the audio thread for every block, written from a request thread
    // when the operator flips the switch.
    private volatile bool _captureEnabled = true;

    /// <summary>
    /// Whether a heard-but-not-decoded transmission is captured automatically.
    /// Persisted in <c>UserSetting.AudioCaptureEnabled</c>; the audio thread
    /// reads this cached copy rather than touching the database.
    /// </summary>
    public bool CaptureEnabled => _captureEnabled;

    /// <summary>
    /// Capture logs under its own category so the "captured N seconds" lines
    /// can be silenced independently, while failures still surface at Warning.
    /// </summary>
    private readonly ILogger _logger = loggerFactory.CreateLogger(LogCategories.AudioCapture);

    /// <summary>
    /// Queued capture.  Carries a count rather than a trimmed array so handing
    /// off a partly-filled manual buffer costs nothing — trimming a 120-second
    /// buffer would mean a ~23 MB copy on the audio thread.
    /// </summary>
    private sealed record PendingWrite(
        string Name, float[] Samples, int Count, int SampleRate, string Reason);

    /// <summary>Per-radio buffers.  Only the radio's capture thread appends.</summary>
    private sealed class RadioBuffers
    {
        public float[] Ring = [];
        public int RingWritePos;
        public bool RingWrapped;

        /// <summary>Non-null while a manual recording is in progress.</summary>
        public float[]? Manual;
        public int ManualCount;

        public long LastCaptureTicks;

        /// <summary>
        /// Guards the manual buffer only.  Start/stop arrive on request threads
        /// while the capture thread is appending; contention is negligible
        /// because appends happen about 47 times a second.
        /// </summary>
        public readonly object Gate = new();
    }

    /// <summary>
    /// Read through the monitor on every use rather than snapshotted, so
    /// editing the capture settings takes effect without a restart.
    /// </summary>
    private AudioCaptureOptions Options => options.CurrentValue;

    private readonly ConcurrentDictionary<string, RadioBuffers> _buffers = new();

    private readonly Channel<PendingWrite> _writes = Channel.CreateBounded<PendingWrite>(
        new BoundedChannelOptions(8)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private string Directory => Path.Combine(environment.ContentRootPath, Options.Directory);

    /// <summary>
    /// Loads the persisted capture switch and applies it.  Call once at
    /// startup, before the modem begins producing audio.
    /// </summary>
    public async Task ApplyFromDatabaseAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var settings = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        _captureEnabled = settings?.AudioCaptureEnabled ?? true;
    }

    /// <summary>Persists and immediately applies the capture switch.</summary>
    public async Task SetCaptureEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var settings = await db.UserSettings.FirstOrDefaultAsync(ct);
        if (settings is not null)
        {
            settings.AudioCaptureEnabled = enabled;
            await db.SaveChangesAsync(ct);
        }

        // The audio thread owns the rolling buffers and releases them itself on
        // its next block — clearing them from here would race with an in-flight
        // append.
        _captureEnabled = enabled;
    }

    /// <summary>True while a manual recording is running for this radio.</summary>
    public bool IsRecording(string radioId) =>
        _buffers.TryGetValue(radioId, out var state) && Volatile.Read(ref state.Manual) is not null;

    /// <summary>
    /// Appends freshly captured audio.  Called on the radio's capture thread for
    /// every block, so it stays allocation-free in the steady state.
    /// </summary>
    public void Append(string radioId, ReadOnlySpan<float> samples, int sampleRate)
    {
        var state = _buffers.GetOrAdd(radioId, _ => new RadioBuffers());

        // The ring exists only to serve automatic capture; manual recording has
        // its own buffer, so turning automatic capture off stops paying for it.
        // Sized here rather than allocated once, so both the operator switch
        // and the pre-buffer length take effect without a restart.
        var wanted = _captureEnabled ? Math.Max(1, Options.PreBufferSeconds) * sampleRate : 0;
        if (state.Ring.Length != wanted)
        {
            state.Ring = wanted == 0 ? [] : new float[wanted];
            state.RingWritePos = 0;
            state.RingWrapped = false;
        }

        if (_captureEnabled)
            AppendToRing(state, samples);

        if (Volatile.Read(ref state.Manual) is not null)
            AppendToManual(state, samples, sampleRate);
    }

    private static void AppendToRing(RadioBuffers state, ReadOnlySpan<float> samples)
    {
        var ring = state.Ring;

        // A block longer than the ring can only contribute its tail.
        if (samples.Length >= ring.Length)
        {
            samples[^ring.Length..].CopyTo(ring);
            state.RingWritePos = 0;
            state.RingWrapped = true;
            return;
        }

        var firstChunk = Math.Min(samples.Length, ring.Length - state.RingWritePos);
        samples[..firstChunk].CopyTo(ring.AsSpan(state.RingWritePos));

        var remaining = samples.Length - firstChunk;
        if (remaining > 0)
        {
            samples[firstChunk..].CopyTo(ring);
            state.RingWrapped = true;
        }

        state.RingWritePos += samples.Length;
        if (state.RingWritePos >= ring.Length)
        {
            state.RingWritePos -= ring.Length;
            state.RingWrapped = true;
        }
    }

    private void AppendToManual(RadioBuffers state, ReadOnlySpan<float> samples, int sampleRate)
    {
        lock (state.Gate)
        {
            var manual = state.Manual;
            if (manual is null)
                return;

            var room = manual.Length - state.ManualCount;
            var take = Math.Min(room, samples.Length);
            if (take > 0)
            {
                samples[..take].CopyTo(manual.AsSpan(state.ManualCount));
                state.ManualCount += take;
            }

            // Hit the cap — finalise here so a forgotten recording cannot grow
            // without bound, and so the operator still gets the audio.
            if (state.ManualCount >= manual.Length)
                FinishManualLocked(state, sampleRate, "manual-capped");
        }
    }

    /// <summary>
    /// Saves the rolling buffer because a transmission was heard but did not
    /// decode.  Rate-limited, and the snapshot copy is the only allocation.
    /// </summary>
    public void CaptureMissedDecode(string radioId, string label, int sampleRate)
    {
        if (!_captureEnabled)
            return;

        var state = _buffers.GetOrAdd(radioId, _ => new RadioBuffers());
        var now = DateTime.UtcNow.Ticks;
        var minGap = TimeSpan.FromSeconds(Math.Max(0, Options.MinCaptureIntervalSeconds)).Ticks;
        if (now - Volatile.Read(ref state.LastCaptureTicks) < minGap)
            return;
        Volatile.Write(ref state.LastCaptureTicks, now);

        var snapshot = SnapshotRing(state);
        if (snapshot.Length == 0)
            return;

        Enqueue(FileName(label, "missed"), snapshot, snapshot.Length, sampleRate, "missed-decode");
    }

    /// <summary>Starts a manual recording. False if one is already running.</summary>
    public bool StartRecording(string radioId, int sampleRate)
    {
        var state = _buffers.GetOrAdd(radioId, _ => new RadioBuffers());
        lock (state.Gate)
        {
            if (state.Manual is not null)
                return false;

            state.ManualCount = 0;
            // Pre-allocated to the cap so appends never trigger a resize on the
            // capture thread.
            Volatile.Write(
                ref state.Manual, new float[Math.Max(1, Options.MaxManualSeconds) * sampleRate]);
            return true;
        }
    }

    /// <summary>Stops a manual recording and queues it for writing.</summary>
    public bool StopRecording(string radioId, string label, int sampleRate)
    {
        if (!_buffers.TryGetValue(radioId, out var state))
            return false;

        lock (state.Gate)
        {
            if (state.Manual is null)
                return false;
            FinishManualLocked(state, sampleRate, "manual", label);
            return true;
        }
    }

    private void FinishManualLocked(
        RadioBuffers state, int sampleRate, string reason, string label = "manual")
    {
        var manual = state.Manual;
        if (manual is null)
            return;

        var count = state.ManualCount;
        Volatile.Write(ref state.Manual, null);
        state.ManualCount = 0;

        if (count > 0)
            Enqueue(FileName(label, "rec"), manual, count, sampleRate, reason);
    }

    private static float[] SnapshotRing(RadioBuffers state)
    {
        var ring = state.Ring;
        if (ring.Length == 0)
            return [];

        if (!state.RingWrapped)
            return ring[..state.RingWritePos];

        // Unwrap: oldest samples sit after the write cursor.
        var snapshot = new float[ring.Length];
        var tail = ring.Length - state.RingWritePos;
        ring.AsSpan(state.RingWritePos).CopyTo(snapshot);
        ring.AsSpan(0, state.RingWritePos).CopyTo(snapshot.AsSpan(tail));
        return snapshot;
    }

    private void Enqueue(string name, float[] samples, int count, int sampleRate, string reason)
    {
        if (!_writes.Writer.TryWrite(new PendingWrite(name, samples, count, sampleRate, reason)))
            _logger.LogWarning("Audio capture \"{Name}\" dropped — write queue is full.", name);
    }

    private static string FileName(string label, string kind)
    {
        var safeLabel = string.Concat(
            label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        return $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeLabel}-{kind}.wav";
    }

    /// <summary>Captures currently on disk, newest first.</summary>
    public IReadOnlyList<AudioCaptureInfo> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return [];

            // Ordered and timestamped by last-write rather than creation time:
            // creation time is not reliably available on every Linux
            // filesystem, and a capture is written exactly once anyway.
            return [.. new DirectoryInfo(Directory)
                .GetFiles("*.wav")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new AudioCaptureInfo(
                    f.Name,
                    f.Length,
                    f.LastWriteTimeUtc,
                    DurationOf(f),
                    ReasonFromName(f.Name)))];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not list audio captures.");
            return [];
        }
    }

    /// <summary>
    /// Duration from the file's own header, so this never has to assume a
    /// sample rate it does not own.  Unreadable files report zero rather than
    /// failing the whole listing.
    /// </summary>
    private static double DurationOf(FileInfo file)
    {
        try
        {
            return WaveFile.ReadInfo(file.FullName).Duration.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReasonFromName(string name) =>
        name.Contains("-missed.wav", StringComparison.Ordinal) ? "missed-decode" : "manual";

    /// <summary>Full path of a capture, or null if the name is not a capture we hold.</summary>
    public string? ResolvePath(string name)
    {
        // Never let a caller-supplied name escape the capture directory.
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
            return null;
        if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        var path = ResolvePath(name);
        if (path is null)
            return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not delete audio capture \"{Name}\".", name);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var write in _writes.Reader.ReadAllAsync(ct))
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, write.Name);
                WaveFile.Write(path, write.Samples.AsSpan(0, write.Count), write.SampleRate);

                _logger.LogInformation(
                    "Captured {Seconds:F1}s of audio to \"{Name}\" ({Reason}).",
                    (double)write.Count / write.SampleRate, write.Name, write.Reason);

                Prune();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write audio capture \"{Name}\".", write.Name);
            }
        }
    }

    private void Prune()
    {
        var max = Math.Max(1, Options.MaxFiles);
        var stale = new DirectoryInfo(Directory)
            .GetFiles("*.wav")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(max);

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
                _logger.LogDebug("Pruned old audio capture \"{Name}\".", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not prune audio capture \"{Name}\".", file.Name);
            }
        }
    }
}

using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;

namespace DireControl.Api.Services.Terminal;

/// <summary>
/// Persists terminal session transcripts: one <see cref="TerminalSessionRecord"/>
/// per session, with the byte stream batched into
/// <see cref="TerminalTranscriptChunk"/> rows.  Appends buffer in memory and
/// flush on a timer (or at 16 KB), coalescing same-direction runs so a typing
/// session does not become thousands of one-byte rows.
/// </summary>
public sealed class TerminalTranscriptRecorder(
    IServiceScopeFactory scopeFactory,
    ILogger<TerminalTranscriptRecorder> logger) : IDisposable
{
    private const int FlushThresholdBytes = 16 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private sealed class PendingChunk
    {
        public DateTime Timestamp { get; init; }
        public TranscriptDirection Direction { get; init; }
        public MemoryStream Data { get; } = new();
    }

    private sealed class SessionBuffer
    {
        public int RecordId { get; init; }
        public List<PendingChunk> Pending { get; } = [];
        public long BytesIn;
        public long BytesOut;
    }

    private readonly Dictionary<string, SessionBuffer> _buffers = [];
    private readonly Lock _lock = new();
    private Timer? _timer;

    private Timer EnsureTimer() =>
        _timer ??= new Timer(_ => FlushAllSafe(), null, FlushInterval, FlushInterval);

    /// <summary>Creates the transcript header row and starts buffering for the session.</summary>
    public async Task StartAsync(
        string sessionId,
        TerminalSessionOrigin origin,
        int channel,
        string localCallsign,
        string remoteCallsign,
        string digiPath,
        CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var record = new TerminalSessionRecord
        {
            SessionId = sessionId,
            Origin = origin,
            Channel = channel,
            LocalCallsign = localCallsign,
            RemoteCallsign = remoteCallsign,
            DigiPath = digiPath,
            StartedAt = DateTime.UtcNow,
        };
        db.TerminalSessionRecords.Add(record);
        await db.SaveChangesAsync(ct);

        lock (_lock)
        {
            _buffers[sessionId] = new SessionBuffer { RecordId = record.Id };
            EnsureTimer();
        }
    }

    /// <summary>Buffers session bytes; same-direction runs coalesce into one chunk.</summary>
    public void Append(string sessionId, TranscriptDirection direction, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        var flushNeeded = false;
        lock (_lock)
        {
            if (!_buffers.TryGetValue(sessionId, out var buffer))
                return;

            if (direction == TranscriptDirection.Received)
                buffer.BytesIn += data.Length;
            else
                buffer.BytesOut += data.Length;

            var last = buffer.Pending.Count > 0 ? buffer.Pending[^1] : null;
            if (last is null || last.Direction != direction || last.Data.Length >= FlushThresholdBytes)
            {
                last = new PendingChunk { Timestamp = DateTime.UtcNow, Direction = direction };
                buffer.Pending.Add(last);
            }
            last.Data.Write(data);

            flushNeeded = buffer.Pending.Sum(p => p.Data.Length) >= FlushThresholdBytes;
        }

        if (flushNeeded)
            _ = FlushAsync();
    }

    /// <summary>Flushes every buffered chunk to the database.</summary>
    public async Task FlushAsync()
    {
        List<(int RecordId, PendingChunk Chunk)> toWrite;
        lock (_lock)
        {
            toWrite = _buffers.Values
                .SelectMany(b => b.Pending.Select(p => (b.RecordId, p)))
                .ToList();
            foreach (var buffer in _buffers.Values)
                buffer.Pending.Clear();
        }

        if (toWrite.Count == 0)
            return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        foreach (var (recordId, chunk) in toWrite)
        {
            db.TerminalTranscriptChunks.Add(new TerminalTranscriptChunk
            {
                TerminalSessionRecordId = recordId,
                Timestamp = chunk.Timestamp,
                Direction = chunk.Direction,
                Data = chunk.Data.ToArray(),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Flushes remaining bytes and finalises the header row.</summary>
    public async Task FinalizeAsync(string sessionId, string? endReason)
    {
        SessionBuffer? buffer;
        lock (_lock)
        {
            if (_buffers.Remove(sessionId, out buffer) is false)
                return;
        }

        // Write any bytes still pending for this session.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        foreach (var chunk in buffer!.Pending)
        {
            db.TerminalTranscriptChunks.Add(new TerminalTranscriptChunk
            {
                TerminalSessionRecordId = buffer.RecordId,
                Timestamp = chunk.Timestamp,
                Direction = chunk.Direction,
                Data = chunk.Data.ToArray(),
            });
        }

        var record = await db.TerminalSessionRecords.FindAsync(buffer.RecordId);
        if (record is not null)
        {
            record.EndedAt = DateTime.UtcNow;
            record.EndReason = endReason;
            record.BytesIn = buffer.BytesIn;
            record.BytesOut = buffer.BytesOut;
        }
        await db.SaveChangesAsync();
    }

    private void FlushAllSafe()
    {
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcript flush failed.");
        }
    }

    public void Dispose() => _timer?.Dispose();
}

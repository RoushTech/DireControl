using DireControl.Api.Controllers.Models;
using DireControl.Enums;
using DireControl.Modem.Ax25;

namespace DireControl.Api.Services;

/// <summary>What a producer knows about a frame it is about to queue.</summary>
public sealed record TxFrameInfo(TxFrameKind Kind, string Title, string? Ref = null)
{
    /// <summary>Best guess for producers that only state a priority.</summary>
    public static TxFrameInfo ForPriority(TxPriority priority) => priority switch
    {
        TxPriority.Session => new(TxFrameKind.Session, "Session frame"),
        TxPriority.Digipeat => new(TxFrameKind.Digipeat, "Digipeat"),
        _ => new(TxFrameKind.Unknown, "Frame"),
    };
}

/// <summary>
/// The single record of every frame DireControl tries to transmit, from the
/// moment it is queued to the moment it is on air and sent — or failed or
/// dropped, with the reason. Every enqueue gets an id; the transmit path
/// reports each transition here, and <see cref="Changed"/> feeds the
/// <c>txFrameStateChanged</c> hub event. A bounded window of recent frames
/// backs <c>GET api/v0/tx/recent</c> so a reloaded page can resync.
/// </summary>
/// <remarks>
/// Reports arrive from the transmit thread; everything is under one lock and
/// <see cref="Changed"/> runs outside it.
/// </remarks>
public sealed class TxFrameRegistry(TimeProvider time)
{
    /// <summary>How many recent frames are kept for resync.</summary>
    public const int Capacity = 200;

    private readonly Lock _lock = new();
    private readonly Dictionary<long, TxFrameDto> _frames = [];
    private readonly Queue<long> _order = new();
    private long _seq;

    /// <summary>Raised after every state change, outside the lock.</summary>
    public event Action<TxFrameDto>? Changed;

    public static bool IsFinal(TxFrameState s) =>
        s is TxFrameState.Sent or TxFrameState.Failed or TxFrameState.Dropped;

    /// <summary>
    /// Allocates an id for a frame about to be queued. Nothing is published
    /// until <see cref="Queued"/> (or <see cref="Report"/>) — a frame no
    /// backend accepts is <see cref="Abandon"/>ed silently.
    /// </summary>
    public long Begin(TxFrameInfo info, byte[]? frame = null)
    {
        var id = Interlocked.Increment(ref _seq);
        var now = time.GetUtcNow().UtcDateTime;
        var dto = new TxFrameDto
        {
            Id = id,
            Kind = info.Kind,
            State = TxFrameState.Unknown,
            At = now,
            QueuedAt = now,
            Title = info.Title,
            Detail = frame is not null && Ax25Decoder.TryDecode(frame, out var f) ? Truncate(f.ToTnc2(), 160) : null,
            Ref = info.Ref,
        };
        lock (_lock)
        {
            _frames[id] = dto;
            _order.Enqueue(id);
            while (_order.Count > Capacity && _order.TryDequeue(out var old))
                _frames.Remove(old);
        }
        return id;
    }

    /// <summary>
    /// The frame was accepted by a transmitter for <paramref name="radioId"/>.
    /// The transmit thread may already have reported a later state (it can
    /// win the race with the enqueuing thread); then only the radio is filled in.
    /// </summary>
    public void Queued(long id, string? radioId, string? radioName) =>
        Update(id, d => d with
        {
            RadioId = radioId,
            RadioName = radioName,
            State = d.State == TxFrameState.Unknown ? TxFrameState.Queued : d.State,
        });

    /// <summary>No transmitter took the frame: forget it without publishing.</summary>
    public void Abandon(long id)
    {
        lock (_lock)
            _frames.Remove(id);
    }

    /// <summary>
    /// Records a transition. Ignored for id 0 (untracked), unknown ids, and
    /// frames already in a final state — a late report can never resurrect a
    /// failed frame or downgrade a sent one.
    /// </summary>
    public void Report(long id, TxFrameState state, string? reason = null) =>
        Update(id, d => d with
        {
            State = state,
            Reason = reason ?? (state is TxFrameState.Failed or TxFrameState.Dropped ? d.Reason : null),
        });

    /// <summary>Every frame still queued for <paramref name="radioId"/> is now waiting for a clear channel.</summary>
    public void WaitingForChannel(string radioId)
    {
        List<long> ids;
        lock (_lock)
            ids = _frames.Values
                .Where(d => d.RadioId == radioId && d.State == TxFrameState.Queued)
                .Select(d => d.Id)
                .ToList();
        foreach (var id in ids)
            Report(id, TxFrameState.WaitingForChannel);
    }

    /// <summary>The radio's modem stopped: nothing still pending for it will ever go to air.</summary>
    public void FailPending(string radioId, string reason)
    {
        List<long> ids;
        lock (_lock)
            ids = _frames.Values
                .Where(d => d.RadioId == radioId && !IsFinal(d.State) && d.State != TxFrameState.Unknown)
                .Select(d => d.Id)
                .ToList();
        foreach (var id in ids)
            Report(id, TxFrameState.Failed, reason);
    }

    public TxFrameDto? Get(long id)
    {
        lock (_lock)
            return _frames.GetValueOrDefault(id);
    }

    /// <summary>The newest published frame carrying <paramref name="reference"/> (e.g. a message number).</summary>
    public TxFrameDto? LatestByRef(TxFrameKind kind, string reference)
    {
        lock (_lock)
            return _frames.Values
                .Where(d => d.Kind == kind && d.Ref == reference && d.State != TxFrameState.Unknown)
                .MaxBy(d => d.Id);
    }

    /// <summary>Completes with the frame once it reaches a final state, or null on timeout.</summary>
    public async Task<TxFrameDto?> WaitForFinalAsync(long id, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<TxFrameDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(TxFrameDto d)
        {
            if (d.Id == id && IsFinal(d.State))
                tcs.TrySetResult(d);
        }
        Changed += OnChanged;
        try
        {
            if (Get(id) is { } now && IsFinal(now.State))
                return now;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            Changed -= OnChanged;
        }
    }

    /// <summary>Recent published frames, newest first.</summary>
    public IReadOnlyList<TxFrameDto> Recent(int max = 50)
    {
        lock (_lock)
            return _frames.Values
                .Where(d => d.State != TxFrameState.Unknown)
                .OrderByDescending(d => d.Id)
                .Take(Math.Clamp(max, 1, Capacity))
                .ToList();
    }

    private void Update(long id, Func<TxFrameDto, TxFrameDto> change)
    {
        if (id == 0)
            return;
        TxFrameDto updated;
        lock (_lock)
        {
            if (!_frames.TryGetValue(id, out var current) || IsFinal(current.State))
                return;
            var now = time.GetUtcNow().UtcDateTime;
            updated = change(current) with { At = now };
            if (updated.State == current.State && updated.Reason == current.Reason
                && updated.RadioId == current.RadioId)
                return;
            updated = updated.State switch
            {
                TxFrameState.WaitingForChannel => updated with { WaitingSince = current.WaitingSince ?? now },
                TxFrameState.OnAir => updated with { OnAirAt = now },
                TxFrameState.Sent => updated with { SentAt = now },
                _ => updated,
            };
            _frames[id] = updated;
        }
        Changed?.Invoke(updated);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

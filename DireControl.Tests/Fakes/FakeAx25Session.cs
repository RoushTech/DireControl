using System.Threading.Channels;
using DireControl.Api.Services.Ax25;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;

namespace DireControl.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IAx25Session"/> test double: tests feed bytes with
/// <see cref="Deliver"/>, read what the code under test sent from
/// <see cref="SentData"/>, and end the session with <see cref="Close"/>.
/// </summary>
internal sealed class FakeAx25Session : IAx25Session
{
    private readonly Channel<byte[]> _received =
        System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

    public FakeAx25Session(string local = "W3UWU", string remote = "KI4XYZ", bool isInbound = true)
    {
        Local = Ax25Address.Parse(local);
        Remote = Ax25Address.Parse(remote);
        IsInbound = isInbound;
    }

    public string Id { get; } = Guid.NewGuid().ToString("n");
    public Ax25Address Local { get; }
    public Ax25Address Remote { get; }
    public int Channel { get; init; }
    public IReadOnlyList<Ax25Address> Path { get; init; } = [];
    public bool IsInbound { get; }
    public LapbState State { get; private set; } = LapbState.Connected;
    public Ax25SessionStats Stats { get; set; } = new();
    public ChannelReader<byte[]> Received => _received.Reader;

    public List<byte[]> SentData { get; } = [];
    public bool DisconnectRequested { get; private set; }
    public bool AbortRequested { get; private set; }

    public event Action<IAx25Session, LapbState, LapbState>? StateChanged;
    public event Action<IAx25Session, LapbDisconnectReason>? Closed;

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        lock (SentData)
            SentData.Add(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        DisconnectRequested = true;
        Close(LapbDisconnectReason.LocalRequest);
        return Task.CompletedTask;
    }

    public void Abort()
    {
        AbortRequested = true;
        Close(LapbDisconnectReason.LocalRequest);
    }

    /// <summary>Simulates the remote station sending data.</summary>
    public void Deliver(byte[] data) => _received.Writer.TryWrite(data);

    /// <summary>Simulates the link closing.</summary>
    public void Close(LapbDisconnectReason reason)
    {
        if (State == LapbState.Disconnected)
            return;
        var previous = State;
        State = LapbState.Disconnected;
        _received.Writer.TryComplete();
        StateChanged?.Invoke(this, previous, LapbState.Disconnected);
        Closed?.Invoke(this, reason);
    }

    /// <summary>Concatenation of everything sent, for line-protocol assertions.</summary>
    public byte[] AllSent()
    {
        lock (SentData)
            return [.. SentData.SelectMany(d => d)];
    }
}

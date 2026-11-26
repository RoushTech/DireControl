using DireControl.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Hubs;

/// <summary>
/// Streams live modem audio to the browser.  Audio rides its own hub rather
/// than <see cref="PacketHub"/> so its continuous frame rate gets a dedicated
/// transport and can never delay packet, status, or alert events.
/// </summary>
public class AudioHub(RadioAudioBroker broker) : Hub
{
    public const string HubPath = "/hubs/audio";

    /// <summary>The wire format of the frames <see cref="Listen"/> produces.</summary>
    public AudioStreamFormat Format() => AudioStreamFormat.Current;

    /// <summary>
    /// Streams one radio's audio as 16-bit PCM frames.  Stream parameters are
    /// fixed for the life of the stream — to change squelch or TX monitoring,
    /// cancel and start a new stream, which costs nothing.
    /// </summary>
    public IAsyncEnumerable<byte[]> Listen(
        string radioId,
        bool squelchGated,
        bool includeTx,
        CancellationToken ct) =>
        broker.ListenAsync(radioId, squelchGated, includeTx, ct);
}

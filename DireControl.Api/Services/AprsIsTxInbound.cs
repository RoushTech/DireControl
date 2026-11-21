using System.Threading.Channels;

namespace DireControl.Api.Services;

/// <summary>
/// Packet lines the APRS-IS server pushes down our send-only (gating) login.
/// Servers such as aprsc and javAPRSSrvr deliver messages for stations we
/// gate to the connection those stations were gated through — the TX one,
/// not the filtered receive login — so these must reach the receive pipeline
/// or IS→RF gating never sees them.
/// </summary>
public sealed class AprsIsTxInbound
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(500)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public ChannelReader<string> Reader => _channel.Reader;

    public void Publish(string line) => _channel.Writer.TryWrite(line);
}

using System.Threading.Channels;

namespace DireControl.Api.Services;

/// <summary>
/// Unbounded channel that carries callsigns of stations whose packets were just
/// parsed. Consumed by <see cref="AlertingService"/> to evaluate alert rules.
/// </summary>
public sealed class PendingAlertChannel
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelWriter<string> Writer => _channel.Writer;
    public ChannelReader<string> Reader => _channel.Reader;
}

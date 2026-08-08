using System.Globalization;
using System.Net.Sockets;

namespace DireControl.Modem.Ptt;

/// <summary>
/// PTT via hamlib's rigctld TCP protocol (CAT control).  Also exposes the
/// rig's current frequency for status display.  Reconnects transparently if
/// rigctld restarts; commands time out rather than hanging the TX thread.
/// </summary>
public sealed class RigctldPtt(string host, int port) : IPttController
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public void SetPtt(bool transmit)
    {
        var reply = SendCommand(transmit ? "T 1" : "T 0")
            ?? throw new IOException($"rigctld at {host}:{port} is unreachable.");

        if (!reply.StartsWith("RPRT 0", StringComparison.Ordinal))
            throw new IOException($"rigctld rejected PTT command: {reply}");
    }

    /// <summary>
    /// Reads the rig's current frequency in Hz, or <see langword="null"/> if
    /// rigctld is unreachable or replies with an error.
    /// </summary>
    public long? TryGetFrequencyHz()
    {
        var reply = SendCommand("f");
        return reply is not null && long.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz)
            ? hz
            : null;
    }

    /// <summary>
    /// Sends one rigctld command and returns the first reply line, or
    /// <see langword="null"/> on any connection failure (after dropping the
    /// connection so the next call reconnects).
    /// </summary>
    private string? SendCommand(string command)
    {
        lock (_lock)
        {
            try
            {
                EnsureConnected();
                _writer!.WriteLine(command);
                return _reader!.ReadLine();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Disconnect();
                return null;
            }
        }
    }

    private void EnsureConnected()
    {
        if (_client is { Connected: true })
            return;

        Disconnect();

        var client = new TcpClient { SendTimeout = (int)CommandTimeout.TotalMilliseconds, ReceiveTimeout = (int)CommandTimeout.TotalMilliseconds };
        try
        {
            if (!client.ConnectAsync(host, port).Wait(CommandTimeout))
                throw new IOException($"Timed out connecting to rigctld at {host}:{port}.");
        }
        catch (AggregateException ex)
        {
            client.Dispose();
            throw new IOException(
                $"Cannot connect to rigctld at {host}:{port}: {ex.InnerException?.Message}",
                ex.InnerException);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();
        _client = client;
        _reader = new StreamReader(stream);
        _writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                if (_client is { Connected: true })
                {
                    _writer!.WriteLine("T 0");
                    _reader!.ReadLine();
                }
            }
            catch { /* best effort unkey */ }
            Disconnect();
        }
    }
}

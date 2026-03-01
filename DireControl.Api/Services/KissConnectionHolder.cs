using AprsSharp.KissTnc;

namespace DireControl.Api.Services;

/// <summary>
/// Singleton that holds a reference to the active <see cref="TcpTnc"/> managed
/// by <see cref="KissTcpService"/> so other services can transmit frames
/// without opening a second TCP connection.
/// </summary>
public sealed class KissConnectionHolder
{
    private TcpTnc? _tnc;
    private readonly Lock _lock = new();

    public void SetTnc(TcpTnc? tnc)
    {
        lock (_lock)
            _tnc = tnc;
    }

    /// <summary>
    /// Sends a raw AX.25 frame via KISS.  Returns <see langword="false"/> when
    /// there is no active connection.
    /// </summary>
    public bool TrySend(byte[] ax25Frame)
    {
        lock (_lock)
        {
            if (_tnc is null)
                return false;

            _tnc.SendData(ax25Frame);
            return true;
        }
    }

    public bool IsConnected
    {
        get { lock (_lock) return _tnc is not null; }
    }
}

namespace DireControl.Modem.Ptt;

/// <summary>
/// Keys and unkeys the transmitter.  Implementations must be safe to call
/// from the transmit thread and must leave the radio unkeyed on
/// <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IPttController : IDisposable
{
    /// <summary>Asserts (<see langword="true"/>) or releases PTT.</summary>
    void SetPtt(bool transmit);
}

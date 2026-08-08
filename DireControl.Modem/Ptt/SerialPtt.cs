using System.IO.Ports;

namespace DireControl.Modem.Ptt;

/// <summary>
/// PTT via the RTS and/or DTR modem-control lines of a serial port — the
/// classic keying method for USB serial interfaces.  The port is opened once
/// and held; baud rate is irrelevant since no data is sent.
/// </summary>
public sealed class SerialPtt : IPttController
{
    private readonly SerialPort _port;
    private readonly bool _useRts;
    private readonly bool _useDtr;

    public SerialPtt(string portName, bool useRts, bool useDtr)
    {
        if (!useRts && !useDtr)
            throw new ArgumentException("Serial PTT requires at least one of RTS or DTR.");

        _useRts = useRts;
        _useDtr = useDtr;

        _port = new SerialPort(portName);
        _port.Open();

        // Some adapters power up with the lines asserted — start unkeyed.
        SetPtt(false);
    }

    public void SetPtt(bool transmit)
    {
        if (_useRts)
            _port.RtsEnable = transmit;
        if (_useDtr)
            _port.DtrEnable = transmit;
    }

    public void Dispose()
    {
        try { SetPtt(false); } catch { /* port may already be gone */ }
        _port.Dispose();
    }
}

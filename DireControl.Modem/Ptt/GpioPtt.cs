using System.Device.Gpio;
using System.Device.Gpio.Drivers;

namespace DireControl.Modem.Ptt;

/// <summary>
/// PTT via a Linux GPIO character-device line (/dev/gpiochipN) — for
/// Raspberry Pi hat interfaces.  Uses libgpiod through System.Device.Gpio.
/// </summary>
public sealed class GpioPtt : IPttController
{
    private readonly GpioController _controller;
    private readonly int _line;
    private readonly bool _activeLow;

    public GpioPtt(int chipNumber, int line, bool activeLow)
    {
        _line = line;
        _activeLow = activeLow;
        _controller = new GpioController(new LibGpiodDriver(chipNumber));
        _controller.OpenPin(_line, PinMode.Output);
        SetPtt(false);
    }

    public void SetPtt(bool transmit)
    {
        var asserted = transmit ^ _activeLow;
        _controller.Write(_line, asserted ? PinValue.High : PinValue.Low);
    }

    public void Dispose()
    {
        try { SetPtt(false); } catch { /* chip may already be gone */ }
        _controller.Dispose();
    }
}

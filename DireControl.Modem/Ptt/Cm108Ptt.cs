namespace DireControl.Modem.Ptt;

/// <summary>
/// PTT via a GPIO pin on a CM108/CM119 USB sound chip — the method used by
/// DigiRig, AllStar/AIOC and similar interfaces, where pin 3 drives the PTT
/// transistor.  Keying is a 4-byte HID output report written to the chip's
/// hidraw device.
/// </summary>
public sealed class Cm108Ptt : IPttController
{
    private readonly FileStream _hid;
    private readonly int _pin;

    /// <param name="hidrawPath">hidraw device node, e.g. /dev/hidraw0.</param>
    /// <param name="pin">CM108 GPIO pin 1–8; virtually all interfaces use 3.</param>
    public Cm108Ptt(string hidrawPath, int pin)
    {
        if (pin is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(pin), "CM108 GPIO pin must be 1–8.");

        _pin = pin;
        _hid = new FileStream(hidrawPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        SetPtt(false);
    }

    public void SetPtt(bool transmit)
    {
        var report = BuildPttReport(_pin, transmit);
        _hid.Write(report);
        _hid.Flush();
    }

    /// <summary>
    /// Builds the CM108 HID output report:
    /// [report id 0, HID register 0, GPIO data, GPIO direction mask].
    /// The direction byte marks the pin as an output; the data byte drives it.
    /// </summary>
    public static byte[] BuildPttReport(int pin, bool transmit)
    {
        var mask = (byte)(1 << (pin - 1));
        return [0x00, 0x00, transmit ? mask : (byte)0x00, mask];
    }

    public void Dispose()
    {
        try { SetPtt(false); } catch { /* device may already be gone */ }
        _hid.Dispose();
    }
}

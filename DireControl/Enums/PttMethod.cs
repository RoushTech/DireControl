namespace DireControl.Enums;

/// <summary>How the native sound modem keys the transmitter.</summary>
public enum PttMethod
{
    Unknown = 0,

    /// <summary>No PTT control — the radio keys itself via VOX.</summary>
    None = 1,

    /// <summary>RTS and/or DTR line on a serial port.</summary>
    SerialRtsDtr = 2,

    /// <summary>GPIO pin on a CM108/CM119 USB sound chip (DigiRig-style interfaces).</summary>
    Cm108 = 3,

    /// <summary>Linux GPIO line (Raspberry Pi hats).</summary>
    Gpio = 4,

    /// <summary>Hamlib rigctld over TCP (CAT control).</summary>
    Rigctld = 5,
}

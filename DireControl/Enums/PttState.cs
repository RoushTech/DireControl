namespace DireControl.Enums;

/// <summary>Live PTT line state of a radio's modem.</summary>
public enum PttState
{
    Unknown = 0,
    Released = 1,
    Keyed = 2,
    /// <summary>Unkey failed — the radio may still be transmitting.</summary>
    Stuck = 3,
}

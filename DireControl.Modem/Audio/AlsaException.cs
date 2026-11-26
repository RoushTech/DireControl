namespace DireControl.Modem.Audio;

/// <summary>Raised when an ALSA call fails.</summary>
public sealed class AlsaException(string operation, int errorCode)
    : Exception($"{operation} failed: {AlsaNative.ErrorString(errorCode)} ({errorCode})")
{
    public int ErrorCode { get; } = errorCode;
}

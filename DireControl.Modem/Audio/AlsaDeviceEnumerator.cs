using System.Runtime.InteropServices;

namespace DireControl.Modem.Audio;

/// <summary>An ALSA PCM device as reported by the name-hint API.</summary>
public sealed record AlsaPcmDevice(string Name, string Description, bool SupportsCapture, bool SupportsPlayback);

/// <summary>
/// Enumerates ALSA PCM devices via <c>snd_device_name_hint</c> — the same list
/// <c>arecord -L</c> shows, covering plugins (plughw, dsnoop) as well as raw
/// hardware devices.
/// </summary>
public static class AlsaDeviceEnumerator
{
    public static IReadOnlyList<AlsaPcmDevice> ListPcmDevices()
    {
        var err = AlsaNative.snd_device_name_hint(-1, "pcm", out var hints);
        if (err < 0)
            throw new AlsaException("snd_device_name_hint", err);

        var devices = new List<AlsaPcmDevice>();
        try
        {
            for (var i = 0; ; i++)
            {
                var hint = Marshal.ReadIntPtr(hints, i * IntPtr.Size);
                if (hint == IntPtr.Zero)
                    break;

                var name = GetHint(hint, "NAME");
                if (name is null)
                    continue;

                var description = GetHint(hint, "DESC")?.Replace('\n', ' ') ?? name;

                // IOID is null when the device supports both directions.
                var ioid = GetHint(hint, "IOID");
                devices.Add(new AlsaPcmDevice(
                    name,
                    description,
                    SupportsCapture: ioid is null or "Input",
                    SupportsPlayback: ioid is null or "Output"));
            }
        }
        finally
        {
            AlsaNative.snd_device_name_free_hint(hints);
        }

        return devices;
    }

    private static string? GetHint(IntPtr hint, string id)
    {
        var ptr = AlsaNative.snd_device_name_get_hint(hint, id);
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            AlsaNative.free(ptr);
        }
    }
}

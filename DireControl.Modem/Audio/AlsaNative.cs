using System.Runtime.InteropServices;

namespace DireControl.Modem.Audio;

/// <summary>
/// Minimal P/Invoke surface over libasound (ALSA) — just enough for PCM
/// capture/playback with <c>snd_pcm_set_params</c> and device enumeration via
/// name hints.  Linux-only by design.
/// </summary>
internal static partial class AlsaNative
{
    private const string Lib = "libasound.so.2";

    // ── PCM stream/format constants ───────────────────────────────────────────

    internal const int SndPcmStreamPlayback = 0;
    internal const int SndPcmStreamCapture = 1;

    /// <summary>SND_PCM_FORMAT_S16_LE</summary>
    internal const int SndPcmFormatS16Le = 2;

    /// <summary>SND_PCM_ACCESS_RW_INTERLEAVED</summary>
    internal const int SndPcmAccessRwInterleaved = 3;

    // ── PCM ───────────────────────────────────────────────────────────────────

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_close(IntPtr pcm);

    /// <summary>SND_PCM_NONBLOCK open mode.</summary>
    internal const int SndPcmNonblock = 1;

    /// <summary>Switches an open PCM between blocking (0) and non-blocking (1) I/O.</summary>
    [LibraryImport(Lib)]
    internal static partial int snd_pcm_nonblock(IntPtr pcm, int nonblock);

    /// <summary>
    /// One-call configuration: format, access, channels, rate, soft-resample,
    /// and required latency in microseconds.
    /// </summary>
    [LibraryImport(Lib)]
    internal static partial int snd_pcm_set_params(
        IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyUs);

    // ── Explicit hardware parameters (period/buffer sizing) ───────────────────

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_malloc(out IntPtr hwParams);

    [LibraryImport(Lib)]
    internal static partial void snd_pcm_hw_params_free(IntPtr hwParams);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_any(IntPtr pcm, IntPtr hwParams);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_access(IntPtr pcm, IntPtr hwParams, int access);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_format(IntPtr pcm, IntPtr hwParams, int format);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_channels(IntPtr pcm, IntPtr hwParams, uint channels);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_rate_resample(IntPtr pcm, IntPtr hwParams, uint enable);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_rate_near(IntPtr pcm, IntPtr hwParams, ref uint rate, ref int dir);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_buffer_time_near(IntPtr pcm, IntPtr hwParams, ref uint us, ref int dir);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params_set_period_time_near(IntPtr pcm, IntPtr hwParams, ref uint us, ref int dir);

    [LibraryImport(Lib)]
    internal static partial int snd_pcm_hw_params(IntPtr pcm, IntPtr hwParams);

    [LibraryImport(Lib)]
    internal static partial long snd_pcm_readi(IntPtr pcm, IntPtr buffer, ulong frames);

    [LibraryImport(Lib)]
    internal static partial long snd_pcm_writei(IntPtr pcm, IntPtr buffer, ulong frames);

    /// <summary>Recovers from -EPIPE (overrun/underrun) and -ESTRPIPE (suspend).</summary>
    [LibraryImport(Lib)]
    internal static partial int snd_pcm_recover(IntPtr pcm, int err, int silent);

    /// <summary>Blocks until all queued playback samples have played out.</summary>
    [LibraryImport(Lib)]
    internal static partial int snd_pcm_drain(IntPtr pcm);

    [LibraryImport(Lib)]
    internal static partial IntPtr snd_strerror(int errnum);

    internal static string ErrorString(int errnum) =>
        Marshal.PtrToStringUTF8(snd_strerror(errnum)) ?? $"ALSA error {errnum}";

    // ── Device name hints ─────────────────────────────────────────────────────

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int snd_device_name_hint(int card, string iface, out IntPtr hints);

    [LibraryImport(Lib)]
    internal static partial int snd_device_name_free_hint(IntPtr hints);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr snd_device_name_get_hint(IntPtr hint, string id);

    /// <summary>Frees strings returned by <see cref="snd_device_name_get_hint"/> (malloc'd).</summary>
    [LibraryImport("libc")]
    internal static partial void free(IntPtr ptr);
}

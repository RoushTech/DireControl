using DireControl.Modem.Ax25;

namespace DireControl.Api.Services;

/// <summary>
/// Pure WIDEn-N digipeater path logic (New-Paradigm), extracted as static
/// functions so every path-handling branch is unit-testable without the
/// service plumbing.
/// </summary>
public static class DigipeaterLogic
{
    /// <summary>
    /// Decides whether <paramref name="frame"/> should be digipeated by
    /// <paramref name="myCallsign"/>, and if so returns the rewritten frame to
    /// transmit.  Returns <see langword="null"/> when the frame must not be
    /// repeated.
    /// </summary>
    /// <remarks>
    /// Rules implemented:
    /// <list type="bullet">
    /// <item>Only UI frames are digipeated.</item>
    /// <item>The first unused (H bit clear) path entry decides everything;
    ///   entries after it are untouched.</item>
    /// <item>Our own callsign as the unused entry → mark it used.</item>
    /// <item><c>WIDEn-N</c> (n 1–7, N ≥ 1): decrement N; when it reaches 0 the
    ///   alias is replaced by our callsign (callsign substitution), otherwise
    ///   our callsign is inserted before the decremented alias.  Both carry
    ///   the H bit.</item>
    /// <item><c>WIDEn</c> with n &gt; <paramref name="maxWideN"/> is trapped:
    ///   digipeated once with our callsign substituted, killing the path.</item>
    /// <item><paramref name="fillInOnly"/> restricts service to
    ///   <c>WIDE1-1</c> — classic fill-in digi behaviour.</item>
    /// </list>
    /// </remarks>
    public static Ax25Frame? TryBuildDigipeat(
        Ax25Frame frame,
        string myCallsign,
        int maxWideN,
        bool fillInOnly)
    {
        if (frame.Control != Ax25Frame.UiControl)
            return null;

        var my = Ax25Address.Parse(myCallsign.ToUpperInvariant());

        // Find the first path entry that has not been used yet.
        var index = -1;
        for (var i = 0; i < frame.Path.Count; i++)
        {
            if (!frame.Path[i].HasBeenRepeated)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return null; // path exhausted

        var hop = frame.Path[index];
        var newPath = new List<Ax25Address>(frame.Path);

        if (hop.Callsign.Equals(my.Callsign, StringComparison.OrdinalIgnoreCase) && hop.Ssid == my.Ssid)
        {
            // Explicitly addressed to us — mark used.
            newPath[index] = hop with { HasBeenRepeated = true };
        }
        else if (TryParseWide(hop, out var n) && !fillInOnly)
        {
            if (n > maxWideN)
            {
                // Abusive path (e.g. WIDE7-7) — trap it: one hop, path dead.
                newPath[index] = my with { HasBeenRepeated = true };
            }
            else if (hop.Ssid <= 1)
            {
                // Last hop of this alias — callsign substitution.
                newPath[index] = my with { HasBeenRepeated = true };
            }
            else
            {
                // Decrement and insert ourselves in front.
                newPath[index] = hop with { Ssid = hop.Ssid - 1 };
                newPath.Insert(index, my with { HasBeenRepeated = true });
            }
        }
        else if (fillInOnly && IsWide1Hop(hop))
        {
            // Fill-in digi: serve only the first WIDE1-1 hop, by substitution.
            newPath[index] = my with { HasBeenRepeated = true };
        }
        else
        {
            return null;
        }

        return new Ax25Frame
        {
            Destination = frame.Destination,
            Source = frame.Source,
            Path = newPath,
            Control = frame.Control,
            Pid = frame.Pid,
            Info = frame.Info,
        };
    }

    /// <summary>
    /// Matches <c>WIDEn-N</c> aliases with n 1–7 and a remaining hop count
    /// N ≥ 1 (the address SSID).  <c>WIDEn-0</c> / bare <c>WIDEn</c> are
    /// exhausted and do not match.
    /// </summary>
    private static bool TryParseWide(Ax25Address address, out int n)
    {
        n = 0;
        var call = address.Callsign;
        if (call.Length != 5 ||
            !call.StartsWith("WIDE", StringComparison.OrdinalIgnoreCase) ||
            call[4] is < '1' or > '7')
        {
            return false;
        }

        n = call[4] - '0';
        return address.Ssid >= 1;
    }

    private static bool IsWide1Hop(Ax25Address address) =>
        address.Callsign.Equals("WIDE1", StringComparison.OrdinalIgnoreCase) && address.Ssid == 1;
}

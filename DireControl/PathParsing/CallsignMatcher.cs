using DireControl.Data.Models;
using DireControl.Enums;

namespace DireControl.PathParsing;

/// <summary>
/// Determines whether an APRS packet source callsign matches a configured radio.
/// Handles the -0 SSID equivalence rule: "W3UWU-0" is treated as "W3UWU".
/// </summary>
public static class CallsignMatcher
{
    /// <summary>
    /// Returns true if <paramref name="packetSource"/> matches the radio's full callsign exactly,
    /// treating a missing SSID and "-0" as equivalent on both sides.
    /// </summary>
    public static bool Matches(Radio radio, string packetSource) =>
        Normalise(Radio.ComputeFullCallsign(radio.Callsign, radio.Ssid)) == Normalise(packetSource);

    /// <summary>
    /// Finds the active radio that owns <paramref name="packet"/>, using the correct
    /// matching strategy for each <see cref="PacketSource"/>:
    /// <list type="bullet">
    ///   <item>
    ///     <term><see cref="PacketSource.Rf"/></term>
    ///     <description>
    ///       Match by KISS channel number, then verify the packet's source callsign
    ///       matches the radio's configured callsign. Returns <see langword="null"/> if
    ///       the channel matches but the callsign does not (different station on same port).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>all other sources (e.g. <see cref="PacketSource.AprsIs"/>)</term>
    ///     <description>
    ///       KISS channel is meaningless for non-RF packets (always 0 by default).
    ///       Match by callsign alone.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    /// <returns>The matching <see cref="Radio"/>, or <see langword="null"/> if none.</returns>
    public static Radio? FindMatchingRadio(Packet packet, IEnumerable<Radio> activeRadios)
    {
        if (packet.Source == PacketSource.Rf)
        {
            var byChannel = activeRadios.FirstOrDefault(r => r.ChannelNumber == packet.KissChannel);
            if (byChannel is null)
                return null;

            // Channel matched — verify the callsign also matches.
            // If it doesn't, the packet is from a different station on the same KISS port;
            // return null so it is not treated as an own-beacon.
            return Matches(byChannel, packet.StationCallsign) ? byChannel : null;
        }

        // APRS-IS and any future non-RF source: channel has no meaning, match by callsign.
        return activeRadios.FirstOrDefault(r => Matches(r, packet.StationCallsign));
    }

    public static string Normalise(string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return callsign;

        var upper = callsign.ToUpperInvariant();

        // "-0" is equivalent to no SSID in APRS
        if (upper.EndsWith("-0"))
            return upper[..^2];

        return upper;
    }
}

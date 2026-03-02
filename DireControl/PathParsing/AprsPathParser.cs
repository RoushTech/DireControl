using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DireControl.Data.Models;

namespace DireControl.PathParsing;

/// <summary>
/// Static helpers for extracting the via-hop chain from an APRS packet path.
/// </summary>
public static class AprsPathParser
{
    /// <summary>
    /// Pattern matching generic APRS path aliases of the form WIDE2-1, TRACE3-3, RELAY, etc.
    /// </summary>
    private static readonly Regex GenericAliasPattern =
        new(@"^(WIDE|RELAY|TRACE|NCA|GATE|ECHO|IGATE)(\d(-\d+)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Fixed-string aliases that are never real station callsigns.
    /// </summary>
    private static readonly HashSet<string> FixedAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "RFONLY", "NOGATE", "TCPIP", "TCPXX",
        "qAR", "qAC", "qAS", "qAO",
    };

    /// <summary>
    /// Splits a TNC2-format string into its three header components.
    /// TNC2 format: <c>SOURCE&gt;TOCALL,HOP1,...,HOPn:INFO</c>
    /// </summary>
    /// <param name="raw">The full raw TNC2 packet string.</param>
    /// <returns>
    ///   <list type="bullet">
    ///     <item><description><c>Source</c>  — the originating callsign.</description></item>
    ///     <item><description><c>Tocall</c>  — the destination / TOCALL field.</description></item>
    ///     <item><description>
    ///       <c>RawPath</c> — the via-hop portion of the path with TOCALL excluded and
    ///       asterisk markers intact (e.g. <c>"WE4MB-3*,WIDE2"</c>).
    ///       Empty string when there are no via entries.
    ///     </description></item>
    ///   </list>
    /// </returns>
    public static (string Source, string Tocall, string RawPath) ParseTnc2Header(string raw)
    {
        var colonIdx = raw.IndexOf(':');
        var header   = colonIdx >= 0 ? raw[..colonIdx] : raw;

        var gtIdx = header.IndexOf('>');
        if (gtIdx < 0)
            return (raw, string.Empty, string.Empty);

        var source  = header[..gtIdx];
        var afterGt = header[(gtIdx + 1)..];

        var firstComma = afterGt.IndexOf(',');
        if (firstComma < 0)
            return (source, afterGt, string.Empty);  // only TOCALL, no via entries

        var tocall  = afterGt[..firstComma];
        var rawPath = afterGt[(firstComma + 1)..];   // "WE4MB-3*,WIDE2"

        return (source, tocall, rawPath);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="callsign"/> is a generic APRS alias
    /// (e.g. WIDE1, WIDE2-1, RELAY, RFONLY) that does not identify a specific station.
    /// Leading or trailing <c>*</c> and whitespace are ignored.
    /// </summary>
    public static bool IsGenericAlias(string callsign)
    {
        var stripped = callsign.TrimEnd('*').Trim();
        return GenericAliasPattern.IsMatch(stripped) || FixedAliases.Contains(stripped);
    }

    /// <summary>
    /// Extracts the intermediate (via) hop list from an APRS path array.
    /// <para>
    /// The first element of <paramref name="aprsPath"/> is always the TOCALL (destination
    /// field) — it is never a digipeater hop and is unconditionally skipped, regardless of
    /// whether it looks like a software identifier or a real callsign.
    /// </para>
    /// <para>
    /// Only path entries ending with <c>*</c> represent hops the packet actually passed
    /// through.  An entry without <c>*</c> is an unused request (alias requested but not
    /// consumed).  When no starred entries exist the packet was heard direct — zero hops.
    /// </para>
    /// <para>
    /// Generic aliases (WIDE1, WIDE2, RELAY, etc.) are never real hops.  When a starred
    /// generic alias immediately follows a real callsign, its name is stored as
    /// <see cref="ResolvedPathEntry.AliasUsed"/> on that preceding hop entry.
    /// </para>
    /// </summary>
    /// <param name="aprsPath">
    ///   The path list whose first element is the TOCALL.
    /// </param>
    /// <returns>
    ///   The intermediate hop entries (HopIndex 1…N) and the count of real digipeater hops.
    /// </returns>
    public static (List<ResolvedPathEntry> Hops, int HopCount) ExtractViaHops(
        IList<string>? aprsPath)
    {
        var allEntries = aprsPath?.ToList() ?? [];

        // allEntries[0] is the TOCALL — always skip it, no conditions.
        var viaEntries = allEntries.Count > 0 ? allEntries.Skip(1).ToList() : [];

        var hops = new List<ResolvedPathEntry>();

        foreach (var raw in viaEntries)
        {
            var isUsed   = raw.TrimEnd().EndsWith('*');
            var callsign = raw.TrimEnd('*').Trim();

            if (!isUsed)
                continue;  // unused alias request — not a hop, not metadata to record

            if (string.IsNullOrWhiteSpace(callsign))
                continue;

            if (IsGenericAlias(callsign))
            {
                // Attach as metadata to the most recently added real hop
                if (hops.Count > 0)
                    hops[^1].AliasUsed = callsign;
                continue;
            }

            // Real callsign that was used — it's a hop
            hops.Add(new ResolvedPathEntry
            {
                Callsign = callsign,
                HopIndex = hops.Count + 1,  // 0 is reserved for the originating station
                Known    = false,
                AliasUsed = null,
            });
        }

        return (hops, hops.Count);
    }
}

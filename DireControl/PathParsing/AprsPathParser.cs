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

        // Only starred entries were actually used as hops.
        // Entries without '*' are unused alias requests — not hops.
        // No starred entries → direct packet, zero hops.
        var hops = viaEntries
            .Where(e => e.TrimEnd().EndsWith('*'))
            .Select((e, i) => new ResolvedPathEntry
            {
                Callsign = e.TrimEnd('*').Trim(),
                HopIndex = i + 1,  // 0 is reserved for the originating station
                Known = false,
            })
            .Where(e => !string.IsNullOrWhiteSpace(e.Callsign))
            .ToList();

        var hopCount = hops.Count;

        return (hops, hopCount);
    }
}

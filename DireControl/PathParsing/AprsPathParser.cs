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
        new(@"^(WIDE|RELAY|TRACE|NCA|GATE|ECHO|IGATE)(\d(-\d)?)?$",
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
    /// Extracts the intermediate (via) hop list from an APRSSharp path array.
    /// <para>
    /// APRSSharp puts the TOCALL (destination field) at index 0 when parsing TNC2
    /// format — it is the packet's destination identifier, not a digipeater hop, and is
    /// always skipped.  The remaining entries are the actual via path.
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>With <c>*</c> markers</b> — only entries ending with <c>*</c> are included;
    ///     these are the hops that have actually repeated the packet.
    ///   </item>
    ///   <item>
    ///     <b>Without <c>*</c> markers</b> — all via entries are included in order.
    ///     Direwolf sometimes strips the <c>*</c> flags; treating every entry as a used hop
    ///     is the best we can do.  Generic aliases are preserved as <c>Known=false</c> entries
    ///     so the hop count is accurate and the UI can display them with a "?" indicator.
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="aprsPath">
    ///   The <c>Packet.Path</c> list from APRSSharp, whose first element is the TOCALL.
    /// </param>
    /// <returns>
    ///   The intermediate hop entries (HopIndex 1…N) and the count of real digipeater hops.
    /// </returns>
    public static (List<ResolvedPathEntry> Hops, int HopCount) ExtractViaHops(
        IList<string>? aprsPath)
    {
        var allEntries = aprsPath?.ToList() ?? [];

        // Path[0] is the TOCALL — skip it.
        var viaEntries = allEntries.Count > 0 ? allEntries.Skip(1).ToList() : [];

        bool hasStarMarkers = viaEntries.Any(e => e.TrimEnd().EndsWith('*'));

        List<ResolvedPathEntry> hops;
        int hopCount;

        if (hasStarMarkers)
        {
            // Standard: only starred entries were actually repeated.
            // Unused future hops (no '*') are not part of the actual path.
            hops = viaEntries
                .Where(e => e.TrimEnd().EndsWith('*'))
                .Select((e, i) => new ResolvedPathEntry
                {
                    Callsign = e.TrimEnd('*').Trim(),
                    HopIndex = i + 1,  // 0 is reserved for the originating station
                    Known = false,
                })
                .Where(e => !string.IsNullOrWhiteSpace(e.Callsign))
                .ToList();

            hopCount = hops.Count;
        }
        else
        {
            // No star markers: Direwolf may have stripped '*' flags.
            // Include every via entry so the hop chain is complete.
            // Generic aliases will be marked Known=false by the coordinate resolver.
            hops = viaEntries
                .Select((e, i) => new ResolvedPathEntry
                {
                    Callsign = e.Trim(),
                    HopIndex = i + 1,  // 0 is reserved for the originating station
                    Known = false,
                })
                .Where(e => !string.IsNullOrWhiteSpace(e.Callsign))
                .ToList();

            hopCount = hops.Count(e => !IsGenericAlias(e.Callsign));
        }

        return (hops, hopCount);
    }
}

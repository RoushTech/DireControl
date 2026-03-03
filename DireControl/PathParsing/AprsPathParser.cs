using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DireControl.Data.Models;
using DireControl.Enums;

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
    /// Returns <c>true</c> if <paramref name="hop"/> is an internet-routing token that
    /// must never appear as a hop node in <c>ResolvedPath</c>.
    /// Covers <c>q</c> codes, <c>TCPIP</c>, <c>TCPXX</c>, <c>NOGATE</c>, and <c>RFONLY</c>.
    /// Leading or trailing <c>*</c> and whitespace are ignored.
    /// </summary>
    public static bool IsInternetToken(string hop)
    {
        var stripped = hop.TrimEnd('*').Trim();
        return stripped.StartsWith("q", StringComparison.OrdinalIgnoreCase)
            || stripped.Equals("TCPIP",  StringComparison.OrdinalIgnoreCase)
            || stripped.Equals("TCPXX",  StringComparison.OrdinalIgnoreCase)
            || stripped.Equals("NOGATE", StringComparison.OrdinalIgnoreCase)
            || stripped.Equals("RFONLY", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies how a packet was received based on its via-path entries and RF hop count.
    /// </summary>
    /// <param name="pathEntries">
    ///   The via-path entries after the TOCALL, with <c>*</c> markers intact.
    ///   These are the raw comma-separated tokens from the path field.
    /// </param>
    /// <param name="hopCount">Number of RF digipeater hops (internet tokens excluded).</param>
    public static HeardVia ClassifyHeardVia(IReadOnlyList<string> pathEntries, int hopCount)
    {
        bool hasQConstruct = pathEntries.Any(h =>
            h.TrimEnd('*').Trim().StartsWith("q", StringComparison.OrdinalIgnoreCase));
        bool hasTcpIp = pathEntries.Any(h =>
            h.TrimEnd('*').Trim().StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase));

        if (!hasQConstruct && !hasTcpIp)
        {
            // Pure RF packet
            return hopCount == 0 ? HeardVia.Direct : HeardVia.Digi;
        }

        if (hasTcpIp || pathEntries.Any(h =>
                h.TrimEnd('*').Trim().Equals("qAC", StringComparison.OrdinalIgnoreCase)))
        {
            // Originated on the internet, no RF leg
            return HeardVia.Internet;
        }

        if (pathEntries.Any(h =>
            {
                var s = h.TrimEnd('*').Trim();
                return s.Equals("qAR", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("qAO", StringComparison.OrdinalIgnoreCase);
            }))
        {
            // Originated on RF, igated to internet
            return hopCount > 0 ? HeardVia.IgateRfDigi : HeardVia.IgateRf;
        }

        return HeardVia.Unknown;
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
    /// This covers both the standard form (<c>W4CAT-2*,WIDE2-1</c>) and the non-standard
    /// form used by some digipeaters where the callsign is unstarred and the alias it
    /// consumed carries the star (<c>W4CAT-2,WIDE2*</c>).
    /// </para>
    /// <para>
    /// Internet tokens (<c>q</c> codes, TCPIP, TCPXX, NOGATE, RFONLY) and the igate
    /// callsign that follows a <c>q</c> code are never added as hop nodes.  Processing
    /// stops once the internet section begins.
    /// </para>
    /// </summary>
    /// <param name="aprsPath">
    ///   The path list whose first element is the TOCALL.
    /// </param>
    /// <returns>
    ///   The intermediate hop entries (HopIndex 1…N) and the count of real RF digipeater hops.
    /// </returns>
    public static (List<ResolvedPathEntry> Hops, int HopCount) ExtractViaHops(
        IList<string>? aprsPath)
    {
        var allEntries = aprsPath?.ToList() ?? [];

        // allEntries[0] is the TOCALL — always skip it, no conditions.
        var viaEntries = allEntries.Count > 0 ? allEntries.Skip(1).ToList() : [];

        var hops = new List<ResolvedPathEntry>();

        bool internetSectionStarted = false;
        bool prevWasQCode = false;

        for (int i = 0; i < viaEntries.Count; i++)
        {
            var raw      = viaEntries[i];
            var callsign = raw.TrimEnd('*').Trim();

            if (IsInternetToken(callsign))
            {
                internetSectionStarted = true;
                prevWasQCode = callsign.StartsWith("q", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (internetSectionStarted && prevWasQCode)
            {
                // This entry is the igate callsign immediately after a q code — skip it
                prevWasQCode = false;
                continue;
            }

            if (internetSectionStarted)
                break;  // Past the internet section; nothing further is an RF hop

            var isUsed = raw.TrimEnd().EndsWith('*');

            if (!isUsed)
            {
                if (IsGenericAlias(callsign))
                    continue;  // unused alias request — not a hop, not metadata to record

                // Unstarred real callsign.  Some digipeaters insert their own call
                // (without '*') before the alias they consumed, producing a path
                // segment like "W4CAT-2,WIDE2*" instead of the standard "W4CAT-2*,WIDE2-1".
                // If the next entry is a starred generic alias, this callsign is that hop.
                if (i + 1 < viaEntries.Count)
                {
                    var nextRaw      = viaEntries[i + 1];
                    var nextCallsign = nextRaw.TrimEnd('*').Trim();
                    if (nextRaw.TrimEnd().EndsWith('*') && IsGenericAlias(nextCallsign))
                    {
                        hops.Add(new ResolvedPathEntry
                        {
                            Callsign  = callsign,
                            HopIndex  = hops.Count + 1,
                            Known     = false,
                            AliasUsed = nextCallsign,
                        });
                        i++;  // consume the starred alias — already recorded as AliasUsed
                        continue;
                    }
                }

                continue;  // unstarred real callsign with no starred alias following — skip
            }

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
                Callsign  = callsign,
                HopIndex  = hops.Count + 1,  // 0 is reserved for the originating station
                Known     = false,
                AliasUsed = null,
            });
        }

        return (hops, hops.Count);
    }
}

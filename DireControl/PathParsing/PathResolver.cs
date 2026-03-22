using System.Collections.Generic;
using System.Linq;
using DireControl.Data.Models;
using DireControl.Enums;

namespace DireControl.PathParsing;

/// <summary>
/// Result of resolving an APRS TNC2 packet path.
/// </summary>
public sealed class PathResolveResult
{
    /// <summary>Number of RF digipeater hops (internet tokens excluded).</summary>
    public int HopCount { get; init; }

    /// <summary>How the packet was received.</summary>
    public HeardVia HeardVia { get; init; }

    /// <summary>
    /// Full hop list: source station at index 0, intermediate digipeaters next,
    /// home/receiving station last.
    /// </summary>
    public List<ResolvedPathEntry> Hops { get; init; } = [];
}

/// <summary>
/// Resolves a raw TNC2 packet string into a fully-classified path result.
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Parses <paramref name="raw"/> and returns a <see cref="PathResolveResult"/> containing
    /// the RF hop chain, hop count, and <see cref="HeardVia"/> classification.
    /// </summary>
    /// <param name="raw">Full TNC2 packet string.</param>
    /// <param name="homeCallsign">Callsign of the receiving station appended as the last hop.</param>
    /// <param name="stationLookup">
    ///   Optional lookup for resolving hop coordinates.  When provided, entries whose callsign
    ///   exists in the dictionary have <see cref="ResolvedPathEntry.Known"/> set to <c>true</c>
    ///   and their coordinates populated.
    /// </param>
    public static PathResolveResult Resolve(
        string raw,
        string homeCallsign,
        IReadOnlyDictionary<string, (double Lat, double Lon)>? stationLookup = null)
    {
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        List<string> pathList = new List<string> { tocall };
        if (!string.IsNullOrEmpty(rawPath))
        {
            pathList.AddRange(rawPath.Split(',', System.StringSplitOptions.RemoveEmptyEntries));
        }

        var (viaHops, hopCount) = AprsPathParser.ExtractViaHops(pathList);

        // Classify using the raw via entries (with * intact).
        var pathEntries = string.IsNullOrEmpty(rawPath)
            ? (IReadOnlyList<string>)[]
            : rawPath.Split(',', System.StringSplitOptions.RemoveEmptyEntries);

        var heardVia = AprsPathParser.ClassifyHeardVia(pathEntries);

        // Build final hop list: source → intermediate digisp → home
        var allHops = new List<ResolvedPathEntry>(viaHops.Count + 2);

        allHops.Add(MakeEntry(source, 0, null, stationLookup));

        for (var i = 0; i < viaHops.Count; i++)
        {
            var h = viaHops[i];
            h.HopIndex = i + 1;
            if (stationLookup is not null && stationLookup.TryGetValue(h.Callsign, out var coords))
            {
                h.Known = true;
                h.Latitude = coords.Lat;
                h.Longitude = coords.Lon;
            }
            allHops.Add(h);
        }

        allHops.Add(MakeEntry(homeCallsign, allHops.Count, null, stationLookup));

        return new PathResolveResult
        {
            HopCount = hopCount,
            HeardVia = heardVia,
            Hops     = allHops,
        };
    }

    private static ResolvedPathEntry MakeEntry(
        string callsign,
        int hopIndex,
        string? aliasUsed,
        IReadOnlyDictionary<string, (double Lat, double Lon)>? lookup)
    {
        var entry = new ResolvedPathEntry
        {
            Callsign  = callsign,
            HopIndex  = hopIndex,
            AliasUsed = aliasUsed,
            Known     = false,
        };

        if (lookup is not null && lookup.TryGetValue(callsign, out var coords))
        {
            entry.Known     = true;
            entry.Latitude  = coords.Lat;
            entry.Longitude = coords.Lon;
        }

        return entry;
    }
}

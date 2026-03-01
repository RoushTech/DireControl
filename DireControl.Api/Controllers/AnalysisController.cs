using DireControl.Api.Contracts;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/analysis")]
public class AnalysisController(DireControlContext db) : ControllerBase
{
    // Generic APRS path aliases that are not real station callsigns.
    private static readonly HashSet<string> GenericAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "RELAY", "WIDE", "TRACE", "GATE", "NOGATE", "RFONLY", "TCPIP", "TCPXX", "IGATE",
    };

    [HttpGet("coverage")]
    public async Task<ActionResult<IReadOnlyList<CoverageGridSquareDto>>> GetCoverage(CancellationToken ct)
    {
        var raw = await db.Packets
            .AsNoTracking()
            .Where(p => p.GridSquare != null && p.GridSquare != string.Empty &&
                        p.Latitude != null && p.Longitude != null)
            .Select(p => new { p.GridSquare, p.Latitude, p.Longitude })
            .ToListAsync(ct);

        var result = raw
            .GroupBy(p => p.GridSquare!)
            .Select(g => new CoverageGridSquareDto
            {
                GridSquare = g.Key,
                Lat = g.Average(p => p.Latitude!.Value),
                Lon = g.Average(p => p.Longitude!.Value),
                PacketCount = g.Count(),
            })
            .OrderByDescending(g => g.PacketCount)
            .ToList();

        return Ok(result);
    }

    [HttpGet("digipeaters")]
    public async Task<ActionResult<IReadOnlyList<DigipeaterAnalysisEntryDto>>> GetDigipeaters(
        CancellationToken ct)
    {
        var cutoff24h = DateTime.UtcNow.AddHours(-24);

        // Load path strings from all packets that have a non-empty path.
        // Processing in memory avoids joining on a JSON column.
        var packetPaths = await db.Packets
            .AsNoTracking()
            .Where(p => p.Path != null && p.Path != string.Empty)
            .Select(p => new { p.Path, p.ReceivedAt })
            .ToListAsync(ct);

        // Aggregate per digipeater callsign found in the path field.
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var last24h = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hopPositions = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkt in packetPaths)
        {
            var entries = pkt.Path.Split(',', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                // Strip the '*' used-marker and normalise.
                var callsign = entries[i].TrimEnd('*').Trim();

                if (string.IsNullOrWhiteSpace(callsign) || IsGenericAlias(callsign))
                    continue;

                totals.TryAdd(callsign, 0);
                last24h.TryAdd(callsign, 0);
                if (!hopPositions.ContainsKey(callsign))
                    hopPositions[callsign] = [];

                totals[callsign]++;
                if (pkt.ReceivedAt >= cutoff24h)
                    last24h[callsign]++;
                hopPositions[callsign].Add(i + 1); // 1-based hop position
            }
        }

        var result = totals
            .Select(kv => new DigipeaterAnalysisEntryDto
            {
                Callsign = kv.Key,
                TotalPacketsForwarded = kv.Value,
                Last24h = last24h.GetValueOrDefault(kv.Key),
                AverageHopsFromUs = hopPositions.TryGetValue(kv.Key, out var hops) && hops.Count > 0
                    ? hops.Average()
                    : 0.0,
            })
            .OrderByDescending(d => d.TotalPacketsForwarded)
            .Take(20)
            .ToList();

        return Ok(result);
    }

    private static bool IsGenericAlias(string callsign)
    {
        // GenericAliases covers exact matches; also filter WIDE1-1, WIDE2-2, TRACE3-3, etc.
        if (GenericAliases.Contains(callsign))
            return true;

        foreach (var prefix in new[] { "WIDE", "TRACE", "RELAY" })
        {
            if (callsign.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

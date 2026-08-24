using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Api.Services.Weather;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

/// <summary>
/// Direct-RF reception — which stations each radio has heard with no digipeater in between,
/// and how that has trended.
///
/// Most of this is derived live from <see cref="Packet"/>: every packet carries its own
/// <see cref="Packet.HeardVia"/> classification, so a grouped query answers "who did this
/// radio hear direct" without any precomputation, and can never drift from the truth. Only
/// the day-by-day trend reads from the <see cref="RfHeardDaily"/> archive, which exists so
/// the series outlives packet-retention pruning.
///
/// Everything is keyed by KISS channel rather than radio id, so history survives a radio
/// being renamed or replaced; the radio name is resolved at read time.
/// </summary>
[ApiController]
[Route("api/v0/rf-heard")]
public class RfHeardController(
    DireControlContext db,
    IOptions<DireControlOptions> options) : ControllerBase
{
    private const int MaxDays = 400;

    /// <summary>
    /// Per-radio, per-day direct reception over the last <paramref name="days"/> local days,
    /// from the archive. Days with no reception are returned as explicit zero rows so a chart
    /// draws a continuous line rather than joining across gaps.
    /// </summary>
    [HttpGet("daily")]
    public async Task<ActionResult<IReadOnlyList<RfHeardDailyDto>>> GetDaily(
        [FromQuery] int days = 30,
        [FromQuery] int? channel = null,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);

        var today = RfHeardLogic.LocalDay(DateTime.UtcNow);
        var from = today.AddDays(-(days - 1));

        var query = db.RfHeardDailies.AsNoTracking().Where(r => r.Day >= from && r.Day <= today);
        if (channel is { } ch)
            query = query.Where(r => r.ChannelNumber == ch);

        var rows = await query.ToListAsync(ct);
        var radios = await LoadRadiosByChannelAsync(ct);

        // Channels to draw: any with data in the window, plus every configured radio so a
        // newly added radio shows a flat line instead of nothing at all.
        var channels = rows.Select(r => r.ChannelNumber)
            .Concat(radios.Keys)
            .Distinct()
            .Where(c => channel is null || c == channel)
            .OrderBy(c => c)
            .ToList();

        var byKey = rows.ToDictionary(r => (r.ChannelNumber, r.Day));
        var result = new List<RfHeardDailyDto>(channels.Count * days);

        foreach (var ch2 in channels)
        {
            var (radioId, radioName) = ResolveRadio(radios, ch2);

            for (var day = from; day <= today; day = day.AddDays(1))
            {
                byKey.TryGetValue((ch2, day), out var row);

                result.Add(new RfHeardDailyDto
                {
                    Day = day,
                    ChannelNumber = ch2,
                    RadioId = radioId,
                    RadioName = radioName,
                    UniqueDirectStations = row?.UniqueDirectStations ?? 0,
                    NewDirectStations = row?.NewDirectStations ?? 0,
                    DirectPackets = row?.DirectPackets ?? 0,
                    MaxDirectDistanceKm = row?.MaxDirectDistanceKm,
                    MedianDirectDistanceKm = row?.MedianDirectDistanceKm,
                    DirectStationsWithPosition = row?.DirectStationsWithPosition ?? 0,
                });
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Stations heard directly on RF, most recently heard first — derived live from packets,
    /// so it is always exactly what is stored. Reaches back as far as packet retention keeps
    /// packets; the daily archive covers the trend beyond that.
    /// </summary>
    [HttpGet("stations")]
    public async Task<ActionResult<IReadOnlyList<RfHeardStationDto>>> GetStations(
        [FromQuery] int? channel = null,
        [FromQuery] int limit = 500,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 5000);

        var rows = await QueryHeardAsync(channel, ct);
        var radios = await LoadRadiosByChannelAsync(ct);
        var ourCallsign = options.Value.OurCallsign;

        var heard = rows
            .Where(r => !RfHeardLogic.IsOwnStation(r.Callsign, radios.Values, ourCallsign))
            .OrderByDescending(r => r.Last)
            .Take(limit)
            .ToList();

        var callsigns = heard.Select(r => r.Callsign).Distinct().ToList();

        var stations = await db.Stations
            .AsNoTracking()
            .Where(s => callsigns.Contains(s.Callsign))
            .Select(s => new { s.Callsign, s.Symbol, s.StationType, s.LastSeen, s.LastLat, s.LastLon })
            .ToDictionaryAsync(s => s.Callsign, ct);

        var result = heard.Select(r =>
        {
            var (radioId, radioName) = ResolveRadio(radios, r.Channel);
            stations.TryGetValue(r.Callsign, out var station);

            return new RfHeardStationDto
            {
                Callsign = r.Callsign,
                ChannelNumber = r.Channel,
                RadioId = radioId,
                RadioName = radioName,
                FirstHeardDirect = r.First,
                LastHeardDirect = r.Last,
                DirectPacketCount = r.Count,
                DistanceKm = DistanceFromHome(station?.LastLat, station?.LastLon),
                Symbol = station?.Symbol,
                StationType = station?.StationType ?? StationType.Unknown,
                LastSeen = station?.LastSeen,
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Headline figures per radio. Unique-station counts are distinct-station counts over
    /// each window — not sums of the daily rows, which would count a station once per day
    /// it appeared.
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<IReadOnlyList<RfHeardSummaryDto>>> GetSummary(
        CancellationToken ct = default)
    {
        var todayStartUtc = RfHeardLogic.LocalDayRangeUtc(RfHeardLogic.LocalDay(DateTime.UtcNow)).StartUtc;
        var week = DateTime.UtcNow.AddDays(-7);
        var month = DateTime.UtcNow.AddDays(-30);

        var radios = await LoadRadiosByChannelAsync(ct);
        var ourCallsign = options.Value.OurCallsign;

        var heard = (await QueryHeardAsync(channel: null, ct))
            .Where(r => !RfHeardLogic.IsOwnStation(r.Callsign, radios.Values, ourCallsign))
            .ToList();

        // Farthest ever is a property of the trend archive, not of the retained packets —
        // it must not shrink just because old packets were pruned.
        var archiveBest = await db.RfHeardDailies
            .AsNoTracking()
            .Where(r => r.MaxDirectDistanceKm != null)
            .GroupBy(r => r.ChannelNumber)
            .Select(g => new { Channel = g.Key, Best = g.Max(r => r.MaxDirectDistanceKm) })
            .ToDictionaryAsync(g => g.Channel, g => g.Best, ct);

        var channels = heard.Select(r => r.Channel)
            .Concat(radios.Keys)
            .Concat(archiveBest.Keys)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        var result = channels.Select(ch =>
        {
            var (radioId, radioName) = ResolveRadio(radios, ch);
            var forChannel = heard.Where(r => r.Channel == ch).ToList();

            return new RfHeardSummaryDto
            {
                ChannelNumber = ch,
                RadioId = radioId,
                RadioName = radioName,
                UniqueDirectToday = forChannel.Count(r => r.Last >= todayStartUtc),
                UniqueDirect7d = forChannel.Count(r => r.Last >= week),
                UniqueDirect30d = forChannel.Count(r => r.Last >= month),
                UniqueDirectAllTime = forChannel.Count,
                BestDistanceKm = archiveBest.GetValueOrDefault(ch),
                LastHeardDirect = forChannel.Count == 0 ? null : forChannel.Max(r => r.Last),
            };
        }).ToList();

        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // Shared queries
    // -------------------------------------------------------------------------

    private sealed record HeardRow(int Channel, string Callsign, DateTime First, DateTime Last, int Count);

    /// <summary>
    /// Direct-RF reception grouped per (channel, station). The grouping runs in SQL, so only
    /// one row per station reaches the app regardless of how many packets it sent.
    /// </summary>
    private async Task<List<HeardRow>> QueryHeardAsync(int? channel, CancellationToken ct)
    {
        var query = db.Packets
            .AsNoTracking()
            .Where(p => p.Source == PacketSource.Rf && p.HeardVia == HeardVia.Direct);

        if (channel is { } ch)
            query = query.Where(p => p.KissChannel == ch);

        return await query
            .GroupBy(p => new { p.KissChannel, p.StationCallsign })
            .Select(g => new HeardRow(
                g.Key.KissChannel,
                g.Key.StationCallsign,
                g.Min(p => p.ReceivedAt),
                g.Max(p => p.ReceivedAt),
                g.Count()))
            .ToListAsync(ct);
    }

    private double? DistanceFromHome(double? lat, double? lon)
    {
        if (options.Value.HomeLat is not { } hlat || options.Value.HomeLon is not { } hlon)
            return null;

        if (lat is not { } slat || lon is not { } slon)
            return null;

        return LightningAlertLogic.HaversineKm(hlat, hlon, slat, slon);
    }

    /// <summary>
    /// Maps KISS channel to the radio currently configured on it. A channel can legitimately
    /// have no radio — reception history outlives radio rows on purpose.
    /// </summary>
    private async Task<Dictionary<int, Radio>> LoadRadiosByChannelAsync(CancellationToken ct)
    {
        var radios = await db.Radios.AsNoTracking().ToListAsync(ct);

        // Two radios sharing a channel is a misconfiguration; prefer the active one.
        return radios
            .GroupBy(r => r.ChannelNumber)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.IsActive).First());
    }

    private static (string? RadioId, string RadioName) ResolveRadio(
        IReadOnlyDictionary<int, Radio> radios, int channel) =>
        radios.TryGetValue(channel, out var radio)
            ? (radio.Id, radio.Name)
            : (null, $"Channel {channel}");
}

using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/stations")]
public class StationsController(
    DireControlContext db,
    IOptions<DireControlOptions> options,
    CallsignLookupService lookupService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StationDto>>> GetActiveStations(
        [FromQuery] bool includeStale = false,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        var now = DateTime.UtcNow;

        IQueryable<DireControl.Data.Models.Station> query = db.Stations.AsNoTracking();

        if (!includeStale)
        {
            var mobileCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Mobile));
            var fixedCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Fixed));
            var weatherCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Weather));
            var digiCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Digipeater));
            var igateCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.IGate));
            var unknownCutoff = now.AddMinutes(-opts.GetExpiryMinutes(StationType.Unknown));

            query = query.Where(s =>
                (s.StationType == StationType.Mobile && s.LastSeen >= mobileCutoff) ||
                (s.StationType == StationType.Fixed && s.LastSeen >= fixedCutoff) ||
                (s.StationType == StationType.Weather && s.LastSeen >= weatherCutoff) ||
                (s.StationType == StationType.Digipeater && s.LastSeen >= digiCutoff) ||
                (s.StationType == StationType.IGate && s.LastSeen >= igateCutoff) ||
                (s.StationType == StationType.Unknown && s.LastSeen >= unknownCutoff));
        }

        var stations = await query
            .OrderByDescending(s => s.LastSeen)
            .Select(ToStationDto())
            .ToListAsync(ct);

        return Ok(stations);
    }

    [HttpGet("{callsign}")]
    public async Task<ActionResult<StationDto>> GetStation(string callsign, CancellationToken ct)
    {
        var station = await db.Stations
            .AsNoTracking()
            .Where(s => s.Callsign == callsign)
            .Select(ToStationDto())
            .FirstOrDefaultAsync(ct);

        return station is null ? NotFound() : Ok(station);
    }

    [HttpGet("{callsign}/packets")]
    public async Task<ActionResult<PaginatedResponse<PacketDto>>> GetStationPackets(
        string callsign,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1)
            page = 1;

        pageSize = Math.Clamp(pageSize, 1, 200);

        var stationExists = await db.Stations.AsNoTracking().AnyAsync(s => s.Callsign == callsign, ct);
        if (!stationExists)
            return NotFound();

        var query = db.Packets
            .AsNoTracking()
            .Where(p => p.StationCallsign == callsign)
            .OrderByDescending(p => p.ReceivedAt);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToPacketDto())
            .ToListAsync(ct);

        return Ok(new PaginatedResponse<PacketDto>
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Items = items
        });
    }

    [HttpGet("{callsign}/track")]
    public async Task<ActionResult<IReadOnlyList<TrackPointDto>>> GetStationTrack(
        string callsign,
        [FromQuery] int durationMinutes = 60,
        CancellationToken ct = default)
    {
        var stationExists = await db.Stations.AsNoTracking().AnyAsync(s => s.Callsign == callsign, ct);
        if (!stationExists)
            return NotFound();

        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Clamp(durationMinutes, 1, 43200));

        var points = await db.Packets
            .AsNoTracking()
            .Where(p => p.StationCallsign == callsign
                     && p.Latitude != null && p.Longitude != null
                     && p.ReceivedAt >= cutoff)
            .OrderBy(p => p.ReceivedAt)
            .Select(p => new TrackPointDto
            {
                Latitude = p.Latitude!.Value,
                Longitude = p.Longitude!.Value,
                ReceivedAt = p.ReceivedAt,
                Speed = null,
                Heading = null,
            })
            .ToListAsync(ct);

        return Ok(points);
    }

    [HttpGet("{callsign}/weather")]
    public async Task<ActionResult<IReadOnlyList<WeatherReadingDto>>> GetStationWeather(
        string callsign,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var stationExists = await db.Stations.AsNoTracking().AnyAsync(s => s.Callsign == callsign, ct);
        if (!stationExists)
            return NotFound();

        // WeatherData is a JSON value-converted column; project in-memory after fetching.
        var baseQuery = db.Packets
            .AsNoTracking()
            .Where(p => p.StationCallsign == callsign && p.WeatherData != null);

        if (from.HasValue || to.HasValue)
        {
            var fromUtc = from.HasValue
                ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)
                : DateTime.UtcNow.AddDays(-7);
            var toUtc = to.HasValue
                ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc)
                : DateTime.UtcNow;

            var rangeHours = (toUtc - fromUtc).TotalHours;
            var bucketMinutes = rangeHours <= 25 ? 5 : 60;

            var packets = await baseQuery
                .Where(p => p.ReceivedAt >= fromUtc && p.ReceivedAt <= toUtc)
                .OrderBy(p => p.ReceivedAt)
                .ToListAsync(ct);

            return Ok(BucketWeatherReadings(packets, bucketMinutes));
        }
        else
        {
            // Legacy: last 50 readings, newest-first.
            var packets = await baseQuery
                .OrderByDescending(p => p.ReceivedAt)
                .Take(50)
                .ToListAsync(ct);

            var readings = packets.Select(p => new WeatherReadingDto
            {
                ReceivedAt = p.ReceivedAt,
                Temperature = p.WeatherData!.TemperatureF,
                Humidity = p.WeatherData.HumidityPercent,
                WindSpeed = p.WeatherData.WindSpeedMph,
                WindDirection = p.WeatherData.WindDirectionDeg,
                WindGust = p.WeatherData.WindGustMph,
                Pressure = p.WeatherData.PressureMbar,
                RainLastHour = p.WeatherData.RainfallLastHourIn,
                RainLast24h = p.WeatherData.RainfallLast24hIn,
                RainSinceMidnight = p.WeatherData.RainfallSinceMidnightIn,
            }).ToList();

            return Ok(readings);
        }
    }

    private static IReadOnlyList<WeatherReadingDto> BucketWeatherReadings(
        List<DireControl.Data.Models.Packet> packets,
        int bucketMinutes)
    {
        var epoch = DateTime.UnixEpoch;

        var groups = packets
            .GroupBy(p =>
            {
                var totalMinutes = (long)(p.ReceivedAt - epoch).TotalMinutes;
                var bucket = totalMinutes / bucketMinutes * bucketMinutes;
                return epoch.AddMinutes(bucket);
            })
            .OrderBy(g => g.Key);

        return groups.Select(g =>
        {
            var wx = g.Select(p => p.WeatherData!).ToList();

            var temps = wx.Where(w => w.TemperatureF.HasValue).Select(w => w.TemperatureF!.Value).ToList();
            var winds = wx.Where(w => w.WindSpeedMph.HasValue).Select(w => w.WindSpeedMph!.Value).ToList();
            var gusts = wx.Where(w => w.WindGustMph.HasValue).Select(w => w.WindGustMph!.Value).ToList();
            var humidities = wx.Where(w => w.HumidityPercent.HasValue).Select(w => (double)w.HumidityPercent!.Value).ToList();
            var pressures = wx.Where(w => w.PressureMbar.HasValue).Select(w => w.PressureMbar!.Value).ToList();
            var rainHour = wx.Where(w => w.RainfallLastHourIn.HasValue).Select(w => w.RainfallLastHourIn!.Value).ToList();
            var rain24h = wx.Where(w => w.RainfallLast24hIn.HasValue).Select(w => w.RainfallLast24hIn!.Value).ToList();
            var rainMidnight = wx.Where(w => w.RainfallSinceMidnightIn.HasValue).Select(w => w.RainfallSinceMidnightIn!.Value).ToList();
            var windDirs = wx.Where(w => w.WindDirectionDeg.HasValue).Select(w => w.WindDirectionDeg!.Value).ToList();

            return new WeatherReadingDto
            {
                ReceivedAt = g.Key,
                Temperature = temps.Count > 0 ? temps.Average() : null,
                WindSpeed = winds.Count > 0 ? winds.Average() : null,
                WindGust = gusts.Count > 0 ? gusts.Average() : null,
                Humidity = humidities.Count > 0 ? (int)Math.Round(humidities.Average()) : null,
                Pressure = pressures.Count > 0 ? pressures.Average() : null,
                RainLastHour = rainHour.Count > 0 ? rainHour.Average() : null,
                RainLast24h = rain24h.Count > 0 ? rain24h.Average() : null,
                RainSinceMidnight = rainMidnight.Count > 0 ? rainMidnight.Average() : null,
                WindDirection = windDirs.Count > 0 ? CircularMeanDegrees(windDirs) : null,
            };
        }).ToList();
    }

    private static int? CircularMeanDegrees(List<int> degrees)
    {
        if (degrees.Count == 0) return null;
        var sinSum = degrees.Sum(d => Math.Sin(d * Math.PI / 180.0));
        var cosSum = degrees.Sum(d => Math.Cos(d * Math.PI / 180.0));
        var mean = Math.Atan2(sinSum, cosSum) * 180.0 / Math.PI;
        return (int)Math.Round(mean < 0 ? mean + 360.0 : mean);
    }

    [HttpGet("watchlist")]
    public async Task<ActionResult<IReadOnlyList<StationDto>>> GetWatchList(CancellationToken ct)
    {
        var stations = await db.Stations
            .AsNoTracking()
            .Where(s => s.IsOnWatchList)
            .OrderBy(s => s.Callsign)
            .Select(ToStationDto())
            .ToListAsync(ct);

        return Ok(stations);
    }

    [HttpPut("{callsign}/watch")]
    public async Task<IActionResult> ToggleWatch(string callsign, CancellationToken ct)
    {
        var station = await db.Stations.FirstOrDefaultAsync(s => s.Callsign == callsign, ct);
        if (station is null) return NotFound();

        station.IsOnWatchList = !station.IsOnWatchList;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{callsign}/lookup")]
    public async Task<ActionResult<CallsignLookupDto>> LookupCallsign(string callsign, CancellationToken ct)
    {
        if (!CallsignLookupService.IsValidCallsign(callsign))
            return BadRequest("Not a recognised amateur radio callsign format.");

        var result = await lookupService.LookupAsync(callsign, ct);
        if (result is null)
            return NotFound();

        return Ok(new CallsignLookupDto
        {
            Name = result.Name,
            City = result.City,
            State = result.State,
            LicenseClass = result.LicenseClass,
            GridSquare = result.GridSquare,
        });
    }

    [HttpGet("{callsign}/signal")]
    public async Task<ActionResult<IReadOnlyList<SignalPointDto>>> GetStationSignal(
        string callsign,
        CancellationToken ct)
    {
        var stationExists = await db.Stations.AsNoTracking().AnyAsync(s => s.Callsign == callsign, ct);
        if (!stationExists)
            return NotFound();

        // SignalData is null for all packets received via KISS TCP (Direwolf does not
        // expose signal metadata over the KISS interface).  This endpoint returns an
        // empty array in that case, which the frontend treats as "not available".
        var packets = await db.Packets
            .AsNoTracking()
            .Where(p => p.StationCallsign == callsign && p.SignalData != null)
            .OrderBy(p => p.ReceivedAt)
            .ToListAsync(ct);

        var points = packets.Select(p => new SignalPointDto
        {
            ReceivedAt = p.ReceivedAt,
            DecodeQuality = p.SignalData!.DecodeQuality,
            FrequencyOffsetHz = p.SignalData.FrequencyOffsetHz,
        }).ToList();

        return Ok(points);
    }

    [HttpGet("{callsign}/stats")]
    public async Task<ActionResult<StationStatisticDto>> GetStationStats(string callsign, CancellationToken ct)
    {
        var stationExists = await db.Stations.AsNoTracking().AnyAsync(s => s.Callsign == callsign, ct);
        if (!stationExists)
            return NotFound();

        var stat = await db.StationStatistics
            .AsNoTracking()
            .FirstOrDefaultAsync(ss => ss.Callsign == callsign, ct);

        var totalPackets = await db.Packets
            .CountAsync(p => p.StationCallsign == callsign, ct);

        // Build hourly histogram using grouped counts instead of loading all timestamps.
        var now = DateTime.UtcNow;
        var h24Start = now.AddHours(-24);

        var hourBuckets = await db.Packets
            .Where(p => p.StationCallsign == callsign && p.ReceivedAt >= h24Start)
            .GroupBy(p => (int)((now.Ticks - p.ReceivedAt.Ticks) / TimeSpan.TicksPerHour))
            .Select(g => new { HoursAgo = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var packetsPerHour = new int[24];
        foreach (var bucket in hourBuckets)
        {
            if (bucket.HoursAgo >= 0 && bucket.HoursAgo < 24)
                packetsPerHour[23 - bucket.HoursAgo] = bucket.Count;
        }

        return Ok(new StationStatisticDto
        {
            PacketsToday = stat?.PacketsToday ?? 0,
            PacketsAllTime = totalPackets,
            AveragePacketsPerHour = stat?.AveragePacketsPerHour ?? 0.0,
            LongestGapMinutes = stat?.LongestGapMinutes ?? 0,
            PacketsPerHour = packetsPerHour,
        });
    }

    private static System.Linq.Expressions.Expression<Func<DireControl.Data.Models.Station, StationDto>> ToStationDto() => s => new StationDto
    {
        Callsign = s.Callsign,
        FirstSeen = s.FirstSeen,
        LastSeen = s.LastSeen,
        LastLat = s.LastLat,
        LastLon = s.LastLon,
        LastHeading = s.LastHeading,
        LastSpeed = s.LastSpeed,
        LastAltitude = s.LastAltitude,
        Symbol = s.Symbol,
        Status = s.Status,
        IsWeatherStation = s.IsWeatherStation,
        StationType = s.StationType,
        QrzLookupData = s.QrzLookupData,
        IsOnWatchList = s.IsOnWatchList,
        GridSquare = s.GridSquare,
        HeardVia = s.HeardVia,
        LastHeardRf = s.LastHeardRf,
        LastHeardAprsIs = s.LastHeardAprsIs,
    };

    private static System.Linq.Expressions.Expression<Func<DireControl.Data.Models.Packet, PacketDto>> ToPacketDto() => p => new PacketDto
    {
        Id = p.Id,
        StationCallsign = p.StationCallsign,
        ReceivedAt = p.ReceivedAt,
        RawPacket = p.RawPacket,
        ParsedType = p.ParsedType,
        Source = p.Source,
        Latitude = p.Latitude,
        Longitude = p.Longitude,
        Path = p.Path,
        ResolvedPath = p.ResolvedPath,
        HopCount = p.HopCount,
        UnknownHopCount = p.UnknownHopCount,
        IsDirectHeard = p.HopCount == 0,
        Comment = p.Comment,
        WeatherData = p.WeatherData,
        TelemetryData = p.TelemetryData,
        MessageData = p.MessageData,
        SignalData = p.SignalData,
        GridSquare = p.GridSquare,
    };
}

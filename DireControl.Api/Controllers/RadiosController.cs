using DireControl.Api.Controllers.Models;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/radios")]
public class RadiosController(DireControlContext db) : ControllerBase
{
    // ─── List ──────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RadioDto>>> GetRadios(CancellationToken ct)
    {
        var radios = await db.Radios
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var ids = radios.Select(r => r.Id).ToList();

        // Most recent OwnBeacon per radio
        var lastBeacons = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => ids.Contains(b.RadioId))
            .GroupBy(b => b.RadioId)
            .Select(g => new
            {
                RadioId = g.Key,
                BeaconedAt = g.Max(b => b.BeaconedAt),
            })
            .ToListAsync(ct);

        // Total confirmation count per radio (via join)
        var confirmCounts = await db.DigiConfirmations
            .AsNoTracking()
            .Join(db.OwnBeacons.Where(b => ids.Contains(b.RadioId)),
                  c => c.OwnBeaconId,
                  b => b.Id,
                  (c, b) => b.RadioId)
            .GroupBy(radioId => radioId)
            .Select(g => new { RadioId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Beacon count per radio
        var beaconCounts = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => ids.Contains(b.RadioId))
            .GroupBy(b => b.RadioId)
            .Select(g => new { RadioId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var lastBeaconMap = lastBeacons.ToDictionary(x => x.RadioId, x => x.BeaconedAt);
        var confirmMap = confirmCounts.ToDictionary(x => x.RadioId, x => x.Count);
        var beaconMap = beaconCounts.ToDictionary(x => x.RadioId, x => x.Count);
        var now = DateTime.UtcNow;

        return Ok(radios.Select(r => ToDto(r, lastBeaconMap, confirmMap, beaconMap, now)));
    }

    // ─── Create ────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<ActionResult<RadioDto>> CreateRadio(
        [FromBody] CreateRadioRequest request,
        CancellationToken ct)
    {
        var radio = new Radio
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Callsign = request.Callsign.Trim().ToUpperInvariant(),
            Ssid = string.IsNullOrWhiteSpace(request.Ssid) ? null : request.Ssid.Trim(),
            ChannelNumber = request.ChannelNumber,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpectedIntervalSeconds = request.ExpectedIntervalSeconds > 0 ? request.ExpectedIntervalSeconds : 600,
        };

        radio.FullCallsign = Radio.ComputeFullCallsign(radio.Callsign, radio.Ssid);

        db.Radios.Add(radio);
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        return CreatedAtAction(nameof(GetRadio), new { id = radio.Id },
            ToDto(radio, [], [], [], now));
    }

    // ─── Single ────────────────────────────────────────────────────────────────

    [HttpGet("{id}")]
    public async Task<ActionResult<RadioDto>> GetRadio(string id, CancellationToken ct)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        var lastBeacon = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .Select(b => (DateTime?)b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        var confirmCount = await db.DigiConfirmations
            .AsNoTracking()
            .Join(db.OwnBeacons.Where(b => b.RadioId == id),
                  c => c.OwnBeaconId,
                  b => b.Id,
                  (c, b) => c.Id)
            .CountAsync(ct);

        var beaconCount = await db.OwnBeacons.CountAsync(b => b.RadioId == id, ct);

        var now = DateTime.UtcNow;
        var lastBeaconMap = lastBeacon.HasValue
            ? new Dictionary<string, DateTime> { [id] = lastBeacon.Value }
            : new Dictionary<string, DateTime>();
        var confirmMap = new Dictionary<string, int> { [id] = confirmCount };
        var beaconMap = new Dictionary<string, int> { [id] = beaconCount };

        return Ok(ToDto(radio, lastBeaconMap, confirmMap, beaconMap, now));
    }

    // ─── Update ────────────────────────────────────────────────────────────────

    [HttpPut("{id}")]
    public async Task<ActionResult<RadioDto>> UpdateRadio(
        string id,
        [FromBody] UpdateRadioRequest request,
        CancellationToken ct)
    {
        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        radio.Name = request.Name.Trim();
        radio.Callsign = request.Callsign.Trim().ToUpperInvariant();
        radio.Ssid = string.IsNullOrWhiteSpace(request.Ssid) ? null : request.Ssid.Trim();
        radio.ChannelNumber = request.ChannelNumber;
        radio.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        radio.ExpectedIntervalSeconds = request.ExpectedIntervalSeconds > 0 ? request.ExpectedIntervalSeconds : 600;
        radio.FullCallsign = Radio.ComputeFullCallsign(radio.Callsign, radio.Ssid);

        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var lastBeacon = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .Select(b => (DateTime?)b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        var lastBeaconMap = lastBeacon.HasValue
            ? new Dictionary<string, DateTime> { [id] = lastBeacon.Value }
            : new Dictionary<string, DateTime>();

        return Ok(ToDto(radio, lastBeaconMap, [], [], now));
    }

    // ─── Delete ────────────────────────────────────────────────────────────────

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRadio(string id, CancellationToken ct)
    {
        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        db.Radios.Remove(radio);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ─── Toggle active ─────────────────────────────────────────────────────────

    [HttpPatch("{id}/active")]
    public async Task<ActionResult<RadioDto>> ToggleActive(string id, CancellationToken ct)
    {
        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        radio.IsActive = !radio.IsActive;
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        return Ok(ToDto(radio, [], [], [], now));
    }

    // ─── Last beacon ───────────────────────────────────────────────────────────

    [HttpGet("{id}/lastbeacon")]
    public async Task<ActionResult<LastBeaconDto>> GetLastBeacon(string id, CancellationToken ct)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        var beacon = await db.OwnBeacons
            .AsNoTracking()
            .Include(b => b.Confirmations)
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        return Ok(new LastBeaconDto
        {
            RadioId = radio.Id,
            RadioName = radio.Name,
            FullCallsign = radio.FullCallsign,
            BeaconedAt = beacon?.BeaconedAt,
            SecondsSinceBeacon = beacon is not null ? (int)(now - beacon.BeaconedAt).TotalSeconds : null,
            Latitude = beacon?.Latitude,
            Longitude = beacon?.Longitude,
            PathUsed = beacon?.PathUsed,
            Comment = beacon?.Comment,
            Confirmations = beacon?.Confirmations
                .OrderBy(c => c.SecondsAfterBeacon)
                .Select(c => new DigiConfirmationDto
                {
                    Digipeater = c.DigipeaterCallsign,
                    ConfirmedAt = c.ConfirmedAt,
                    SecondsAfterBeacon = c.SecondsAfterBeacon,
                    Lat = c.DigipeaterLat,
                    Lon = c.DigipeaterLon,
                    AliasUsed = c.AliasUsed,
                })
                .ToList() ?? [],
        });
    }

    // ─── Beacon history ────────────────────────────────────────────────────────

    [HttpGet("{id}/beaconhistory")]
    public async Task<ActionResult<IReadOnlyList<OwnBeaconHistoryItemDto>>> GetBeaconHistory(
        string id,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        var capped = Math.Clamp(limit, 1, 200);

        var beacons = await db.OwnBeacons
            .AsNoTracking()
            .Include(b => b.Confirmations)
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .Take(capped)
            .ToListAsync(ct);

        return Ok(beacons.Select(b => new OwnBeaconHistoryItemDto
        {
            Id = b.Id,
            BeaconedAt = b.BeaconedAt,
            Latitude = b.Latitude,
            Longitude = b.Longitude,
            PathUsed = b.PathUsed,
            HopCount = b.HopCount,
            Confirmations = b.Confirmations
                .OrderBy(c => c.SecondsAfterBeacon)
                .Select(c => new DigiConfirmationDto
                {
                    Digipeater = c.DigipeaterCallsign,
                    ConfirmedAt = c.ConfirmedAt,
                    SecondsAfterBeacon = c.SecondsAfterBeacon,
                    Lat = c.DigipeaterLat,
                    Lon = c.DigipeaterLon,
                    AliasUsed = c.AliasUsed,
                })
                .ToList(),
        }));
    }

    // ─── Helper ────────────────────────────────────────────────────────────────

    private static RadioDto ToDto(
        Radio radio,
        Dictionary<string, DateTime> lastBeaconMap,
        Dictionary<string, int> confirmMap,
        Dictionary<string, int> beaconMap,
        DateTime now)
    {
        lastBeaconMap.TryGetValue(radio.Id, out var lastBeaconedAt);
        return new RadioDto
        {
            Id = radio.Id,
            Name = radio.Name,
            Callsign = radio.Callsign,
            Ssid = radio.Ssid,
            FullCallsign = radio.FullCallsign,
            ChannelNumber = radio.ChannelNumber,
            Notes = radio.Notes,
            IsActive = radio.IsActive,
            ExpectedIntervalSeconds = radio.ExpectedIntervalSeconds,
            LastBeaconedAt = lastBeaconMap.ContainsKey(radio.Id) ? lastBeaconedAt : null,
            SecondsSinceBeacon = lastBeaconMap.ContainsKey(radio.Id)
                ? (int)(now - lastBeaconedAt).TotalSeconds
                : null,
            ConfirmationCount = confirmMap.GetValueOrDefault(radio.Id),
            BeaconCount = beaconMap.GetValueOrDefault(radio.Id),
        };
    }
}

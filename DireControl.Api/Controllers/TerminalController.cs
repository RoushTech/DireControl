using DireControl.Api.Controllers.Models;
using DireControl.Api.Services.Terminal;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

/// <summary>
/// Terminal session lifecycle plus transcript, preset, and macro management.
/// The byte streams themselves ride the SignalR terminal hub, not this API.
/// </summary>
[ApiController]
[Route("api/v0/terminal")]
public class TerminalController(
    TerminalSessionService terminals,
    DireControlContext db) : ControllerBase
{
    // ── Sessions ─────────────────────────────────────────────────────────────

    [HttpGet("sessions")]
    public ActionResult<IReadOnlyList<TerminalSessionDto>> ListSessions() =>
        Ok(terminals.ListSessions());

    [HttpPost("sessions")]
    public async Task<ActionResult<TerminalSessionDto>> OpenSession(
        [FromBody] OpenTerminalSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RemoteCallsign))
            return BadRequest("Remote callsign is required.");
        if (request.PacLen is < 16 or > 256)
            return BadRequest("Paclen must be between 16 and 256.");
        if (request.WindowSize is < 1 or > 63)
            return BadRequest("Window size must be between 1 and 63.");
        if (request.MaxRetries is < 1 or > 30)
            return BadRequest("Retry limit must be between 1 and 30.");
        if (request.T1Seconds is < 1 or > 60)
            return BadRequest("T1 must be between 1 and 60 seconds.");

        try
        {
            return Ok(await terminals.OpenAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("sessions/local-pms")]
    public async Task<ActionResult<TerminalSessionDto>> OpenLocalPms(CancellationToken ct)
    {
        var session = await terminals.OpenLocalPmsAsync(ct);
        return session is null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, "The PMS is not enabled.")
            : Ok(session);
    }

    [HttpDelete("sessions/{id}")]
    public async Task<ActionResult> CloseSession(string id, [FromQuery] bool abort, CancellationToken ct) =>
        await terminals.CloseAsync(id, abort, ct) ? NoContent() : NotFound();

    // ── Transcripts ──────────────────────────────────────────────────────────

    [HttpGet("transcripts")]
    public async Task<ActionResult<IReadOnlyList<TerminalTranscriptSummaryDto>>> ListTranscripts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? callsign = null,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.TerminalSessionRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            var needle = callsign.Trim().ToUpperInvariant();
            query = query.Where(r => r.RemoteCallsign.StartsWith(needle));
        }

        var records = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(records.Select(ToSummary).ToList());
    }

    [HttpGet("transcripts/{id:int}")]
    public async Task<ActionResult<TerminalTranscriptDetailDto>> GetTranscript(int id, CancellationToken ct)
    {
        var record = await db.TerminalSessionRecords
            .AsNoTracking()
            .Include(r => r.Chunks.OrderBy(c => c.Id))
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
            return NotFound();

        return Ok(new TerminalTranscriptDetailDto
        {
            Summary = ToSummary(record),
            Chunks = record.Chunks.Select(c => new TerminalTranscriptChunkDto
            {
                Timestamp = c.Timestamp,
                Direction = c.Direction,
                DataBase64 = Convert.ToBase64String(c.Data),
            }).ToList(),
        });
    }

    [HttpDelete("transcripts/{id:int}")]
    public async Task<ActionResult> DeleteTranscript(int id, CancellationToken ct)
    {
        var record = await db.TerminalSessionRecords.FindAsync([id], ct);
        if (record is null)
            return NotFound();
        db.TerminalSessionRecords.Remove(record); // chunks cascade
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Presets ──────────────────────────────────────────────────────────────

    [HttpGet("presets")]
    public async Task<ActionResult<IReadOnlyList<TerminalPresetDto>>> ListPresets(CancellationToken ct)
    {
        var presets = await db.TerminalPresets
            .AsNoTracking()
            .OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.LastUsedAt)
            .ToListAsync(ct);
        return Ok(presets.Select(ToPresetDto).ToList());
    }

    [HttpPost("presets")]
    public async Task<ActionResult<TerminalPresetDto>> CreatePreset(
        [FromBody] SaveTerminalPresetRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RemoteCallsign))
            return BadRequest("Remote callsign is required.");

        var preset = new TerminalPreset
        {
            RemoteCallsign = request.RemoteCallsign.Trim().ToUpperInvariant(),
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };
        ApplyPreset(preset, request);
        db.TerminalPresets.Add(preset);
        await db.SaveChangesAsync(ct);
        return Ok(ToPresetDto(preset));
    }

    [HttpPut("presets/{id:int}")]
    public async Task<ActionResult<TerminalPresetDto>> UpdatePreset(
        int id, [FromBody] SaveTerminalPresetRequest request, CancellationToken ct)
    {
        var preset = await db.TerminalPresets.FindAsync([id], ct);
        if (preset is null)
            return NotFound();
        if (!string.IsNullOrWhiteSpace(request.RemoteCallsign))
            preset.RemoteCallsign = request.RemoteCallsign.Trim().ToUpperInvariant();
        ApplyPreset(preset, request);
        await db.SaveChangesAsync(ct);
        return Ok(ToPresetDto(preset));
    }

    [HttpDelete("presets/{id:int}")]
    public async Task<ActionResult> DeletePreset(int id, CancellationToken ct)
    {
        var preset = await db.TerminalPresets.FindAsync([id], ct);
        if (preset is null)
            return NotFound();
        db.TerminalPresets.Remove(preset);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Macros ───────────────────────────────────────────────────────────────

    [HttpGet("macros")]
    public async Task<ActionResult<IReadOnlyList<TerminalMacroDto>>> ListMacros(CancellationToken ct)
    {
        var macros = await db.TerminalMacros
            .AsNoTracking()
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
        return Ok(macros.Select(ToMacroDto).ToList());
    }

    [HttpPost("macros")]
    public async Task<ActionResult<TerminalMacroDto>> CreateMacro(
        [FromBody] SaveTerminalMacroRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest("Macro label is required.");
        if (!IsValidBase64(request.PayloadBase64))
            return BadRequest("Macro payload must be valid base64.");

        var macro = new TerminalMacro
        {
            Label = request.Label.Trim(),
            SortOrder = request.SortOrder,
            PayloadBase64 = request.PayloadBase64,
            AppendCr = request.AppendCr,
            CreatedAt = DateTime.UtcNow,
        };
        db.TerminalMacros.Add(macro);
        await db.SaveChangesAsync(ct);
        return Ok(ToMacroDto(macro));
    }

    [HttpPut("macros/{id:int}")]
    public async Task<ActionResult<TerminalMacroDto>> UpdateMacro(
        int id, [FromBody] SaveTerminalMacroRequest request, CancellationToken ct)
    {
        var macro = await db.TerminalMacros.FindAsync([id], ct);
        if (macro is null)
            return NotFound();
        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest("Macro label is required.");
        if (!IsValidBase64(request.PayloadBase64))
            return BadRequest("Macro payload must be valid base64.");

        macro.Label = request.Label.Trim();
        macro.SortOrder = request.SortOrder;
        macro.PayloadBase64 = request.PayloadBase64;
        macro.AppendCr = request.AppendCr;
        await db.SaveChangesAsync(ct);
        return Ok(ToMacroDto(macro));
    }

    [HttpDelete("macros/{id:int}")]
    public async Task<ActionResult> DeleteMacro(int id, CancellationToken ct)
    {
        var macro = await db.TerminalMacros.FindAsync([id], ct);
        if (macro is null)
            return NotFound();
        db.TerminalMacros.Remove(macro);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static void ApplyPreset(TerminalPreset preset, SaveTerminalPresetRequest request)
    {
        preset.Name = string.IsNullOrWhiteSpace(request.Name)
            ? preset.RemoteCallsign
            : request.Name.Trim();
        preset.Channel = request.Channel;
        preset.DigiPath = request.DigiPath?.Trim().ToUpperInvariant() ?? string.Empty;
        preset.LocalCallsign = string.IsNullOrWhiteSpace(request.LocalCallsign)
            ? null
            : request.LocalCallsign.Trim().ToUpperInvariant();
        preset.IsPinned = request.IsPinned;
        preset.Params = new TerminalLapbParams
        {
            Mod128 = request.Mod128,
            PacLen = request.PacLen,
            WindowSize = request.WindowSize,
            MaxRetries = request.MaxRetries,
            T1Seconds = request.T1Seconds,
        };
    }

    private static TerminalTranscriptSummaryDto ToSummary(TerminalSessionRecord r) => new()
    {
        Id = r.Id,
        SessionId = r.SessionId,
        Origin = r.Origin,
        Channel = r.Channel,
        LocalCallsign = r.LocalCallsign,
        RemoteCallsign = r.RemoteCallsign,
        DigiPath = r.DigiPath,
        StartedAt = r.StartedAt,
        EndedAt = r.EndedAt,
        EndReason = r.EndReason,
        BytesIn = r.BytesIn,
        BytesOut = r.BytesOut,
    };

    private static TerminalPresetDto ToPresetDto(TerminalPreset p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        RemoteCallsign = p.RemoteCallsign,
        Channel = p.Channel,
        DigiPath = p.DigiPath,
        LocalCallsign = p.LocalCallsign,
        IsPinned = p.IsPinned,
        LastUsedAt = p.LastUsedAt,
        UseCount = p.UseCount,
        Mod128 = p.Params?.Mod128,
        PacLen = p.Params?.PacLen,
        WindowSize = p.Params?.WindowSize,
        MaxRetries = p.Params?.MaxRetries,
        T1Seconds = p.Params?.T1Seconds,
    };

    private static TerminalMacroDto ToMacroDto(TerminalMacro m) => new()
    {
        Id = m.Id,
        Label = m.Label,
        SortOrder = m.SortOrder,
        PayloadBase64 = m.PayloadBase64,
        AppendCr = m.AppendCr,
    };

    private static bool IsValidBase64(string value)
    {
        var buffer = new Span<byte>(new byte[value.Length]);
        return Convert.TryFromBase64String(value, buffer, out _);
    }
}

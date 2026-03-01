using DireControl.Api.Controllers.Models;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/proximityrules")]
public class ProximityRulesController(DireControlContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProximityRuleDto>>> GetRules(CancellationToken ct)
    {
        var rules = await db.ProximityRules
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new ProximityRuleDto
            {
                Id = r.Id,
                Name = r.Name,
                TargetCallsign = r.TargetCallsign,
                CenterLat = r.CenterLat,
                CenterLon = r.CenterLon,
                RadiusMetres = r.RadiusMetres,
                IsActive = r.IsActive,
            })
            .ToListAsync(ct);

        return Ok(rules);
    }

    [HttpPost]
    public async Task<ActionResult<ProximityRuleDto>> CreateRule(
        [FromBody] CreateProximityRuleRequest request,
        CancellationToken ct)
    {
        var rule = new ProximityRule
        {
            Name = request.Name,
            TargetCallsign = string.IsNullOrWhiteSpace(request.TargetCallsign)
                ? null
                : request.TargetCallsign.Trim().ToUpperInvariant(),
            CenterLat = request.CenterLat,
            CenterLon = request.CenterLon,
            RadiusMetres = request.RadiusMetres,
            IsActive = true,
        };

        db.ProximityRules.Add(rule);
        await db.SaveChangesAsync(ct);

        var dto = new ProximityRuleDto
        {
            Id = rule.Id,
            Name = rule.Name,
            TargetCallsign = rule.TargetCallsign,
            CenterLat = rule.CenterLat,
            CenterLon = rule.CenterLon,
            RadiusMetres = rule.RadiusMetres,
            IsActive = rule.IsActive,
        };

        return CreatedAtAction(nameof(GetRules), dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteRule(int id, CancellationToken ct)
    {
        var rule = await db.ProximityRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();

        db.ProximityRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

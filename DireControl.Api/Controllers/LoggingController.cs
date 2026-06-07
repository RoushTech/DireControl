using DireControl.Api.Controllers.Models;
using DireControl.Api.Logging;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/logging")]
public class LoggingController(LogLevelService logLevels) : ControllerBase
{
    [HttpGet("levels")]
    public async Task<ActionResult<LogLevelsResponse>> GetLevels(CancellationToken ct)
    {
        var overrides = await logLevels.GetOverridesAsync(ct);

        return Ok(new LogLevelsResponse
        {
            Overrides = overrides
                .Select(kv => new LogLevelDto { Category = kv.Key, Level = kv.Value })
                .OrderBy(o => o.Category)
                .ToList(),
            AvailableLevels = LogLevelService.AvailableLevels,
            CommonCategories = LogLevelService.CommonCategories,
        });
    }

    [HttpPut("levels")]
    public async Task<ActionResult> SetLevel(
        [FromBody] UpdateLogLevelRequest request,
        CancellationToken ct)
    {
        var category = request.Category?.Trim();
        if (string.IsNullOrEmpty(category))
            return BadRequest("Category is required.");

        var level = string.IsNullOrWhiteSpace(request.Level) ? null : request.Level.Trim();
        if (level is not null && !LogLevelService.IsValidLevel(level))
            return BadRequest($"Invalid level '{level}'. Valid levels: {string.Join(", ", LogLevelService.AvailableLevels)}.");

        await logLevels.SetLevelAsync(category, level, ct);
        return NoContent();
    }
}

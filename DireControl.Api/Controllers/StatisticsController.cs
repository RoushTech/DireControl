using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/statistics")]
public class StatisticsController(StatisticsService statisticsService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<StatisticsDto>> GetStatistics(CancellationToken ct)
    {
        var stats = await statisticsService.GetStatisticsAsync(ct);
        return Ok(stats);
    }
}

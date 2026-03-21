using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/statistics")]
public class StatisticsController(StatisticsService statisticsService) : ControllerBase
{
    [HttpGet]
    public ActionResult<StatisticsDto> GetStatistics()
    {
        var stats = statisticsService.GetStatistics();
        return Ok(stats);
    }
}

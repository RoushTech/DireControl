using DireControl.Data;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HealthController(DireControlContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var canConnect = await db.Database.CanConnectAsync();
        return canConnect
            ? Ok(new { status = "healthy", database = "connected" })
            : Problem("Database connection check failed.");
    }
}

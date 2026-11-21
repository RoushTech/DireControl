using DireControl.Api.Services;
using DireControl.Api.Controllers.Models;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/about")]
public class AboutController : ControllerBase
{
    [HttpGet]
    public ActionResult<AboutDto> Get()
    {
        // Same version string the beacons transmit; the build's commit (the
        // "+sha" metadata) is reported separately rather than glued on.
        var informational = typeof(AboutController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var plus = informational?.IndexOf('+') ?? -1;

        return Ok(new AboutDto
        {
            Version = BeaconService.ResolveVersion(informational),
            Commit = plus >= 0 ? informational![(plus + 1)..] : null,
            ServerTime = DateTime.UtcNow,
        });
    }
}

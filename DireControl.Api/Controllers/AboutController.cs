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
        var version = typeof(AboutController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        return Ok(new AboutDto { Version = version });
    }
}

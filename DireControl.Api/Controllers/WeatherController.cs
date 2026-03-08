using DireControl.Api.Controllers.Models;
using DireControl.Api.Services.Weather;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/weather")]
public class WeatherController(
    RadarCache radarCache,
    WindTileCache windTileCache,
    LightningCache lightningCache,
    DireControlContext db,
    ILogger<WeatherController> logger) : ControllerBase
{
    // 1×1 fully-transparent PNG returned when an upstream tile provider rejects a
    // zoom level, so Leaflet renders nothing rather than a broken-image tile.
    private static readonly byte[] TransparentTile = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    // ── Radar (RainViewer) ──────────────────────────────────────────────────

    [HttpGet("radar/manifest")]
    public ActionResult<WeatherManifestDto> GetRadarManifest()
    {
        var (manifest, _) = radarCache.GetManifest();
        if (manifest is null)
            return StatusCode(503, "Radar manifest not yet available.");

        return Ok(new WeatherManifestDto
        {
            Generated = manifest.Generated,
            Radar = new WeatherRadarManifestDto
            {
                Past = manifest.Radar.Past
                    .Select(f => new WeatherFrameDto { Time = f.Time, Path = f.Path })
                    .ToList(),
                Nowcast = manifest.Radar.Nowcast
                    .Select(f => new WeatherFrameDto { Time = f.Time, Path = f.Path })
                    .ToList(),
            },
        });
    }

    // RainViewer free tier only provides tiles up to zoom 6.
    private const int RadarMaxNativeZoom = 6;

    // Route: /api/weather/radar/tile/{z}/{x}/{y}/{**framePath}
    // framePath captures the RainViewer frame path (e.g. "v2/radar/1620046800")
    // with the leading slash already stripped by the route pattern.
    [HttpGet("radar/tile/{z:int}/{x:int}/{y:int}/{**framePath}")]
    public async Task<IActionResult> GetRadarTile(int z, int x, int y, string framePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(framePath))
            return BadRequest("framePath is required.");

        // Cap zoom to the free-tier maximum; shift x/y to the containing tile at that zoom.
        if (z > RadarMaxNativeZoom)
        {
            var shift = z - RadarMaxNativeZoom;
            x >>= shift;
            y >>= shift;
            z = RadarMaxNativeZoom;
        }

        byte[]? data;
        try
        {
            data = await radarCache.GetTileAsync(framePath, z, x, y, ct);
        }
        catch (HttpRequestException)
        {
            // Upstream rejected the tile (e.g. zoom level not supported by RainViewer).
            // Return a transparent tile so Leaflet renders nothing here.
            return File(TransparentTile, "image/png");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch radar tile {FramePath}/{Z}/{X}/{Y}", framePath, z, x, y);
            return StatusCode(502, "Failed to fetch radar tile from upstream.");
        }

        if (data is null)
            return StatusCode(503, "Radar manifest not yet available.");

        return File(data, "image/png");
    }

    // ── Wind (OpenWeatherMap) ──────────────────────────────────────────────

    [HttpGet("wind/tile/{z:int}/{x:int}/{y:int}")]
    public async Task<IActionResult> GetWindTile(int z, int x, int y, CancellationToken ct)
    {
        var setting = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (string.IsNullOrWhiteSpace(setting?.OpenWeatherMapApiKey))
            return NoContent();

        byte[] data;
        try
        {
            data = await windTileCache.GetTileAsync(z, x, y, setting.OpenWeatherMapApiKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch OWM wind tile {Z}/{X}/{Y}", z, x, y);
            return StatusCode(502, "Failed to fetch wind tile from upstream.");
        }

        return File(data, "image/png");
    }

    // ── Lightning (Tomorrow.io) ────────────────────────────────────────────

    [HttpGet("lightning/tile/{z:int}/{x:int}/{y:int}")]
    public async Task<IActionResult> GetLightningTile(int z, int x, int y, CancellationToken ct)
    {
        var setting = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (string.IsNullOrWhiteSpace(setting?.TomorrowIoApiKey))
            return NoContent();

        byte[] data;
        try
        {
            data = await lightningCache.GetTileAsync(z, x, y, setting.TomorrowIoApiKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Tomorrow.io lightning tile {Z}/{X}/{Y}", z, x, y);
            return StatusCode(502, "Failed to fetch lightning tile from upstream.");
        }

        return File(data, "image/png");
    }

    // ── Status ─────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public async Task<ActionResult<WeatherStatusDto>> GetStatus(CancellationToken ct)
    {
        var setting = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var (manifest, manifestFetchedAt) = radarCache.GetManifest();
        var radarStatus = manifest is not null
            ? new WeatherLayerStatusDto
            {
                Available = true,
                FrameCount = manifest.Radar.Past.Count,
                LastUpdated = manifestFetchedAt,
            }
            : new WeatherLayerStatusDto
            {
                Available = false,
                Reason = "Manifest not yet loaded.",
            };

        var owmConfigured = !string.IsNullOrWhiteSpace(setting?.OpenWeatherMapApiKey);
        var windStatus = new WeatherLayerStatusDto
        {
            Available = owmConfigured,
            Reason = owmConfigured ? null : "API key not configured",
        };

        var tomorrowConfigured = !string.IsNullOrWhiteSpace(setting?.TomorrowIoApiKey);
        var lightningStatus = new WeatherLayerStatusDto
        {
            Available = tomorrowConfigured,
            Reason = tomorrowConfigured ? null : "API key not configured",
        };

        return Ok(new WeatherStatusDto
        {
            Radar = radarStatus,
            Wind = windStatus,
            Lightning = lightningStatus,
        });
    }
}

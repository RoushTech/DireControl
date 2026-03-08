using DireControl.Enums;

namespace DireControl.Api.Services.Weather;

public interface IRadarProvider
{
    Task RefreshAsync(CancellationToken ct);
    (NormalizedRadarManifest? Manifest, DateTime FetchedAt) GetManifest();

    /// <summary>
    /// Fetch (or return cached) tile bytes. <paramref name="apiKey"/> is only used by
    /// <see cref="RainViewerRadarProvider"/> when serving Pro tiles; other providers ignore it.
    /// </summary>
    Task<byte[]?> GetTileAsync(string framePath, int z, int x, int y, string? apiKey, CancellationToken ct);
}

public sealed class NormalizedRadarManifest
{
    public long Generated { get; set; }
    public List<NormalizedRadarFrame> Past { get; set; } = [];
    public List<NormalizedRadarFrame> Nowcast { get; set; } = [];
}

public sealed class NormalizedRadarFrame
{
    /// <summary>Unix timestamp (seconds) of this frame.</summary>
    public long Time { get; set; }

    /// <summary>
    /// Opaque identifier passed back to the tile endpoint as the <c>framePath</c> route segment.
    /// For RainViewer this is the RainViewer frame path (e.g. <c>v2/radar/1620046800</c>);
    /// for IEM it is the layer name (e.g. <c>nexrad-n0q-900913</c>).
    /// </summary>
    public string Path { get; set; } = "";
}

/// <summary>
/// Static per-provider tile metadata constants.
/// </summary>
internal static class RadarProviderConfig
{
    /// <summary>Maximum zoom level at which provider tiles contain real data.</summary>
    public static int MaxNativeZoom(RadarProvider p) => p switch
    {
        RadarProvider.RainViewerPro => 12,
        RadarProvider.RainViewer    => 7,
        _                           => 8,   // IemNexrad
    };

    /// <summary>Tile size in pixels served by this provider.</summary>
    public static int TileSize(RadarProvider p) => p switch
    {
        RadarProvider.IemNexrad => 256,
        _                       => 512,
    };
}

using System.Collections.Concurrent;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Radar tile provider backed by the Iowa Environmental Mesonet (IEM) NEXRAD composite.
/// Serves 256×256 tiles up to zoom 8. Updates every 5 minutes.
/// No API key required.
/// </summary>
public sealed class IemRadarProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<IemRadarProvider> logger) : IRadarProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // IEM layer names in order from oldest to newest.
    // nexrad-n0q-900913 is the current frame; mXXm layers are N minutes ago.
    private static readonly string[] FrameLayers =
    [
        "nexrad-n0q-m55m",
        "nexrad-n0q-m50m",
        "nexrad-n0q-m45m",
        "nexrad-n0q-m40m",
        "nexrad-n0q-m35m",
        "nexrad-n0q-m30m",
        "nexrad-n0q-m25m",
        "nexrad-n0q-m20m",
        "nexrad-n0q-m15m",
        "nexrad-n0q-m10m",
        "nexrad-n0q-m05m",
        "nexrad-n0q-900913",  // current
    ];

    private readonly ConcurrentDictionary<string, (byte[] Data, DateTime FetchedAt)> _tiles = new();

    // IEM frames are time-derived so RefreshAsync has nothing to do.
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;

    public (NormalizedRadarManifest? Manifest, DateTime FetchedAt) GetManifest()
    {
        // Round current time down to the nearest 5-minute boundary.
        var now = DateTime.UtcNow;
        var currentBucket = new DateTime(
            now.Year, now.Month, now.Day,
            now.Hour, (now.Minute / 5) * 5, 0, DateTimeKind.Utc);

        var frames = FrameLayers
            .Select((layer, i) =>
            {
                // i=0 is the oldest frame (55 min ago), i=11 is the current frame.
                var offsetMinutes = (FrameLayers.Length - 1 - i) * 5;
                var frameTime = currentBucket.AddMinutes(-offsetMinutes);
                return new NormalizedRadarFrame
                {
                    Time = ((DateTimeOffset)frameTime).ToUnixTimeSeconds(),
                    Path = layer,
                };
            })
            .ToList();

        var manifest = new NormalizedRadarManifest
        {
            Generated = ((DateTimeOffset)currentBucket).ToUnixTimeSeconds(),
            Past = frames,
            Nowcast = [],
        };

        return (manifest, DateTime.UtcNow);
    }

    public async Task<byte[]?> GetTileAsync(string framePath, int z, int x, int y, string? apiKey, CancellationToken ct)
    {
        var key = $"{framePath}/{z}/{x}/{y}";

        if (_tiles.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
            return cached.Data;

        var url = $"https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/{framePath}/{z}/{x}/{y}.png";
        var http = httpClientFactory.CreateClient("IEM");
        var data = await http.GetByteArrayAsync(url, ct);
        _tiles[key] = (data, DateTime.UtcNow);

        logger.LogDebug("IEM tile fetched: {FramePath}/{Z}/{X}/{Y}", framePath, z, x, y);
        return data;
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DireControl.Api.Services.Weather;

// ── Internal upstream model ────────────────────────────────────────────────

internal sealed class RainViewerManifest
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("generated")]
    public long Generated { get; set; }

    [JsonPropertyName("radar")]
    public RainViewerRadarSection Radar { get; set; } = new();
}

internal sealed class RainViewerRadarSection
{
    [JsonPropertyName("past")]
    public List<RainViewerFrame> Past { get; set; } = [];

    [JsonPropertyName("nowcast")]
    public List<RainViewerFrame> Nowcast { get; set; } = [];
}

internal sealed class RainViewerFrame
{
    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

// ── Provider ───────────────────────────────────────────────────────────────

public sealed class RainViewerRadarProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<RainViewerRadarProvider> logger) : IRadarProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private RainViewerManifest? _manifest;
    private DateTime _manifestFetchedAt;

    private readonly ConcurrentDictionary<string, (byte[] Data, DateTime FetchedAt)> _tiles = new();

    public async Task RefreshAsync(CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("RainViewer");
        var json = await http.GetStringAsync(
            "https://api.rainviewer.com/public/weather-maps.json", ct);

        var manifest = JsonSerializer.Deserialize<RainViewerManifest>(json, JsonOpts)
            ?? throw new InvalidOperationException("RainViewer manifest was null after deserialisation.");

        _manifest = manifest;
        _manifestFetchedAt = DateTime.UtcNow;

        EvictStaleTiles(manifest);

        logger.LogDebug("RainViewer manifest refreshed: {Count} past frames", manifest.Radar.Past.Count);
    }

    public (NormalizedRadarManifest? Manifest, DateTime FetchedAt) GetManifest()
    {
        if (_manifest is null)
            return (null, _manifestFetchedAt);

        var norm = new NormalizedRadarManifest
        {
            Generated = _manifest.Generated,
            Past = _manifest.Radar.Past
                .Select(f => new NormalizedRadarFrame { Time = f.Time, Path = f.Path.TrimStart('/') })
                .ToList(),
            Nowcast = _manifest.Radar.Nowcast
                .Select(f => new NormalizedRadarFrame { Time = f.Time, Path = f.Path.TrimStart('/') })
                .ToList(),
        };
        return (norm, _manifestFetchedAt);
    }

    public async Task<byte[]?> GetTileAsync(string framePath, int z, int x, int y, string? apiKey, CancellationToken ct)
    {
        if (_manifest is null)
            return null;

        var key = $"{framePath}/{z}/{x}/{y}";

        if (_tiles.TryGetValue(key, out var cached) &&
            DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(30))
            return cached.Data;

        // Pro tiles use a different colour scheme and append the API key.
        // NOTE: The exact RainViewer Pro tile URL format should be confirmed against the
        // RainViewer Pro documentation — this is based on the known free URL pattern plus
        // the apikey query parameter. The colour scheme index (4) may also differ.
        string url;
        if (!string.IsNullOrWhiteSpace(apiKey))
            url = $"{_manifest.Host}/{framePath}/512/{z}/{x}/{y}/4/1_1.png?apikey={apiKey}";
        else
            url = $"{_manifest.Host}/{framePath}/512/{z}/{x}/{y}/2/1_1.png";

        var http = httpClientFactory.CreateClient("RainViewer");
        var data = await http.GetByteArrayAsync(url, ct);
        _tiles[key] = (data, DateTime.UtcNow);
        return data;
    }

    private void EvictStaleTiles(RainViewerManifest manifest)
    {
        var validPaths = new HashSet<string>(
            manifest.Radar.Past.Concat(manifest.Radar.Nowcast)
                    .Select(f => f.Path.TrimStart('/')));

        foreach (var key in _tiles.Keys)
        {
            // key format: "v2/radar/1620046800/{z}/{x}/{y}" — first three segments are the frame path
            var parts = key.Split('/');
            if (parts.Length < 3)
            {
                _tiles.TryRemove(key, out _);
                continue;
            }
            var framePath = string.Join("/", parts.Take(3));
            if (!validPaths.Contains(framePath))
                _tiles.TryRemove(key, out _);
        }
    }
}

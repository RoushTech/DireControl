using System.Collections.Concurrent;

namespace DireControl.Api.Services.Weather;

public sealed class WindTileCache(IHttpClientFactory httpClientFactory, ILogger<WindTileCache> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (byte[] Data, DateTime FetchedAt)> _tiles = new();

    public async Task<byte[]> GetTileAsync(int z, int x, int y, string apiKey, CancellationToken ct)
    {
        var key = $"{z}/{x}/{y}";

        if (_tiles.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
            return cached.Data;

        var url = $"https://tile.openweathermap.org/map/wind_new/{z}/{x}/{y}.png?appid={apiKey}";
        var http = httpClientFactory.CreateClient("OpenWeatherMap");
        var data = await http.GetByteArrayAsync(url, ct);
        _tiles[key] = (data, DateTime.UtcNow);

        logger.LogDebug("OWM wind tile fetched: {Z}/{X}/{Y}", z, x, y);
        return data;
    }

    public void EvictStale()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var key in _tiles.Keys)
        {
            if (_tiles.TryGetValue(key, out var entry) && entry.FetchedAt < cutoff)
                _tiles.TryRemove(key, out _);
        }
    }
}

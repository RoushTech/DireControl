using System.Collections.Concurrent;

namespace DireControl.Api.Services.Weather;

public sealed class LightningCache(IHttpClientFactory httpClientFactory, ILogger<LightningCache> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private string _currentTimestamp = "";
    private DateTime _periodStart = DateTime.MinValue;

    private readonly ConcurrentDictionary<string, (byte[] Data, DateTime FetchedAt)> _tiles = new();

    public Task RefreshAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _currentTimestamp = new DateTime(
            now.Year, now.Month, now.Day,
            now.Hour, (now.Minute / 5) * 5, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ");
        _periodStart = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var key in _tiles.Keys)
        {
            if (_tiles.TryGetValue(key, out var entry) && entry.FetchedAt < cutoff)
                _tiles.TryRemove(key, out _);
        }

        logger.LogDebug("Lightning cache period updated to {Timestamp}", _currentTimestamp);
        return Task.CompletedTask;
    }

    public async Task<byte[]> GetTileAsync(int z, int x, int y, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentTimestamp))
            await RefreshAsync(ct);

        var key = $"{_currentTimestamp}/{z}/{x}/{y}";

        if (_tiles.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
            return cached.Data;

        var url = $"https://api.tomorrow.io/v4/map/tile/{z}/{x}/{y}" +
                  $"/lightningStrikeCount/{_currentTimestamp}.png?apikey={apiKey}";
        var http = httpClientFactory.CreateClient("TomorrowIo");
        var data = await http.GetByteArrayAsync(url, ct);
        _tiles[key] = (data, DateTime.UtcNow);

        logger.LogDebug("Tomorrow.io lightning tile fetched: {Z}/{X}/{Y} @ {Ts}", z, x, y, _currentTimestamp);
        return data;
    }

    public string CurrentTimestamp => _currentTimestamp;
    public DateTime PeriodStart => _periodStart;
}

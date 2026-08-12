using System.Text.Json;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Decodes the Blitzortung.org WebSocket feed. Frames arrive as UTF-8 text compressed
/// with an LZW variant (codes ≥ 256 are emitted as literal Unicode code points); the
/// decompressed payload is one JSON object per strike.
/// </summary>
public static class BlitzortungDecoder
{
    /// <summary>Decompresses a raw frame into its JSON payload.</summary>
    public static string Decompress(string input)
    {
        if (input.Length == 0)
            return "";

        var dict = new Dictionary<int, string>();
        var currChar = input[0].ToString();
        var oldPhrase = currChar;
        var output = new System.Text.StringBuilder(input.Length * 3);
        output.Append(currChar);
        var code = 256;

        for (var i = 1; i < input.Length; i++)
        {
            int currCode = input[i];
            string phrase;
            if (currCode < 256)
                phrase = input[i].ToString();
            else if (!dict.TryGetValue(currCode, out phrase!))
                phrase = oldPhrase + currChar;

            output.Append(phrase);
            currChar = phrase[0].ToString();
            dict[code] = oldPhrase + currChar;
            code++;
            oldPhrase = phrase;
        }

        return output.ToString();
    }

    /// <summary>
    /// Decompresses and parses a raw frame into a strike, or null when the frame is
    /// not a valid strike message. Strike time is nanoseconds since the Unix epoch.
    /// </summary>
    public static LightningStrike? DecodeStrike(string rawFrame)
    {
        try
        {
            using var doc = JsonDocument.Parse(Decompress(rawFrame));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("time", out var time)
                || !root.TryGetProperty("lat", out var lat)
                || !root.TryGetProperty("lon", out var lon))
            {
                return null;
            }

            return new LightningStrike(
                DateTime.UnixEpoch.AddTicks(time.GetInt64() / 100),
                lat.GetDouble(),
                lon.GetDouble());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

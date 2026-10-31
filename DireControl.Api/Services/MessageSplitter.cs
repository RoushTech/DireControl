using System.Text;

namespace DireControl.Api.Services;

/// <summary>
/// Splits outbound message text into APRS-sized parts.  APRS message text is
/// limited to 67 characters, so longer text is word-wrapped into numbered parts
/// with a <c>" (i/n)"</c> suffix — the same convention APRSdroid uses — and each
/// part is sent as its own message with its own ID and ACK.
/// </summary>
public static class MessageSplitter
{
    /// <summary>Maximum APRS message text length (APRS101 §14).</summary>
    public const int MaxPartLength = 67;

    /// <summary>Cap on parts per send, so one compose can't flood the channel.</summary>
    public const int MaxParts = 10;

    /// <summary>
    /// Replaces characters APRS forbids in message text: <c>|</c> and <c>~</c>
    /// are reserved, and <c>{</c> starts the message-ID suffix, so a stray one
    /// would corrupt the receiver's ID parsing.  Also collapses newlines.
    /// </summary>
    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '{' => '(',
                '}' => ')',
                '|' => '/',
                '~' => '-',
                '\r' or '\n' or '\t' => ' ',
                _ => c,
            });
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Returns the parts to send for <paramref name="text"/>.  Text that fits in
    /// one message is returned unchanged (no suffix).  Empty text yields no parts.
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        text = Sanitize(text);
        if (text.Length == 0)
            return [];
        if (text.Length <= MaxPartLength)
            return [text];

        // The suffix width depends on the part count ("(9/9)" vs "(10/12)"), so
        // wrap with a guess and re-wrap until the count is stable.
        var count = 2;
        while (true)
        {
            var suffixLength = $" ({count}/{count})".Length;
            var chunks = Wrap(text, MaxPartLength - suffixLength);
            if (chunks.Count <= count)
                return chunks.Select((c, i) => $"{c} ({i + 1}/{chunks.Count})").ToList();
            count = chunks.Count;
        }
    }

    private static List<string> Wrap(string text, int width)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var remaining = word;
            // Words longer than a whole part are hard-broken.
            while (remaining.Length > width)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                chunks.Add(remaining[..width]);
                remaining = remaining[width..];
            }

            if (current.Length > 0 && current.Length + 1 + remaining.Length > width)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(remaining);
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }
}

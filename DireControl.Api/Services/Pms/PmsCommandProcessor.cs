using DireControl.Data.Models;
using DireControl.Enums;

namespace DireControl.Api.Services.Pms;

/// <summary>
/// The PMS line protocol: one instance per connection, fed complete lines,
/// returning response lines.  Pure logic — no I/O, no clock reads (injected),
/// storage behind <see cref="IPmsMailStore"/>.  Commands follow W0RLI/AA4RE
/// personal-mailbox conventions so BBS-literate operators feel at home.
/// </summary>
public sealed class PmsCommandProcessor(
    IPmsMailStore store,
    string callerCallsign,
    string stationCallsign,
    Func<DateTime> utcNow,
    Func<CancellationToken, Task<IReadOnlyList<HeardStation>>>? heardList = null,
    TerminalSessionOrigin origin = TerminalSessionOrigin.Inbound)
{
    private enum Mode { Command, AwaitingSubject, AwaitingBody }

    public const string Version = "DireControl-1.0-PMS";

    /// <summary>Base callsign of the connected user — mail matches on this, SSID stripped.</summary>
    private readonly string _caller = StripSsid(callerCallsign);

    private Mode _mode = Mode.Command;
    private PmsMessageType _composeType;
    private string _composeTo = string.Empty;
    private string _composeSubject = string.Empty;
    private readonly List<string> _composeBody = [];

    /// <summary>Set once the user signs off; the session handler disconnects.</summary>
    public bool DisconnectRequested { get; private set; }

    /// <summary>Whether the handler should print the command prompt after the last response.</summary>
    public bool AtCommandPrompt => _mode == Mode.Command;

    /// <summary>The connect banner: SID line, banner text, and unread-mail count.</summary>
    public async Task<IReadOnlyList<string>> GetBannerAsync(string bannerText, CancellationToken ct = default)
    {
        var unread = await store.CountUnreadForAsync(_caller, ct);
        var lines = new List<string> { $"[{Version}$]", bannerText };
        if (unread > 0)
            lines.Add(unread == 1 ? "You have 1 new message." : $"You have {unread} new messages.");
        return lines;
    }

    public async Task<IReadOnlyList<string>> ProcessLineAsync(string line, CancellationToken ct = default)
    {
        return _mode switch
        {
            Mode.AwaitingSubject => TakeSubject(line),
            Mode.AwaitingBody => await TakeBodyLineAsync(line, ct),
            _ => await ProcessCommandAsync(line, ct),
        };
    }

    private async Task<IReadOnlyList<string>> ProcessCommandAsync(string line, CancellationToken ct)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return [];

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = tokens[0].ToUpperInvariant();

        switch (command)
        {
            case "B" or "BYE":
                DisconnectRequested = true;
                return [$"73 de {stationCallsign} PMS"];

            case "H" or "?" or "HELP":
                return
                [
                    "B)ye        Sign off and disconnect",
                    "H)elp       This screen",
                    "J)heard     Recently heard stations",
                    "K)ill n     Kill message n (KM: all your read mail)",
                    "L)ist       List messages",
                    "R)ead n     Read message n",
                    "S)end CALL  Send private mail (SP CALL; SB CAT for a bulletin)",
                    "V)ersion    Software version",
                ];

            case "L" or "LIST":
                return await ListAsync(ct);

            case "R" or "READ":
                return await ReadAsync(tokens, ct);

            case "S" or "SP":
                return BeginCompose(tokens, PmsMessageType.Private);

            case "SB":
                return BeginCompose(tokens, PmsMessageType.Bulletin);

            case "K" or "KILL":
                return await KillAsync(tokens, ct);

            case "KM":
                var killed = await store.KillReadMailAsync(_caller, ct);
                return [killed == 0 ? "No read mail to kill." : $"{killed} message(s) killed."];

            case "J" or "JHEARD":
                return await HeardAsync(ct);

            case "V" or "VERSION":
                return [Version];

            default:
                return ["? Unknown command. H for help."];
        }
    }

    private async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        var messages = await store.ListVisibleAsync(_caller, ct);
        if (messages.Count == 0)
            return ["No messages."];

        var lines = new List<string> { "Msg#  TR From    To      Date  Subject" };
        lines.AddRange(messages.Select(m =>
        {
            var type = m.Type == PmsMessageType.Bulletin ? 'B' : 'P';
            var read = m.ReadAt is not null ? 'Y' : 'N';
            return $"{m.Id,-5} {type}{read} {m.FromCallsign,-7} {m.ToCallsign,-7} {m.CreatedAt:MMdd}  {m.Subject}";
        }));
        lines.Add(messages.Count == 1 ? "1 message." : $"{messages.Count} messages.");
        return lines;
    }

    private async Task<IReadOnlyList<string>> ReadAsync(string[] tokens, CancellationToken ct)
    {
        if (tokens.Length < 2 || !int.TryParse(tokens[1], out var id))
            return ["Usage: R n"];

        var message = await store.GetAsync(id, ct);
        if (message is null || message.IsKilled || !IsVisible(message))
            return [$"No such message: {id}"];

        if (string.Equals(message.ToCallsign, _caller, StringComparison.OrdinalIgnoreCase))
            await store.MarkReadAsync(id, ct);

        return
        [
            $"Msg #{message.Id}  From: {message.FromCallsign}  To: {message.ToCallsign}  {message.CreatedAt:yyyy-MM-dd HH:mm}Z",
            $"Subject: {message.Subject}",
            .. message.Body.Split('\n').Select(l => l.TrimEnd('\r')),
        ];
    }

    private IReadOnlyList<string> BeginCompose(string[] tokens, PmsMessageType type)
    {
        if (tokens.Length < 2)
            return [type == PmsMessageType.Bulletin ? "Usage: SB CATEGORY" : "Usage: S CALLSIGN"];

        _composeType = type;
        _composeTo = tokens[1].ToUpperInvariant();
        _composeSubject = string.Empty;
        _composeBody.Clear();
        _mode = Mode.AwaitingSubject;
        return ["Subject:"];
    }

    private IReadOnlyList<string> TakeSubject(string line)
    {
        _composeSubject = line.Trim();
        _mode = Mode.AwaitingBody;
        return ["Enter message, end with /EX on a line by itself:"];
    }

    private async Task<IReadOnlyList<string>> TakeBodyLineAsync(string line, CancellationToken ct)
    {
        // /EX (conventional) or a lone Ctrl-Z both terminate the body.
        var trimmed = line.Trim();
        if (!trimmed.Equals("/EX", StringComparison.OrdinalIgnoreCase) && trimmed != "\x1a")
        {
            _composeBody.Add(line);
            return [];
        }

        var message = await store.SaveAsync(new PmsMessage
        {
            Type = _composeType,
            FromCallsign = _caller,
            ToCallsign = _composeTo,
            Subject = _composeSubject,
            Body = string.Join('\n', _composeBody),
            CreatedAt = utcNow(),
            Origin = origin,
        }, ct);

        _mode = Mode.Command;
        return [$"Msg #{message.Id} saved."];
    }

    private async Task<IReadOnlyList<string>> KillAsync(string[] tokens, CancellationToken ct)
    {
        if (tokens.Length < 2 || !int.TryParse(tokens[1], out var id))
            return ["Usage: K n  (or KM for all your read mail)"];

        return await store.KillAsync(id, _caller, ct)
            ? [$"Msg #{id} killed."]
            : [$"Cannot kill message {id}."];
    }

    private async Task<IReadOnlyList<string>> HeardAsync(CancellationToken ct)
    {
        if (heardList is null)
            return ["Heard list unavailable."];

        var heard = await heardList(ct);
        if (heard.Count == 0)
            return ["Nothing heard yet."];
        return
        [
            "Callsign   Last heard (UTC)",
            .. heard.Take(10).Select(h => $"{h.Callsign,-10} {h.LastHeardUtc:yyyy-MM-dd HH:mm}"),
        ];
    }

    private bool IsVisible(PmsMessage message) =>
        message.Type == PmsMessageType.Bulletin
        || string.Equals(message.ToCallsign, _caller, StringComparison.OrdinalIgnoreCase)
        || string.Equals(message.FromCallsign, _caller, StringComparison.OrdinalIgnoreCase);

    private static string StripSsid(string callsign)
    {
        var dash = callsign.IndexOf('-');
        return (dash > 0 ? callsign[..dash] : callsign).ToUpperInvariant();
    }
}

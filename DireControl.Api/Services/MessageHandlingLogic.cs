using System.Text.RegularExpressions;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Pure message-handling helpers extracted from <see cref="AprsPacketParsingService"/>
/// so the core logic can be unit-tested without constructing the full background service.
/// </summary>
internal static partial class MessageHandlingLogic
{
    /// <summary>
    /// How long after the last copy of a message a byte-identical retransmission
    /// is still treated as a retry rather than a new message.  Message IDs are
    /// short and wrap, so an unbounded match would swallow a genuinely new
    /// message that happens to reuse an old ID and text.
    /// </summary>
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(1);

    // APRS 1.1 reply-ack suffix: "text{MM}AA" where MM is this message's ID and
    // the optional AA piggybacks an ACK for a message the sender received from us.
    [GeneratedRegex(@"^(?<body>.*)\{(?<id>[A-Za-z0-9]{1,5})\}(?<ack>[A-Za-z0-9]{0,5})$", RegexOptions.Singleline)]
    private static partial Regex ReplyAckSuffix();

    /// <summary>
    /// Splits an APRS 1.1 reply-ack suffix (<c>{MM}</c> or <c>{MM}AA</c>) off a
    /// message body.  AprsSharp only understands the classic <c>{MM</c> form and
    /// otherwise leaves the suffix in the content with an empty ID, which breaks
    /// both auto-ACK and retransmission dedup (APRSdroid sends reply-acks).
    /// When <paramref name="parsedId"/> is already populated the input is
    /// returned unchanged.
    /// </summary>
    internal static (string Body, string MessageId, string? ReplyAck) NormalizeMessageText(
        string content,
        string? parsedId)
    {
        if (!string.IsNullOrEmpty(parsedId))
            return (content, parsedId, null);

        var match = ReplyAckSuffix().Match(content);
        if (!match.Success)
            return (content, string.Empty, null);

        var ack = match.Groups["ack"].Value;
        return (match.Groups["body"].Value, match.Groups["id"].Value, ack.Length > 0 ? ack : null);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="body"/> is an APRS ACK packet.
    /// Sets <paramref name="originalMsgId"/> to the message number echoed by the remote
    /// station (the text after "ack", minus any reply-ack <c>}AA</c> tail).
    /// </summary>
    internal static bool TryParseAck(string body, out string originalMsgId)
    {
        if (body.StartsWith("ack", StringComparison.OrdinalIgnoreCase) && body.Length > 3)
        {
            var id = body[3..].Trim();
            var brace = id.IndexOf('}');
            if (brace >= 0)
                id = id[..brace];
            if (id.Length > 0)
            {
                originalMsgId = id;
                return true;
            }
        }

        originalMsgId = string.Empty;
        return false;
    }

    /// <summary>
    /// Finds the inbox row that an incoming message is a retransmission of: same
    /// sender, same message ID (empty for ID-less messages), same text, last
    /// heard within <see cref="DuplicateWindow"/> of <paramref name="receivedAt"/>.
    /// A match means the remote station is retrying because our ACK was lost
    /// (or, for ID-less messages, the sender is simply repeating itself).
    /// </summary>
    internal static Task<Message?> FindDuplicateAsync(
        string fromCallsign,
        string messageId,
        string body,
        DateTime receivedAt,
        DireControlContext db,
        CancellationToken ct)
    {
        var cutoff = receivedAt - DuplicateWindow;
        return db.Messages
            .Where(m => m.FromCallsign == fromCallsign
                     && m.MessageId == messageId
                     && m.Body == body
                     && (m.LastReceivedAt ?? m.ReceivedAt) >= cutoff)
            .OrderByDescending(m => m.ReceivedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Records another received copy of <paramref name="existing"/>.</summary>
    internal static void RecordRetransmission(Message existing, DateTime receivedAt)
    {
        existing.ReceiveCount++;
        if (existing.LastReceivedAt is null || receivedAt > existing.LastReceivedAt)
            existing.LastReceivedAt = receivedAt;
    }

    /// <summary>
    /// One-shot repair for inbox rows stored before reply-ack parsing existed:
    /// strips the <c>{MM}</c> suffix out of bodies that still carry it, then
    /// folds retransmitted copies (same sender, ID and text, each within
    /// <see cref="DuplicateWindow"/> of the previous copy) into the earliest row,
    /// summing <see cref="Message.ReceiveCount"/>.  Idempotent.
    /// </summary>
    /// <returns>The number of duplicate rows removed.</returns>
    internal static async Task<int> RepairInboxAsync(
        DireControlContext db,
        string ourCallsign,
        CancellationToken ct)
    {
        var ucOur = ourCallsign.Trim().ToUpperInvariant();

        // Only inbound rows: outbound rows are ours and already carry their ID.
        var inbound = await db.Messages
            .Where(m => m.FromCallsign != ucOur)
            .OrderBy(m => m.ReceivedAt)
            .ToListAsync(ct);

        foreach (var m in inbound.Where(m => m.MessageId.Length == 0))
        {
            var (body, id, _) = NormalizeMessageText(m.Body, null);
            m.Body = body;
            m.MessageId = id;
        }

        var removed = 0;
        foreach (var group in inbound.GroupBy(m => (m.FromCallsign, m.MessageId, m.Body)))
        {
            Message? keeper = null;
            foreach (var m in group)
            {
                var keeperLast = keeper is null ? (DateTime?)null : keeper.LastReceivedAt ?? keeper.ReceivedAt;
                if (keeper is null || m.ReceivedAt - keeperLast > DuplicateWindow)
                {
                    keeper = m;
                    continue;
                }

                keeper.ReceiveCount += m.ReceiveCount;
                var mLast = m.LastReceivedAt ?? m.ReceivedAt;
                if (keeper.LastReceivedAt is null || mLast > keeper.LastReceivedAt)
                    keeper.LastReceivedAt = mLast;
                keeper.IsRead |= m.IsRead;
                keeper.AckSent |= m.AckSent;
                keeper.ReplySent |= m.ReplySent;
                db.Messages.Remove(m);
                removed++;
            }
        }

        await db.SaveChangesAsync(ct);
        return removed;
    }

    /// <summary>
    /// For a third-party APRS packet (info field starts with <c>}</c>), extracts
    /// the inner TNC2 string and the callsign of the original sender.
    /// Returns <see langword="false"/> if <paramref name="rawPacket"/> is not a
    /// third-party frame or the inner content is empty.
    /// </summary>
    internal static bool TryExtractThirdPartyInner(
        string rawPacket,
        out string innerRaw,
        out string innerSender)
    {
        var colonIdx = rawPacket.IndexOf(':');
        if (colonIdx >= 0
            && colonIdx + 2 < rawPacket.Length
            && rawPacket[colonIdx + 1] == '}')
        {
            var candidate = rawPacket[(colonIdx + 2)..];
            if (!string.IsNullOrEmpty(candidate))
            {
                var senderEnd = candidate.IndexOf('>');
                innerRaw = candidate;
                innerSender = senderEnd > 0 ? candidate[..senderEnd].Trim() : candidate;
                return true;
            }
        }

        innerRaw = string.Empty;
        innerSender = string.Empty;
        return false;
    }

    /// <summary>
    /// Marks the outbound message sent by <paramref name="ourCallsign"/> to
    /// <paramref name="fromCallsign"/> with id <paramref name="originalMsgId"/> as
    /// acknowledged and clears its retry schedule.
    /// </summary>
    /// <returns>
    /// The database primary key of the acknowledged message, or
    /// <see langword="null"/> if no matching unacknowledged message was found.
    /// </returns>
    internal static async Task<int?> TryApplyAckAsync(
        string fromCallsign,
        string originalMsgId,
        DireControlContext db,
        string ourCallsign,
        CancellationToken ct)
    {
        // Sent messages are stored with ToUpperInvariant() callsigns; normalise
        // the parameters so the comparison is a plain equality that EF Core can
        // translate to SQL regardless of the database provider.
        var ucOur = ourCallsign.ToUpperInvariant();
        var ucFrom = fromCallsign.ToUpperInvariant();

        var sentMsg = await db.Messages.FirstOrDefaultAsync(
            m => m.MessageId == originalMsgId
              && m.FromCallsign == ucOur
              && m.ToCallsign == ucFrom,
            ct);

        if (sentMsg is null || sentMsg.AckSent)
            return null;

        sentMsg.AckSent = true;
        sentMsg.RetryState = RetryState.Acknowledged;
        sentMsg.NextRetryAt = null;
        await db.SaveChangesAsync(ct);
        return sentMsg.Id;
    }
}

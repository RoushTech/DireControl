using DireControl.Data;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Pure message-handling helpers extracted from <see cref="AprsPacketParsingService"/>
/// so the core logic can be unit-tested without constructing the full background service.
/// </summary>
internal static class MessageHandlingLogic
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="body"/> is an APRS ACK packet.
    /// Sets <paramref name="originalMsgId"/> to the message number echoed by the remote
    /// station (the text after "ack").
    /// </summary>
    internal static bool TryParseAck(string body, out string originalMsgId)
    {
        if (body.StartsWith("ack", StringComparison.OrdinalIgnoreCase) && body.Length > 3)
        {
            var id = body[3..].Trim();
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
    /// Returns <see langword="true"/> if the inbox already contains a message from
    /// <paramref name="fromCallsign"/> with the given <paramref name="messageId"/>.
    /// A <see langword="true"/> result means the remote station is retransmitting
    /// because our earlier ACK was lost.
    /// </summary>
    internal static Task<bool> IsMessageDuplicateAsync(
        string fromCallsign,
        string messageId,
        DireControlContext db,
        CancellationToken ct)
        => db.Messages.AnyAsync(
            m => m.FromCallsign == fromCallsign && m.MessageId == messageId,
            ct);

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

using System.Text;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Formats APRS message packets, encodes them as AX.25/KISS frames, and
/// writes them to Direwolf via the shared <see cref="KissConnectionHolder"/>.
/// Outbound messages are stored in the <see cref="Message"/> table.
/// </summary>
public sealed class MessageSendingService(
    KissConnectionHolder connectionHolder,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<MessageSendingService> logger)
{
    private static int _msgIdCounter;

    /// <summary>
    /// Generates a unique 1–5 character alphanumeric APRS message ID.
    /// </summary>
    public static string GenerateMessageId()
    {
        var n = Interlocked.Increment(ref _msgIdCounter) % 99999;
        return (n == 0 ? 1 : n).ToString();
    }

    /// <summary>
    /// Sends a message to <paramref name="toCallsign"/>, stores it in the
    /// database with <c>AckSent = false</c>, and returns the saved record.
    /// Returns <see langword="null"/> if there is no active Direwolf connection.
    /// </summary>
    public async Task<Message?> SendMessageAsync(
        string toCallsign,
        string body,
        string messageId,
        CancellationToken ct = default)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildMessageInfo(toCallsign, body, messageId);
        var frame = BuildAx25Frame(ourCallsign, info);

        if (!connectionHolder.TrySend(frame))
        {
            logger.LogWarning(
                "Cannot send message to {ToCallsign}: no active Direwolf connection.",
                toCallsign);
            return null;
        }

        logger.LogInformation(
            "Sent APRS message to {ToCallsign} (id={MessageId}): {Body}",
            toCallsign, messageId, body);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var message = new Message
        {
            FromCallsign = ourCallsign,
            ToCallsign = toCallsign.Trim().ToUpperInvariant(),
            Body = body,
            MessageId = messageId,
            ReceivedAt = DateTime.UtcNow,
            IsRead = true,
            AckSent = false,
            ReplySent = false,
            RetryCount = 0,
            MaxRetries = options.Value.MaxRetryAttempts,
            RetryState = RetryState.Retrying,
            LastSentAt = DateTime.UtcNow,
            NextRetryAt = DateTime.UtcNow.AddSeconds(options.Value.InitialRetryDelaySeconds),
        };

        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>
    /// Sends an ACK for a received message.  Does not store anything new in the
    /// database; the caller is responsible for setting <c>AckSent = true</c> on
    /// the corresponding <see cref="Message"/> record.
    /// </summary>
    public void SendAck(string toCallsign, string originalMessageId)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildAckInfo(toCallsign, originalMessageId);
        var frame = BuildAx25Frame(ourCallsign, info);

        if (!connectionHolder.TrySend(frame))
        {
            logger.LogWarning(
                "Cannot send ACK to {ToCallsign}: no active Direwolf connection.",
                toCallsign);
            return;
        }

        logger.LogDebug("Sent ACK to {ToCallsign} for message id={MessageId}.", toCallsign, originalMessageId);
    }

    /// <summary>
    /// Retransmits an existing message from the database.
    /// Rebuilds and sends the AX.25 frame without creating a new database record.
    /// Returns <see langword="true"/> if sent successfully,
    /// <see langword="false"/> if there is no active Direwolf connection.
    /// </summary>
    public Task<bool> RetransmitAsync(Message message, CancellationToken ct = default)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildMessageInfo(message.ToCallsign, message.Body, message.MessageId);
        var frame = BuildAx25Frame(ourCallsign, info);

        if (!connectionHolder.TrySend(frame))
        {
            logger.LogWarning(
                "Cannot retransmit message {Id} to {ToCallsign}: no active Direwolf connection.",
                message.Id, message.ToCallsign);
            return Task.FromResult(false);
        }

        logger.LogInformation(
            "Retransmitted message {Id} to {ToCallsign} (id={MessageId}, attempt {Attempt}).",
            message.Id, message.ToCallsign, message.MessageId, message.RetryCount + 1);
        return Task.FromResult(true);
    }

    // -------------------------------------------------------------------------
    // APRS packet formatting
    // -------------------------------------------------------------------------

    /// <summary>
    /// Formats the APRS info field for a message:
    /// <c>:ADDRESSEE :body{msgid}</c>
    /// </summary>
    private static string BuildMessageInfo(string toCallsign, string body, string messageId)
    {
        // Addressee field is exactly 9 characters, right-padded with spaces.
        var addressee = toCallsign.ToUpperInvariant().PadRight(9)[..9];
        return $":{addressee}:{body}{{{messageId}}}";
    }

    /// <summary>
    /// Formats the APRS info field for an ACK:
    /// <c>:ADDRESSEE :ackMSGID</c>
    /// </summary>
    private static string BuildAckInfo(string toCallsign, string originalMessageId)
    {
        var addressee = toCallsign.ToUpperInvariant().PadRight(9)[..9];
        return $":{addressee}:ack{originalMessageId}";
    }

    // -------------------------------------------------------------------------
    // AX.25 frame encoding
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a raw AX.25 UI frame suitable for passing to
    /// <see cref="AprsSharp.KissTnc.Tnc.SendData"/>.
    /// </summary>
    private static byte[] BuildAx25Frame(string sourceCallsign, string aprsInfo)
    {
        // APRS destination — "APRS" is the standard tocall for generic APRS.
        const string destination = "APRS";

        var (destBase, destSsid) = SplitCallsign(destination);
        var (srcBase, srcSsid) = SplitCallsign(sourceCallsign);

        var frame = new List<byte>(128);

        // Destination address (not the last address)
        frame.AddRange(EncodeAddress(destBase, destSsid, isLast: false));

        // Source address (last address — no digipeater path)
        frame.AddRange(EncodeAddress(srcBase, srcSsid, isLast: true));

        // AX.25 UI frame control + PID
        frame.Add(0x03); // Control: Unnumbered Information (UI)
        frame.Add(0xF0); // PID: no layer-3 protocol

        // APRS info field
        frame.AddRange(Encoding.ASCII.GetBytes(aprsInfo));

        return [.. frame];
    }

    /// <summary>
    /// Encodes a single AX.25 address field (7 bytes).
    /// </summary>
    private static byte[] EncodeAddress(string callsign, int ssid, bool isLast)
    {
        // Pad or truncate to exactly 6 characters.
        var padded = callsign.ToUpperInvariant().PadRight(6)[..6];

        var bytes = new byte[7];
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)((padded[i] & 0x7F) << 1);

        // SSID byte: bits 7-6 = 1 (H/C reserved), bits 4-1 = SSID, bit 0 = end
        bytes[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (isLast ? 0x01 : 0x00));

        return bytes;
    }

    private static (string callsign, int ssid) SplitCallsign(string raw)
    {
        var parts = raw.Split('-', 2);
        var ssid = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (parts[0], ssid);
    }
}

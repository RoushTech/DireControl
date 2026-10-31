using System.Reflection;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Tests for APRS message formatting, ACK packet parsing, inbox deduplication,
/// and acknowledgement processing.  Uses an in-memory SQLite database so that
/// DB-level logic (dedup queries, ACK application) is exercised against real SQL
/// rather than an in-memory EF stub.
/// </summary>
[TestFixture]
public sealed class MessageHandlingTests
{
    private SqliteConnection _connection = null!;
    private DireControlContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        // Keep the connection open for the life of the test so the in-memory
        // SQLite database is not destroyed between context operations.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<DireControlContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new DireControlContext(options);
        _db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // =========================================================================
    // BuildMessageInfo — verifies correct APRS message packet formatting.
    // In particular, confirms no trailing '}' after the message number; that
    // trailing brace was the root cause of ACK round-trip failures.
    // =========================================================================

    [TestCase("W1ABC", "Hello", "1", ":W1ABC    :Hello{1")]
    [TestCase("W1ABC-7", "Test msg", "42", ":W1ABC-7  :Test msg{42")]
    [TestCase("N0CALL", "hi", "99999", ":N0CALL   :hi{99999")]
    [TestCase("KB9VBR", "73 de me", "5", ":KB9VBR   :73 de me{5")]
    public void BuildMessageInfo_ProducesCorrectAprsFormat(
        string toCallsign, string body, string msgId, string expected)
    {
        var method = typeof(MessageSendingService)
            .GetMethod("BuildMessageInfo", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, [toCallsign, body, msgId])!;

        Assert.That(result, Is.EqualTo(expected));
        // Belt-and-suspenders: ensure no trailing brace (the fixed bug).
        Assert.That(result, Does.Not.Contain('}'.ToString()));
    }

    // =========================================================================
    // BuildAckInfo — verifies ACK packet formatting.
    // =========================================================================

    [TestCase("W1ABC", "1", ":W1ABC    :ack1")]
    [TestCase("W1ABC-7", "42", ":W1ABC-7  :ack42")]
    [TestCase("N0CALL", "3DF4A", ":N0CALL   :ack3DF4A")]
    public void BuildAckInfo_ProducesCorrectAprsFormat(
        string toCallsign, string msgId, string expected)
    {
        var method = typeof(MessageSendingService)
            .GetMethod("BuildAckInfo", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, [toCallsign, msgId])!;

        Assert.That(result, Is.EqualTo(expected));
    }

    // =========================================================================
    // TryParseAck — body → (isAck, originalMsgId)
    // =========================================================================

    [TestCase("ack42", true, "42")]
    [TestCase("ACK42", true, "42")]     // case-insensitive
    [TestCase("Ack42", true, "42")]     // mixed case
    [TestCase("ack3DF4A", true, "3DF4A")] // alphanumeric ID (real-world example)
    [TestCase("ack1", true, "1")]      // single-digit ID
    [TestCase("ack", false, "")]       // "ack" alone — no ID follows, too short
    [TestCase("Hello", false, "")]       // plain message body
    [TestCase("", false, "")]       // empty
    [TestCase("ack ", false, "")]       // "ack " — body.Length == 4 but ID is whitespace; trims to ""
    [TestCase("ack87}", true, "87")]     // reply-ack capable ACK
    [TestCase("ack87}AB", true, "87")]   // reply-ack ACK carrying a piggybacked ACK
    [TestCase("ack}", false, "")]        // brace but no ID
    public void TryParseAck_ClassifiesBodyCorrectly(string body, bool expectedIsAck, string expectedId)
    {
        var isAck = MessageHandlingLogic.TryParseAck(body, out var id);

        Assert.That(isAck, Is.EqualTo(expectedIsAck));
        Assert.That(id, Is.EqualTo(expectedId));
    }

    // =========================================================================
    // NormalizeMessageText — APRS 1.1 reply-ack suffix ({MM} / {MM}AA)
    // AprsSharp leaves these in the content with an empty ID (APRSdroid sends them).
    // =========================================================================

    [TestCase("Should be off now?{87}", null, "Should be off now?", "87", null)]
    [TestCase("Hello{AB}CD", null, "Hello", "AB", "CD")]
    [TestCase("Turned off radio now. (2/2){89}", null, "Turned off radio now. (2/2)", "89", null)]
    [TestCase("Plain message", null, "Plain message", "", null)]
    [TestCase("Classic", "42", "Classic", "42", null)]           // parser already found the ID
    [TestCase("Braces {in} the middle", null, "Braces {in} the middle", "", null)]
    [TestCase("Too long{123456}", null, "Too long{123456}", "", null)] // IDs are at most 5 chars
    public void NormalizeMessageText_SplitsReplyAckSuffix(
        string content, string? parsedId, string expectedBody, string expectedId, string? expectedAck)
    {
        var (body, id, ack) = MessageHandlingLogic.NormalizeMessageText(content, parsedId);

        Assert.That(body, Is.EqualTo(expectedBody));
        Assert.That(id, Is.EqualTo(expectedId));
        Assert.That(ack, Is.EqualTo(expectedAck));
    }

    [Test]
    public void ReplyAckMessage_RealPacket_ParsesIdAfterNormalization()
    {
        var packet = new AprsSharp.AprsParser.Packet(
            "K4DDA-7>APDR16,WIDE1-1::W3UWU    :Should be off now? Are you still seeing it?{87}");
        var info = (AprsSharp.AprsParser.MessageInfo)packet.InfoField!;

        var (body, id, ack) = MessageHandlingLogic.NormalizeMessageText(info.Content ?? "", info.Id);

        Assert.That(body, Is.EqualTo("Should be off now? Are you still seeing it?"));
        Assert.That(id, Is.EqualTo("87"));
        Assert.That(ack, Is.Null);
    }

    // =========================================================================
    // FindDuplicateAsync — retransmission dedup query
    // =========================================================================

    private static readonly DateTime T0 = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task FindDuplicate_ReturnsNull_WhenNoMatchingMessageExists()
    {
        var dup = await MessageHandlingLogic.FindDuplicateAsync("W1ABC", "42", "Test", T0, _db, default);

        Assert.That(dup, Is.Null);
    }

    [Test]
    public async Task FindDuplicate_ReturnsRow_WhenSameSenderIdAndBodyWithinWindow()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42", receivedAt: T0);
        await _db.SaveChangesAsync();

        var dup = await MessageHandlingLogic.FindDuplicateAsync(
            "W1ABC", "42", "Test", T0.AddMinutes(5), _db, default);

        Assert.That(dup, Is.Not.Null);
    }

    [TestCase("W9XYZ", "42", "Test")]  // different sender
    [TestCase("W1ABC", "99", "Test")]  // different ID
    [TestCase("W1ABC", "42", "Other")] // ID reused for different text
    public async Task FindDuplicate_ReturnsNull_WhenKeyDiffers(string from, string id, string body)
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42", receivedAt: T0);
        await _db.SaveChangesAsync();

        var dup = await MessageHandlingLogic.FindDuplicateAsync(from, id, body, T0.AddMinutes(1), _db, default);

        Assert.That(dup, Is.Null);
    }

    [Test]
    public async Task FindDuplicate_ReturnsNull_OutsideWindow()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42", receivedAt: T0);
        await _db.SaveChangesAsync();

        var dup = await MessageHandlingLogic.FindDuplicateAsync(
            "W1ABC", "42", "Test", T0 + MessageHandlingLogic.DuplicateWindow + TimeSpan.FromMinutes(1), _db, default);

        Assert.That(dup, Is.Null);
    }

    [Test]
    public async Task FindDuplicate_WindowSlides_WithLastReceivedAt()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42", receivedAt: T0);
        await _db.SaveChangesAsync();
        var row = await _db.Messages.SingleAsync();
        MessageHandlingLogic.RecordRetransmission(row, T0.AddMinutes(50));
        await _db.SaveChangesAsync();

        // 90 min after the first copy but only 40 after the latest retry.
        var dup = await MessageHandlingLogic.FindDuplicateAsync(
            "W1ABC", "42", "Test", T0.AddMinutes(90), _db, default);

        Assert.That(dup, Is.Not.Null);
        Assert.That(dup!.ReceiveCount, Is.EqualTo(2));
    }

    [Test]
    public async Task FindDuplicate_MatchesIdLessMessagesByText()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "", body: "no id here", receivedAt: T0);
        await _db.SaveChangesAsync();

        var dup = await MessageHandlingLogic.FindDuplicateAsync(
            "W1ABC", "", "no id here", T0.AddMinutes(2), _db, default);

        Assert.That(dup, Is.Not.Null);
    }

    [Test]
    public void RecordRetransmission_IncrementsCountAndTracksLatest()
    {
        var m = new Message { FromCallsign = "W1ABC", ToCallsign = "W3UWU", ReceivedAt = T0 };

        MessageHandlingLogic.RecordRetransmission(m, T0.AddMinutes(3));
        MessageHandlingLogic.RecordRetransmission(m, T0.AddMinutes(1)); // out of order

        Assert.That(m.ReceiveCount, Is.EqualTo(3));
        Assert.That(m.LastReceivedAt, Is.EqualTo(T0.AddMinutes(3)));
    }

    // =========================================================================
    // RepairInboxAsync — folds rows stored before reply-ack parsing existed
    // =========================================================================

    [Test]
    public async Task RepairInbox_StripsSuffixAndMergesRetransmissions()
    {
        SeedMessage(from: "K4DDA-7", to: "W3UWU", body: "Should be off now?{87}", receivedAt: T0, isRead: true);
        SeedMessage(from: "K4DDA-7", to: "W3UWU", body: "Should be off now?{87}", receivedAt: T0.AddMinutes(2));
        SeedMessage(from: "K4DDA-7", to: "W3UWU", body: "Should be off now?{87}", receivedAt: T0.AddMinutes(4));
        SeedMessage(from: "K4DDA-7", to: "W3UWU", body: "Turned off radio now.{89}", receivedAt: T0.AddMinutes(5));
        await _db.SaveChangesAsync();

        var removed = await MessageHandlingLogic.RepairInboxAsync(_db, "W3UWU", default);

        var rows = await _db.Messages.OrderBy(m => m.ReceivedAt).ToListAsync();
        Assert.That(removed, Is.EqualTo(2));
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[0].Body, Is.EqualTo("Should be off now?"));
        Assert.That(rows[0].MessageId, Is.EqualTo("87"));
        Assert.That(rows[0].ReceiveCount, Is.EqualTo(3));
        Assert.That(rows[0].ReceivedAt, Is.EqualTo(T0));
        Assert.That(rows[0].LastReceivedAt, Is.EqualTo(T0.AddMinutes(4)));
        Assert.That(rows[0].IsRead, Is.True);
        Assert.That(rows[1].MessageId, Is.EqualTo("89"));
        Assert.That(rows[1].ReceiveCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RepairInbox_KeepsCopiesSeparatedByMoreThanWindow()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "1", body: "ok", receivedAt: T0);
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "1", body: "ok", receivedAt: T0.AddDays(3));
        await _db.SaveChangesAsync();

        var removed = await MessageHandlingLogic.RepairInboxAsync(_db, "W3UWU", default);

        Assert.That(removed, Is.Zero);
        Assert.That(await _db.Messages.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task RepairInbox_LeavesOutboundRowsAlone()
    {
        SeedMessage(from: "W3UWU", to: "K4DDA-7", messageId: "5", body: "hi", receivedAt: T0);
        SeedMessage(from: "W3UWU", to: "K4DDA-7", messageId: "5", body: "hi", receivedAt: T0.AddMinutes(1));
        await _db.SaveChangesAsync();

        var removed = await MessageHandlingLogic.RepairInboxAsync(_db, "w3uwu", default);

        Assert.That(removed, Is.Zero);
    }

    [Test]
    public async Task RepairInbox_IsIdempotent()
    {
        SeedMessage(from: "K4DDA-7", to: "W3UWU", body: "x{1}", receivedAt: T0);
        SeedMessage(from: "K4DDA-7", to: "W3UWU", body: "x{1}", receivedAt: T0.AddMinutes(1));
        await _db.SaveChangesAsync();

        await MessageHandlingLogic.RepairInboxAsync(_db, "W3UWU", default);
        var second = await MessageHandlingLogic.RepairInboxAsync(_db, "W3UWU", default);

        Assert.That(second, Is.Zero);
        Assert.That((await _db.Messages.SingleAsync()).ReceiveCount, Is.EqualTo(2));
    }

    // =========================================================================
    // TryApplyAckAsync — marks sent message acknowledged
    // =========================================================================

    [Test]
    public async Task TryApplyAck_MarksMessageAcknowledgedAndReturnsId()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying, nextRetryAt: DateTime.UtcNow.AddSeconds(30));
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            fromCallsign: "W1ABC",
            originalMsgId: "7",
            db: _db,
            ourCallsign: "W3UWU",
            ct: default);

        Assert.That(ackedId, Is.Not.Null);

        var msg = await _db.Messages.SingleAsync();
        Assert.That(msg.AckSent, Is.True);
        Assert.That(msg.RetryState, Is.EqualTo(RetryState.Acknowledged));
        Assert.That(msg.NextRetryAt, Is.Null);
    }

    [Test]
    public async Task TryApplyAck_ReturnedId_MatchesMessagePrimaryKey()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();
        var expectedId = (await _db.Messages.SingleAsync()).Id;

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "7", _db, "W3UWU", default);

        Assert.That(ackedId, Is.EqualTo(expectedId));
    }

    [Test]
    public async Task TryApplyAck_IsCaseInsensitiveForBothCallsigns()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();

        // Lowercase callsigns from the incoming packet should still match.
        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            fromCallsign: "w1abc",
            originalMsgId: "7",
            db: _db,
            ourCallsign: "w3uwu",
            ct: default);

        Assert.That(ackedId, Is.Not.Null);
        Assert.That((await _db.Messages.SingleAsync()).AckSent, Is.True);
    }

    [Test]
    public async Task TryApplyAck_ReturnsNull_WhenNoMatchingSentMessage()
    {
        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "999", _db, "W3UWU", default);

        Assert.That(ackedId, Is.Null);
    }

    [Test]
    public async Task TryApplyAck_ReturnsNull_WhenMessageAlreadyAcknowledged()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            ackSent: true, retryState: RetryState.Acknowledged);
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "7", _db, "W3UWU", default);

        Assert.That(ackedId, Is.Null);
    }

    [Test]
    public async Task TryApplyAck_ReturnsNull_WhenMessageIdWrongButCallsignsMatch()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "8", _db, "W3UWU", default);

        Assert.That(ackedId, Is.Null);
        Assert.That((await _db.Messages.SingleAsync()).AckSent, Is.False);
    }

    [Test]
    public async Task TryApplyAck_DoesNotAckMessageSentToWrongStation()
    {
        // Our message is addressed to W1ABC, but the ACK arrives from W9XYZ.
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            fromCallsign: "W9XYZ",
            originalMsgId: "7",
            db: _db,
            ourCallsign: "W3UWU",
            ct: default);

        Assert.That(ackedId, Is.Null);
        Assert.That((await _db.Messages.SingleAsync()).AckSent, Is.False);
    }

    // =========================================================================
    // TryExtractThirdPartyInner — raw TNC2 → (innerRaw, innerSender)
    // =========================================================================

    // Real-world packet: igate W3UWU forwarding a message from W3UWU-9
    [TestCase(
        "W3UWU>APDW18,WE4MB-3*,WIDE1*,WIDE2-1:}W3UWU-9>APDR16,TCPIP,W3UWU*::W3UWU    :Hiii{3",
        true,
        "W3UWU-9>APDR16,TCPIP,W3UWU*::W3UWU    :Hiii{3",
        "W3UWU-9")]
    // Minimal well-formed third-party frame
    [TestCase(
        "IGATE>APRS:}W1ABC>APRS::W3UWU   :Hello{1",
        true,
        "W1ABC>APRS::W3UWU   :Hello{1",
        "W1ABC")]
    // Not a third-party packet (no '}' after the colon)
    [TestCase(
        "W1ABC>APRS,WIDE2-1:=4903.50N/07201.75W-Test",
        false,
        "",
        "")]
    // Empty string
    [TestCase("", false, "", "")]
    // Colon present but followed by a non-'}' character
    [TestCase("W1ABC>APRS:Hello world", false, "", "")]
    // Third-party prefix with nothing after '}' (empty inner content)
    [TestCase("W1ABC>APRS:}", false, "", "")]
    public void TryExtractThirdPartyInner_ParsesCorrectly(
        string rawPacket,
        bool expectedSuccess,
        string expectedInnerRaw,
        string expectedInnerSender)
    {
        var success = MessageHandlingLogic.TryExtractThirdPartyInner(
            rawPacket, out var innerRaw, out var innerSender);

        Assert.That(success, Is.EqualTo(expectedSuccess));
        Assert.That(innerRaw, Is.EqualTo(expectedInnerRaw));
        Assert.That(innerSender, Is.EqualTo(expectedInnerSender));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void SeedMessage(
        string from,
        string to,
        string messageId = "",
        string body = "Test",
        bool ackSent = false,
        RetryState retryState = RetryState.Pending,
        DateTime? nextRetryAt = null,
        DateTime? receivedAt = null,
        bool isRead = false)
    {
        _db.Messages.Add(new Message
        {
            FromCallsign = from,
            ToCallsign = to,
            Body = body,
            MessageId = messageId,
            ReceivedAt = receivedAt ?? DateTime.UtcNow,
            IsRead = isRead,
            AckSent = ackSent,
            ReplySent = false,
            RetryState = retryState,
            MaxRetries = 5,
            NextRetryAt = nextRetryAt,
        });
    }
}

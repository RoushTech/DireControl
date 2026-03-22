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
    public void TryParseAck_ClassifiesBodyCorrectly(string body, bool expectedIsAck, string expectedId)
    {
        var isAck = MessageHandlingLogic.TryParseAck(body, out var id);

        Assert.That(isAck, Is.EqualTo(expectedIsAck));
        Assert.That(id, Is.EqualTo(expectedId));
    }

    // =========================================================================
    // IsMessageDuplicateAsync — dedup query
    // =========================================================================

    [Test]
    public async Task IsMessageDuplicate_ReturnsFalse_WhenNoMatchingMessageExists()
    {
        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W1ABC", "42", _db, default);

        Assert.That(isDuplicate, Is.False);
    }

    [Test]
    public async Task IsMessageDuplicate_ReturnsTrue_WhenExactMatchExists()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42");
        await _db.SaveChangesAsync();

        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W1ABC", "42", _db, default);

        Assert.That(isDuplicate, Is.True);
    }

    [Test]
    public async Task IsMessageDuplicate_ReturnsFalse_WhenFromCallsignDiffers()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42");
        await _db.SaveChangesAsync();

        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W9XYZ", "42", _db, default);

        Assert.That(isDuplicate, Is.False);
    }

    [Test]
    public async Task IsMessageDuplicate_ReturnsFalse_WhenMessageIdDiffers()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42");
        await _db.SaveChangesAsync();

        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W1ABC", "99", _db, default);

        Assert.That(isDuplicate, Is.False);
    }

    [Test]
    public async Task IsMessageDuplicate_DoesNotMatchAcrossMultipleSenders()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "1");
        SeedMessage(from: "W2DEF", to: "W3UWU", messageId: "2");
        await _db.SaveChangesAsync();

        // Each callsign's message ID is independent.
        Assert.That(await MessageHandlingLogic.IsMessageDuplicateAsync("W1ABC", "2", _db, default), Is.False);
        Assert.That(await MessageHandlingLogic.IsMessageDuplicateAsync("W2DEF", "1", _db, default), Is.False);
        Assert.That(await MessageHandlingLogic.IsMessageDuplicateAsync("W1ABC", "1", _db, default), Is.True);
        Assert.That(await MessageHandlingLogic.IsMessageDuplicateAsync("W2DEF", "2", _db, default), Is.True);
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
        DateTime? nextRetryAt = null)
    {
        _db.Messages.Add(new Message
        {
            FromCallsign = from,
            ToCallsign = to,
            Body = body,
            MessageId = messageId,
            ReceivedAt = DateTime.UtcNow,
            IsRead = false,
            AckSent = ackSent,
            ReplySent = false,
            RetryState = retryState,
            MaxRetries = 5,
            NextRetryAt = nextRetryAt,
        });
    }
}

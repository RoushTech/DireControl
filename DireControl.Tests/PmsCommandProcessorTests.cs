using System.Text;
using DireControl.Api.Services;
using DireControl.Api.Services.Pms;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// PMS line-protocol tests: the command processor against the real EF-backed
/// mail store (in-memory SQLite), plus the byte-stream session handler fed
/// byte-by-byte through a fake AX.25 session.
/// </summary>
[TestFixture]
public sealed class PmsCommandProcessorTests
{
    private static readonly DateTime FixedNow = new(2026, 8, 10, 21, 14, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private PmsMailStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<DireControlContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<DireControlContext>().Database.EnsureCreated();
        _store = new PmsMailStore(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private PmsCommandProcessor Processor(string caller = "KI4XYZ") =>
        new(_store, caller, "W3UWU", () => FixedNow);

    private async Task<PmsMessage> Seed(
        string from, string to, string subject,
        PmsMessageType type = PmsMessageType.Private,
        bool killed = false, DateTime? readAt = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var message = new PmsMessage
        {
            Type = type,
            FromCallsign = from,
            ToCallsign = to,
            Subject = subject,
            Body = $"body of {subject}",
            CreatedAt = FixedNow,
            IsKilled = killed,
            ReadAt = readAt,
        };
        db.PmsMessages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    // ---------------------------------------------------------------- banner

    [Test]
    public async Task Banner_IncludesSidAndUnreadCount()
    {
        await Seed("W3UWU", "KI4XYZ", "one");
        await Seed("W3UWU", "KI4XYZ", "two");
        await Seed("W3UWU", "N4WR", "not yours");

        var banner = await Processor().GetBannerAsync("Welcome!");

        Assert.That(banner[0], Is.EqualTo("[DireControl-1.0-PMS$]"));
        Assert.That(banner[1], Is.EqualTo("Welcome!"));
        Assert.That(banner[2], Is.EqualTo("You have 2 new messages."));
    }

    [Test]
    public async Task Banner_NoUnread_OmitsCountLine()
    {
        var banner = await Processor().GetBannerAsync("Hi");
        Assert.That(banner, Has.Count.EqualTo(2));
    }

    // ---------------------------------------------------------------- listing

    [Test]
    public async Task List_ShowsOnlyVisibleMail()
    {
        await Seed("W3UWU", "KI4XYZ", "to caller");
        await Seed("KI4XYZ", "N4WR", "from caller");
        await Seed("W3UWU", "ALL", "bulletin", PmsMessageType.Bulletin);
        await Seed("W3UWU", "N4WR", "private to someone else");
        await Seed("W3UWU", "KI4XYZ", "killed", killed: true);

        var lines = await Processor().ProcessLineAsync("L");

        Assert.That(lines[^1], Is.EqualTo("3 messages."));
        var listing = string.Join('\n', lines);
        Assert.That(listing, Does.Contain("to caller"));
        Assert.That(listing, Does.Contain("from caller"));
        Assert.That(listing, Does.Contain("bulletin"));
        Assert.That(listing, Does.Not.Contain("someone else"));
        Assert.That(listing, Does.Not.Contain("killed"));
    }

    [Test]
    public async Task List_CallerSsidIsStripped()
    {
        await Seed("W3UWU", "KI4XYZ", "for the base call");

        var lines = await Processor(caller: "KI4XYZ-7").ProcessLineAsync("L");

        Assert.That(lines[^1], Is.EqualTo("1 message."), "KI4XYZ-7 sees mail for KI4XYZ");
    }

    // ---------------------------------------------------------------- reading

    [Test]
    public async Task Read_MarksReadOnlyForAddressee()
    {
        var toCaller = await Seed("W3UWU", "KI4XYZ", "yours");
        var bulletin = await Seed("W3UWU", "ALL", "bulletin", PmsMessageType.Bulletin);

        var lines = await Processor().ProcessLineAsync($"R {toCaller.Id}");
        Assert.That(string.Join('\n', lines), Does.Contain("Subject: yours"));
        Assert.That(string.Join('\n', lines), Does.Contain($"body of yours"));

        await Processor().ProcessLineAsync($"R {bulletin.Id}");

        Assert.That((await _store.GetAsync(toCaller.Id))!.ReadAt, Is.Not.Null, "addressee read");
        Assert.That((await _store.GetAsync(bulletin.Id))!.ReadAt, Is.Null, "bulletin reader is not the addressee");
    }

    [Test]
    public async Task Read_InvisibleOrMissing_Refused()
    {
        var other = await Seed("W3UWU", "N4WR", "not yours");

        Assert.That((await Processor().ProcessLineAsync($"R {other.Id}"))[0], Does.StartWith("No such message"));
        Assert.That((await Processor().ProcessLineAsync("R 999"))[0], Does.StartWith("No such message"));
        Assert.That((await Processor().ProcessLineAsync("R x"))[0], Does.StartWith("Usage"));
    }

    // ---------------------------------------------------------------- compose

    [Test]
    public async Task Compose_PrivateMail_FullFlow()
    {
        var p = Processor();

        var r1 = await p.ProcessLineAsync("SP W3UWU");
        Assert.That(r1[0], Is.EqualTo("Subject:"));
        Assert.That(p.AtCommandPrompt, Is.False);

        var r2 = await p.ProcessLineAsync("Antenna question");
        Assert.That(r2[0], Does.StartWith("Enter message"));

        await p.ProcessLineAsync("Do you still have the spare J-pole?");
        await p.ProcessLineAsync("Asking for a friend.");
        var r3 = await p.ProcessLineAsync("/EX");

        Assert.That(r3[0], Does.Match(@"^Msg #\d+ saved\.$"));
        Assert.That(p.AtCommandPrompt, Is.True);

        var saved = (await _store.ListVisibleAsync("W3UWU")).Single();
        Assert.That(saved.Type, Is.EqualTo(PmsMessageType.Private));
        Assert.That(saved.FromCallsign, Is.EqualTo("KI4XYZ"));
        Assert.That(saved.ToCallsign, Is.EqualTo("W3UWU"));
        Assert.That(saved.Subject, Is.EqualTo("Antenna question"));
        Assert.That(saved.Body, Is.EqualTo("Do you still have the spare J-pole?\nAsking for a friend."));
        Assert.That(saved.CreatedAt, Is.EqualTo(FixedNow));
    }

    [Test]
    public async Task Compose_CtrlZ_AlsoTerminates()
    {
        var p = Processor();
        await p.ProcessLineAsync("S W3UWU");
        await p.ProcessLineAsync("subject");
        await p.ProcessLineAsync("body");
        var result = await p.ProcessLineAsync("\x1a");

        Assert.That(result[0], Does.Contain("saved"));
    }

    [Test]
    public async Task Compose_Bulletin_UsesSbCategory()
    {
        var p = Processor();
        await p.ProcessLineAsync("SB ALL");
        await p.ProcessLineAsync("Club net moved");
        await p.ProcessLineAsync("Now at 8pm.");
        await p.ProcessLineAsync("/EX");

        var saved = (await _store.ListVisibleAsync("W1AAA")).Single();
        Assert.That(saved.Type, Is.EqualTo(PmsMessageType.Bulletin));
        Assert.That(saved.ToCallsign, Is.EqualTo("ALL"));
    }

    [Test]
    public async Task Compose_MissingCallsign_Usage()
    {
        Assert.That((await Processor().ProcessLineAsync("S"))[0], Does.StartWith("Usage"));
    }

    // ---------------------------------------------------------------- killing

    [Test]
    public async Task Kill_AllowedForSenderOrAddressee_Only()
    {
        var toCaller = await Seed("W3UWU", "KI4XYZ", "kill me");
        var fromCaller = await Seed("KI4XYZ", "N4WR", "mine");
        var unrelated = await Seed("W3UWU", "N4WR", "hands off");

        Assert.That((await Processor().ProcessLineAsync($"K {toCaller.Id}"))[0], Does.Contain("killed"));
        Assert.That((await Processor().ProcessLineAsync($"K {fromCaller.Id}"))[0], Does.Contain("killed"));
        Assert.That((await Processor().ProcessLineAsync($"K {unrelated.Id}"))[0], Does.StartWith("Cannot kill"));

        Assert.That((await _store.GetAsync(unrelated.Id))!.IsKilled, Is.False);
    }

    [Test]
    public async Task KillMine_KillsOnlyReadMailAddressedToCaller()
    {
        await Seed("W3UWU", "KI4XYZ", "read", readAt: FixedNow);
        var unread = await Seed("W3UWU", "KI4XYZ", "unread");
        var othersRead = await Seed("W3UWU", "N4WR", "other", readAt: FixedNow);

        var result = await Processor().ProcessLineAsync("KM");

        Assert.That(result[0], Is.EqualTo("1 message(s) killed."));
        Assert.That((await _store.GetAsync(unread.Id))!.IsKilled, Is.False);
        Assert.That((await _store.GetAsync(othersRead.Id))!.IsKilled, Is.False);
    }

    // ---------------------------------------------------------------- misc

    [Test]
    public async Task UnknownCommand_AndBye()
    {
        var p = Processor();
        Assert.That((await p.ProcessLineAsync("XYZZY"))[0], Is.EqualTo("? Unknown command. H for help."));
        Assert.That(p.DisconnectRequested, Is.False);

        var bye = await p.ProcessLineAsync("B");
        Assert.That(bye[0], Is.EqualTo("73 de W3UWU PMS"));
        Assert.That(p.DisconnectRequested, Is.True);
    }

    [Test]
    public async Task Help_And_Version_And_EmptyLine()
    {
        var p = Processor();
        Assert.That(await p.ProcessLineAsync("H"), Has.Count.GreaterThan(5));
        Assert.That((await p.ProcessLineAsync("V"))[0], Is.EqualTo("DireControl-1.0-PMS"));
        Assert.That(await p.ProcessLineAsync("   "), Is.Empty);
    }

    // ------------------------------------------------- session handler (bytes)

    [Test]
    public async Task SessionHandler_ByteByByte_AllLineEndingsWork()
    {
        // Settings row (banner text) comes from the DB the handler scopes into.
        var handler = new PmsSessionHandler(
            _store,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DireControlOptions { OurCallsign = "W3UWU-10" }),
            NullLogger<PmsSessionHandler>.Instance);

        var session = new FakeAx25Session(local: "W3UWU-1", remote: "KI4XYZ-7");
        var handlerTask = handler.HandleSessionAsync(session, CancellationToken.None);

        // One byte at a time, mixing CR, LF, and CRLF line endings.
        void FeedBytes(string text)
        {
            foreach (var b in Encoding.ASCII.GetBytes(text))
                session.Deliver([b]);
        }

        FeedBytes("V\r");        // CR
        FeedBytes("H\n");        // LF
        FeedBytes("L\r\n");      // CRLF — must count as ONE line, not two
        FeedBytes("B\r");

        await handlerTask.WaitAsync(TimeSpan.FromSeconds(5));

        var transcript = Encoding.ASCII.GetString(session.AllSent());
        Assert.That(transcript, Does.StartWith("[DireControl-1.0-PMS$]\r"));
        Assert.That(transcript, Does.Contain("DireControl-1.0-PMS\r"), "V response");
        Assert.That(transcript, Does.Contain("B)ye"), "H response");
        Assert.That(transcript, Does.Contain("No messages."), "L response — exactly once proves CRLF is one line");
        Assert.That(transcript.Split("No messages.").Length - 1, Is.EqualTo(1),
            "CRLF handled as a single line ending");
        Assert.That(transcript, Does.Contain("73 de W3UWU PMS"));
        Assert.That(session.DisconnectRequested, Is.True, "B disconnects");
        Assert.That(transcript, Does.Contain(">\r"), "command prompt shown");
    }
}

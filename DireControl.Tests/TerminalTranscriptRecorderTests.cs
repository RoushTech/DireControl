using System.Text;
using DireControl.Api.Services.Terminal;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Transcript recorder tests: chunk coalescing, direction splits, flush, and
/// finalisation — against in-memory SQLite.
/// </summary>
[TestFixture]
public sealed class TerminalTranscriptRecorderTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private TerminalTranscriptRecorder _recorder = null!;

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

        _recorder = new TerminalTranscriptRecorder(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TerminalTranscriptRecorder>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _recorder.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private DireControlContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<DireControlContext>();

    [Test]
    public async Task Start_CreatesHeaderRow()
    {
        await _recorder.StartAsync("abc123", TerminalSessionOrigin.Outbound, 0, "W3UWU", "KB4BBS-7", "WIDE1-1");

        using var scope = _provider.CreateScope();
        var record = Db(scope).TerminalSessionRecords.Single();
        Assert.That(record.SessionId, Is.EqualTo("abc123"));
        Assert.That(record.Origin, Is.EqualTo(TerminalSessionOrigin.Outbound));
        Assert.That(record.RemoteCallsign, Is.EqualTo("KB4BBS-7"));
        Assert.That(record.EndedAt, Is.Null);
    }

    [Test]
    public async Task Append_CoalescesSameDirectionRuns()
    {
        await _recorder.StartAsync("s1", TerminalSessionOrigin.Outbound, 0, "W3UWU", "KB4BBS-7", "");

        // Three same-direction appends then a direction change then another run.
        _recorder.Append("s1", TranscriptDirection.Received, "Wel"u8);
        _recorder.Append("s1", TranscriptDirection.Received, "come"u8);
        _recorder.Append("s1", TranscriptDirection.Received, "!\r"u8);
        _recorder.Append("s1", TranscriptDirection.Sent, "L\r"u8);
        _recorder.Append("s1", TranscriptDirection.Received, "Msg# ..."u8);
        await _recorder.FlushAsync();

        using var scope = _provider.CreateScope();
        var chunks = Db(scope).TerminalTranscriptChunks.OrderBy(c => c.Id).ToList();
        Assert.That(chunks, Has.Count.EqualTo(3), "runs coalesce, direction changes split");
        Assert.That(Encoding.ASCII.GetString(chunks[0].Data), Is.EqualTo("Welcome!\r"));
        Assert.That(chunks[0].Direction, Is.EqualTo(TranscriptDirection.Received));
        Assert.That(Encoding.ASCII.GetString(chunks[1].Data), Is.EqualTo("L\r"));
        Assert.That(chunks[1].Direction, Is.EqualTo(TranscriptDirection.Sent));
        Assert.That(chunks[2].Direction, Is.EqualTo(TranscriptDirection.Received));
    }

    [Test]
    public async Task Finalize_WritesPendingAndClosesHeader()
    {
        await _recorder.StartAsync("s2", TerminalSessionOrigin.Inbound, 1, "W3UWU-1", "KI4XYZ", "");
        _recorder.Append("s2", TranscriptDirection.Received, new byte[100]);
        _recorder.Append("s2", TranscriptDirection.Sent, new byte[40]);

        await _recorder.FinalizeAsync("s2", "RemoteDisc");

        using var scope = _provider.CreateScope();
        var record = Db(scope).TerminalSessionRecords.Include(r => r.Chunks).Single();
        Assert.That(record.EndedAt, Is.Not.Null);
        Assert.That(record.EndReason, Is.EqualTo("RemoteDisc"));
        Assert.That(record.BytesIn, Is.EqualTo(100));
        Assert.That(record.BytesOut, Is.EqualTo(40));
        Assert.That(record.Chunks, Has.Count.EqualTo(2), "pending bytes flushed at finalize");
    }

    [Test]
    public async Task AppendAfterFinalize_IsIgnored()
    {
        await _recorder.StartAsync("s3", TerminalSessionOrigin.Outbound, 0, "W3UWU", "KB4BBS", "");
        await _recorder.FinalizeAsync("s3", "LocalRequest");

        _recorder.Append("s3", TranscriptDirection.Received, [1, 2, 3]);
        await _recorder.FlushAsync();

        using var scope = _provider.CreateScope();
        Assert.That(Db(scope).TerminalTranscriptChunks.Count(), Is.Zero);
    }

    [Test]
    public async Task DeletingRecord_CascadesChunks()
    {
        await _recorder.StartAsync("s4", TerminalSessionOrigin.Outbound, 0, "W3UWU", "KB4BBS", "");
        _recorder.Append("s4", TranscriptDirection.Received, [1]);
        await _recorder.FinalizeAsync("s4", null);

        using var scope = _provider.CreateScope();
        var db = Db(scope);
        db.TerminalSessionRecords.Remove(db.TerminalSessionRecords.Single());
        await db.SaveChangesAsync();

        Assert.That(db.TerminalTranscriptChunks.Count(), Is.Zero, "FK cascade");
    }
}

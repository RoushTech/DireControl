using System.Text;
using DireControl.Api.Services;
using DireControl.Modem.Ax25;
using DireControl.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// RF→IS gating and APRS storage through the real ingest path: only APRS
/// (UI frames with PID 0xF0) may reach APRS-IS under our passcode, and an
/// RF info field must never be able to inject extra lines into the login.
/// </summary>
[TestFixture]
public sealed class RfIngestGatingTests
{
    private static byte[] Frame(byte control, byte? pid, string info, string source = "K1ABC") =>
        Ax25Encoder.Encode(new Ax25Frame
        {
            Destination = Ax25Address.Parse("APRS"),
            Source = Ax25Address.Parse(source),
            Path = [Ax25Address.Parse("N1BBS")],
            Control = control,
            Pid = pid,
            Info = Encoding.ASCII.GetBytes(info),
            DestCommandBit = true,
        });

    [Test]
    public async Task AprsUiFrame_IsGatedAndStored()
    {
        using var harness = new IngestHarness();

        var id = await harness.Ingest.IngestAsync(Frame(0x03, 0xF0, "!3518.00N/08508.00W-hello"), 0, null, default);

        Assert.That(id, Is.Not.Null);
        Assert.That(harness.DrainGatedLines(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task IFrame_IsNeitherGatedNorStored()
    {
        using var harness = new IngestHarness();

        // I-frame, N(S)=0 N(R)=0, from another station's BBS session.
        var id = await harness.Ingest.IngestAsync(
            Frame(0x00, 0xF0, "Hello from the BBS, 3 messages waiting"), 0, null, default);

        Assert.That(id, Is.Null);
        Assert.That(harness.DrainGatedLines(), Is.Empty);
        await using var db = harness.CreateDb();
        Assert.That(await db.Packets.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task NetRomUiFrame_IsNeitherGatedNorStored()
    {
        using var harness = new IngestHarness();

        var id = await harness.Ingest.IngestAsync(Frame(0x03, 0xCF, "!3518.00N/08508.00W-netrom"), 0, null, default);

        Assert.That(id, Is.Null);
        Assert.That(harness.DrainGatedLines(), Is.Empty);
    }

    [Test]
    public async Task SupervisoryFrame_IsNotGated()
    {
        using var harness = new IngestHarness();

        await harness.Ingest.IngestAsync(Frame(0x01, null, ""), 0, null, default); // RR

        Assert.That(harness.DrainGatedLines(), Is.Empty);
    }

    [Test]
    public async Task CrLfInInfo_CannotInjectALine()
    {
        using var harness = new IngestHarness();

        await harness.Ingest.IngestAsync(Frame(0x03, 0xF0, ">hi\r\nEVIL>APRS,TCPIP*:!spoof"), 0, null, default);

        var lines = harness.DrainGatedLines();
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Not.Contain("\r").And.Not.Contain("\n"));
        Assert.That(lines[0], Does.EndWith(":>hi"));
    }

    [Test]
    public async Task OwnDigipeatLoopback_IsNotGatedAgain()
    {
        using var harness = new IngestHarness();
        var digipeated = Ax25Encoder.Encode(new Ax25Frame
        {
            Destination = Ax25Address.Parse("APRS"),
            Source = Ax25Address.Parse("K1ABC"),
            Path = [Ax25Address.Parse("N0CALL-10") with { HasBeenRepeated = true }],
            Info = Encoding.ASCII.GetBytes("!3518.00N/08508.00W-hi"),
            DestCommandBit = true,
        });

        await harness.Ingest.IngestAsync(digipeated, 0, null, default, isOwnTransmission: true);

        Assert.That(harness.DrainGatedLines(), Is.Empty);
    }

    [Test]
    public async Task OwnBeaconLoopback_IsStillGated()
    {
        using var harness = new IngestHarness();

        await harness.Ingest.IngestAsync(
            Frame(0x03, 0xF0, "!3518.00N/08508.00W-beacon", source: "N0CALL-10"), 0, null, default,
            isOwnTransmission: true);

        Assert.That(harness.DrainGatedLines(), Has.Count.EqualTo(1));
    }
}

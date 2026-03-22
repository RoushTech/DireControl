using System.Collections.Generic;
using AprsSharp.AprsParser;
using ResolvedPathEntry = DireControl.Data.Models.ResolvedPathEntry;
using DireControl.Enums;
using DireControl.PathParsing;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class PathParserTests
{
    // -------------------------------------------------------------------------
    // IsGenericAlias
    // -------------------------------------------------------------------------

    [TestCase("WIDE1",    true)]
    [TestCase("WIDE2",    true)]
    [TestCase("WIDE1-1",  true)]
    [TestCase("WIDE2-2",  true)]
    [TestCase("WIDE3-3",  true)]
    [TestCase("RELAY",    true)]
    [TestCase("TRACE",    true)]
    [TestCase("TRACE3-3", true)]
    [TestCase("IGATE",    true)]
    [TestCase("GATE",     true)]
    [TestCase("ECHO",     true)]
    [TestCase("NCA",      true)]
    [TestCase("RFONLY",   true)]
    [TestCase("NOGATE",   true)]
    [TestCase("TCPIP",    true)]
    [TestCase("TCPXX",    true)]
    [TestCase("qAR",      true)]
    [TestCase("qAC",      true)]
    [TestCase("qAS",      true)]
    [TestCase("qAO",      true)]
    // Star-marked variants must also be recognised
    [TestCase("WIDE1*",   true)]
    [TestCase("WIDE2-1*", true)]
    // Real callsigns must NOT match
    [TestCase("WE4MB-3",  false)]
    [TestCase("KN6RO-13", false)]
    [TestCase("KC4SAR-5", false)]
    [TestCase("W3UWU",    false)]
    [TestCase("APRS",     false)]
    public void IsGenericAlias_IdentifiesCorrectly(string callsign, bool expected)
    {
        Assert.That(AprsPathParser.IsGenericAlias(callsign), Is.EqualTo(expected));
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — no * markers → direct packet, zero hops
    // -------------------------------------------------------------------------

    /// <summary>
    /// Packet 1 from issue #11.
    /// Raw:  KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:...
    /// No starred entries — packet was heard direct.  No hops should be returned.
    /// </summary>
    [Test]
    public void ExtractViaHops_Packet1_NoStars_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate.");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Packet 2 from issue #11.
    /// Raw:  ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:...
    /// No starred entries — packet was heard direct.  No hops should be returned.
    /// </summary>
    [Test]
    public void ExtractViaHops_Packet2_NoStars_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3319.66NI08432.59W#PHG9250PI-DIREWOLF APRS IGATE/DIGI SENOIA GA 73!");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — with * markers (standard case)
    // -------------------------------------------------------------------------

    [Test]
    public void ExtractViaHops_WithStarMarkers_IncludesOnlyStarredEntries()
    {
        // APRS is the TOCALL; W1DEF* was repeated; WIDE2-1 was not used
        var aprs = new Packet("N1ABC>APRS,W1DEF*,WIDE2-1:!3400.59NT08402.69W&Test");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("W1DEF"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hopCount, Is.EqualTo(1));
    }

    [Test]
    public void ExtractViaHops_WithStarMarkers_MultipleStarred_AliasExcluded()
    {
        // KN6RO-13 is the TOCALL; WIDE1* is a generic alias with no prior real hop
        // (dropped entirely); WE4MB-3* is the only real digi hop; WIDE2 is unused.
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("WE4MB-3"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hopCount, Is.EqualTo(1));
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — TOCALL is unconditionally excluded
    // -------------------------------------------------------------------------

    [Test]
    public void ExtractViaHops_StandardAprsDestination_ExcludedFromHops()
    {
        // APRS is the canonical TOCALL used by most clients; WE4MB-3* is the real digi
        var aprs = new Packet("W1ABC>APRS,WE4MB-3*,WIDE2-1:!3400.59NT08402.69W&Test");
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        // "APRS" must NOT be in the hop list
        Assert.That(hops, Has.None.Matches<ResolvedPathEntry>(h => h.Callsign == "APRS"));
        // "WE4MB-3" (starred real callsign) IS the used hop
        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("WE4MB-3"));
    }

    /// <summary>
    /// TOCALL exclusion must not be conditional on what the TOCALL looks like.
    /// Here the TOCALL is a real callsign (WE4MB-3) rather than a software ID.
    /// </summary>
    [Test]
    public void ExtractViaHops_CallsignTocall_ExcludedFromHops()
    {
        // WE4MB-3 is the TOCALL; WE4MB-3* is a starred hop from a different packet
        var rawPath = new List<string> { "WE4MB-3", "WE4MB-3*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        // TOCALL excluded; one starred hop remains
        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("WE4MB-3"));
        Assert.That(hopCount, Is.EqualTo(1));
    }

    [Test]
    public void ExtractViaHops_EmptyPath_ReturnsEmpty()
    {
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(null);
        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Issue #12 test packet.
    /// Raw path list ["KN6RO-13", "WE4MB-3", "WIDE2"] — no asterisks.
    /// No starred entries → direct packet, zero hops.
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue12Packet_NoStars_DirectPacket_ZeroHops()
    {
        var rawPath = new List<string> { "KN6RO-13", "WE4MB-3", "WIDE2" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    // -------------------------------------------------------------------------
    // Alias entries start as Known=false; real entries also start as Known=false.
    // Coordinate resolution (setting Known=true) is performed later by
    // ResolvePathCoordinatesAsync and requires a station database lookup.
    // -------------------------------------------------------------------------

    [Test]
    public void ExtractViaHops_AllEntriesInitiallyNotKnown()
    {
        // Use a packet with starred hops so we actually get entries to inspect
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test");
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var h in hops)
                Assert.That(h.Known, Is.False);
        });
    }

    // -------------------------------------------------------------------------
    // Issue #13 — direct packet (unused alias, no hops)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Issue #13 test packet.
    /// Raw:  KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN
    /// TOCALL WE4MB-3 must be excluded; WIDE1 has no '*' — unused alias, not a hop.
    /// Packet is direct: zero hops returned.
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue13Packet_UnusedWide1_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Direct packet with no path entries at all — only TOCALL present, no via entries.
    /// </summary>
    [Test]
    public void ExtractViaHops_NoPathEntries_DirectPacket_ZeroHops()
    {
        var rawPath = new List<string> { "APRS" };  // only the TOCALL
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Digipeated packet — WE4MB-3 is starred, WIDE2 is an unused alias not starred.
    /// Only the starred entry (WE4MB-3) should appear; hop count = 1.
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue13_StarredRealCallsign_OneHop()
    {
        var rawPath = new List<string> { "APRS", "WE4MB-3*", "WIDE2" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("WE4MB-3"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hopCount, Is.EqualTo(1));
    }

    // -------------------------------------------------------------------------
    // ParseTnc2Header — source / TOCALL / rawPath extraction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Issue #14 test packet.
    /// Raw: W4PFT-1>KN6RO-13,WE4MB-3*,WIDE2:@020107z3422.75N/08313.65W#…
    /// After the AX.25 H-bit fix, RawPacket contains the starred path.
    /// ParseTnc2Header must return the via portion without the TOCALL.
    /// </summary>
    [Test]
    public void ParseTnc2Header_Issue14_DigipeatedPacket()
    {
        const string raw = "W4PFT-1>KN6RO-13,WE4MB-3*,WIDE2:@020107z3422.75N/08313.65W#WX3in1Mini Updated 03-20-2023 U=14.2V";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.That(source, Is.EqualTo("W4PFT-1"));
        Assert.That(tocall, Is.EqualTo("KN6RO-13"));
        Assert.That(rawPath, Is.EqualTo("WE4MB-3*,WIDE2"));
    }

    /// <summary>
    /// Issue #11 — Packet 1.
    /// Raw: KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!…
    /// No asterisks — direct packet stored as-is.
    /// </summary>
    [Test]
    public void ParseTnc2Header_Issue11_Packet1_NoAsterisks()
    {
        const string raw = "KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate.";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.That(source, Is.EqualTo("KC4SAR-5"));
        Assert.That(tocall, Is.EqualTo("KN6RO-13"));
        Assert.That(rawPath, Is.EqualTo("WIDE1,WE4MB-3,WIDE2"));
    }

    /// <summary>
    /// Issue #13 test packet — TOCALL is a real callsign (WE4MB-3), one via entry (WIDE1).
    /// Raw: KM4KMO-14>WE4MB-3,WIDE1:@…
    /// </summary>
    [Test]
    public void ParseTnc2Header_Issue13_CallsignTocall_OneViaEntry()
    {
        const string raw = "KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.That(source, Is.EqualTo("KM4KMO-14"));
        Assert.That(tocall, Is.EqualTo("WE4MB-3"));
        Assert.That(rawPath, Is.EqualTo("WIDE1"));
    }

    /// <summary>
    /// Packet with no via entries — only TOCALL present after '>'.
    /// Raw: W1ABC>APRS:!…
    /// rawPath must be empty.
    /// </summary>
    [Test]
    public void ParseTnc2Header_TocallOnly_EmptyRawPath()
    {
        const string raw = "W1ABC>APRS:!3400.59NT08402.69W&Test";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.That(source, Is.EqualTo("W1ABC"));
        Assert.That(tocall, Is.EqualTo("APRS"));
        Assert.That(rawPath, Is.EqualTo(string.Empty));
    }

    /// <summary>
    /// Multiple starred hops — both entries appear in rawPath with asterisks intact.
    /// Raw: KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!…
    /// </summary>
    [Test]
    public void ParseTnc2Header_MultipleStarredHops_AsterisksPreserved()
    {
        const string raw = "KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.That(source, Is.EqualTo("KC4SAR-5"));
        Assert.That(tocall, Is.EqualTo("KN6RO-13"));
        Assert.That(rawPath, Is.EqualTo("WIDE1*,WE4MB-3*,WIDE2"));
    }

    // -------------------------------------------------------------------------
    // Issue #17 — starred generic aliases must never become hops
    // -------------------------------------------------------------------------

    /// <summary>
    /// Primary case from issue #17.
    /// Path: KA4EMA-3*,WIDE1*,WE4MB-3*,WIDE2*
    /// KA4EMA-3 consumed WIDE1; WE4MB-3 consumed WIDE2.
    /// Exactly two real hops; WIDE1 and WIDE2 are metadata, not hops.
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue17_StarredAliases_NotHops_AttachedAsAliasUsed()
    {
        var aprs = new Packet("KR4BRU-9>APMI0A,KA4EMA-3*,WIDE1*,WE4MB-3*,WIDE2*:/020150z3504.35N/08511.40Wa065/000/A=000751Ramble-Ambulance");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hopCount, Is.EqualTo(2));
        Assert.That(hops.Count, Is.EqualTo(2));

        Assert.That(hops[0].Callsign, Is.EqualTo("KA4EMA-3"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hops[0].AliasUsed, Is.EqualTo("WIDE1"));

        Assert.That(hops[1].Callsign, Is.EqualTo("WE4MB-3"));
        Assert.That(hops[1].HopIndex, Is.EqualTo(2));
        Assert.That(hops[1].AliasUsed, Is.EqualTo("WIDE2"));

        Assert.That(hops, Has.None.Matches<ResolvedPathEntry>(h => AprsPathParser.IsGenericAlias(h.Callsign)));
    }

    /// <summary>
    /// Single real hop with alias consumed — VK2RXX consumed WIDE2.
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue17_SingleHopWithAlias()
    {
        var rawPath = new List<string> { "APRS", "VK2RXX*", "WIDE2*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hopCount, Is.EqualTo(1));
        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("VK2RXX"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hops[0].AliasUsed, Is.EqualTo("WIDE2"));

        Assert.That(hops, Has.None.Matches<ResolvedPathEntry>(h => AprsPathParser.IsGenericAlias(h.Callsign)));
    }

    /// <summary>
    /// Path with only unused alias entries — direct packet, zero hops.
    /// Raw: KM4KMO-14>WE4MB-3,WIDE1
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue17_UnusedAlias_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }

    /// <summary>
    /// Real starred hop with no alias following it — aliasUsed must be null.
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue17_RealHopNoAlias_AliasUsedIsNull()
    {
        var rawPath = new List<string> { "APRS", "WE4MB-3*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hopCount, Is.EqualTo(1));
        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("WE4MB-3"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hops[0].AliasUsed, Is.Null);
    }

    /// <summary>
    /// Mixed: first real hop consumes WIDE1, second real hop has no alias.
    /// Path: VK2RXX*,WIDE1*,VK2RYY*
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue17_Mixed_FirstHopAlias_SecondHopNoAlias()
    {
        var rawPath = new List<string> { "APRS", "VK2RXX*", "WIDE1*", "VK2RYY*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hopCount, Is.EqualTo(2));
        Assert.That(hops.Count, Is.EqualTo(2));

        Assert.That(hops[0].Callsign, Is.EqualTo("VK2RXX"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hops[0].AliasUsed, Is.EqualTo("WIDE1"));

        Assert.That(hops[1].Callsign, Is.EqualTo("VK2RYY"));
        Assert.That(hops[1].HopIndex, Is.EqualTo(2));
        Assert.That(hops[1].AliasUsed, Is.Null);

        Assert.That(hops, Has.None.Matches<ResolvedPathEntry>(h => AprsPathParser.IsGenericAlias(h.Callsign)));
    }

    /// <summary>
    /// TOCALL is a real callsign — must still be excluded.
    /// Path: KC4SAR-5>KN6RO-13,WE4MB-3*,WIDE2*
    /// </summary>
    [Test]
    public void ExtractViaHops_Issue17_CallsignTocall_ExcludedAlias_AliasUsedAttached()
    {
        var rawPath = new List<string> { "KN6RO-13", "WE4MB-3*", "WIDE2*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hopCount, Is.EqualTo(1));
        Assert.That(hops, Has.Count.EqualTo(1));
        Assert.That(hops[0].Callsign, Is.EqualTo("WE4MB-3"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hops[0].AliasUsed, Is.EqualTo("WIDE2"));

        Assert.That(hops, Has.None.Matches<ResolvedPathEntry>(h => AprsPathParser.IsGenericAlias(h.Callsign)));
    }

    // -------------------------------------------------------------------------
    // "Digi-before-alias" pattern — unstarred callsign immediately before a
    // starred generic alias, e.g. "W4CAT-2,WIDE2*".
    // Some digipeaters write their own callsign (unstarred) then mark the alias
    // they consumed instead of the more common "W4CAT-2*,WIDE2-1" convention.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Exact real-world path reported by user:
    /// NT4UX-2,WIDE2,W4CAT-2,WIDE2*,qAR,N8DEU-7
    /// W4CAT-2 (unstarred) immediately precedes WIDE2* — it consumed that alias.
    /// Should produce exactly one hop (W4CAT-2, AliasUsed=WIDE2) and IgateRfDigi.
    /// </summary>
    [Test]
    public void ExtractViaHops_UnstarredDigiBeforeStarredAlias_CountedAsHop()
    {
        // APRS is TOCALL; WIDE2 unused; W4CAT-2 unstarred before WIDE2*; then igated
        var rawPath = new List<string> { "APRS", "WIDE2", "W4CAT-2", "WIDE2*", "qAR", "N8DEU-7" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hopCount, Is.EqualTo(1));
        Assert.That(hops.Count, Is.EqualTo(2));
        Assert.That(hops[0].Callsign, Is.EqualTo("W4CAT-2"));
        Assert.That(hops[0].HopIndex, Is.EqualTo(1));
        Assert.That(hops[0].AliasUsed, Is.EqualTo("WIDE2"));
        Assert.That(hops[0].IsIgate, Is.False);

        Assert.That(hops[1].Callsign, Is.EqualTo("N8DEU-7"));
        Assert.That(hops[1].IsIgate, Is.True);

        Assert.That(hops, Has.None.Matches<ResolvedPathEntry>(h => AprsPathParser.IsGenericAlias(h.Callsign)));
    }

    /// <summary>
    /// Unstarred real callsign NOT followed by a starred generic alias must still
    /// be treated as unused (direct packet, zero hops).
    /// Path: APRS, W4CAT-2, WIDE2  — both unstarred, nothing consumed.
    /// </summary>
    [Test]
    public void ExtractViaHops_UnstarredDigiWithoutStarredAlias_NotCountedAsHop()
    {
        var rawPath = new List<string> { "APRS", "W4CAT-2", "WIDE2" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.That(hops, Is.Empty);
        Assert.That(hopCount, Is.EqualTo(0));
    }
}

// -------------------------------------------------------------------------
// PathResolver.Resolve — full-path classification including source and home
// -------------------------------------------------------------------------

[TestFixture]
public class PathResolverTests
{
    // No station coordinates needed for these tests; pass null for stationLookup.
    private static readonly IReadOnlyDictionary<string, (double Lat, double Lon)>? MockStations = null;

    private static IEnumerable<TestCaseData> PathClassificationData()
    {
        // Direct RF — no hops, no q construct
        yield return new TestCaseData(
            "W1ABC>APRS:!data",
            new[] { "W1ABC", "W3UWU" },
            new string?[] { null, null },
            0, HeardVia.Direct);

        // RF via one digi
        yield return new TestCaseData(
            "W1ABC>APRS,KD4RFT-10*,WIDE1*:!data",
            new[] { "W1ABC", "KD4RFT-10", "W3UWU" },
            new string?[] { null, "WIDE1", null },
            1, HeardVia.Digi);

        // RF via two digis
        yield return new TestCaseData(
            "W1ABC>APRS,KD4RFT-10*,WIDE1*,WE4MB-3*,WIDE2*:!data",
            new[] { "W1ABC", "KD4RFT-10", "WE4MB-3", "W3UWU" },
            new string?[] { null, "WIDE1", "WIDE2", null },
            2, HeardVia.Digi);

        // igated from RF direct — qAR, no RF hops, igate included
        yield return new TestCaseData(
            "W1ABC>APRS,qAR,VK2ION:!data",
            new[] { "W1ABC", "VK2ION", "W3UWU" },
            new string?[] { null, null, null },
            0, HeardVia.IgateRf);

        // igated from RF via digi — qAR with starred hops before it, igate included
        yield return new TestCaseData(
            "W1ABC>APRS,KD4RFT-10*,WIDE1*,qAR,VK2ION:!data",
            new[] { "W1ABC", "KD4RFT-10", "VK2ION", "W3UWU" },
            new string?[] { null, "WIDE1", null, null },
            1, HeardVia.IgateRfDigi);

        // Pure internet origin — qAC
        yield return new TestCaseData(
            "W1ABC>APRS,TCPIP*,qAC,VK2ION:!data",
            new[] { "W1ABC", "W3UWU" },
            new string?[] { null, null },
            0, HeardVia.Internet);

        // TCPIP direct inject — no q code
        yield return new TestCaseData(
            "W1ABC>APRS,TCPIP*:!data",
            new[] { "W1ABC", "W3UWU" },
            new string?[] { null, null },
            0, HeardVia.Internet);

        // Unused path entries before q — WIDE2-1 unstarred, then qAR, igate included
        yield return new TestCaseData(
            "W1ABC>APRS,WIDE2-1,qAR,VK2ION:!data",
            new[] { "W1ABC", "VK2ION", "W3UWU" },
            new string?[] { null, null, null },
            0, HeardVia.IgateRf);

        // Unstarred digi before starred alias — W4CAT-2,WIDE2* pattern (real-world report), igate included
        yield return new TestCaseData(
            "W1ABC>APRS,WIDE2,W4CAT-2,WIDE2*,qAR,N8DEU-7:!data",
            new[] { "W1ABC", "W4CAT-2", "N8DEU-7", "W3UWU" },
            new string?[] { null, "WIDE2", null, null },
            1, HeardVia.IgateRfDigi);

        // NOGATE token — RF only, not internet
        yield return new TestCaseData(
            "W1ABC>APRS,WE4MB-3*,WIDE2*,NOGATE:!data",
            new[] { "W1ABC", "WE4MB-3", "W3UWU" },
            new string?[] { null, "WIDE2", null },
            1, HeardVia.Digi);
    }

    [TestCaseSource(nameof(PathClassificationData))]
    public void PathParser_ClassifiesCorrectly(
        string raw,
        string[] expectedCallsigns,
        string?[] expectedAliasUsed,
        int expectedHopCount,
        HeardVia expectedHeardVia)
    {
        var result = PathResolver.Resolve(raw, homeCallsign: "W3UWU", stationLookup: MockStations);

        Assert.That(result.HopCount, Is.EqualTo(expectedHopCount));
        Assert.That(result.HeardVia, Is.EqualTo(expectedHeardVia));
        Assert.That(result.Hops.Count, Is.EqualTo(expectedCallsigns.Length));

        for (var i = 0; i < expectedCallsigns.Length; i++)
        {
            Assert.That(result.Hops[i].Callsign, Is.EqualTo(expectedCallsigns[i]));
            Assert.That(result.Hops[i].AliasUsed, Is.EqualTo(expectedAliasUsed[i]));
        }

        // Internet tokens must never appear as hop nodes
        Assert.That(result.Hops, Has.None.Matches<ResolvedPathEntry>(h =>
            h.Callsign.StartsWith("q", System.StringComparison.OrdinalIgnoreCase) ||
            h.Callsign.Equals("TCPIP", System.StringComparison.OrdinalIgnoreCase) ||
            h.Callsign.Equals("TCPXX", System.StringComparison.OrdinalIgnoreCase)));
    }
}

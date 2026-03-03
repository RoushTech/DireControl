using System.Collections.Generic;
using AprsSharp.AprsParser;
using DireControl.Enums;
using DireControl.PathParsing;
using Xunit;

namespace DireControl.Tests;

public class PathParserTests
{
    // -------------------------------------------------------------------------
    // IsGenericAlias
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("WIDE1",    true)]
    [InlineData("WIDE2",    true)]
    [InlineData("WIDE1-1",  true)]
    [InlineData("WIDE2-2",  true)]
    [InlineData("WIDE3-3",  true)]
    [InlineData("RELAY",    true)]
    [InlineData("TRACE",    true)]
    [InlineData("TRACE3-3", true)]
    [InlineData("IGATE",    true)]
    [InlineData("GATE",     true)]
    [InlineData("ECHO",     true)]
    [InlineData("NCA",      true)]
    [InlineData("RFONLY",   true)]
    [InlineData("NOGATE",   true)]
    [InlineData("TCPIP",    true)]
    [InlineData("TCPXX",    true)]
    [InlineData("qAR",      true)]
    [InlineData("qAC",      true)]
    [InlineData("qAS",      true)]
    [InlineData("qAO",      true)]
    // Star-marked variants must also be recognised
    [InlineData("WIDE1*",   true)]
    [InlineData("WIDE2-1*", true)]
    // Real callsigns must NOT match
    [InlineData("WE4MB-3",  false)]
    [InlineData("KN6RO-13", false)]
    [InlineData("KC4SAR-5", false)]
    [InlineData("W3UWU",    false)]
    [InlineData("APRS",     false)]
    public void IsGenericAlias_IdentifiesCorrectly(string callsign, bool expected)
    {
        Assert.Equal(expected, AprsPathParser.IsGenericAlias(callsign));
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — no * markers → direct packet, zero hops
    // -------------------------------------------------------------------------

    /// <summary>
    /// Packet 1 from issue #11.
    /// Raw:  KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:...
    /// No starred entries — packet was heard direct.  No hops should be returned.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Packet1_NoStars_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate.");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    /// <summary>
    /// Packet 2 from issue #11.
    /// Raw:  ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:...
    /// No starred entries — packet was heard direct.  No hops should be returned.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Packet2_NoStars_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3319.66NI08432.59W#PHG9250PI-DIREWOLF APRS IGATE/DIGI SENOIA GA 73!");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — with * markers (standard case)
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractViaHops_WithStarMarkers_IncludesOnlyStarredEntries()
    {
        // APRS is the TOCALL; W1DEF* was repeated; WIDE2-1 was not used
        var aprs = new Packet("N1ABC>APRS,W1DEF*,WIDE2-1:!3400.59NT08402.69W&Test");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Single(hops);
        Assert.Equal("W1DEF", hops[0].Callsign);
        Assert.Equal(1, hops[0].HopIndex);
        Assert.Equal(1, hopCount);
    }

    [Fact]
    public void ExtractViaHops_WithStarMarkers_MultipleStarred_AliasExcluded()
    {
        // KN6RO-13 is the TOCALL; WIDE1* is a generic alias with no prior real hop
        // (dropped entirely); WE4MB-3* is the only real digi hop; WIDE2 is unused.
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Single(hops);
        Assert.Equal("WE4MB-3", hops[0].Callsign);
        Assert.Equal(1, hops[0].HopIndex);
        Assert.Equal(1, hopCount);
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — TOCALL is unconditionally excluded
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractViaHops_StandardAprsDestination_ExcludedFromHops()
    {
        // APRS is the canonical TOCALL used by most clients; WE4MB-3* is the real digi
        var aprs = new Packet("W1ABC>APRS,WE4MB-3*,WIDE2-1:!3400.59NT08402.69W&Test");
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        // "APRS" must NOT be in the hop list
        Assert.DoesNotContain(hops, h => h.Callsign == "APRS");
        // "WE4MB-3" (starred real callsign) IS the used hop
        Assert.Single(hops);
        Assert.Equal("WE4MB-3", hops[0].Callsign);
    }

    /// <summary>
    /// TOCALL exclusion must not be conditional on what the TOCALL looks like.
    /// Here the TOCALL is a real callsign (WE4MB-3) rather than a software ID.
    /// </summary>
    [Fact]
    public void ExtractViaHops_CallsignTocall_ExcludedFromHops()
    {
        // WE4MB-3 is the TOCALL; WE4MB-3* is a starred hop from a different packet
        var rawPath = new List<string> { "WE4MB-3", "WE4MB-3*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        // TOCALL excluded; one starred hop remains
        Assert.Single(hops);
        Assert.Equal("WE4MB-3", hops[0].Callsign);
        Assert.Equal(1, hopCount);
    }

    [Fact]
    public void ExtractViaHops_EmptyPath_ReturnsEmpty()
    {
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(null);
        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    /// <summary>
    /// Issue #12 test packet.
    /// Raw path list ["KN6RO-13", "WE4MB-3", "WIDE2"] — no asterisks.
    /// No starred entries → direct packet, zero hops.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue12Packet_NoStars_DirectPacket_ZeroHops()
    {
        var rawPath = new List<string> { "KN6RO-13", "WE4MB-3", "WIDE2" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    // -------------------------------------------------------------------------
    // Alias entries start as Known=false; real entries also start as Known=false.
    // Coordinate resolution (setting Known=true) is performed later by
    // ResolvePathCoordinatesAsync and requires a station database lookup.
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractViaHops_AllEntriesInitiallyNotKnown()
    {
        // Use a packet with starred hops so we actually get entries to inspect
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test");
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.NotEmpty(hops);
        Assert.All(hops, h => Assert.False(h.Known));
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
    [Fact]
    public void ExtractViaHops_Issue13Packet_UnusedWide1_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    /// <summary>
    /// Direct packet with no path entries at all — only TOCALL present, no via entries.
    /// </summary>
    [Fact]
    public void ExtractViaHops_NoPathEntries_DirectPacket_ZeroHops()
    {
        var rawPath = new List<string> { "APRS" };  // only the TOCALL
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    /// <summary>
    /// Digipeated packet — WE4MB-3 is starred, WIDE2 is an unused alias not starred.
    /// Only the starred entry (WE4MB-3) should appear; hop count = 1.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue13_StarredRealCallsign_OneHop()
    {
        var rawPath = new List<string> { "APRS", "WE4MB-3*", "WIDE2" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Single(hops);
        Assert.Equal("WE4MB-3", hops[0].Callsign);
        Assert.Equal(1, hops[0].HopIndex);
        Assert.Equal(1, hopCount);
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
    [Fact]
    public void ParseTnc2Header_Issue14_DigipeatedPacket()
    {
        const string raw = "W4PFT-1>KN6RO-13,WE4MB-3*,WIDE2:@020107z3422.75N/08313.65W#WX3in1Mini Updated 03-20-2023 U=14.2V";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.Equal("W4PFT-1",   source);
        Assert.Equal("KN6RO-13",  tocall);
        Assert.Equal("WE4MB-3*,WIDE2", rawPath);
    }

    /// <summary>
    /// Issue #11 — Packet 1.
    /// Raw: KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!…
    /// No asterisks — direct packet stored as-is.
    /// </summary>
    [Fact]
    public void ParseTnc2Header_Issue11_Packet1_NoAsterisks()
    {
        const string raw = "KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate.";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.Equal("KC4SAR-5",           source);
        Assert.Equal("KN6RO-13",           tocall);
        Assert.Equal("WIDE1,WE4MB-3,WIDE2", rawPath);
    }

    /// <summary>
    /// Issue #13 test packet — TOCALL is a real callsign (WE4MB-3), one via entry (WIDE1).
    /// Raw: KM4KMO-14>WE4MB-3,WIDE1:@…
    /// </summary>
    [Fact]
    public void ParseTnc2Header_Issue13_CallsignTocall_OneViaEntry()
    {
        const string raw = "KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.Equal("KM4KMO-14", source);
        Assert.Equal("WE4MB-3",   tocall);
        Assert.Equal("WIDE1",     rawPath);
    }

    /// <summary>
    /// Packet with no via entries — only TOCALL present after '>'.
    /// Raw: W1ABC>APRS:!…
    /// rawPath must be empty.
    /// </summary>
    [Fact]
    public void ParseTnc2Header_TocallOnly_EmptyRawPath()
    {
        const string raw = "W1ABC>APRS:!3400.59NT08402.69W&Test";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.Equal("W1ABC", source);
        Assert.Equal("APRS",  tocall);
        Assert.Equal(string.Empty, rawPath);
    }

    /// <summary>
    /// Multiple starred hops — both entries appear in rawPath with asterisks intact.
    /// Raw: KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!…
    /// </summary>
    [Fact]
    public void ParseTnc2Header_MultipleStarredHops_AsterisksPreserved()
    {
        const string raw = "KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test";
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.Equal("KC4SAR-5",              source);
        Assert.Equal("KN6RO-13",              tocall);
        Assert.Equal("WIDE1*,WE4MB-3*,WIDE2", rawPath);
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
    [Fact]
    public void ExtractViaHops_Issue17_StarredAliases_NotHops_AttachedAsAliasUsed()
    {
        var aprs = new Packet("KR4BRU-9>APMI0A,KA4EMA-3*,WIDE1*,WE4MB-3*,WIDE2*:/020150z3504.35N/08511.40Wa065/000/A=000751Ramble-Ambulance");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Equal(2, hopCount);
        Assert.Equal(2, hops.Count);

        Assert.Equal("KA4EMA-3", hops[0].Callsign);
        Assert.Equal(1,          hops[0].HopIndex);
        Assert.Equal("WIDE1",    hops[0].AliasUsed);

        Assert.Equal("WE4MB-3",  hops[1].Callsign);
        Assert.Equal(2,          hops[1].HopIndex);
        Assert.Equal("WIDE2",    hops[1].AliasUsed);

        Assert.DoesNotContain(hops, h => AprsPathParser.IsGenericAlias(h.Callsign));
    }

    /// <summary>
    /// Single real hop with alias consumed — VK2RXX consumed WIDE2.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue17_SingleHopWithAlias()
    {
        var rawPath = new List<string> { "APRS", "VK2RXX*", "WIDE2*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Equal(1, hopCount);
        Assert.Single(hops);
        Assert.Equal("VK2RXX", hops[0].Callsign);
        Assert.Equal(1,        hops[0].HopIndex);
        Assert.Equal("WIDE2",  hops[0].AliasUsed);

        Assert.DoesNotContain(hops, h => AprsPathParser.IsGenericAlias(h.Callsign));
    }

    /// <summary>
    /// Path with only unused alias entries — direct packet, zero hops.
    /// Raw: KM4KMO-14>WE4MB-3,WIDE1
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue17_UnusedAlias_DirectPacket_ZeroHops()
    {
        var aprs = new Packet("KM4KMO-14>WE4MB-3,WIDE1:@020113z3507.38NI08509.86W#Harrison, TN");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }

    /// <summary>
    /// Real starred hop with no alias following it — aliasUsed must be null.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue17_RealHopNoAlias_AliasUsedIsNull()
    {
        var rawPath = new List<string> { "APRS", "WE4MB-3*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Equal(1, hopCount);
        Assert.Single(hops);
        Assert.Equal("WE4MB-3", hops[0].Callsign);
        Assert.Equal(1,         hops[0].HopIndex);
        Assert.Null(hops[0].AliasUsed);
    }

    /// <summary>
    /// Mixed: first real hop consumes WIDE1, second real hop has no alias.
    /// Path: VK2RXX*,WIDE1*,VK2RYY*
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue17_Mixed_FirstHopAlias_SecondHopNoAlias()
    {
        var rawPath = new List<string> { "APRS", "VK2RXX*", "WIDE1*", "VK2RYY*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Equal(2, hopCount);
        Assert.Equal(2, hops.Count);

        Assert.Equal("VK2RXX", hops[0].Callsign);
        Assert.Equal(1,        hops[0].HopIndex);
        Assert.Equal("WIDE1",  hops[0].AliasUsed);

        Assert.Equal("VK2RYY", hops[1].Callsign);
        Assert.Equal(2,        hops[1].HopIndex);
        Assert.Null(hops[1].AliasUsed);

        Assert.DoesNotContain(hops, h => AprsPathParser.IsGenericAlias(h.Callsign));
    }

    /// <summary>
    /// TOCALL is a real callsign — must still be excluded.
    /// Path: KC4SAR-5>KN6RO-13,WE4MB-3*,WIDE2*
    /// </summary>
    [Fact]
    public void ExtractViaHops_Issue17_CallsignTocall_ExcludedAlias_AliasUsedAttached()
    {
        var rawPath = new List<string> { "KN6RO-13", "WE4MB-3*", "WIDE2*" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Equal(1, hopCount);
        Assert.Single(hops);
        Assert.Equal("WE4MB-3", hops[0].Callsign);
        Assert.Equal(1,         hops[0].HopIndex);
        Assert.Equal("WIDE2",   hops[0].AliasUsed);

        Assert.DoesNotContain(hops, h => AprsPathParser.IsGenericAlias(h.Callsign));
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
    [Fact]
    public void ExtractViaHops_UnstarredDigiBeforeStarredAlias_CountedAsHop()
    {
        // APRS is TOCALL; WIDE2 unused; W4CAT-2 unstarred before WIDE2*; then igated
        var rawPath = new List<string> { "APRS", "WIDE2", "W4CAT-2", "WIDE2*", "qAR", "N8DEU-7" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Equal(1, hopCount);
        Assert.Single(hops);
        Assert.Equal("W4CAT-2", hops[0].Callsign);
        Assert.Equal(1,         hops[0].HopIndex);
        Assert.Equal("WIDE2",   hops[0].AliasUsed);

        Assert.DoesNotContain(hops, h => AprsPathParser.IsGenericAlias(h.Callsign));
    }

    /// <summary>
    /// Unstarred real callsign NOT followed by a starred generic alias must still
    /// be treated as unused (direct packet, zero hops).
    /// Path: APRS, W4CAT-2, WIDE2  — both unstarred, nothing consumed.
    /// </summary>
    [Fact]
    public void ExtractViaHops_UnstarredDigiWithoutStarredAlias_NotCountedAsHop()
    {
        var rawPath = new List<string> { "APRS", "W4CAT-2", "WIDE2" };
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(rawPath);

        Assert.Empty(hops);
        Assert.Equal(0, hopCount);
    }
}

// -------------------------------------------------------------------------
// PathResolver.Resolve — full-path classification including source and home
// -------------------------------------------------------------------------

public class PathResolverTests
{
    // No station coordinates needed for these tests; pass null for stationLookup.
    private static readonly IReadOnlyDictionary<string, (double Lat, double Lon)>? MockStations = null;

    [Theory]

    // Direct RF — no hops, no q construct
    [InlineData(
        "W1ABC>APRS:!data",
        new[] { "W1ABC", "W3UWU" },
        new[] { (string?)null, null },
        0, HeardVia.Direct)]

    // RF via one digi
    [InlineData(
        "W1ABC>APRS,KD4RFT-10*,WIDE1*:!data",
        new[] { "W1ABC", "KD4RFT-10", "W3UWU" },
        new[] { (string?)null, "WIDE1", null },
        1, HeardVia.Digi)]

    // RF via two digis
    [InlineData(
        "W1ABC>APRS,KD4RFT-10*,WIDE1*,WE4MB-3*,WIDE2*:!data",
        new[] { "W1ABC", "KD4RFT-10", "WE4MB-3", "W3UWU" },
        new[] { (string?)null, "WIDE1", "WIDE2", null },
        2, HeardVia.Digi)]

    // igated from RF direct — qAR, no RF hops
    [InlineData(
        "W1ABC>APRS,qAR,VK2ION:!data",
        new[] { "W1ABC", "W3UWU" },
        new[] { (string?)null, null },
        0, HeardVia.IgateRf)]

    // igated from RF via digi — qAR with starred hops before it
    [InlineData(
        "W1ABC>APRS,KD4RFT-10*,WIDE1*,qAR,VK2ION:!data",
        new[] { "W1ABC", "KD4RFT-10", "W3UWU" },
        new[] { (string?)null, "WIDE1", null },
        1, HeardVia.IgateRfDigi)]

    // Pure internet origin — qAC
    [InlineData(
        "W1ABC>APRS,TCPIP*,qAC,VK2ION:!data",
        new[] { "W1ABC", "W3UWU" },
        new[] { (string?)null, null },
        0, HeardVia.Internet)]

    // TCPIP direct inject — no q code
    [InlineData(
        "W1ABC>APRS,TCPIP*:!data",
        new[] { "W1ABC", "W3UWU" },
        new[] { (string?)null, null },
        0, HeardVia.Internet)]

    // igate callsign must not appear as a hop node (same path as igated-via-digi case)
    [InlineData(
        "W1ABC>APRS,KD4RFT-10*,WIDE1*,qAR,VK2ION:!data",
        new[] { "W1ABC", "KD4RFT-10", "W3UWU" },
        new[] { (string?)null, "WIDE1", null },
        1, HeardVia.IgateRfDigi)]

    // Unused path entries before q — WIDE2-1 unstarred, then qAR
    [InlineData(
        "W1ABC>APRS,WIDE2-1,qAR,VK2ION:!data",
        new[] { "W1ABC", "W3UWU" },
        new[] { (string?)null, null },
        0, HeardVia.IgateRf)]

    // Unstarred digi before starred alias — W4CAT-2,WIDE2* pattern (real-world report)
    [InlineData(
        "W1ABC>APRS,WIDE2,W4CAT-2,WIDE2*,qAR,N8DEU-7:!data",
        new[] { "W1ABC", "W4CAT-2", "W3UWU" },
        new[] { (string?)null, "WIDE2", null },
        1, HeardVia.IgateRfDigi)]

    // NOGATE token — RF only, not internet
    [InlineData(
        "W1ABC>APRS,WE4MB-3*,WIDE2*,NOGATE:!data",
        new[] { "W1ABC", "WE4MB-3", "W3UWU" },
        new[] { (string?)null, "WIDE2", null },
        1, HeardVia.Digi)]

    public void PathParser_ClassifiesCorrectly(
        string raw,
        string[] expectedCallsigns,
        string?[] expectedAliasUsed,
        int expectedHopCount,
        HeardVia expectedHeardVia)
    {
        var result = PathResolver.Resolve(raw, homeCallsign: "W3UWU", stationLookup: MockStations);

        Assert.Equal(expectedHopCount, result.HopCount);
        Assert.Equal(expectedHeardVia, result.HeardVia);
        Assert.Equal(expectedCallsigns.Length, result.Hops.Count);

        for (var i = 0; i < expectedCallsigns.Length; i++)
        {
            Assert.Equal(expectedCallsigns[i], result.Hops[i].Callsign);
            Assert.Equal(expectedAliasUsed[i], result.Hops[i].AliasUsed);
        }

        // Internet tokens must never appear as hop nodes
        Assert.DoesNotContain(result.Hops, h =>
            h.Callsign.StartsWith("q", System.StringComparison.OrdinalIgnoreCase) ||
            h.Callsign.Equals("TCPIP", System.StringComparison.OrdinalIgnoreCase) ||
            h.Callsign.Equals("TCPXX", System.StringComparison.OrdinalIgnoreCase));
    }
}

using System.Collections.Generic;
using AprsSharp.AprsParser;
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
    public void ExtractViaHops_WithStarMarkers_MultipleStarred()
    {
        // KN6RO-13 is the TOCALL; WIDE1* and WE4MB-3* were both used
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1*,WE4MB-3*,WIDE2:!3400.59NT08402.69W&Test");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Equal(2, hops.Count);
        Assert.Equal("WIDE1",   hops[0].Callsign);
        Assert.Equal("WE4MB-3", hops[1].Callsign);
        Assert.Equal(1, hops[0].HopIndex);
        Assert.Equal(2, hops[1].HopIndex);
        // Both WIDE1 and WE4MB-3 were starred (actually repeated), so hopCount = 2
        Assert.Equal(2, hopCount);
    }

    // -------------------------------------------------------------------------
    // ExtractViaHops — TOCALL is unconditionally excluded
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractViaHops_StandardAprsDestination_ExcludedFromHops()
    {
        // APRS is the canonical TOCALL used by most clients
        var aprs = new Packet("W1ABC>APRS,RELAY*,WIDE2-1:!3400.59NT08402.69W&Test");
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        // "APRS" must NOT be in the hop list
        Assert.DoesNotContain(hops, h => h.Callsign == "APRS");
        // "RELAY" (starred) IS the used hop
        Assert.Single(hops);
        Assert.Equal("RELAY", hops[0].Callsign);
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
}

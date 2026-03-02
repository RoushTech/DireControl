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
    // ExtractViaHops — no * markers (Direwolf may strip them)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Packet 1 from issue #11.
    /// Raw:  KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:...
    /// TOCALL KN6RO-13 must be excluded; all remaining entries must appear in order.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Packet1_NoStars_ExcludesTocallAndIncludesAllEntries()
    {
        var aprs = new Packet("KC4SAR-5>KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate.");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        // Three via entries: WIDE1, WE4MB-3, WIDE2
        Assert.Equal(3, hops.Count);

        Assert.Equal("WIDE1",   hops[0].Callsign);
        Assert.Equal("WE4MB-3", hops[1].Callsign);
        Assert.Equal("WIDE2",   hops[2].Callsign);

        // HopIndex 0 is reserved for the source; via hops start at 1
        Assert.Equal(1, hops[0].HopIndex);
        Assert.Equal(2, hops[1].HopIndex);
        Assert.Equal(3, hops[2].HopIndex);

        // Only WE4MB-3 is a real callsign; aliases do not count toward hopCount
        Assert.Equal(1, hopCount);
    }

    /// <summary>
    /// Packet 2 from issue #11.
    /// Raw:  ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:...
    /// TOCALL AB4KN-2 must be excluded; KN6RO-13 must appear as the first via hop.
    /// </summary>
    [Fact]
    public void ExtractViaHops_Packet2_NoStars_ExcludesTocallAndIncludesAllEntries()
    {
        var aprs = new Packet("ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3319.66NI08432.59W#PHG9250PI-DIREWOLF APRS IGATE/DIGI SENOIA GA 73!");
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        // Four via entries: KN6RO-13, WIDE1, WE4MB-3, WIDE2
        Assert.Equal(4, hops.Count);

        Assert.Equal("KN6RO-13", hops[0].Callsign);
        Assert.Equal("WIDE1",    hops[1].Callsign);
        Assert.Equal("WE4MB-3",  hops[2].Callsign);
        Assert.Equal("WIDE2",    hops[3].Callsign);

        Assert.Equal(1, hops[0].HopIndex);
        Assert.Equal(2, hops[1].HopIndex);
        Assert.Equal(3, hops[2].HopIndex);
        Assert.Equal(4, hops[3].HopIndex);

        // KN6RO-13 and WE4MB-3 are real callsigns
        Assert.Equal(2, hopCount);
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
    // ExtractViaHops — TOCALL is never a callsign-only TOCALL being station
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

    [Fact]
    public void ExtractViaHops_EmptyPath_ReturnsEmpty()
    {
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(null);
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
        var aprs = new Packet("ND1J-10>AB4KN-2,KN6RO-13,WIDE1,WE4MB-3,WIDE2:!3319.66NI08432.59W#Test");
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.All(hops, h => Assert.False(h.Known));
    }
}

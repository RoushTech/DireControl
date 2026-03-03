using DireControl.Data.Models;
using DireControl.PathParsing;
using Xunit;

namespace DireControl.Tests;

public class CallsignMatcherTests
{
    // -------------------------------------------------------------------------
    // Matches — exact callsign+SSID matching with -0 equivalence
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("W3UWU", null, "W3UWU",   true)]   // exact match, no SSID
    [InlineData("W3UWU", null, "W3UWU-0", true)]   // -0 is equivalent to no SSID
    [InlineData("W3UWU", null, "W3UWU-9", false)]  // different SSID — no match
    [InlineData("W3UWU", "9",  "W3UWU-9", true)]   // exact match with SSID
    [InlineData("W3UWU", "9",  "W3UWU",   false)]  // configured with SSID, packet without — no match
    [InlineData("W3UWU", "9",  "W3UWU-1", false)]  // different SSID — no match
    public void CallsignMatch_IsExact(string radioCall, string? radioSsid, string packetSource, bool expected)
    {
        var radio = new Radio { Name = "Test", Callsign = radioCall, Ssid = radioSsid };
        Assert.Equal(expected, CallsignMatcher.Matches(radio, packetSource));
    }

    // -------------------------------------------------------------------------
    // Normalise — case insensitivity and -0 stripping
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("w3uwu",   "W3UWU")]    // lowercase uppercased
    [InlineData("W3UWU",   "W3UWU")]    // already upper — unchanged
    [InlineData("W3UWU-0", "W3UWU")]    // -0 stripped
    [InlineData("w3uwu-0", "W3UWU")]    // lowercase + -0 stripped
    [InlineData("W3UWU-9", "W3UWU-9")] // other SSID left intact
    [InlineData("W3UWU-10","W3UWU-10")] // two-digit SSID not confused with -0
    public void Normalise_ProducesExpectedForm(string input, string expected)
    {
        Assert.Equal(expected, CallsignMatcher.Normalise(input));
    }

    // -------------------------------------------------------------------------
    // -0 equivalence is symmetric: radio "-0" matches no-SSID packet
    // -------------------------------------------------------------------------

    [Fact]
    public void Matches_RadioConfiguredWithSsid0_MatchesPacketWithNoSsid()
    {
        // A radio stored with SSID "0" is the same station as no SSID
        var radio = new Radio { Name = "Test", Callsign = "W3UWU", Ssid = "0" };
        Assert.True(CallsignMatcher.Matches(radio, "W3UWU"));
    }

    [Fact]
    public void Matches_RadioConfiguredWithNoSsid_MatchesPacketWithSsid0()
    {
        var radio = new Radio { Name = "Test", Callsign = "W3UWU", Ssid = null };
        Assert.True(CallsignMatcher.Matches(radio, "W3UWU-0"));
    }

    // -------------------------------------------------------------------------
    // Two radios with same base callsign, different SSIDs — no cross-matching
    // -------------------------------------------------------------------------

    [Fact]
    public void Matches_TwoRadiosSameBaseCallsign_EachMatchesOnlyOwnPackets()
    {
        var home   = new Radio { Name = "Home",   Callsign = "W3UWU", Ssid = null };
        var mobile = new Radio { Name = "Mobile", Callsign = "W3UWU", Ssid = "9" };

        Assert.True(CallsignMatcher.Matches(home,   "W3UWU"));
        Assert.True(CallsignMatcher.Matches(mobile, "W3UWU-9"));

        Assert.False(CallsignMatcher.Matches(home,   "W3UWU-9"));
        Assert.False(CallsignMatcher.Matches(mobile, "W3UWU"));
        Assert.False(CallsignMatcher.Matches(mobile, "W3UWU-1"));
    }
}

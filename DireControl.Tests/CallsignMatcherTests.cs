using DireControl.Data.Models;
using DireControl.PathParsing;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class CallsignMatcherTests
{
    // -------------------------------------------------------------------------
    // Matches — exact callsign+SSID matching with -0 equivalence
    // -------------------------------------------------------------------------

    [TestCase("W3UWU", null, "W3UWU",   true)]   // exact match, no SSID
    [TestCase("W3UWU", null, "W3UWU-0", true)]   // -0 is equivalent to no SSID
    [TestCase("W3UWU", null, "W3UWU-9", false)]  // different SSID — no match
    [TestCase("W3UWU", "9",  "W3UWU-9", true)]   // exact match with SSID
    [TestCase("W3UWU", "9",  "W3UWU",   false)]  // configured with SSID, packet without — no match
    [TestCase("W3UWU", "9",  "W3UWU-1", false)]  // different SSID — no match
    public void CallsignMatch_IsExact(string radioCall, string? radioSsid, string packetSource, bool expected)
    {
        var radio = new Radio { Name = "Test", Callsign = radioCall, Ssid = radioSsid };
        Assert.That(CallsignMatcher.Matches(radio, packetSource), Is.EqualTo(expected));
    }

    // -------------------------------------------------------------------------
    // Normalise — case insensitivity and -0 stripping
    // -------------------------------------------------------------------------

    [TestCase("w3uwu",   "W3UWU")]    // lowercase uppercased
    [TestCase("W3UWU",   "W3UWU")]    // already upper — unchanged
    [TestCase("W3UWU-0", "W3UWU")]    // -0 stripped
    [TestCase("w3uwu-0", "W3UWU")]    // lowercase + -0 stripped
    [TestCase("W3UWU-9", "W3UWU-9")] // other SSID left intact
    [TestCase("W3UWU-10","W3UWU-10")] // two-digit SSID not confused with -0
    public void Normalise_ProducesExpectedForm(string input, string expected)
    {
        Assert.That(CallsignMatcher.Normalise(input), Is.EqualTo(expected));
    }

    // -------------------------------------------------------------------------
    // -0 equivalence is symmetric: radio "-0" matches no-SSID packet
    // -------------------------------------------------------------------------

    [Test]
    public void Matches_RadioConfiguredWithSsid0_MatchesPacketWithNoSsid()
    {
        // A radio stored with SSID "0" is the same station as no SSID
        var radio = new Radio { Name = "Test", Callsign = "W3UWU", Ssid = "0" };
        Assert.That(CallsignMatcher.Matches(radio, "W3UWU"), Is.True);
    }

    [Test]
    public void Matches_RadioConfiguredWithNoSsid_MatchesPacketWithSsid0()
    {
        var radio = new Radio { Name = "Test", Callsign = "W3UWU", Ssid = null };
        Assert.That(CallsignMatcher.Matches(radio, "W3UWU-0"), Is.True);
    }

    // -------------------------------------------------------------------------
    // Two radios with same base callsign, different SSIDs — no cross-matching
    // -------------------------------------------------------------------------

    [Test]
    public void Matches_TwoRadiosSameBaseCallsign_EachMatchesOnlyOwnPackets()
    {
        var home   = new Radio { Name = "Home",   Callsign = "W3UWU", Ssid = null };
        var mobile = new Radio { Name = "Mobile", Callsign = "W3UWU", Ssid = "9" };

        Assert.That(CallsignMatcher.Matches(home,   "W3UWU"), Is.True);
        Assert.That(CallsignMatcher.Matches(mobile, "W3UWU-9"), Is.True);

        Assert.That(CallsignMatcher.Matches(home,   "W3UWU-9"), Is.False);
        Assert.That(CallsignMatcher.Matches(mobile, "W3UWU"), Is.False);
        Assert.That(CallsignMatcher.Matches(mobile, "W3UWU-1"), Is.False);
    }
}

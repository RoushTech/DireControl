using DireControl.Api.Services;
using DireControl.Data.Models;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Station-identity settings: callsign validation for the settings endpoint,
/// and applying stored UserSetting overrides onto the live options without
/// clobbering configured values when no override is set.
/// </summary>
[TestFixture]
public sealed class StationIdentityLogicTests
{
    [TestCase("W3UWU")]
    [TestCase("W3UWU-10")]
    [TestCase("N0CALL-15")]
    [TestCase("K4ABC-0")]
    [TestCase("W1A")]
    public void ValidCallsigns_Accepted(string callsign)
    {
        Assert.That(StationIdentityLogic.IsValidCallsign(callsign), Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("W3UWU-16")]
    [TestCase("TOOLONG1")]
    [TestCase("W3 UWU")]
    [TestCase("W3UWU-")]
    [TestCase("-5")]
    [TestCase("w3uwu")]
    public void InvalidCallsigns_Rejected(string callsign)
    {
        Assert.That(StationIdentityLogic.IsValidCallsign(callsign), Is.False);
    }

    [Test]
    public void Overrides_ApplyOntoOptions()
    {
        var options = new DireControlOptions
        {
            OurCallsign = "N0CALL-10",
            HomeLat = 1.0,
            HomeLon = 2.0,
        };
        var setting = new UserSetting
        {
            OurCallsign = "W3UWU",
            HomeLat = 35.30,
            HomeLon = -85.08,
        };

        StationIdentityLogic.ApplyOverrides(setting, options);

        Assert.That(options.OurCallsign, Is.EqualTo("W3UWU"));
        Assert.That(options.HomeLat, Is.EqualTo(35.30));
        Assert.That(options.HomeLon, Is.EqualTo(-85.08));
    }

    [Test]
    public void NullOverrides_KeepConfiguredValues()
    {
        var options = new DireControlOptions
        {
            OurCallsign = "N0CALL-10",
            HomeLat = 1.0,
            HomeLon = 2.0,
        };

        StationIdentityLogic.ApplyOverrides(new UserSetting(), options);

        Assert.That(options.OurCallsign, Is.EqualTo("N0CALL-10"));
        Assert.That(options.HomeLat, Is.EqualTo(1.0));
        Assert.That(options.HomeLon, Is.EqualTo(2.0));
    }

    [Test]
    public void PartialHomePosition_IsNotApplied()
    {
        var options = new DireControlOptions { HomeLat = 1.0, HomeLon = 2.0 };
        var setting = new UserSetting { HomeLat = 35.30, HomeLon = null };

        StationIdentityLogic.ApplyOverrides(setting, options);

        Assert.That(options.HomeLat, Is.EqualTo(1.0));
        Assert.That(options.HomeLon, Is.EqualTo(2.0));
    }
}

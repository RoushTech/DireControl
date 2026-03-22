using DireControl;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class AprsPasscodeTests
{
    [TestCase("W1AW",   25988)]
    [TestCase("VK2RXX", 20387)]
    [TestCase("W3UWU",   9350)]
    public void GeneratePasscode_KnownCallsigns_ReturnExpectedValue(string callsign, int expected)
    {
        var result = AprsPasscodeHelper.GeneratePasscode(callsign);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("W1AW")]
    [TestCase("VK2RXX")]
    [TestCase("W3UWU")]
    [TestCase("N0CALL")]
    public void GeneratePasscode_AlwaysInRange(string callsign)
    {
        var result = AprsPasscodeHelper.GeneratePasscode(callsign);
        Assert.That(result, Is.InRange(0, 32767));
    }

    [TestCase("W1AW")]
    [TestCase("w1aw")]
    [TestCase("W1AW-9")]
    [TestCase("w1aw-9")]
    public void GeneratePasscode_CaseAndSsidInsensitive(string callsign)
    {
        var result = AprsPasscodeHelper.GeneratePasscode(callsign);
        Assert.That(result, Is.EqualTo(25988));
    }
}

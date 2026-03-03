using DireControl;
using Xunit;

namespace DireControl.Tests;

public class AprsPasscodeTests
{
    [Theory]
    [InlineData("W1AW",   25988)]
    [InlineData("VK2RXX", 20387)]
    [InlineData("W3UWU",   9350)]
    public void GeneratePasscode_KnownCallsigns_ReturnExpectedValue(string callsign, int expected)
    {
        var result = AprsPasscodeHelper.GeneratePasscode(callsign);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("W1AW")]
    [InlineData("VK2RXX")]
    [InlineData("W3UWU")]
    [InlineData("N0CALL")]
    public void GeneratePasscode_AlwaysInRange(string callsign)
    {
        var result = AprsPasscodeHelper.GeneratePasscode(callsign);
        Assert.InRange(result, 0, 32767);
    }

    [Theory]
    [InlineData("W1AW")]
    [InlineData("w1aw")]
    [InlineData("W1AW-9")]
    [InlineData("w1aw-9")]
    public void GeneratePasscode_CaseAndSsidInsensitive(string callsign)
    {
        var result = AprsPasscodeHelper.GeneratePasscode(callsign);
        Assert.Equal(25988, result);
    }
}

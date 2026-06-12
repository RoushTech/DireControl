using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class MaidenheadLocatorTests
{
    // Well-known reference locators
    [TestCase(41.714775, -72.727260, "FN31pr")]   // W1AW, Newington CT
    [TestCase(51.477500, -0.001400, "IO91xl")]    // Greenwich (just west of the meridian)
    [TestCase(-33.856800, 151.215300, "QF56od")]  // Sydney
    [TestCase(35.301763, -85.079382, "EM75lh")]   // SE Tennessee
    [TestCase(0.0, 0.0, "JJ00aa")]                // Null Island
    public void FromLatLon_KnownLocations(double lat, double lon, string expected)
    {
        Assert.That(MaidenheadLocator.FromLatLon(lat, lon), Is.EqualTo(expected));
    }

    [Test]
    public void FromLatLon_EdgeOfWorld_FallsInLastCell()
    {
        Assert.That(MaidenheadLocator.FromLatLon(90.0, 180.0), Is.EqualTo("RR99xx"));
        Assert.That(MaidenheadLocator.FromLatLon(-90.0, -180.0), Is.EqualTo("AA00aa"));
    }

    [TestCase(double.NaN, 0.0)]
    [TestCase(0.0, double.NaN)]
    [TestCase(91.0, 0.0)]
    [TestCase(0.0, 181.0)]
    [TestCase(-91.0, 0.0)]
    [TestCase(0.0, -181.0)]
    public void FromLatLon_InvalidInput_ReturnsNull(double lat, double lon)
    {
        Assert.That(MaidenheadLocator.FromLatLon(lat, lon), Is.Null);
    }
}

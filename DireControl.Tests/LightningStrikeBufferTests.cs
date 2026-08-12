using System;
using System.Linq;
using DireControl.Api.Services.Weather;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class LightningStrikeBufferTests
{
    private static LightningStrike At(double lat, double lon, TimeSpan age)
        => new(DateTime.UtcNow - age, lat, lon);

    [Test]
    public void Add_PrunesStrikesPastRetention()
    {
        var buffer = new LightningStrikeBuffer();
        buffer.Add(At(10, 10, LightningStrikeBuffer.Retention + TimeSpan.FromMinutes(5)));
        buffer.Add(At(20, 20, TimeSpan.FromMinutes(1)));

        Assert.That(buffer.Count, Is.EqualTo(1));
        var result = buffer.Query(-90, 90, -180, 180, DateTime.UtcNow.AddHours(-2), 100);
        Assert.That(result.Single().Latitude, Is.EqualTo(20));
    }

    [Test]
    public void Query_FiltersByBoundingBoxAndCutoff()
    {
        var buffer = new LightningStrikeBuffer();
        buffer.Add(At(45, -90, TimeSpan.FromMinutes(30)));   // outside cutoff below
        buffer.Add(At(45, -90, TimeSpan.FromMinutes(5)));    // match
        buffer.Add(At(60, -90, TimeSpan.FromMinutes(5)));    // lat outside box
        buffer.Add(At(45, 10, TimeSpan.FromMinutes(5)));     // lon outside box

        var result = buffer.Query(40, 50, -100, -80, DateTime.UtcNow.AddMinutes(-10), 100);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Longitude, Is.EqualTo(-90));
    }

    [Test]
    public void Query_LimitKeepsMostRecent_OldestFirstOrder()
    {
        var buffer = new LightningStrikeBuffer();
        for (var i = 0; i < 10; i++)
            buffer.Add(At(i, 0, TimeSpan.FromMinutes(10 - i)));

        var result = buffer.Query(-90, 90, -180, 180, DateTime.UtcNow.AddHours(-1), 3);

        // The three newest strikes (lat 7, 8, 9), returned oldest first.
        Assert.That(result.Select(s => s.Latitude), Is.EqualTo(new double[] { 7, 8, 9 }));
    }

    [Test]
    public void Query_HandlesAntimeridianCrossingBox()
    {
        var buffer = new LightningStrikeBuffer();
        buffer.Add(At(0, 175, TimeSpan.FromMinutes(1)));
        buffer.Add(At(0, -175, TimeSpan.FromMinutes(1)));
        buffer.Add(At(0, 0, TimeSpan.FromMinutes(1)));

        var result = buffer.Query(-10, 10, 170, -170, DateTime.UtcNow.AddMinutes(-5), 100);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(s => s.Longitude), Is.EquivalentTo(new double[] { 175, -175 }));
    }

    [Test]
    public void Query_LeafletWrappedLongitudesAreNormalized()
    {
        var buffer = new LightningStrikeBuffer();
        buffer.Add(At(0, -100, TimeSpan.FromMinutes(1)));

        // Leaflet can report a world-wrapped view, e.g. lon 250 for -110.
        var result = buffer.Query(-10, 10, 250, 270, DateTime.UtcNow.AddMinutes(-5), 100);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void Query_BoxSpanningFullWorldMatchesAllLongitudes()
    {
        var buffer = new LightningStrikeBuffer();
        buffer.Add(At(0, 179, TimeSpan.FromMinutes(1)));
        buffer.Add(At(0, -179, TimeSpan.FromMinutes(1)));

        var result = buffer.Query(-10, 10, -400, 400, DateTime.UtcNow.AddMinutes(-5), 100);

        Assert.That(result, Has.Count.EqualTo(2));
    }
}

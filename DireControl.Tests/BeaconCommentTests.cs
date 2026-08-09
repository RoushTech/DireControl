using DireControl.Api.Services;
using DireControl.Data.Models;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The beacon comment: a radio with no configured comment beacons
/// "DireControl v{version}" (build metadata trimmed); a configured comment is
/// sent with any {version} tokens expanded, otherwise verbatim.
/// </summary>
[TestFixture]
public sealed class BeaconCommentTests
{
    private static Radio MakeRadio(string? comment) =>
        new() { Name = "Test", Callsign = "W3UWU", BeaconComment = comment };

    [Test]
    public void ResolveVersion_TrimsBuildMetadata()
    {
        Assert.That(
            BeaconService.ResolveVersion("0.3.0.213+ac4057ca"),
            Is.EqualTo("0.3.0.213"));
    }

    [Test]
    public void ResolveVersion_NoMetadata_UsedAsIs()
    {
        Assert.That(BeaconService.ResolveVersion("1.2.3"), Is.EqualTo("1.2.3"));
    }

    [Test]
    public void ResolveVersion_NullVersion_FallsBackToUnknown()
    {
        Assert.That(BeaconService.ResolveVersion(null), Is.EqualTo("unknown"));
    }

    [Test]
    public void EffectiveComment_BlankConfigured_UsesDefaultTemplate()
    {
        var radio = MakeRadio("  ");
        Assert.That(
            BeaconService.EffectiveComment(radio, "1.2.3"),
            Is.EqualTo("DireControl v1.2.3"));
    }

    [Test]
    public void EffectiveComment_NullConfigured_UsesDefaultTemplate()
    {
        var radio = MakeRadio(null);
        Assert.That(
            BeaconService.EffectiveComment(radio, "1.2.3"),
            Is.EqualTo("DireControl v1.2.3"));
    }

    [Test]
    public void EffectiveComment_Configured_NoToken_SentVerbatim()
    {
        var radio = MakeRadio("Chattanooga fill-in digi");
        Assert.That(
            BeaconService.EffectiveComment(radio, "1.2.3"),
            Is.EqualTo("Chattanooga fill-in digi"));
    }

    [Test]
    public void EffectiveComment_Configured_ExpandsVersionToken()
    {
        var radio = MakeRadio("Chattanooga digi - DireControl v{version}");
        Assert.That(
            BeaconService.EffectiveComment(radio, "1.2.3"),
            Is.EqualTo("Chattanooga digi - DireControl v1.2.3"));
    }

    [Test]
    public void EffectiveComment_TokenIsCaseInsensitive()
    {
        var radio = MakeRadio("v{VERSION}");
        Assert.That(BeaconService.EffectiveComment(radio, "1.2.3"), Is.EqualTo("v1.2.3"));
    }

    [Test]
    public void EffectiveComment_RuntimeVersion_MatchesResolvedAssemblyVersion()
    {
        var radio = MakeRadio("{version}");
        Assert.That(
            BeaconService.EffectiveComment(radio),
            Is.EqualTo(BeaconService.Version));
    }
}

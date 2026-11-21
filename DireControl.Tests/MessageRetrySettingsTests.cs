using DireControl.Api.Services;
using DireControl.Data.Models;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>Editable message retry policy (review 1.25): stored overrides onto the live options.</summary>
[TestFixture]
public sealed class MessageRetrySettingsTests
{
    [Test]
    public void StoredOverrides_ReplaceTheConfiguredPolicy()
    {
        var options = new DireControlOptions { MaxRetryAttempts = 5, InitialRetryDelaySeconds = 30 };
        StationIdentityLogic.ApplyOverrides(
            new UserSetting { Id = 1, MaxRetryAttempts = 3, InitialRetryDelaySeconds = 90 }, options);

        Assert.Multiple(() =>
        {
            Assert.That(options.MaxRetryAttempts, Is.EqualTo(3));
            Assert.That(options.InitialRetryDelaySeconds, Is.EqualTo(90));
        });
    }

    [Test]
    public void NullOverrides_KeepTheConfiguredPolicy()
    {
        var options = new DireControlOptions { MaxRetryAttempts = 5, InitialRetryDelaySeconds = 30 };
        StationIdentityLogic.ApplyOverrides(new UserSetting { Id = 1 }, options);

        Assert.That((options.MaxRetryAttempts, options.InitialRetryDelaySeconds), Is.EqualTo((5, 30)));
    }

    [Test]
    public void OutOfRangeStoredValues_AreClamped()
    {
        var options = new DireControlOptions();
        StationIdentityLogic.ApplyOverrides(
            new UserSetting { Id = 1, MaxRetryAttempts = 99, InitialRetryDelaySeconds = 1 }, options);

        Assert.That((options.MaxRetryAttempts, options.InitialRetryDelaySeconds), Is.EqualTo((10, 10)));
    }

    [TestCase(0, 30, "attempts")]
    [TestCase(11, 30, "attempts")]
    [TestCase(5, 9, "delay")]
    [TestCase(5, 601, "delay")]
    public void Validation_RejectsOutOfRange(int attempts, int delay, string mentions) =>
        Assert.That(StationIdentityLogic.ValidateRetry(attempts, delay), Does.Contain(mentions));

    [Test]
    public void Validation_AcceptsTheBounds()
    {
        Assert.That(StationIdentityLogic.ValidateRetry(1, 10), Is.Null);
        Assert.That(StationIdentityLogic.ValidateRetry(10, 600), Is.Null);
    }
}

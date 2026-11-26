using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public sealed class MessageSplitterTests
{
    [Test]
    public void ShortText_IsSingleUnnumberedPart()
    {
        Assert.That(MessageSplitter.Split("Hello there"), Is.EqualTo(new[] { "Hello there" }));
    }

    [Test]
    public void ExactlyMaxLength_IsSinglePart()
    {
        var text = new string('a', MessageSplitter.MaxPartLength);

        Assert.That(MessageSplitter.Split(text), Is.EqualTo(new[] { text }));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void EmptyText_YieldsNoParts(string text)
    {
        Assert.That(MessageSplitter.Split(text), Is.Empty);
    }

    [Test]
    public void LongText_WordWrapsIntoNumberedParts_WithinLimit()
    {
        const string text =
            "I am not able to reach a digipeater or I-Gate from my house. Need more power and a better antenna.";

        var parts = MessageSplitter.Split(text);

        Assert.That(parts, Has.Count.EqualTo(2));
        Assert.That(parts, Has.All.Length.LessThanOrEqualTo(MessageSplitter.MaxPartLength));
        Assert.That(parts[0], Does.EndWith(" (1/2)"));
        Assert.That(parts[1], Does.EndWith(" (2/2)"));
        // No word is cut: stripping the suffixes and rejoining restores the text.
        var rejoined = string.Join(' ', parts.Select(p => p[..p.LastIndexOf(" (", StringComparison.Ordinal)]));
        Assert.That(rejoined, Is.EqualTo(text));
    }

    [Test]
    public void UnbrokenRun_IsHardBroken()
    {
        var text = new string('x', 150);

        var parts = MessageSplitter.Split(text);

        Assert.That(parts, Has.All.Length.LessThanOrEqualTo(MessageSplitter.MaxPartLength));
        Assert.That(string.Concat(parts.Select(p => p[..p.LastIndexOf(" (", StringComparison.Ordinal)])), Is.EqualTo(text));
    }

    [Test]
    public void DoubleDigitPartCount_StillFitsLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 200)); // ~1000 chars

        var parts = MessageSplitter.Split(text);

        Assert.That(parts.Count, Is.GreaterThanOrEqualTo(10));
        Assert.That(parts, Has.All.Length.LessThanOrEqualTo(MessageSplitter.MaxPartLength));
        Assert.That(parts[^1], Does.EndWith($" ({parts.Count}/{parts.Count})"));
    }

    [TestCase("ok {5}", "ok (5)")]
    [TestCase("a|b~c", "a/b-c")]
    [TestCase("line one\nline two", "line one line two")]
    [TestCase("  spaced   out  ", "spaced out")]
    public void Sanitize_ReplacesReservedCharacters(string input, string expected)
    {
        Assert.That(MessageSplitter.Sanitize(input), Is.EqualTo(expected));
    }
}

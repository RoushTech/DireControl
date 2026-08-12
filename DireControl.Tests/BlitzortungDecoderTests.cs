using System;
using System.Collections.Generic;
using System.Text;
using DireControl.Api.Services.Weather;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class BlitzortungDecoderTests
{
    // A real frame captured from wss://ws1.blitzortung.org/ (base64 of the raw UTF-8
    // text payload). Decompresses to a strike JSON with the values asserted below.
    private const string CapturedFrameBase64 =
        "eyJ0aW1lIjoxNzg2NTM3NDgwMTMyODQ1MMSYLCJsYXTEhjQ0LjPEijA2MsSabG9uxIY5LsSmNcSVOcSaYWzEnjowxJpwb2zEhsS4Im1kc8SGNzU2xLLEv2NnxIbEkjPEmnN0xJ11xYLEh8SacmVnacSqxIY4xY5pxYo6W8SAxY9hxIYyNznEp8SBxIPEhTrElDkxNDfEqMSdxJ/EoTM0xJE2xKjFmjo4LjUxxJcwxYfEtMS2MzXFjsWQdMWSxJ99LMWixZDFpTgyxYfEgsSExIbGhznFuDHFs8S2xKDErsWvMjPGnMSbxbwxMC7Fr8SwNMaExLXGmMaIIsWjxovFkzEyxo7GkMWkOjPGg8aVxavGmDjGgcaHxp3EnzPErjbFsMW3xbvEq8SHxqg2NDY4N8WNIsaFxYvFh8ayxozFrTTGt8axxpHEhzLHh8SaxpbFrMW4MMSTx5PEnMaeNS7EiMa7xqLHisWLx403Nse0xqTHlceMxonFkca0MMecxaPGmDIxxZzFqsaXOsSgxJDFpseDyIXHqzMzNsSTxbLGpceLxb41NTTGgcSzxq7Hn8Wpx5jGtDbHvceeOMiNx6LGvsiFyKHFqMWHx6nEn8eryJTEkMSKx7DFvS7EicSXx4LHlMiZMTnFusedx7rFpcifxrnGo8evyIPFrDXEjjfIiMSbxbTIhcShxoDEpTfIkMSpx4s3LsWxMMiHyJjEtsaCx7nGs8WLxrbGj8i6xaU5OciQx6PEhsmDyI7HocmHx6rErsSUxbjJjsamxqjGmse0MMWpx7fJoMeXxorHmca1yL3GksiUyKPIhDY3xJHEkciJxJYuxpTEoDnGsMmPxYsxLseAyb/EpcmWxYvHk8icyZvJusWtxJDHk8mjOseQybLJocqDyKvJoMSjNMivMcqMxYXJhTPGrcmXypPJt8a0yZzGuMWDx5HJvcWsxYTEpsiiyajFtcesxI/FsMiCyorHn8mSNcqIx5PHt8qsyLvHjMqWxIjKkMmBxYPEpseRyKjJiDTHhcqlNsaDx6jGpsqMxIoxxIvIgse3xpTIucqUx5/LiMe0xanKmsezN8uaxL7IqciKyZLGo8SVy5bHi8a1x6zEjcWnyLnHt8uayZnJuMqvyZ7HjMeSyrPFmzkwy5HFqcuqyoTFuDg4ybzIkcSGNsW/xLDElMmzyLbFt8aky6DGp8qWxrvLgcu+OjnMh8W3x6jLkMeryabIp8ivzIzGuzjMgMawy4TLlMu4xZMyx5vJnce+xa0yMMawyprGpzLGosyIyoPJkcaDx4DLisq+xK3Ms8i4zJDGnsuRzKvKlcyvx57Gu8i4zJnGp8utyaDKg8W2xIrHkMq9xbzHq8eSyYzNg8yLyp7Iusmax5rKlsWmxprNjMSlxbfMqsq4y6vHiMytyLnKvs2WxI/Hj8qRxa3MmM2cy7nMlsytxr3IhManxIvElMqJy5DMjMi3yoDNocyKyr/JjcqlxYXNr8qGzYbNns2IxrnFr8qZyKTGp8qIxYXNu8aezIzIrMyBxqTKvsyMxInEjcSPza/Hs8qjzbLHu8qWOcqpzaLJocmNzJ7Ok8yNxJDOnM6BzpnIoceAyJDHt8ezyJvKrcexypbHkcakzLXGtciVx6DMui7EkcePyKHIr8mRxKDIgcSLza/EsMiQzJTHvM6KxpjGg8ukzo7EksiUxbDKgzLOv8umxIjKpM+WxI3IuMiOzobMss6IzK3Nn8egyILOusWxx5DLj82Eyo3ElsWxzZTLsMeFzIDHoMSUz4jJtsuGybnPjcSHxI/Jos+RxIzGgc6Sx4TFv8qlxqzIr8erza3Mm82vxoHPtc2dzK7KsDrEicS+zrrHjsuByY7LkMmRxJfMj8mtyJLGqceHMsaAza/Pus+hy4gwxI7NjMyyyLfQo86+xqzLmjTMg8W8xb7GlM+kzKjImciWz4rOtcWtzZ/IlM+QzbfMt8unzbHMhMygyI7MnMqkz7DJq8SM0J/Pns6gxo3PuMSIz7PLi8ef0LTQrM6+xIvElsemyK/FvsuUxJbPmci1xobQh9GJyIXLiMWFzo3Qu8eAyqLOvsmlxI7PrsexyLHIgciVy4PImcaTzJPQttCMy7vJhc65zo7ElMSS0Z3MhMmRxafHksSN0ZbGqcegxajOsdCzyYPQodGLxKPRttC7z53KiMqfxb/Jhcm/zp/KvsWwyoXSidCd0J/PlNGey7rMsMSSyYbMtca70KzKgNCpx5HFt9CZyovGqcSJxajQssmX0IPSmtGgx4DNjMenyY3Pv8mJzr/MgMqyzoHEoceRxIrJgMe3xbjOiNGzzLDGo8ajzYzFuMSPyoLNpsW4xb/SvM2uzoHErceHyo7RrsS2x47Rsc+2yJ7PuMai0Z3MtdCs0KfKvcuQxb7GtcajzIHSgMiMyqnIgM2vxJXGsMug0pvHnsyyz4fRjsWw0J3Lm8qD06DGosaB0anIsNOl0b3TqMyt04DNn8mFzbbFrNOxzKbQqNOJ07XLrdOkyIzLnNGvyLTLoNOB063Elsy0zo7Lkci4xa/SjsyIy6PKicamxKHJv8uZ0YfEvtSOypbFp9Gi1ILHj8iWxqfTtM6/x7TShs6ByqXHhsWFzLnRm8mkyrfTq31dxJpkZcScecSsyZLHimPEvcad1YDEt30=";

    private static string CapturedFrame()
        => Encoding.UTF8.GetString(Convert.FromBase64String(CapturedFrameBase64));

    [Test]
    public void Decompress_RealFrame_ProducesStrikeJson()
    {
        var json = BlitzortungDecoder.Decompress(CapturedFrame());

        Assert.That(json, Does.StartWith(
            "{\"time\":1786537480132845000,\"lat\":44.386062,\"lon\":9.625849,"));
        Assert.That(json, Does.EndWith("}"));
    }

    [Test]
    public void DecodeStrike_RealFrame_ParsesTimeAndPosition()
    {
        var strike = BlitzortungDecoder.DecodeStrike(CapturedFrame());

        Assert.That(strike, Is.Not.Null);
        Assert.That(strike!.Value.Latitude, Is.EqualTo(44.386062));
        Assert.That(strike.Value.Longitude, Is.EqualTo(9.625849));
        // 1786537480132845000 ns since epoch → 2026-08-12T09:04:40.1328450Z
        Assert.That(strike.Value.TimeUtc,
            Is.EqualTo(DateTime.UnixEpoch.AddTicks(1786537480132845000 / 100)));
        Assert.That(strike.Value.TimeUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void DecodeStrike_NonStrikeJson_ReturnsNull()
    {
        // Compresses to itself for short inputs with no repetition.
        Assert.That(BlitzortungDecoder.DecodeStrike("{\"a\":111}"), Is.Null);
        Assert.That(BlitzortungDecoder.DecodeStrike("not json at all"), Is.Null);
        Assert.That(BlitzortungDecoder.DecodeStrike(""), Is.Null);
    }

    [TestCase("{\"time\":1700000000000000000,\"lat\":1.5,\"lon\":-2.5}")]
    [TestCase("{\"time\":1786537480132845000,\"lat\":44.386062,\"lon\":9.625849,\"alt\":0,\"pol\":0,\"mds\":7569,\"mcg\":133,\"status\":1,\"region\":8}")]
    public void Decompress_InvertsLzwCompression(string original)
    {
        Assert.That(BlitzortungDecoder.Decompress(Compress(original)), Is.EqualTo(original));
    }

    [Test]
    public void Decompress_HighlyRepetitiveInput_RoundTrips()
    {
        var original = string.Concat(
            System.Linq.Enumerable.Repeat("{\"lat\":12.34,\"lon\":56.78}", 50));
        Assert.That(BlitzortungDecoder.Decompress(Compress(original)), Is.EqualTo(original));
    }

    /// <summary>
    /// Reference LZW compressor matching the feed's scheme: dictionary codes start at
    /// 256 and are emitted as literal char values.
    /// </summary>
    private static string Compress(string input)
    {
        if (input.Length == 0)
            return "";

        var dict = new Dictionary<string, int>();
        var output = new StringBuilder();
        var nextCode = 256;
        var phrase = input[0].ToString();

        for (var i = 1; i < input.Length; i++)
        {
            var candidate = phrase + input[i];
            if (dict.ContainsKey(candidate))
            {
                phrase = candidate;
                continue;
            }

            output.Append(phrase.Length == 1 ? phrase[0] : (char)dict[phrase]);
            dict[candidate] = nextCode++;
            phrase = input[i].ToString();
        }

        output.Append(phrase.Length == 1 ? phrase[0] : (char)dict[phrase]);
        return output.ToString();
    }
}

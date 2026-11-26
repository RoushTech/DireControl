using DireControl.Api.Services;
using DireControl.Tests.Fakes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Missed-decode capture runs on the audio thread, so it must not allocate a
/// multi-megabyte snapshot there.
/// </summary>
[TestFixture]
public sealed class ModemAudioCaptureTests
{
    private sealed class StaticOptions(AudioCaptureOptions value) : IOptionsMonitor<AudioCaptureOptions>
    {
        public AudioCaptureOptions CurrentValue => value;
        public AudioCaptureOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AudioCaptureOptions, string?> listener) => null;
    }

    private sealed class TempEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Test]
    public void CaptureMissedDecode_DoesNotAllocateASnapshotOnTheAudioThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"direcontrol-capture-{Guid.NewGuid():N}");
        using var harness = new IngestHarness();
        var service = new ModemAudioCaptureService(
            new StaticOptions(new AudioCaptureOptions { PreBufferSeconds = 15, MinCaptureIntervalSeconds = 0 }),
            harness.ScopeFactory,
            new TempEnvironment(root),
            NullLoggerFactory.Instance);
        try
        {
            const int rate = 48000;
            var block = new float[1024];
            for (var i = 0; i < rate * 16 / block.Length; i++)
                service.Append("radio", block, rate);   // ring (and its spare) allocated here

            var before = GC.GetAllocatedBytesForCurrentThread();
            service.CaptureMissedDecode("radio", "N0CALL-10", rate);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.LessThan(64 * 1024),
                "the 15 s snapshot (~2.9 MB) must go into the preallocated spare");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

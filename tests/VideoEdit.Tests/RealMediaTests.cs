using System.Globalization;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class RealMediaTests
{
    [Fact]
    public async Task RendersAKnownSourceRangeWithTheSharedRenderer()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var runner = new ProcessRunner();
        try
        {
            var source = Path.Combine(root, "source.mov");
            var generated = await runner.RunAsync(
                "ffmpeg",
                ["-hide_banner", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x72:r=30",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                    "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source],
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(generated.ExitCode == 0, generated.StandardError);
            var destination = Path.Combine(root, "clip.mp4");
            var renderer = TestSupport.Renderer(root, runner);

            await renderer.RenderRangesAsync(
                source,
                [new TimeRange(0.7205, 1.7205)],
                destination,
                preview: true,
                TimeSpan.FromSeconds(60),
                CancellationToken.None);

            Assert.True(File.Exists(destination));
            Assert.False(File.Exists(destination + ".incomplete"));

            var probe = await runner.RunAsync(
                "ffprobe",
                ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", destination],
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            Assert.Equal(0, probe.ExitCode);
            var duration = double.Parse(probe.StandardOutput.Trim(), CultureInfo.InvariantCulture);
            Assert.InRange(duration, 0.9, 1.15);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

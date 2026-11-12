using System.Globalization;
using System.Text.RegularExpressions;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public sealed class CompositionAudioTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "video-edit-audio-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessRunner runner = new();

    [Fact]
    public async Task RoughCutKeepsCloseSceneSpeechWhenGeneratedAudioIs44100Mono()
    {
        Directory.CreateDirectory(Path.Combine(root, "edit"));
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        Directory.CreateDirectory(Path.Combine(root, "generated", "narration-test"));

        var source = Path.Combine(root, "sources", "source.mov");
        var still = Path.Combine(root, "sources", "still.jpg");
        var narration = Path.Combine(root, "generated", "narration-test", "audio.mp3");
        await RunFfmpeg(
            "-f", "lavfi", "-i", "color=c=black:s=128x72:r=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "2", "-ac", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ar", "48000",
            source);
        await RunFfmpeg("-y", "-f", "lavfi", "-i", "color=c=gray:s=128x72", "-frames:v", "1", still);
        await RunFfmpeg(
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100",
            "-t", "1", "-ac", "1", "-c:a", "libmp3lame", "-ar", "44100",
            narration);

        var fingerprint = MediaRenderer.FingerprintFile(source);
        var stillFingerprint = MediaRenderer.FingerprintFile(still);
        var narrationFingerprint = MediaRenderer.FingerprintFile(narration);
        var store = new ProjectStore(new VideoEditOptions { ProjectDirectory = root });
        store.SaveNew(new ProjectDocument
        {
            Name = "audio",
            Revision = 1,
            Sources =
            [
                new SourceAsset
                {
                    Id = "source-1",
                    Path = source,
                    Fingerprint = fingerprint,
                    DurationSeconds = 2,
                    Kind = "video",
                },
                new SourceAsset
                {
                    Id = "still-generated",
                    Path = still,
                    Fingerprint = stillFingerprint,
                    Kind = "image",
                }
            ],
            Scenes =
            [
                new Scene
                {
                    Id = "scene-1",
                    Title = "Open",
                    Position = 1,
                    Selections =
                    [
                        new SourceSelection
                        {
                            SourceAssetId = "source-1",
                            SourceFingerprint = fingerprint,
                            Start = 0,
                            End = 1,
                        }
                    ],
                },
                new Scene
                {
                    Id = "scene-generated",
                    Title = "Generated",
                    Position = 2,
                    NarrationAssetId = "narration-test",
                    NarrationPath = MediaRenderer.ProjectRelative(root, narration),
                    NarrationFingerprint = narrationFingerprint,
                    VisualAssetId = "still-generated",
                    NarrationDurationSeconds = 1,
                },
                new Scene
                {
                    Id = "scene-close",
                    Title = "Close",
                    Position = 3,
                    Selections =
                    [
                        new SourceSelection
                        {
                            SourceAssetId = "source-1",
                            SourceFingerprint = fingerprint,
                            Start = 1,
                            End = 2,
                        }
                    ],
                }
            ],
            Output = new OutputSettings { Width = 128, Height = 72, FrameRate = 30 },
        });

        var renderer = TestSupport.Renderer(root, runner);
        await renderer.BuildRoughCutAsync(CancellationToken.None);

        var generated = await ProbeAudio(Path.Combine(root, "edit", "previews", "scene-scene-generated.mp4"));
        Assert.Equal(48000, generated.SampleRate);
        Assert.Equal(2, generated.Channels);

        var close = Path.Combine(root, "edit", "previews", "scene-scene-close.mp4");
        var rough = Path.Combine(root, "edit", "previews", "rough-cut.mp4");
        var closeMean = await MeanVolume(close, null, null);
        var tailMean = await MeanVolume(rough, 2.0, 0.9);
        Assert.True(
            tailMean > closeMean - 6,
            $"Close-scene speech was lost in concat. close={closeMean:0.1} dB tail={tailMean:0.1} dB");
    }

    private async Task RunFfmpeg(params string[] arguments)
    {
        var result = await runner.RunAsync("ffmpeg", ["-hide_banner", "-y", .. arguments], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private async Task<(int SampleRate, int Channels)> ProbeAudio(string path)
    {
        var result = await runner.RunAsync(
            "ffprobe",
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels", "-of", "csv=p=0", path],
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        var parts = result.StandardOutput.Trim().Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private async Task<double> MeanVolume(string path, double? start, double? duration)
    {
        var args = new List<string> { "-hide_banner" };
        if (start is double ss)
        {
            args.Add("-ss");
            args.Add(ss.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(path);
        if (duration is double t)
        {
            args.Add("-t");
            args.Add(t.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.AddRange(["-af", "volumedetect", "-f", "null", "-"]);
        var result = await runner.RunAsync("ffmpeg", args, TimeSpan.FromSeconds(20), CancellationToken.None);
        var match = Regex.Match(result.StandardError + result.StandardOutput, @"mean_volume:\s+([-\d.]+) dB");
        Assert.True(match.Success, result.StandardError);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        runner.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

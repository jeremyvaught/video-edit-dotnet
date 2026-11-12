using VideoEdit;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public sealed class NarrationIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "video-edit-identity-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessRunner runner = new();

    [Fact]
    public async Task DerivativeSwapUnderTheSameAssetIdInvalidatesAcceptance()
    {
        Directory.CreateDirectory(Path.Combine(root, "edit"));
        Directory.CreateDirectory(Path.Combine(root, "generated", "narration-test"));
        var first = Path.Combine(root, "generated", "narration-test", "bedded-880.wav");
        var second = Path.Combine(root, "generated", "narration-test", "bedded-1760.wav");
        await WriteSine(first, 880);
        await WriteSine(second, 1760);
        var firstPrint = MediaRenderer.FingerprintFile(first);
        var secondPrint = MediaRenderer.FingerprintFile(second);
        Assert.NotEqual(firstPrint, secondPrint);

        var (store, service, _, _) = TestSupport.CreateStore();
        var project = GeneratedOnly(first, firstPrint, "/tmp/still.jpg");
        store.SaveNew(project);
        service.AcceptScene("scene-generated", 1);
        var accepted = store.Load();
        Assert.NotNull(accepted.Scenes[0].Acceptance);
        var before = CompositionHasher.SceneHash(accepted, accepted.Scenes[0]);

        var rebound = service.BindGeneratedNarration(
            "narration-test",
            MediaRenderer.ProjectRelative(store.ProjectDirectory, second),
            secondPrint,
            1,
            accepted.Revision);
        Assert.Null(rebound.Scenes[0].Acceptance);
        Assert.NotEqual(before, CompositionHasher.SceneHash(rebound, rebound.Scenes[0]));
        Assert.Equal(secondPrint, rebound.Scenes[0].NarrationFingerprint);
    }

    [Fact]
    public async Task OverwritingTheBoundFileFailsSourceValidation()
    {
        Directory.CreateDirectory(Path.Combine(root, "edit"));
        var still = Path.Combine(root, "still.jpg");
        await WriteStill(still);
        var audio = Path.Combine(root, "tone.wav");
        await WriteSine(audio, 880);
        var fingerprint = MediaRenderer.FingerprintFile(audio);
        var options = new VideoEditOptions { ProjectDirectory = root };
        var store = new ProjectStore(options);
        store.SaveNew(GeneratedOnly(audio, fingerprint, still));
        var renderer = TestSupport.Renderer(root, runner);
        renderer.EnsureAssets(store.Load());

        await WriteSine(audio, 1760);
        var error = Assert.Throws<VideoEditException>(() => renderer.EnsureAssets(store.Load()));
        Assert.Contains("fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptedSingleScenePublishLeavesNoFinalFile()
    {
        Directory.CreateDirectory(root);
        var dest = Path.Combine(root, "export.mp4");
        Assert.ThrowsAny<Exception>(() => MediaRenderer.PublishFile(Path.Combine(root, "missing.mp4"), dest));
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".tmp"));
    }

    private async Task WriteSine(string path, int frequency)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = await runner.RunAsync(
            "ffmpeg",
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000",
                "-t", "1", "-ac", "2", path
            ],
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private async Task WriteStill(string path)
    {
        var result = await runner.RunAsync(
            "ffmpeg",
            ["-hide_banner", "-y", "-f", "lavfi", "-i", "color=c=gray:s=64x64", "-frames:v", "1", path],
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private static ProjectDocument GeneratedOnly(string audioPath, string fingerprint, string stillPath) =>
        new()
        {
            Name = "identity",
            Revision = 1,
            Sources =
            [
                new SourceAsset
                {
                    Id = "still-generated",
                    Path = stillPath,
                    Fingerprint = File.Exists(stillPath) ? MediaRenderer.FingerprintFile(stillPath) : new string('b', 64),
                    Kind = "image",
                }
            ],
            Scenes =
            [
                new Scene
                {
                    Id = "scene-generated",
                    Title = "Generated",
                    Position = 1,
                    NarrationAssetId = "narration-test",
                    NarrationPath = audioPath,
                    NarrationFingerprint = fingerprint,
                    VisualAssetId = "still-generated",
                    NarrationDurationSeconds = 1,
                }
            ],
        };

    public void Dispose()
    {
        runner.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

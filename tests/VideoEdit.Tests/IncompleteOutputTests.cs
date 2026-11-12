using VideoEdit.Services;

namespace VideoEdit.Tests;

public class IncompleteOutputTests
{
    [Fact]
    public async Task FailedRenderDoesNotPromoteAPartialFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "edit", "previews"));
        var destination = Path.Combine(root, "edit", "previews", "clip.mp4");
        var renderer = TestSupport.Renderer(root, new FailingRunner());

        await Assert.ThrowsAsync<VideoEditException>(() =>
            renderer.RenderRangesAsync(
                "/tmp/source.mov",
                [new Models.TimeRange(1, 2)],
                destination,
                preview: true,
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(destination + ".incomplete"));
        Assert.Equal("partial", File.ReadAllText(destination + ".incomplete"));
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var temp = arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            File.WriteAllText(temp, "partial");
            return Task.FromResult(new ProcessResult(1, "", "ffmpeg failed"));
        }
    }
}

using System.Globalization;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class HostCutProposerTests
{
    [Fact]
    public void ProposesPrerollPauseAndPostrollFromVad()
    {
        var map = new VadSpeechMap
        {
            Segments =
            [
                new SpeechSegment { Start = 2.0, End = 3.5 },
                new SpeechSegment { Start = 6.0, End = 8.0 },
            ],
        };

        var proposals = HostCutProposer.Propose(map, 10, EditingPolicy.Default);
        Assert.Equal(
            [EditKinds.Preroll, EditKinds.Pause, EditKinds.Postroll],
            proposals.Select(item => item.Kind));
        Assert.Equal("e-preroll", proposals[0].EditId);
        Assert.Equal(0, proposals[0].Range.Start);
        Assert.Equal(1.65, proposals[0].Range.End);
        Assert.Equal("e-pause-4", proposals[1].EditId);
        Assert.Equal(3.5, proposals[1].Range.Start);
        Assert.Equal(5.4, proposals[1].Range.End);
        Assert.Equal("e-postroll", proposals[2].EditId);
        Assert.Equal(8.0, proposals[2].Range.Start);
        Assert.Equal(10.0, proposals[2].Range.End);
    }

    [Fact]
    public void LeavesAResidualPauseAndIgnoresShortGaps()
    {
        var map = new VadSpeechMap
        {
            Segments =
            [
                new SpeechSegment { Start = 1.0, End = 2.0 },
                new SpeechSegment { Start = 2.7, End = 3.0 },
                new SpeechSegment { Start = 5.0, End = 6.0 },
            ],
        };

        var proposals = HostCutProposer.Propose(map, 6.1, new EditingPolicy { PauseTargetS = 0.6, MarginAfterS = 0.35 });
        var pause = Assert.Single(proposals, item => item.Kind == EditKinds.Pause);
        Assert.Equal(3.0, pause.Range.Start);
        Assert.Equal(4.4, pause.Range.End);
        Assert.InRange(5.0 - pause.Range.End, 0.59, 0.61);
        Assert.DoesNotContain(proposals, item => item.Range.Start == 2.0 && item.Range.End > 2.6 && item.Range.End < 2.8);
    }

    [Fact]
    public async Task PostrollUsesTheExactProbedEndpointOnAFortyOneFrameSource()
    {
        using var runner = new ProcessRunner();
        var (bootstrap, store, service, root) = TestSupport.CreateBootstrap(runner);
        var source = Path.Combine(root, "sources", "source.mov");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var result = await runner.RunAsync(
            "ffmpeg",
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "color=c=black:s=128x72:r=30",
                "-frames:v", "41",
                "-c:v", "libx264",
                "-video_track_timescale", "15360",
                "-an",
                source,
            ],
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);

        var project = await bootstrap.EnsureProjectAsync();
        var duration = project.Sources[0].DurationSeconds;
        Assert.Equal(1.366667, duration, 6);

        var proposals = HostCutProposer.Propose(
            new VadSpeechMap { Segments = [new SpeechSegment { Start = 0, End = 0.8 }] },
            duration);
        var postroll = Assert.Single(proposals, item => item.Kind == EditKinds.Postroll);
        Assert.Equal(0.8, postroll.Range.Start);
        Assert.Equal(duration, postroll.Range.End);
        Assert.True(postroll.Range.End <= duration);

        var updated = service.AddMissingHostCuts(proposals, project.Revision);
        Assert.Empty(ProjectValidator.Collect(updated));
        var edit = Assert.Single(updated.Scenes[0].Edits, item => item.Kind == EditKinds.Postroll);
        Assert.Equal(duration, edit.End);
        Assert.Equal(duration.ToString("G17", CultureInfo.InvariantCulture), store.Load().Sources[0].DurationSeconds.ToString("G17", CultureInfo.InvariantCulture));
    }
}

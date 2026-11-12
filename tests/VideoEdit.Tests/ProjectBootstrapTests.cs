using System.Globalization;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ProjectBootstrapTests
{
    [Fact]
    public async Task EmptyFolderWithOnlySourceBootsCleanly()
    {
        using var runner = new ProcessRunner();
        var (bootstrap, store, service, root) = TestSupport.CreateBootstrap(runner);
        var source = Path.Combine(root, "sources", "source.mov");
        await WriteSourceAsync(runner, source, 2);

        var project = await bootstrap.EnsureProjectAsync();

        Assert.Equal(Path.GetFileName(root), project.Name);
        Assert.Empty(project.Findings);
        Assert.Empty(project.Questions);
        Assert.Empty(project.Scenes[0].Edits);
        Assert.Single(project.Scenes);
        Assert.Equal("scene-1", project.Scenes[0].Id);
        Assert.Equal(0, project.Scenes[0].Selections[0].Start);
        Assert.Equal(project.Sources[0].DurationSeconds, project.Scenes[0].Selections[0].End);
        Assert.InRange(project.Sources[0].DurationSeconds, 1.9, 2.2);
        Assert.Equal(MediaRenderer.FingerprintFile(source), project.Sources[0].Fingerprint);
        Assert.Equal("h264", project.Sources[0].CodecName);
        Assert.Equal(320, project.Sources[0].Width);
        Assert.Equal(240, project.Sources[0].Height);
        Assert.False(string.IsNullOrWhiteSpace(project.Sources[0].AverageFrameRate));
        Assert.NotEqual("example-project", project.Name);
        Assert.True(File.Exists(Path.Combine(root, "edit", EditingPolicy.FileName)));
        Assert.True(File.Exists(Path.Combine(root, "edit", "analysis", SourceMediaProbe.FileName)));
        Assert.False(File.Exists(Path.Combine(root, "edit", "analysis", "findings.json")));

        var again = await bootstrap.EnsureProjectAsync();
        Assert.Equal(project.Revision, again.Revision);
        Assert.Empty(again.Findings);

        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-findings.json");
        var imported = service.ImportFindings(fixture);
        Assert.Single(imported.Findings, finding => finding.Id == "preroll");
        Assert.Single(imported.Scenes[0].Edits, edit => edit.Id == "e-preroll");
        Assert.Single(imported.Questions, question => question.Id == "q-fixture");
        Assert.True(imported.Revision > project.Revision);

        var replay = service.ImportFindings(fixture);
        Assert.Equal(imported.Revision, replay.Revision);
    }

    [Fact]
    public async Task ImportFindingsRejectsRangesPastTheSource()
    {
        using var runner = new ProcessRunner();
        var (bootstrap, _, service, root) = TestSupport.CreateBootstrap(runner);
        await WriteSourceAsync(runner, Path.Combine(root, "sources", "source.mov"), 2);
        await bootstrap.EnsureProjectAsync();

        var fixture = Path.Combine(root, "out-of-range.json");
        File.WriteAllText(
            fixture,
            """
            {"findings":[{"id":"late","kind":"pause","start":10,"end":12,"transcriptEvidence":"","reason":"late","confidence":0.5}],"questions":[]}
            """);

        var error = Assert.Throws<VideoEditException>(() => service.ImportFindings(fixture));
        Assert.Equal("validation", error.Code);
        Assert.Contains("late", error.Message, StringComparison.Ordinal);
        Assert.Empty(service.Load().Findings);
    }

    private static async Task WriteSourceAsync(IProcessRunner runner, string path, int seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = await runner.RunAsync(
            "ffmpeg",
            [
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", seconds.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264",
                "-c:a", "aac",
                path,
            ],
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }
}

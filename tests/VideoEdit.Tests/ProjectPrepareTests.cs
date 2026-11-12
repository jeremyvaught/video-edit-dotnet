using System.Globalization;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ProjectPrepareTests
{
    [Fact]
    public async Task PrepareIsIdempotentAndKeepsDecisions()
    {
        using var runner = new ProcessRunner();
        var (bootstrap, store, service, root) = TestSupport.CreateBootstrap(runner);
        await WriteSourceAsync(runner, Path.Combine(root, "sources", "source.mov"), 4);
        File.WriteAllText(
            Path.Combine(root, "sources", "script.md"),
            """
            # Title

            <!-- preface that is not spoken -->

            I have ten gardeners.
            """);
        await bootstrap.EnsureProjectAsync();
        SeedAnalysis(root, includeSorry: true);

        var prepare = TestSupport.CreatePrepare(store, service, runner);
        var first = await prepare.RunAsync(CancellationToken.None);
        Assert.Contains(first.Scenes[0].Edits, edit => edit.Id == "e-preroll" && edit.Decision == DecisionStates.Pending);
        Assert.Contains(first.Scenes[0].Edits, edit => edit.Kind == EditKinds.Pause);
        Assert.Contains(first.Scenes[0].Edits, edit => edit.Kind == EditKinds.Postroll);
        Assert.DoesNotContain(
            first.Findings,
            finding => (finding.ScriptText ?? "").Contains("ten", StringComparison.Ordinal));
        Assert.Contains(
            first.Findings,
            finding => finding.Kind == ScriptDriftKinds.Insertion
                && (finding.SpokenText ?? "").Contains("sorry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            first.Scenes[0].Edits,
            edit => edit.Kind == EditKinds.Insertion
                && (edit.RemovedText ?? "").Contains("sorry", StringComparison.OrdinalIgnoreCase));

        var revision = first.Revision;
        service.RecordDecision("e-preroll", DecisionStates.Approved, "keep the lead", revision);
        var noted = store.Load();
        service.SaveNote(
            noted.Scenes[0].Edits.First(edit => edit.Kind == EditKinds.Pause).Id,
            "leave a breath",
            noted.Revision);

        var second = await prepare.RunAsync(CancellationToken.None);
        Assert.Equal(store.Load().Revision, second.Revision);
        Assert.Equal(1, second.Scenes[0].Edits.Count(edit => edit.Kind == EditKinds.Preroll));
        Assert.Equal(1, second.Scenes[0].Edits.Count(edit => edit.Kind == EditKinds.Postroll));
        Assert.Equal(
            1,
            second.Scenes[0].Edits.Count(edit => edit.Kind == EditKinds.Pause));
        Assert.Equal(
            1,
            second.Scenes[0].Edits.Count(edit => edit.Kind == EditKinds.Insertion));
        var preroll = second.Scenes[0].Edits.Single(edit => edit.Id == "e-preroll");
        Assert.Equal(DecisionStates.Approved, preroll.Decision);
        Assert.Equal("keep the lead", preroll.Note);
        Assert.Equal(
            "leave a breath",
            second.Scenes[0].Edits.Single(edit => edit.Kind == EditKinds.Pause).Note);
        Assert.Equal(
            first.Findings.Count(finding => ScriptDriftKinds.IsDrift(finding.Kind)),
            second.Findings.Count(finding => ScriptDriftKinds.IsDrift(finding.Kind)));
    }

    [Fact]
    public async Task PrepareWithoutScriptStillProposesHostCuts()
    {
        using var runner = new ProcessRunner();
        var (bootstrap, store, service, root) = TestSupport.CreateBootstrap(runner);
        await WriteSourceAsync(runner, Path.Combine(root, "sources", "source.mov"), 4);
        await bootstrap.EnsureProjectAsync();
        SeedAnalysis(root, includeSorry: false);

        var prepared = await TestSupport.CreatePrepare(store, service, runner).RunAsync(CancellationToken.None);
        Assert.Contains(prepared.Scenes[0].Edits, edit => edit.Kind == EditKinds.Preroll);
        Assert.Contains(prepared.Scenes[0].Edits, edit => edit.Kind == EditKinds.Pause);
        Assert.Contains(prepared.Scenes[0].Edits, edit => edit.Kind == EditKinds.Postroll);
        Assert.DoesNotContain(prepared.Findings, finding => ScriptDriftKinds.IsDrift(finding.Kind));
    }

    private static void SeedAnalysis(string root, bool includeSorry)
    {
        var analysis = Path.Combine(root, "edit", "analysis");
        Directory.CreateDirectory(analysis);
        VadSpeechMap.Write(
            Path.Combine(analysis, VadSpeechMap.FileName),
            new VadSpeechMap
            {
                Segments =
                [
                    new SpeechSegment { Start = 1.0, End = 1.5 },
                    new SpeechSegment { Start = 3.0, End = 3.3 },
                ],
            });
        File.WriteAllText(
            Path.Combine(analysis, EditingPolicy.DiagnosisFileName),
            """
            silence_start: 0
            silence_end: 1.0
            silence_start: 1.5
            silence_end: 3.0
            silence_start: 3.3
            silence_end: 4.0
            """);
        var words = includeSorry
            ? """
              {"transcription":[
                {"text":" sorry","offsets":{"from":1000,"to":1100}},
                {"text":" I","offsets":{"from":1100,"to":1180}},
                {"text":" have","offsets":{"from":1180,"to":1300}},
                {"text":" 10","offsets":{"from":1300,"to":1400}},
                {"text":" gardeners","offsets":{"from":1400,"to":1500}}
              ]}
              """
            : """
              {"transcription":[
                {"text":" I","offsets":{"from":1000,"to":1100}},
                {"text":" have","offsets":{"from":1100,"to":1300}},
                {"text":" 10","offsets":{"from":1300,"to":1400}},
                {"text":" gardeners","offsets":{"from":1400,"to":1500}}
              ]}
              """;
        File.WriteAllText(Path.Combine(analysis, "words.json"), words);
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

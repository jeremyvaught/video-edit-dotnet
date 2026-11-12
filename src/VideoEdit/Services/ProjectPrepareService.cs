using Microsoft.Extensions.Options;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class ProjectPrepareService(
    ProjectStore store,
    ProjectService service,
    VadAnalyzer vad,
    TranscriptService transcript,
    MediaRenderer renderer,
    IProcessRunner runner,
    IOptions<VideoEditOptions> options)
{
    public async Task<ProjectDocument> RunAsync(CancellationToken cancellationToken)
    {
        var project = store.Load();
        var source = project.Sources.FirstOrDefault(item => item.Kind != "image")
            ?? throw VideoEditException.Validation("A recorded video source is required to prepare.");
        var policy = EditingPolicy.LoadOrDefault(Path.Combine(store.ProjectDirectory, "edit", EditingPolicy.FileName));
        var wav = await vad.EnsureAudio16kAsync(source, project.Sources, cancellationToken);
        var wordsPath = await transcript.EnsureWordsAsync(source, project.Sources, wav, cancellationToken);
        var words = WhisperWords.Parse(File.ReadAllText(wordsPath));
        var map = await vad.EnsureMapAsync(source, project.Sources, policy, cancellationToken);
        var hostCuts = HostCutProposer.Propose(map, source.DurationSeconds, policy, words);
        project = service.AddMissingHostCuts(hostCuts, project.Revision);
        var videoOptions = options.Value;
        project = service.SnapPendingEdits(
            project.Revision,
            energyMinimum: proposed => AudioEnergy.FindLocalMinimumAsync(
                runner,
                videoOptions.FfmpegPath,
                source.Path,
                proposed,
                cancellationToken).GetAwaiter().GetResult());
        var scriptPath = Path.Combine(store.ProjectDirectory, "sources", "script.md");
        if (File.Exists(scriptPath))
        {
            var drifts = ScriptAligner.Align(
                File.ReadAllText(scriptPath),
                words,
                project,
                source.Id,
                source.Fingerprint);
            project = service.ReplaceScriptDriftFindings(drifts, project.Revision);
            project = service.AddMissingDriftCuts(project.Revision);
            project = service.SnapPendingEdits(
                project.Revision,
                energyMinimum: proposed => AudioEnergy.FindLocalMinimumAsync(
                    runner,
                    videoOptions.FfmpegPath,
                    source.Path,
                    proposed,
                    cancellationToken).GetAwaiter().GetResult());
        }

        if (project.Scenes.SelectMany(scene => scene.Edits).Any())
        {
            await renderer.BuildSplicesAsync(cancellationToken);
        }

        await renderer.BuildRoughCutAsync(cancellationToken);
        return store.Load();
    }
}

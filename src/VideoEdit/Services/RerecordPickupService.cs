using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class RerecordPickupService(
    ProjectStore store,
    ProjectService projectService,
    PickupMixService mix,
    TranscriptService transcript,
    VadAnalyzer analyzer,
    NarrationService narration,
    IProcessRunner runner,
    IOptions<VideoEditOptions> options)
{
    public async Task<ProjectDocument> ApplyFileAsync(
        string findingId,
        string path,
        int expectedRevision,
        bool cutaway,
        CancellationToken token)
    {
        var project = store.Load();
        if (project.Revision != expectedRevision)
        {
            throw VideoEditException.StaleWrite(expectedRevision, project.Revision);
        }

        var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
            ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
        if (!ScriptDriftKinds.IsDrift(finding.Kind))
        {
            throw VideoEditException.Validation("Only script drifts can take a recorded pickup.");
        }

        var pickupId = ProjectService.NextPickupId(project);
        string? staged = null;
        var hasVideo = false;
        try
        {
            staged = StagePickupFile(path);
            double duration;
            (duration, hasVideo) = await ProbeAsync(staged, token);
            project = projectService.AddStagedPickup(pickupId, staged, duration, hasVideo, expectedRevision);
            staged = null;
        }
        catch
        {
            TryDelete(staged);
            throw;
        }

        var pickup = project.Sources.First(item => item.Id == pickupId);
        var policy = EditingPolicy.LoadOrDefault(Path.Combine(store.ProjectDirectory, "edit", EditingPolicy.FileName));
        var wav = await analyzer.EnsureAudio16kAsync(pickup, project.Sources, token);
        await transcript.EnsureWordsAsync(pickup, project.Sources, wav, token);
        await analyzer.EnsureMapAsync(pickup, project.Sources, policy, token);

        var replace = ProjectService.ClauseRange(store.ProjectDirectory, project, finding);
        var pickupRange = PickupSpeechRange(project, pickup);
        var audioOnly = !hasVideo;
        project = projectService.ApplyPickup(
            pickupId,
            pickupRange.Start,
            pickupRange.End,
            project.Revision,
            findingId: finding.Id,
            replaceStart: replace.Start,
            replaceEnd: replace.End,
            audioOnly: audioOnly);
        if (audioOnly)
        {
            project = await mix.PrepareAsync(project.Revision, token);
        }

        if (!cutaway)
        {
            return project;
        }

        return store.Mutate(project.Revision, current =>
        {
            var changed = false;
            foreach (var selection in current.Scenes.SelectMany(scene => scene.Selections))
            {
                if (selection.AudioSourceAssetId == pickupId || selection.SourceAssetId == pickupId)
                {
                    selection.NeedsCutaway = true;
                    changed = true;
                }
            }

            return changed;
        });
    }

    public async Task<ProjectDocument> ApplyGeneratedAsync(
        string findingId,
        int expectedRevision,
        CancellationToken token)
    {
        var project = store.Load();
        var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
            ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
        if (string.IsNullOrWhiteSpace(project.Narration.VoiceId))
        {
            throw VideoEditException.Validation("Set a project narration voice ID before generating a pickup.");
        }

        var scriptPath = Path.Combine(store.ProjectDirectory, "sources", "script.md");
        var script = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath, token) : null;
        var sentence = ScriptSentence.ForFinding(script, finding);
        if (string.IsNullOrWhiteSpace(sentence))
        {
            throw VideoEditException.Validation("The script sentence for this item is empty.");
        }

        var request = new NarrationRequest(sentence, project.Narration.VoiceId);
        if (!string.IsNullOrWhiteSpace(project.Narration.ModelId))
        {
            request = request with { ModelId = project.Narration.ModelId };
        }

        var asset = await narration.GenerateAsync(request, token);
        return await ApplyFileAsync(findingId, asset.AudioPath, expectedRevision, cutaway: true, token);
    }

    private string StagePickupFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw VideoEditException.Validation($"Source file is missing: {path}");
        }

        var pickupsDir = Path.GetFullPath(Path.Combine(store.ProjectDirectory, "sources", "pickups"));
        Directory.CreateDirectory(pickupsDir);
        var extension = Path.GetExtension(full);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".mov";
        }

        var staged = Path.Combine(pickupsDir, ".incoming-" + Guid.NewGuid().ToString("N") + extension);
        File.Copy(full, staged, overwrite: false);
        return staged;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private TimeRange PickupSpeechRange(ProjectDocument project, SourceAsset pickup)
    {
        var analysis = SourceAnalysis.Directory(store.ProjectDirectory, pickup, project.Sources);
        if (SourceAnalysis.TryWordsPath(analysis) is not string wordsPath)
        {
            return new TimeRange(0, pickup.DurationSeconds);
        }

        var words = WhisperWords.Parse(File.ReadAllText(wordsPath));
        var spoken = new TimeRange(words[0].Start, words[^1].End);
        return WhisperWords.SnapIntoPauses(words, spoken);
    }

    private async Task<(double Duration, bool HasVideo)> ProbeAsync(string path, CancellationToken token)
    {
        var result = await runner.RunAsync(
            options.Value.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", path],
            TimeSpan.FromSeconds(30),
            token);
        if (result.ExitCode != 0)
        {
            throw VideoEditException.Validation($"Could not measure duration for {path}.");
        }

        try
        {
            using var metadata = JsonDocument.Parse(result.StandardOutput);
            var duration = double.Parse(
                metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                CultureInfo.InvariantCulture);
            if (!double.IsFinite(duration) || duration <= 0)
            {
                throw new FormatException();
            }

            var hasVideo = metadata.RootElement.TryGetProperty("streams", out var streams)
                && streams.EnumerateArray().Any(stream =>
                    stream.TryGetProperty("codec_type", out var type)
                    && type.GetString() == "video");
            return (duration, hasVideo);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
            or KeyNotFoundException or ArgumentNullException)
        {
            throw VideoEditException.Validation($"Source {path} has no usable duration.");
        }
    }
}

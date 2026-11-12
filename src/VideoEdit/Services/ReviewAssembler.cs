using System.Text.Json;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class ReviewAssembler(ProjectStore store, MediaRenderer renderer, MediaGate gate)
{
    public ReviewModel Assemble()
    {
        var project = store.Load();
        var manifest = renderer.LoadManifest();
        var fullHash = CompositionHasher.FullHash(project);
        var composition = CompositionResolver.ResolveProposed(project);
        var blocks = CompositionResolver.ExportBlocks(project);
        var scenes = new List<ReviewScene>();
        foreach (var scene in project.Scenes.OrderBy(item => item.Position))
        {
            var resolved = composition.Scenes.First(item => item.SceneId == scene.Id);
            var sceneHash = CompositionHasher.SceneHash(project, scene);
            var edits = scene.Edits.Select(edit =>
            {
                manifest.Splices.TryGetValue(edit.Id, out var splice);
                var spliceFresh = splice is not null
                    && splice.ProposalRevision == edit.ProposalRevision
                    && string.Equals(splice.CompositionHash, renderer.SpliceHash(scene, edit), StringComparison.Ordinal);
                return new ReviewEdit(
                    edit.Id,
                    edit.Kind,
                    CompositionResolver.SourceToOutput(project, edit.SourceAssetId, edit.Start),
                    CompositionResolver.SourceToOutput(project, edit.SourceAssetId, edit.End),
                    edit.Start,
                    edit.End,
                    edit.RemovedText,
                    edit.Reason,
                    edit.Confidence,
                    edit.Decision,
                    edit.Note,
                    edit.ProposalRevision,
                    edit.DecisionMatchesProposal,
                    edit.NoCleanBoundary,
                    spliceFresh && splice!.OriginalPath is not null ? "/media/previews/" + splice.OriginalPath : null,
                    spliceFresh ? "/media/previews/" + splice!.Path : null,
                    !spliceFresh,
                    ContextUrl(project, edit.SourceAssetId, edit.Start, edit.End),
                    ProjectService.ShowsReproposalNote(edit),
                    ProjectService.ShowsNoteNotUnderstood(edit));
            }).ToList();

            var request = scene.IsGenerated
                ? LoadNarrationRequest(store.ProjectDirectory, scene.NarrationAssetId)
                : null;
            var preview = scene.IsGenerated
                ? ReviewPreview.FromScene(manifest, scene.Id, sceneHash)
                : null;
            var reviewMatches = scene.Acceptance is not null
                && string.Equals(scene.Acceptance.CompositionHash, sceneHash, StringComparison.Ordinal);
            scenes.Add(new ReviewScene(
                scene.Id,
                scene.Title,
                resolved.DurationSeconds,
                sceneHash,
                CompositionResolver.SceneAcceptanceValid(project, scene, sceneHash),
                edits,
                scene.IsGenerated ? "generated" : "recorded",
                request?.Text,
                request?.VoiceId,
                request?.ModelId,
                request?.Stability,
                request?.SimilarityBoost,
                preview?.Url,
                preview?.Stale ?? false,
                StillUrl(project, scene),
                reviewMatches ? scene.Acceptance!.ReviewDecision : DecisionStates.Pending,
                reviewMatches ? scene.Acceptance!.Note : null,
                scene.Selections.Select(selection => new ReviewSelection(
                    selection.SourceAssetId,
                    CompositionResolver.SourceToOutput(project, selection.SourceAssetId, selection.Start),
                    CompositionResolver.SourceToOutput(project, selection.SourceAssetId, selection.End),
                    ContextUrl(project, selection.SourceAssetId, selection.Start, selection.End),
                    selection.AudioSourceAssetId,
                    selection.AudioStart,
                    selection.AudioEnd,
                    selection.NeedsCutaway,
                    selection.Start,
                    selection.End)).ToList(),
                CompositionResolver.HasUnresolvedTiming(project, scene)));
        }

        var proposedHash = CompositionHasher.ProposedHash(project);
        var roughFresh = manifest.RoughCut is not null
            && string.Equals(manifest.RoughCut.CompositionHash, proposedHash, StringComparison.Ordinal);

        var rerecord = project.Findings
            .Where(finding => VisibleOnRerecordList(project, finding))
            .OrderBy(finding => finding.Start)
            .Select(finding =>
            {
                return new ReviewRerecord(
                    finding.Id,
                    finding.Kind,
                    CompositionResolver.SourceToOutput(project, finding.SourceAssetId, finding.Start),
                    CompositionResolver.SourceToOutput(project, finding.SourceAssetId, finding.End),
                    finding.Start,
                    finding.End,
                    finding.ScriptText,
                    finding.SpokenText ?? finding.TranscriptEvidence,
                    finding.Reason,
                    finding.Severity ?? DriftSeverity.Rerecord,
                    ContextUrl(project, finding.SourceAssetId, finding.Start, finding.End),
                    StatusForFinding(project, finding),
                    ScriptSentenceFor(store.ProjectDirectory, finding),
                    finding.Note,
                    NoteNotUnderstood(project, finding));
            })
            .ToList();

        return new ReviewModel(
            project.Revision,
            project.Name,
            project.Sources[0].DurationSeconds,
            composition.DurationSeconds,
            fullHash,
            CompositionResolver.FinalApprovalValid(project, fullHash),
            blocks.Count == 0,
            blocks,
            roughFresh ? "/media/previews/" + manifest.RoughCut!.Path : null,
            !roughFresh,
            MapQuestions(project),
            scenes,
            rerecord,
            gate.Status());
    }

    public static bool NoteNotUnderstood(ProjectDocument project, SourceFinding finding) =>
        !string.IsNullOrWhiteSpace(finding.Note)
        && StatusForFinding(project, finding) is RerecordStatuses.Pending or RerecordStatuses.Flagged;

    private static string ScriptSentenceFor(string projectDirectory, SourceFinding finding)
    {
        var scriptPath = Path.Combine(projectDirectory, "sources", "script.md");
        var script = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : null;
        try
        {
            return ScriptSentence.ForFinding(script, finding);
        }
        catch (VideoEditException)
        {
            return "";
        }
    }

    private static NarrationRequest? LoadNarrationRequest(string projectDirectory, string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var path = Path.Combine(projectDirectory, "generated", assetId, "asset.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var asset = JsonSerializer.Deserialize<NarrationAsset>(File.ReadAllText(path), ProjectStore.JsonOptions);
        return asset?.Request;
    }

    private static IReadOnlyList<ReviewQuestion> MapQuestions(ProjectDocument project) =>
        project.Questions.Select(question =>
        {
            if (question.Around is not double around)
            {
                return question;
            }

            var scene = project.Scenes.FirstOrDefault(item => item.Id == question.SceneId)
                ?? project.Scenes[0];
            var sourceId = scene.Selections.FirstOrDefault()?.SourceAssetId ?? project.Sources[0].Id;
            return new ReviewQuestion
            {
                Id = question.Id,
                SceneId = question.SceneId,
                Around = CompositionResolver.SourceToOutput(project, sourceId, around),
                Text = question.Text,
            };
        }).ToList();

    public static bool VisibleOnRerecordList(ProjectDocument project, SourceFinding finding)
    {
        if (ScriptDriftKinds.IsCutProposal(finding.Kind))
        {
            return false;
        }

        if (!ScriptDriftKinds.IsDrift(finding.Kind) && !PickupFindingKinds.IsPickup(finding.Kind))
        {
            return false;
        }

        var status = StatusForFinding(project, finding);
        return status is RerecordStatuses.Pending or RerecordStatuses.Flagged;
    }

    public static string StatusForFinding(ProjectDocument project, SourceFinding finding)
    {
        if (ScriptDriftKinds.IsDrift(finding.Kind) && finding.Decision == DecisionStates.Rejected)
        {
            return RerecordStatuses.Dismissed;
        }

        if (ProjectService.HasOpenCut(project, finding)
            || ProjectService.CoveredByOpenCut(project, finding))
        {
            return RerecordStatuses.ResolvedByCut;
        }

        if (ProjectService.CoveredByApprovedCut(project, finding))
        {
            return RerecordStatuses.ResolvedByCut;
        }

        if (finding.Decision == DecisionStates.Flagged)
        {
            return RerecordStatuses.Flagged;
        }

        if (finding.Kind == PickupFindingKinds.Timing)
        {
            if (finding.Decision == DecisionStates.Rejected)
            {
                return RerecordStatuses.Rejected;
            }

            var scene = project.Scenes.FirstOrDefault(item =>
                item.Selections.Any(selection =>
                    string.Equals(selection.AudioSourceAssetId, finding.SourceAssetId, StringComparison.Ordinal)
                    && selection.AudioRange is TimeRange audio
                    && audio.Overlaps(finding.Range)));
            return scene is not null && CompositionResolver.HasUnresolvedTiming(project, scene)
                ? RerecordStatuses.Pending
                : RerecordStatuses.ResolvedByCut;
        }

        if (finding.Kind == PickupFindingKinds.Retake)
        {
            return project.Scenes.Any(scene =>
                scene.Selections.Any(selection =>
                    string.Equals(selection.AudioSourceAssetId, finding.SourceAssetId, StringComparison.Ordinal)
                    && selection.AudioRange is TimeRange audio
                    && audio.Overlaps(finding.Range)))
                ? RerecordStatuses.Pending
                : RerecordStatuses.ResolvedByCut;
        }

        return OverlapsApprovedKeep(project, finding) ? RerecordStatuses.Pending : RerecordStatuses.ResolvedByCut;
    }

    public static bool OverlapsApprovedKeep(ProjectDocument project, SourceFinding finding)
    {
        foreach (var scene in project.Scenes)
        {
            if (scene.IsGenerated)
            {
                continue;
            }

            var resolved = CompositionResolver.ResolveScene(project, scene);
            foreach (var keep in resolved.KeepRanges)
            {
                var sourceId = string.IsNullOrEmpty(keep.SourceAssetId) ? resolved.SourceAssetId : keep.SourceAssetId;
                if (sourceId == finding.SourceAssetId && keep.Range.Overlaps(finding.Range))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? StillUrl(ProjectDocument project, Scene scene)
    {
        if (scene.VisualAssetId is not string id)
        {
            return null;
        }

        var source = project.Sources.FirstOrDefault(item => item.Id == id);
        if (source is null || source.Kind != "image" || string.IsNullOrWhiteSpace(source.Path))
        {
            return null;
        }

        return "/media/sources/" + Path.GetFileName(source.Path);
    }

    private static string ContextUrl(ProjectDocument project, string sourceAssetId, double start, double end)
    {
        var source = project.Sources.FirstOrDefault(item => item.Id == sourceAssetId)
            ?? project.Sources[0];
        var contextStart = Math.Max(0, start - 8);
        var contextEnd = Math.Min(source.DurationSeconds, end + 8);
        var href = Path.GetFileName(source.Path) is "source.mov"
            ? "/media/source"
            : "/media/sources/" + Path.GetFileName(source.Path);
        return $"{href}#t={contextStart:0.###},{contextEnd:0.###}";
    }
}

public sealed record ReviewModel(
    int Revision,
    string Name,
    double SourceDurationSeconds,
    double OutputDurationSeconds,
    string CompositionHash,
    bool FinalApproved,
    bool CanExport,
    IReadOnlyList<string> Blocks,
    string? RoughCutUrl,
    bool RoughCutStale,
    IReadOnlyList<ReviewQuestion> Questions,
    IReadOnlyList<ReviewScene> Scenes,
    IReadOnlyList<ReviewRerecord> RerecordList,
    object Status);

public sealed record ReviewRerecord(
    string Id,
    string Kind,
    double Start,
    double End,
    double SourceStart,
    double SourceEnd,
    string? ScriptText,
    string? SpokenText,
    string Reason,
    string Severity,
    string ContextUrl,
    string Status = RerecordStatuses.Pending,
    string? ScriptSentence = null,
    string? Note = null,
    bool NoteNotUnderstood = false);

public sealed record ReviewScene(
    string Id,
    string Title,
    double DurationSeconds,
    string CompositionHash,
    bool Accepted,
    IReadOnlyList<ReviewEdit> Edits,
    string Kind = "recorded",
    string? ScriptText = null,
    string? VoiceId = null,
    string? ModelId = null,
    float? Stability = null,
    float? SimilarityBoost = null,
    string? PreviewUrl = null,
    bool PreviewStale = false,
    string? StillUrl = null,
    string ReviewDecision = "pending",
    string? Note = null,
    IReadOnlyList<ReviewSelection>? Selections = null,
    bool HasUnresolvedTiming = false);

public sealed record ReviewSelection(
    string SourceAssetId,
    double Start,
    double End,
    string ContextUrl,
    string? AudioSourceAssetId = null,
    double? AudioStart = null,
    double? AudioEnd = null,
    bool NeedsCutaway = false,
    double SourceStart = 0,
    double SourceEnd = 0);

public sealed record ReviewPreview(string? Url, bool Stale)
{
    public static ReviewPreview FromScene(PreviewManifest manifest, string sceneId, string expectedHash)
    {
        var entry = manifest.Scenes.GetValueOrDefault(sceneId);
        var stale = entry is null || !string.Equals(entry.CompositionHash, expectedHash, StringComparison.Ordinal);
        return new ReviewPreview(stale ? null : "/media/previews/" + entry!.Path, stale);
    }
}

public sealed record ReviewEdit(
    string Id,
    string Kind,
    double Start,
    double End,
    double SourceStart,
    double SourceEnd,
    string RemovedText,
    string Reason,
    double Confidence,
    string Decision,
    string? Note,
    int ProposalRevision,
    bool DecisionMatchesProposal,
    bool NoCleanBoundary,
    string? OriginalUrl,
    string? SpliceUrl,
    bool SpliceStale,
    string ContextUrl,
    bool ReproposedFromNote = false,
    bool NoteNotUnderstood = false);

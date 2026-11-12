using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record RenderRange(
    TimeRange Range,
    double FadeInSeconds,
    double FadeOutSeconds,
    bool CrossfadeIn = false,
    bool CrossfadeOut = false,
    string SourceAssetId = "",
    int InputIndex = 0,
    string AudioSourceAssetId = "",
    TimeRange? AudioRange = null,
    int AudioInputIndex = 0,
    double AudioGainDb = 0,
    int RoomInputIndex = -1,
    double RoomGainDb = 0,
    IReadOnlyList<TimeRange>? AudioKeeps = null)
{
    public double Start => Range.Start;

    public double End => Range.End;

    public double Duration => Range.Duration;
}

public sealed record ResolvedScene(
    string SceneId,
    string SourceAssetId,
    IReadOnlyList<RenderRange> KeepRanges,
    double DurationSeconds,
    string? NarrationAssetId = null,
    string? VisualAssetId = null,
    string? NarrationPath = null,
    string? NarrationFingerprint = null)
{
    public bool IsGenerated => !string.IsNullOrEmpty(NarrationAssetId);
}

public sealed record ResolvedComposition(
    IReadOnlyList<ResolvedScene> Scenes,
    double DurationSeconds,
    BoundRenderSettings? Render = null);

public static class CompositionResolver
{
    public const string HostCutRestriction =
        "Cannot extend/trim across host cuts; reject the pickup or clear the cuts first.";

    public static IReadOnlyList<TimeRange> ApprovedRemovals(Scene scene, string sourceAssetId) =>
        Removals(scene, sourceAssetId, approvedOnly: true);

    public static IReadOnlyList<TimeRange> ProposedRemovals(Scene scene, string sourceAssetId) =>
        Removals(scene, sourceAssetId, approvedOnly: false);

    public static IReadOnlyList<TimeRange> Removals(Scene scene, string sourceAssetId, bool approvedOnly) =>
        scene.Edits
            .Where(edit =>
                edit.SourceAssetId == sourceAssetId
                && edit.DecisionMatchesProposal
                && (approvedOnly
                    ? edit.Decision == DecisionStates.Approved
                    : edit.Decision != DecisionStates.Rejected))
            .Select(edit => edit.Range)
            .ToList();

    public static ResolvedScene ResolveScene(ProjectDocument project, Scene scene) =>
        ResolveScene(project, scene, approvedOnly: true);

    public static ResolvedScene ResolveProposedScene(ProjectDocument project, Scene scene) =>
        ResolveScene(project, scene, approvedOnly: false);

    public static ResolvedScene ResolveScene(ProjectDocument project, Scene scene, bool approvedOnly)
    {
        if (scene.IsGenerated)
        {
            var duration = scene.NarrationDurationSeconds
                ?? throw VideoEditException.Validation($"Scene {scene.Id} is missing a measured narration duration.");
            return new ResolvedScene(
                scene.Id,
                scene.VisualAssetId ?? "",
                [],
                duration,
                scene.NarrationAssetId,
                scene.VisualAssetId,
                scene.NarrationPath,
                scene.NarrationFingerprint);
        }

        var rendered = new List<RenderRange>();
        foreach (var selection in scene.Selections)
        {
            var applied = scene.Edits
                .Where(edit =>
                    edit.SourceAssetId == selection.SourceAssetId
                    && edit.DecisionMatchesProposal
                    && (approvedOnly
                        ? edit.Decision == DecisionStates.Approved
                        : edit.Decision != DecisionStates.Rejected)
                    && selection.Range.Overlaps(edit.Range))
                .ToList();
            var audioKeeps = RetainedAudioKeeps(selection, scene, approvedOnly);
            foreach (var range in RangeMath.Subtract(selection.Range, applied.Select(edit => edit.Range)))
            {
                var (audioRange, keeps) = MapAudio(selection, range, audioKeeps);
                rendered.Add(ToRenderRange(range, applied) with
                {
                    SourceAssetId = selection.SourceAssetId,
                    AudioSourceAssetId = selection.AudioSourceAssetId ?? "",
                    AudioRange = audioRange,
                    AudioKeeps = keeps,
                    AudioGainDb = selection.AudioGainDb ?? 0,
                });
            }
        }

        rendered = ApplyPickupSeams(rendered);
        var sourceId = scene.Selections.FirstOrDefault()?.SourceAssetId ?? "";
        return new ResolvedScene(scene.Id, sourceId, rendered, RangeMath.TotalDuration(rendered.Select(item => item.Range)));
    }

    public static IReadOnlyList<TimeRange>? RetainedAudioKeeps(
        SourceSelection selection,
        Scene scene,
        bool approvedOnly)
    {
        if (selection.AudioRange is not TimeRange audio)
        {
            return null;
        }

        var cuts = scene.Edits
            .Where(edit =>
                string.Equals(edit.SourceAssetId, selection.AudioSourceAssetId, StringComparison.Ordinal)
                && edit.DecisionMatchesProposal
                && (approvedOnly
                    ? edit.Decision == DecisionStates.Approved
                    : edit.Decision != DecisionStates.Rejected)
                && audio.Overlaps(edit.Range))
            .Select(edit => edit.Range)
            .ToList();
        var keeps = cuts.Count == 0
            ? new List<TimeRange> { audio }
            : RangeMath.Subtract(audio, cuts).ToList();
        if (keeps.Count == 1
            && Math.Abs(keeps[0].Start - audio.Start) < 0.000001
            && Math.Abs(keeps[0].End - audio.End) < 0.000001)
        {
            return null;
        }

        return keeps;
    }

    public static double EffectivePictureDuration(SourceSelection selection, Scene scene, bool approvedOnly)
    {
        var cuts = scene.Edits
            .Where(edit =>
                edit.SourceAssetId == selection.SourceAssetId
                && edit.DecisionMatchesProposal
                && (approvedOnly
                    ? edit.Decision == DecisionStates.Approved
                    : edit.Decision != DecisionStates.Rejected)
                && selection.Range.Overlaps(edit.Range))
            .Select(edit => edit.Range);
        return RangeMath.TotalDuration(RangeMath.Subtract(selection.Range, cuts));
    }

    public static bool HostCutsAffectPickup(SourceSelection selection, Scene scene, bool approvedOnly)
    {
        if (!selection.HasAudioOverlay)
        {
            return false;
        }

        return Removals(scene, selection.SourceAssetId, approvedOnly)
            .Any(cut => selection.Range.Overlaps(cut));
    }

    public static TimeRange? PictureExtension(
        ProjectDocument project,
        SourceSelection selection,
        Scene scene,
        bool approvedOnly)
    {
        var extra = RetainedAudioDuration(selection, scene, approvedOnly)
            - EffectivePictureDuration(selection, scene, approvedOnly);
        if (extra <= 0.02)
        {
            return null;
        }

        var source = project.Sources.FirstOrDefault(item => item.Id == selection.SourceAssetId);
        var limit = source?.DurationSeconds ?? selection.End + extra;
        var newEnd = Math.Min(limit, selection.End + extra);
        return newEnd > selection.End + 0.02 ? new TimeRange(selection.End, newEnd) : null;
    }

    public static bool HostCutsBlockTimingDecision(
        ProjectDocument project,
        SourceSelection selection,
        Scene scene,
        string decision,
        bool approvedOnly = false)
    {
        if (decision == TimingDecisions.Reject || !selection.HasAudioOverlay)
        {
            return false;
        }

        if (HostCutsAffectPickup(selection, scene, approvedOnly))
        {
            return true;
        }

        if (decision != TimingDecisions.Extend
            || PictureExtension(project, selection, scene, approvedOnly) is not TimeRange expanded)
        {
            return false;
        }

        return Removals(scene, selection.SourceAssetId, approvedOnly)
            .Any(cut => expanded.Overlaps(cut));
    }

    public static void EnsureTimingDecisionAllowed(
        ProjectDocument project,
        SourceSelection selection,
        Scene scene,
        string decision)
    {
        if (HostCutsBlockTimingDecision(project, selection, scene, decision, approvedOnly: false))
        {
            throw VideoEditException.Validation(HostCutRestriction);
        }
    }

    public static double RetainedAudioDuration(SourceSelection selection, Scene scene, bool approvedOnly)
    {
        if (selection.AudioRange is not TimeRange audio)
        {
            return 0;
        }

        var keeps = RetainedAudioKeeps(selection, scene, approvedOnly);
        return keeps is null ? audio.Duration : RangeMath.TotalDuration(keeps);
    }

    public static bool HasUnresolvedTiming(ProjectDocument project, Scene scene, bool approvedOnly = false)
    {
        foreach (var selection in scene.Selections.Where(item => item.HasAudioOverlay))
        {
            if (HostCutsAffectPickup(selection, scene, approvedOnly)
                || (TimingMismatch(selection, scene, approvedOnly)
                    && !TimingRejected(project, selection)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TimingMismatch(SourceSelection selection, Scene scene, bool approvedOnly)
    {
        if (!selection.HasAudioOverlay)
        {
            return false;
        }

        return RetainedAudioDuration(selection, scene, approvedOnly)
            > EffectivePictureDuration(selection, scene, approvedOnly) + 0.02;
    }

    public static bool TimingRejected(ProjectDocument project, SourceSelection selection) =>
        project.Findings.Any(finding =>
            finding.Kind == PickupFindingKinds.Timing
            && finding.Decision == DecisionStates.Rejected
            && string.Equals(finding.SourceAssetId, selection.AudioSourceAssetId, StringComparison.Ordinal)
            && selection.AudioRange is TimeRange audio
            && audio.Overlaps(finding.Range));

    public static IReadOnlyList<string> TimingBlocks(ProjectDocument project, bool approvedOnly)
    {
        var blocks = new List<string>();
        foreach (var scene in project.Scenes.OrderBy(item => item.Position))
        {
            foreach (var selection in scene.Selections.Where(item => item.HasAudioOverlay))
            {
                if (HostCutsAffectPickup(selection, scene, approvedOnly))
                {
                    blocks.Add($"Scene {scene.Id} has pickup audio over host cuts. {HostCutRestriction}");
                    continue;
                }

                if (!TimingMismatch(selection, scene, approvedOnly) || TimingRejected(project, selection))
                {
                    continue;
                }

                var retained = RetainedAudioDuration(selection, scene, approvedOnly);
                var picture = EffectivePictureDuration(selection, scene, approvedOnly);
                blocks.Add(
                    $"Scene {scene.Id} has unresolved pickup timing: retained audio {retained:0.###}s exceeds host picture {picture:0.###}s.");
            }
        }

        return blocks;
    }

    private static (TimeRange? AudioRange, IReadOnlyList<TimeRange>? Keeps) MapAudio(
        SourceSelection selection,
        TimeRange video,
        IReadOnlyList<TimeRange>? audioKeeps)
    {
        if (selection.AudioRange is not TimeRange audio)
        {
            return (null, audioKeeps);
        }

        var origin = selection.MapOrigin ?? selection.Start;
        var outputStart = video.Start - origin;
        if (audioKeeps is { Count: 0 })
        {
            return (new TimeRange(audio.Start + outputStart, audio.Start + outputStart + video.Duration), audioKeeps);
        }

        var donor = audioKeeps is null ? new List<TimeRange> { audio } : audioKeeps.ToList();
        var sliced = RangeMath.SliceOutput(donor, outputStart, video.Duration);
        if (sliced.Count == 0)
        {
            return (new TimeRange(audio.Start + outputStart, audio.Start + outputStart), []);
        }

        var mapped = new TimeRange(sliced[0].Start, sliced[^1].End);
        if (audioKeeps is null && sliced.Count == 1)
        {
            return (mapped, null);
        }

        return (mapped, sliced);
    }

    public static double FadeSeconds(SceneEdit edit, EditingPolicy? policy = null) =>
        edit.FadeSeconds
        ?? (edit.NoCleanBoundary
            ? (policy ?? EditingPolicy.Default).RoomToneFillS
            : AudioTreatment.DefaultFadeSeconds);

    public static TimeRange SpliceWindow(Scene scene, SceneEdit edit, double contextSeconds)
    {
        var selection = scene.Selections.FirstOrDefault(item =>
                item.SourceAssetId == edit.SourceAssetId && item.Range.Contains(edit.Range))
            ?? scene.Selections.First(item =>
                string.Equals(item.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
                && item.AudioRange is TimeRange audio
                && audio.Contains(edit.Range));
        if (string.Equals(selection.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
            && selection.AudioRange is TimeRange)
        {
            return selection.Range;
        }

        return new TimeRange(
            Math.Max(selection.Start, edit.Start - contextSeconds),
            Math.Min(selection.End, edit.End + contextSeconds));
    }

    public static IReadOnlyList<SceneEdit> ApprovedEditsInWindow(Scene scene, SceneEdit edit, TimeRange window) =>
        scene.Edits
            .Where(item =>
                item.Id != edit.Id
                && item.SourceAssetId == edit.SourceAssetId
                && item.Decision == DecisionStates.Approved
                && item.DecisionMatchesProposal
                && item.Range.Overlaps(window))
            .OrderBy(item => item.Start)
            .ToList();

    public static IReadOnlyList<RenderRange> SpliceRanges(Scene scene, SceneEdit edit, double contextSeconds) =>
        ResolveWindow(scene, edit, contextSeconds, applyCurrent: true);

    public static IReadOnlyList<RenderRange> OriginalRanges(Scene scene, SceneEdit edit, double contextSeconds) =>
        ResolveWindow(scene, edit, contextSeconds, applyCurrent: false);

    private static IReadOnlyList<RenderRange> ResolveWindow(
        Scene scene,
        SceneEdit edit,
        double contextSeconds,
        bool applyCurrent)
    {
        var windowed = WindowedScene(scene, edit, contextSeconds, applyCurrent);
        var resolved = ResolveScene(new ProjectDocument { Scenes = [windowed] }, windowed, approvedOnly: true);
        if (resolved.KeepRanges.Count == 0)
        {
            throw VideoEditException.Validation($"Edit {edit.Id} has no splice context.");
        }

        return resolved.KeepRanges;
    }

    private static Scene WindowedScene(Scene scene, SceneEdit edit, double contextSeconds, bool applyCurrent)
    {
        var window = SpliceWindow(scene, edit, contextSeconds);
        var selections = scene.Selections
            .Where(selection => OwnsEdit(selection, edit))
            .Select(selection => ClipSelection(selection, window))
            .OfType<SourceSelection>()
            .ToList();
        if (selections.Count == 0)
        {
            throw VideoEditException.Validation($"Edit {edit.Id} has no splice context.");
        }

        return new Scene
        {
            Id = scene.Id,
            Title = scene.Title,
            Position = scene.Position,
            Selections = selections,
            Edits = scene.Edits.Select(item => CopyEdit(item, edit.Id, applyCurrent)).ToList(),
        };
    }

    private static bool OwnsEdit(SourceSelection selection, SceneEdit edit) =>
        (selection.SourceAssetId == edit.SourceAssetId && selection.Range.Overlaps(edit.Range))
        || (string.Equals(selection.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
            && selection.AudioRange is TimeRange audio
            && audio.Overlaps(edit.Range));

    private static SourceSelection? ClipSelection(SourceSelection selection, TimeRange window)
    {
        if (selection.Range.Intersect(window) is not TimeRange video)
        {
            return null;
        }

        return new SourceSelection
        {
            SourceAssetId = selection.SourceAssetId,
            SourceFingerprint = selection.SourceFingerprint,
            Start = video.Start,
            End = video.End,
            AudioSourceAssetId = selection.AudioSourceAssetId,
            AudioSourceFingerprint = selection.AudioSourceFingerprint,
            AudioStart = selection.AudioStart,
            AudioEnd = selection.AudioEnd,
            AudioGainDb = selection.AudioGainDb,
            NeedsCutaway = selection.NeedsCutaway,
            ReplaceEnd = selection.ReplaceEnd,
            MapOrigin = selection.MapOrigin ?? selection.Start,
        };
    }

    private static SceneEdit CopyEdit(SceneEdit edit, string targetId, bool applyCurrent)
    {
        var target = edit.Id == targetId;
        var approved = target ? applyCurrent : edit.Decision == DecisionStates.Approved && edit.DecisionMatchesProposal;
        return new SceneEdit
        {
            Id = edit.Id,
            SceneId = edit.SceneId,
            SourceAssetId = edit.SourceAssetId,
            SourceFingerprint = edit.SourceFingerprint,
            FindingId = edit.FindingId,
            ProposalRevision = edit.ProposalRevision,
            Start = edit.Start,
            End = edit.End,
            Kind = edit.Kind,
            RemovedText = edit.RemovedText,
            Reason = edit.Reason,
            Confidence = edit.Confidence,
            Decision = approved ? DecisionStates.Approved : DecisionStates.Pending,
            DecisionProposalRevision = approved ? edit.ProposalRevision : null,
            Note = edit.Note,
            NoCleanBoundary = edit.NoCleanBoundary,
            Crossfade = edit.Crossfade,
            FadeSeconds = edit.FadeSeconds,
        };
    }

    private static List<RenderRange> ApplyPickupSeams(List<RenderRange> ranges)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            if (string.IsNullOrEmpty(ranges[i].AudioSourceAssetId))
            {
                continue;
            }

            ranges[i] = WithPickupCrossfade(ranges[i], fadeIn: true, fadeOut: true);
            if (i > 0)
            {
                ranges[i - 1] = WithPickupCrossfade(ranges[i - 1], fadeIn: ranges[i - 1].CrossfadeIn, fadeOut: true);
            }

            if (i + 1 < ranges.Count)
            {
                ranges[i + 1] = WithPickupCrossfade(ranges[i + 1], fadeIn: true, fadeOut: ranges[i + 1].CrossfadeOut);
            }
        }

        return ranges;
    }

    private static RenderRange WithPickupCrossfade(RenderRange range, bool fadeIn, bool fadeOut)
    {
        var seconds = AudioTreatment.PickupCrossfadeSeconds;
        return range with
        {
            FadeInSeconds = fadeIn ? Math.Max(range.FadeInSeconds, seconds) : range.FadeInSeconds,
            FadeOutSeconds = fadeOut ? Math.Max(range.FadeOutSeconds, seconds) : range.FadeOutSeconds,
            CrossfadeIn = range.CrossfadeIn || fadeIn,
            CrossfadeOut = range.CrossfadeOut || fadeOut,
        };
    }

    private static RenderRange ToRenderRange(TimeRange range, IReadOnlyList<SceneEdit> applied)
    {
        var (fadeIn, crossIn) = FadeAt(applied, range.Start, atStart: false);
        var (fadeOut, crossOut) = FadeAt(applied, range.End, atStart: true);
        return new RenderRange(range, fadeIn, fadeOut, crossIn, crossOut);
    }

    private static (double Seconds, bool Crossfade) FadeAt(IReadOnlyList<SceneEdit> edits, double time, bool atStart)
    {
        var match = edits.FirstOrDefault(edit =>
            atStart
                ? Math.Abs(edit.Start - time) < 0.000001
                : Math.Abs(edit.End - time) < 0.000001);
        return match is null ? (0, false) : (FadeSeconds(match), match.Crossfade);
    }

    public static ResolvedComposition Resolve(ProjectDocument project) =>
        Resolve(project, approvedOnly: true);

    public static ResolvedComposition ResolveProposed(ProjectDocument project) =>
        Resolve(project, approvedOnly: false);

    public static double SourceToOutput(
        ProjectDocument project,
        string sourceAssetId,
        double sourceTime,
        bool approvedOnly = false)
    {
        var render = RenderIdentity.Current(project);
        var output = 0.0;
        var scenes = project.Scenes.OrderBy(scene => scene.Position).ToList();
        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var resolved = ResolveScene(project, scene, approvedOnly);
            if (scene.IsGenerated)
            {
                output += resolved.DurationSeconds;
            }
            else
            {
                foreach (var keep in resolved.KeepRanges)
                {
                    var id = string.IsNullOrEmpty(keep.SourceAssetId) ? resolved.SourceAssetId : keep.SourceAssetId;
                    if (id != sourceAssetId)
                    {
                        output += keep.Duration;
                        continue;
                    }

                    if (sourceTime < keep.Start)
                    {
                        return output;
                    }

                    if (sourceTime < keep.End)
                    {
                        return output + (sourceTime - keep.Start);
                    }

                    output += keep.Duration;
                }
            }

            if (index < scenes.Count - 1)
            {
                output += Math.Max(0, render.SceneBoundaryGapS);
            }
        }

        return output;
    }

    public static ResolvedComposition Resolve(ProjectDocument project, bool approvedOnly)
    {
        var scenes = project.Scenes
            .OrderBy(scene => scene.Position)
            .Select(scene => ResolveScene(project, scene, approvedOnly))
            .ToList();
        var render = RenderIdentity.Current(project);
        var duration = scenes.Sum(scene => scene.DurationSeconds)
            + Math.Max(0, scenes.Count - 1) * render.SceneBoundaryGapS;
        return new ResolvedComposition(scenes, duration, render);
    }

    public static bool SceneAcceptanceValid(ProjectDocument project, Scene scene, string sceneHash)
    {
        if (HasBlockingEdits(scene) || HasUnresolvedTiming(project, scene, approvedOnly: true))
        {
            return false;
        }

        return scene.Acceptance is not null
            && scene.Acceptance.ReviewDecision == DecisionStates.Approved
            && string.Equals(scene.Acceptance.CompositionHash, sceneHash, StringComparison.Ordinal);
    }

    public static bool FinalApprovalValid(ProjectDocument project, string fullHash)
    {
        if (project.Scenes.Any(HasBlockingEdits)
            || project.Scenes.Any(scene => scene.Acceptance is null)
            || project.Scenes.Any(scene => HasUnresolvedTiming(project, scene, approvedOnly: true)))
        {
            return false;
        }

        foreach (var scene in project.Scenes)
        {
            var sceneHash = CompositionHasher.SceneHash(project, scene);
            if (!SceneAcceptanceValid(project, scene, sceneHash))
            {
                return false;
            }
        }

        return project.FinalApproval is not null
            && string.Equals(project.FinalApproval.CompositionHash, fullHash, StringComparison.Ordinal);
    }

    public static bool HasBlockingEdits(Scene scene) =>
        scene.Edits.Any(edit =>
            edit.Decision is DecisionStates.Pending or DecisionStates.Flagged
            || !edit.DecisionMatchesProposal);

    public static IReadOnlyList<string> ExportBlocks(ProjectDocument project, bool requireFinalApproval = true)
    {
        var blocks = new List<string>();
        foreach (var scene in project.Scenes.OrderBy(item => item.Position))
        {
            foreach (var edit in scene.Edits)
            {
                if (!edit.DecisionMatchesProposal)
                {
                    blocks.Add($"Edit {edit.Id} decision does not match its current proposal.");
                }
                else if (edit.Decision is DecisionStates.Pending or DecisionStates.Flagged)
                {
                    blocks.Add($"Edit {edit.Id} is {edit.Decision}.");
                }
            }

            var sceneHash = CompositionHasher.SceneHash(project, scene);
            if (SceneAcceptanceValid(project, scene, sceneHash))
            {
                continue;
            }

            if (scene.Acceptance is not null
                && string.Equals(scene.Acceptance.CompositionHash, sceneHash, StringComparison.Ordinal)
                && scene.Acceptance.ReviewDecision is DecisionStates.Rejected or DecisionStates.Flagged)
            {
                blocks.Add($"Scene {scene.Id} is {scene.Acceptance.ReviewDecision}.");
            }
            else
            {
                blocks.Add($"Scene {scene.Id} is not accepted for the current composition.");
            }
        }

        blocks.AddRange(TimingBlocks(project, approvedOnly: true));

        if (requireFinalApproval)
        {
            var fullHash = CompositionHasher.FullHash(project);
            if (!FinalApprovalValid(project, fullHash))
            {
                blocks.Add("Final approval is missing or stale.");
            }
        }

        return blocks;
    }
}

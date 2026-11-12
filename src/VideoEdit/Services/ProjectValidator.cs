using VideoEdit.Models;

namespace VideoEdit.Services;

public static class ProjectValidator
{
    public static void Validate(ProjectDocument project)
    {
        var errors = Collect(project);
        if (errors.Count > 0)
        {
            throw VideoEditException.Validation(string.Join(" ", errors));
        }
    }

    public static List<string> Collect(ProjectDocument project)
    {
        var errors = new List<string>();
        var sources = project.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var findingIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var finding in project.Findings)
        {
            if (!findingIds.Add(finding.Id))
            {
                errors.Add($"Duplicate finding id {finding.Id}.");
            }

            ValidateBound("Finding", finding.Id, finding.Range, finding.SourceAssetId, finding.SourceFingerprint, sources, errors);
            if (!EditKinds.All.Contains(finding.Kind)
                && !ScriptDriftKinds.IsDrift(finding.Kind)
                && !PickupFindingKinds.IsPickup(finding.Kind))
            {
                errors.Add($"Finding {finding.Id} has unknown kind '{finding.Kind}'.");
            }
        }

        ValidateOutput(project.Output, errors);

        var sceneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in project.Scenes.OrderBy(item => item.Position))
        {
            if (!sceneIds.Add(scene.Id))
            {
                errors.Add($"Duplicate scene id {scene.Id}.");
            }

            ValidateSelections(scene, sources, errors);
            ValidateEdits(scene, sources, errors);
            ValidateGenerated(scene, sources, errors);
        }

        return errors;
    }

    public static readonly HashSet<string> SupportedVideoCodecs = ["libx264"];

    public static readonly HashSet<string> SupportedAudioCodecs = ["aac"];

    public static readonly HashSet<int> SupportedFrameRates = [24, 25, 30, 50, 60];

    private static void ValidateOutput(OutputSettings output, List<string> errors)
    {
        if (output.Width < 16 || output.Height < 16 || output.Width > 7680 || output.Height > 4320
            || output.Width % 2 != 0 || output.Height % 2 != 0)
        {
            errors.Add($"Output size {output.Width}x{output.Height} is not supported.");
        }

        if (!SupportedFrameRates.Contains(output.FrameRate))
        {
            errors.Add($"Output frame rate {output.FrameRate} is not supported.");
        }

        if (!SupportedVideoCodecs.Contains(output.VideoCodec))
        {
            errors.Add($"Output video codec '{output.VideoCodec}' is not supported.");
        }

        if (!SupportedAudioCodecs.Contains(output.AudioCodec))
        {
            errors.Add($"Output audio codec '{output.AudioCodec}' is not supported.");
        }
    }

    private static void ValidateSelections(
        Scene scene,
        IReadOnlyDictionary<string, SourceAsset> sources,
        List<string> errors)
    {
        var bySource = scene.Selections.GroupBy(selection => selection.SourceAssetId, StringComparer.Ordinal);
        foreach (var group in bySource)
        {
            var ranges = new List<TimeRange>();
            foreach (var selection in group)
            {
                ValidateBound(
                    "Selection",
                    $"{scene.Id}/{selection.SourceAssetId}",
                    selection.Range,
                    selection.SourceAssetId,
                    selection.SourceFingerprint,
                    sources,
                    errors);
                if (selection.HasAudioOverlay)
                {
                    if (selection.AudioRange is not TimeRange audio)
                    {
                        errors.Add($"Scene {scene.Id} audio overlay on {selection.SourceAssetId} is missing a range.");
                    }
                    else
                    {
                        ValidateBound(
                            "Audio overlay",
                            $"{scene.Id}/{selection.AudioSourceAssetId}",
                            audio,
                            selection.AudioSourceAssetId ?? "",
                            selection.AudioSourceFingerprint ?? "",
                            sources,
                            errors);
                    }
                }

                ranges.Add(selection.Range);
            }

            if (RangeMath.RangesOverlap(ranges))
            {
                errors.Add(
                    $"Scene {scene.Id} repeats source {group.Key} material; overlapping or duplicated selections are not allowed.");
            }
        }
    }

    private static void ValidateEdits(
        Scene scene,
        IReadOnlyDictionary<string, SourceAsset> sources,
        List<string> errors)
    {
        foreach (var edit in scene.Edits)
        {
            if (edit.SceneId != scene.Id)
            {
                errors.Add($"Edit {edit.Id} scene id {edit.SceneId} does not match {scene.Id}.");
            }

            ValidateBound("Edit", edit.Id, edit.Range, edit.SourceAssetId, edit.SourceFingerprint, sources, errors);
            if (!EditKinds.All.Contains(edit.Kind))
            {
                errors.Add($"Edit {edit.Id} has unknown kind '{edit.Kind}'.");
            }

            if (!DecisionStates.All.Contains(edit.Decision))
            {
                errors.Add($"Edit {edit.Id} has unknown decision '{edit.Decision}'.");
            }

            var containing = scene.Selections.FirstOrDefault(selection =>
                (selection.SourceAssetId == edit.SourceAssetId && selection.Range.Contains(edit.Range))
                || (string.Equals(selection.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
                    && selection.AudioRange is TimeRange audio
                    && audio.Contains(edit.Range)));
            if (containing is null)
            {
                errors.Add($"Edit {edit.Id} is not wholly inside a selected range of scene {scene.Id}.");
            }
        }

        foreach (var group in scene.Edits.GroupBy(edit => edit.SourceAssetId, StringComparer.Ordinal))
        {
            if (RangeMath.RangesOverlap(group.Select(edit => edit.Range).ToList()))
            {
                errors.Add($"Scene {scene.Id} has overlapping removals on {group.Key}.");
            }
        }
    }

    private static void ValidateBound(
        string label,
        string id,
        TimeRange range,
        string sourceAssetId,
        string fingerprint,
        IReadOnlyDictionary<string, SourceAsset> sources,
        List<string> errors)
    {
        if (!range.IsValid)
        {
            errors.Add($"{label} {id} has an invalid range {range}.");
            return;
        }

        if (!sources.TryGetValue(sourceAssetId, out var source))
        {
            errors.Add($"{label} {id} references missing source {sourceAssetId}.");
            return;
        }

        if (!string.Equals(source.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            errors.Add($"{label} {id} fingerprint does not match source {sourceAssetId}.");
        }

        if (source.Kind == "image")
        {
            return;
        }

        if (range.Start < 0 || range.End > source.DurationSeconds + 0.000001)
        {
            errors.Add($"{label} {id} {range} is outside source duration {source.DurationSeconds}.");
        }
    }

    private static void ValidateGenerated(
        Scene scene,
        IReadOnlyDictionary<string, SourceAsset> sources,
        List<string> errors)
    {
        if (!scene.IsGenerated)
        {
            if (scene.Selections.Count == 0)
            {
                errors.Add($"Scene {scene.Id} has no selected material.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(scene.VisualAssetId) || !sources.ContainsKey(scene.VisualAssetId))
        {
            errors.Add($"Scene {scene.Id} needs a still image source.");
        }

        if (scene.NarrationDurationSeconds is not > 0)
        {
            errors.Add($"Scene {scene.Id} needs a measured narration duration.");
        }

        if (string.IsNullOrWhiteSpace(scene.NarrationPath) || string.IsNullOrWhiteSpace(scene.NarrationFingerprint))
        {
            errors.Add($"Scene {scene.Id} needs a bound narration derivative and fingerprint.");
        }
    }
}

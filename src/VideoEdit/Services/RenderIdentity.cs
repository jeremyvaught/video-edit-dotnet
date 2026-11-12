using VideoEdit.Models;

namespace VideoEdit.Services;

public static class RenderIdentity
{
    public static BoundRenderSettings Current(ProjectDocument project) =>
        project.LiveRender ?? project.BoundRender ?? new BoundRenderSettings();

    public static BoundRenderSettings FromLive(ProjectDocument project, string projectDirectory)
    {
        var policy = EditingPolicy.LoadOrDefault(Path.Combine(projectDirectory, "edit", EditingPolicy.FileName));
        var bound = project.BoundRender ?? new BoundRenderSettings();
        string? fingerprint = bound.RoomFingerprint;
        string? path = project.Narration.RoomTone?.Path ?? bound.RoomPath;
        var usesRoom = UsesRoomBed(project, policy.SceneBoundaryGapS);
        if (usesRoom && !string.IsNullOrWhiteSpace(path))
        {
            var resolved = MediaRenderer.ResolveProjectPath(projectDirectory, path);
            fingerprint = File.Exists(resolved) ? MediaRenderer.FingerprintFile(resolved) : null;
        }
        else if (!usesRoom)
        {
            path = null;
            fingerprint = null;
        }

        return new BoundRenderSettings
        {
            SceneBoundaryGapS = policy.SceneBoundaryGapS,
            RoomToneFillMs = policy.RoomToneFillMs,
            RoomPath = path,
            RoomFingerprint = fingerprint,
            RoomGainDb = bound.RoomGainDb,
        };
    }

    public static bool HasAudioOverlay(ProjectDocument project) =>
        project.Scenes.Any(scene => scene.Selections.Any(selection => selection.HasAudioOverlay));

    public static bool UsesRoomBed(ProjectDocument project, double? sceneBoundaryGapS = null)
    {
        if (HasAudioOverlay(project))
        {
            return true;
        }

        var gap = sceneBoundaryGapS
            ?? project.LiveRender?.SceneBoundaryGapS
            ?? (project.BoundRender.SceneBoundaryGapS > 0
                ? project.BoundRender.SceneBoundaryGapS
                : EditingPolicy.Default.SceneBoundaryGapS);
        return project.Scenes.Count >= 2
            && gap > 0
            && !string.IsNullOrWhiteSpace(project.Narration.RoomTone?.Path);
    }

    public static void Append(System.Text.StringBuilder builder, BoundRenderSettings render)
    {
        builder.Append("|gap|");
        builder.Append(render.SceneBoundaryGapS.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("|fill|");
        builder.Append(render.RoomToneFillMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("|room|");
        builder.Append(render.RoomFingerprint ?? "");
        builder.Append("|rgain|");
        builder.Append((render.RoomGainDb ?? 0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }
}

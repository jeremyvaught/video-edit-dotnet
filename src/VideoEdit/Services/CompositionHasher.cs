using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VideoEdit.Models;

namespace VideoEdit.Services;

public static class CompositionHasher
{
    public static string SceneHash(ProjectDocument project, Scene scene) =>
        SceneHash(project, scene, approvedOnly: true);

    public static string ProposedHash(ProjectDocument project)
    {
        var builder = new StringBuilder();
        builder.Append("proposed");
        foreach (var scene in project.Scenes.OrderBy(item => item.Position))
        {
            builder.Append('|');
            builder.Append(SceneHash(project, scene, approvedOnly: false));
        }

        AppendOutput(builder, project.Output);
        RenderIdentity.Append(builder, RenderIdentity.Current(project));
        return Sha256(builder.ToString());
    }

    public static string SceneHash(ProjectDocument project, Scene scene, bool approvedOnly)
    {
        var resolved = CompositionResolver.ResolveScene(project, scene, approvedOnly);
        var builder = new StringBuilder();
        builder.Append("scene|");
        builder.Append(scene.Id);
        builder.Append('|');
        builder.Append(scene.Position.ToString(CultureInfo.InvariantCulture));
        foreach (var selection in scene.Selections)
        {
            builder.Append("|sel|");
            builder.Append(selection.SourceAssetId);
            builder.Append('|');
            builder.Append(Fingerprint(project, selection.SourceAssetId));
            builder.Append('|');
            AppendRange(builder, selection.Range);
            if (selection.HasAudioOverlay && selection.AudioRange is TimeRange audio)
            {
                builder.Append("|audio|");
                builder.Append(selection.AudioSourceAssetId);
                builder.Append('|');
                builder.Append(Fingerprint(project, selection.AudioSourceAssetId ?? ""));
                builder.Append('|');
                AppendRange(builder, audio);
                builder.Append('|');
                builder.Append((selection.AudioGainDb ?? 0).ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append('|');
                builder.Append(selection.NeedsCutaway ? '1' : '0');
            }
        }

        var mixedSources = scene.Selections.Select(item => item.SourceAssetId).Distinct(StringComparer.Ordinal).Count() > 1
            || scene.Selections.Any(item => item.HasAudioOverlay);
        foreach (var keep in resolved.KeepRanges)
        {
            builder.Append("|keep|");
            if (mixedSources)
            {
                builder.Append(keep.SourceAssetId);
                builder.Append('|');
                builder.Append(Fingerprint(project, keep.SourceAssetId));
                builder.Append('|');
            }

            if (!string.IsNullOrEmpty(keep.AudioSourceAssetId) && keep.AudioRange is TimeRange keepAudio)
            {
                builder.Append("|audio|");
                builder.Append(keep.AudioSourceAssetId);
                builder.Append('|');
                builder.Append(Fingerprint(project, keep.AudioSourceAssetId));
                builder.Append('|');
                AppendRange(builder, keepAudio);
            }

            builder.Append("|keeps|");
            if (keep.AudioKeeps is null)
            {
                builder.Append("null");
            }
            else if (keep.AudioKeeps.Count == 0)
            {
                builder.Append("empty");
            }
            else
            {
                foreach (var audioKeep in keep.AudioKeeps)
                {
                    builder.Append('[');
                    AppendRange(builder, audioKeep);
                    builder.Append(']');
                }
            }

            AppendRange(builder, keep.Range);
            builder.Append('|');
            builder.Append(keep.FadeInSeconds.ToString("0.######", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(keep.FadeOutSeconds.ToString("0.######", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(keep.CrossfadeIn ? '1' : '0');
            builder.Append('|');
            builder.Append(keep.CrossfadeOut ? '1' : '0');
        }

        if (resolved.IsGenerated)
        {
            builder.Append("|narration|");
            builder.Append(resolved.NarrationAssetId);
            builder.Append('|');
            builder.Append(resolved.NarrationPath);
            builder.Append('|');
            builder.Append(resolved.NarrationFingerprint);
            builder.Append('|');
            builder.Append(resolved.DurationSeconds.ToString("0.######", CultureInfo.InvariantCulture));
            builder.Append("|visual|");
            builder.Append(resolved.VisualAssetId);
            builder.Append('|');
            builder.Append(Fingerprint(project, resolved.VisualAssetId ?? ""));
        }

        AppendOutput(builder, project.Output);
        RenderIdentity.Append(builder, RenderIdentity.Current(project));
        return Sha256(builder.ToString());
    }

    public static string FullHash(ProjectDocument project)
    {
        var builder = new StringBuilder();
        builder.Append("full");
        foreach (var scene in project.Scenes.OrderBy(item => item.Position))
        {
            builder.Append('|');
            builder.Append(SceneHash(project, scene));
        }

        AppendOutput(builder, project.Output);
        RenderIdentity.Append(builder, RenderIdentity.Current(project));
        return Sha256(builder.ToString());
    }

    public static string ProposalFingerprint(SceneEdit edit)
    {
        var builder = new StringBuilder();
        builder.Append(edit.SourceAssetId);
        builder.Append('|');
        AppendRange(builder, edit.Range);
        builder.Append('|');
        builder.Append(edit.Kind);
        builder.Append('|');
        builder.Append(edit.Reason);
        builder.Append('|');
        builder.Append(edit.NoCleanBoundary ? '1' : '0');
        builder.Append('|');
        builder.Append(edit.Crossfade ? '1' : '0');
        builder.Append('|');
        builder.Append((edit.FadeSeconds ?? -1).ToString("0.######", CultureInfo.InvariantCulture));
        return Sha256(builder.ToString());
    }

    public static string SpliceFingerprint(Scene scene, SceneEdit edit, double contextSeconds)
    {
        var builder = new StringBuilder();
        builder.Append("splice|");
        builder.Append(contextSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        builder.Append('|');
        builder.Append(ProposalFingerprint(edit));
        foreach (var range in CompositionResolver.SpliceRanges(scene, edit, contextSeconds))
        {
            builder.Append("|keep|");
            builder.Append(range.SourceAssetId);
            builder.Append('|');
            AppendRange(builder, range.Range);
            if (!string.IsNullOrEmpty(range.AudioSourceAssetId) && range.AudioRange is TimeRange audio)
            {
                builder.Append("|audio|");
                builder.Append(range.AudioSourceAssetId);
                builder.Append('|');
                AppendRange(builder, audio);
            }

            builder.Append("|keeps|");
            if (range.AudioKeeps is null)
            {
                builder.Append("null");
            }
            else if (range.AudioKeeps.Count == 0)
            {
                builder.Append("empty");
            }
            else
            {
                foreach (var keep in range.AudioKeeps)
                {
                    builder.Append("|ak|");
                    AppendRange(builder, keep);
                }
            }
        }

        return Sha256(builder.ToString());
    }

    private static string Fingerprint(ProjectDocument project, string sourceAssetId)
    {
        var source = project.Sources.FirstOrDefault(item => item.Id == sourceAssetId);
        return source?.Fingerprint ?? "";
    }

    private static void AppendOutput(StringBuilder builder, OutputSettings output)
    {
        builder.Append("|out|");
        builder.Append(output.Width.ToString(CultureInfo.InvariantCulture));
        builder.Append('x');
        builder.Append(output.Height.ToString(CultureInfo.InvariantCulture));
        builder.Append('@');
        builder.Append(output.FrameRate.ToString(CultureInfo.InvariantCulture));
        builder.Append('|');
        builder.Append(output.VideoCodec);
        builder.Append('|');
        builder.Append(output.AudioCodec);
    }

    private static void AppendRange(StringBuilder builder, TimeRange range)
    {
        builder.Append(range.Start.ToString("0.######", CultureInfo.InvariantCulture));
        builder.Append('-');
        builder.Append(range.End.ToString("0.######", CultureInfo.InvariantCulture));
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

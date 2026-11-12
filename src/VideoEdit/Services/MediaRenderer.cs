using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class MediaRenderer
{
    private readonly ProjectStore _store;
    private readonly MediaGate _gate;
    private readonly IProcessRunner _processRunner;
    private readonly VideoEditOptions _options;

    public MediaRenderer(
        ProjectStore store,
        MediaGate gate,
        IProcessRunner processRunner,
        IOptions<VideoEditOptions> options)
    {
        _store = store;
        _gate = gate;
        _processRunner = processRunner;
        _options = options.Value;
    }

    public string PreviewsDirectory => Path.Combine(_store.ProjectDirectory, "edit", "previews");

    public string ExportsDirectory => Path.Combine(_store.ProjectDirectory, "edit", "exports");

    public string RevisionsDirectory => Path.Combine(_store.ProjectDirectory, "edit", "revisions");

    public async Task<PreviewManifest> BuildSplicesAsync(CancellationToken cancellationToken)
    {
        return await _gate.RunAsync("splices", false, async token =>
        {
            var project = _store.Load();
            ProjectValidator.Validate(project);
            EnsureAssets(project);
            var manifest = LoadManifest();
            Directory.CreateDirectory(PreviewsDirectory);
            var roomGain = project.BoundRender.RoomGainDb
                ?? (RenderIdentity.HasAudioOverlay(project)
                    ? await MeasureRoomBedGainAsync(project, token)
                    : null);

            foreach (var scene in project.Scenes.OrderBy(item => item.Position))
            {
                if (scene.IsGenerated || scene.Selections.Count == 0)
                {
                    continue;
                }

                foreach (var edit in scene.Edits)
                {
                    if (scene.Selections.All(selection =>
                        selection.SourceAssetId != edit.SourceAssetId
                        && !string.Equals(selection.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var ranges = SpliceRanges(scene, edit, _options.SpliceContextSeconds);
                    var original = OriginalRanges(scene, edit, _options.SpliceContextSeconds);
                    var fileName = $"splice-{edit.Id}-r{edit.ProposalRevision}.mp4";
                    var originalName = $"original-{edit.Id}-r{edit.ProposalRevision}.mp4";
                    await RenderResolvedWindowAsync(
                        project,
                        ranges,
                        Path.Combine(PreviewsDirectory, fileName),
                        roomGain,
                        token);
                    await RenderResolvedWindowAsync(
                        project,
                        original,
                        Path.Combine(PreviewsDirectory, originalName),
                        roomGain,
                        token);
                    manifest.Splices[edit.Id] = new PreviewEntry
                    {
                        Path = fileName,
                        OriginalPath = originalName,
                        CompositionHash = SpliceHash(scene, edit),
                        ProposalRevision = edit.ProposalRevision,
                    };
                }
            }

            SaveManifest(manifest);
            return manifest;
        }, cancellationToken);
    }

    public async Task<PreviewManifest> BuildRoughCutAsync(CancellationToken cancellationToken)
    {
        return await _gate.RunAsync("rough-cut", false, async token =>
        {
            var project = _store.Load();
            ProjectValidator.Validate(project);
            EnsureAssets(project);
            project = await FreezePreviewRenderAsync(project, token);
            var composition = CompositionResolver.ResolveProposed(project);
            var hash = CompositionHasher.ProposedHash(project);
            var fileName = "rough-cut.mp4";
            var path = Path.Combine(PreviewsDirectory, fileName);
            Directory.CreateDirectory(PreviewsDirectory);
            var sceneFiles = await RenderCompositionAsync(
                project,
                composition,
                PreviewsDirectory,
                preview: true,
                TimeSpan.FromSeconds(_options.PreviewTimeoutSeconds),
                token);
            await ConcatScenesAsync(
                sceneFiles,
                path,
                project,
                preview: true,
                TimeSpan.FromSeconds(_options.PreviewTimeoutSeconds),
                token);
            var manifest = LoadManifest();
            manifest.RoughCut = new PreviewEntry
            {
                Path = fileName,
                CompositionHash = hash,
            };
            foreach (var scene in composition.Scenes)
            {
                manifest.Scenes[scene.SceneId] = new PreviewEntry
                {
                    Path = $"scene-{scene.SceneId}.mp4",
                    CompositionHash = CompositionHasher.SceneHash(project, project.Scenes.First(item => item.Id == scene.SceneId), approvedOnly: false),
                };
            }

            SaveManifest(manifest);
            return manifest;
        }, cancellationToken);
    }

    public async Task<ExportResult> ExportAsync(CancellationToken cancellationToken)
    {
        var begun = false;
        try
        {
            var project = _store.WithWriteLock(loaded =>
            {
                _gate.Begin("export", true);
                begun = true;
                return loaded;
            });

            ProjectValidator.Validate(project);
            EnsureAssets(project);
            var blocks = CompositionResolver.ExportBlocks(project);
            if (blocks.Count > 0)
            {
                throw VideoEditException.ExportBlocked(string.Join(" ", blocks));
            }

            var hash = CompositionHasher.FullHash(project);
            Directory.CreateDirectory(RevisionsDirectory);
            Directory.CreateDirectory(ExportsDirectory);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var snapshotName = $"approved-{stamp}.json";
            var snapshotPath = Path.Combine(RevisionsDirectory, snapshotName);
            File.WriteAllText(
                snapshotPath,
                JsonSerializer.Serialize(project, ProjectStore.JsonOptions));

            var exportName = $"export-{stamp}.mp4";
            var exportPath = Path.Combine(ExportsDirectory, exportName);
            var composition = CompositionResolver.Resolve(project);
            var exportParts = Path.Combine(ExportsDirectory, $".parts-{stamp}");
            Directory.CreateDirectory(exportParts);
            var sceneFiles = await RenderCompositionAsync(
                project,
                composition,
                exportParts,
                preview: false,
                TimeSpan.FromSeconds(_options.ExportTimeoutSeconds),
                cancellationToken);
            await ConcatScenesAsync(
                sceneFiles,
                exportPath,
                project,
                preview: false,
                TimeSpan.FromSeconds(_options.ExportTimeoutSeconds),
                cancellationToken);
            TryDeleteDirectory(exportParts);

            var sidecar = Path.Combine(ExportsDirectory, $"export-{stamp}.json");
            File.WriteAllText(
                sidecar,
                JsonSerializer.Serialize(
                    new
                    {
                        compositionHash = hash,
                        projectRevision = project.Revision,
                        snapshot = snapshotName,
                        file = exportName,
                    },
                    ProjectStore.JsonOptions));

            return new ExportResult(exportName, snapshotName, hash);
        }
        finally
        {
            if (begun)
            {
                _gate.End();
            }
        }
    }

    private async Task RenderResolvedWindowAsync(
        ProjectDocument project,
        IReadOnlyList<RenderRange> keep,
        string destinationPath,
        double? roomGain,
        CancellationToken cancellationToken)
    {
        var sourceId = keep.FirstOrDefault()?.SourceAssetId ?? "";
        var resolved = new ResolvedScene(
            "splice",
            sourceId,
            keep,
            RangeMath.TotalDuration(keep.Select(item => item.Range)));
        var inputs = BindInputs(project, resolved);
        await RenderRangesAsync(
            inputs.Paths,
            ApplyRoomBedGain(inputs.Ranges, roomGain),
            destinationPath,
            preview: true,
            TimeSpan.FromSeconds(_options.PreviewTimeoutSeconds),
            cancellationToken,
            project.Output);
    }

    public Task RenderRangesAsync(
        string sourcePath,
        IReadOnlyList<TimeRange> ranges,
        string destinationPath,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        OutputSettings? output = null) =>
        RenderRangesAsync(
            sourcePath,
            ranges.Select(range => new RenderRange(range, AudioTreatment.DefaultFadeSeconds, AudioTreatment.DefaultFadeSeconds)).ToList(),
            destinationPath,
            preview,
            timeout,
            cancellationToken,
            output);

    public Task RenderRangesAsync(
        string sourcePath,
        IReadOnlyList<RenderRange> ranges,
        string destinationPath,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        OutputSettings? output = null) =>
        RenderRangesAsync([sourcePath], ranges, destinationPath, preview, timeout, cancellationToken, output);

    public async Task RenderRangesAsync(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<RenderRange> ranges,
        string destinationPath,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        OutputSettings? output = null)
    {
        if (ranges.Count == 0)
        {
            throw VideoEditException.Validation("There is nothing to render.");
        }

        if (sourcePaths.Count == 0)
        {
            throw VideoEditException.Validation("There is no source to render.");
        }

        output ??= new OutputSettings();
        ProjectValidator.Validate(new ProjectDocument { Output = output, Sources = [], Scenes = [] });

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".tmp.mp4";
        DeleteIfExists(temp);
        DeleteIfExists(destinationPath + ".incomplete");

        var filter = BuildFilterGraph(ranges, output);
        var arguments = new List<string> { "-hide_banner", "-y" };
        foreach (var path in sourcePaths)
        {
            arguments.Add("-i");
            arguments.Add(path);
        }

        arguments.Add("-filter_complex");
        arguments.Add(filter);
        arguments.AddRange(
        [
            "-map", "[v]", "-map", "[a]",
            "-c:v", output.VideoCodec,
            "-preset", preview ? "veryfast" : "medium",
            "-crf", preview ? "28" : "18",
            "-pix_fmt", "yuv420p",
            "-r", output.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", output.AudioCodec,
            "-b:a", preview ? "128k" : "192k",
            "-ar", OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", OutputChannels.ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            temp,
        ]);

        try
        {
            var result = await _processRunner.RunAsync(_options.FfmpegPath, arguments, timeout, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                MarkIncomplete(destinationPath, temp);
                throw VideoEditException.Conflict(
                    $"ffmpeg failed ({result.ExitCode}). {Trim(result.StandardError)}");
            }

            File.Move(temp, destinationPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            MarkIncomplete(destinationPath, temp);
            throw;
        }
        catch
        {
            if (File.Exists(temp))
            {
                MarkIncomplete(destinationPath, temp);
            }

            throw;
        }
    }

    public PreviewManifest LoadManifest()
    {
        var path = Path.Combine(PreviewsDirectory, "manifest.json");
        if (!File.Exists(path))
        {
            return new PreviewManifest();
        }

        return JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText(path), ProjectStore.JsonOptions)
            ?? new PreviewManifest();
    }

    private async Task<ProjectDocument> FreezePreviewRenderAsync(
        ProjectDocument project,
        CancellationToken cancellationToken)
    {
        var live = RenderIdentity.FromLive(project, _store.ProjectDirectory);
        if (RenderIdentity.HasAudioOverlay(project))
        {
            live.RoomGainDb = await MeasureRoomBedGainAsync(project, cancellationToken);
        }

        return _store.SaveBoundRender(live);
    }

    public void EnsureAssets(ProjectDocument project)
    {
        foreach (var source in project.Sources)
        {
            if (!File.Exists(source.Path))
            {
                throw VideoEditException.Validation($"Source file is missing: {source.Path}");
            }

            var current = FingerprintFile(source.Path);
            if (!string.Equals(current, source.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw VideoEditException.Validation($"Source {source.Id} changed; fingerprint no longer matches.");
            }
        }

        foreach (var scene in project.Scenes.Where(item => item.IsGenerated))
        {
            if (string.IsNullOrWhiteSpace(scene.NarrationPath))
            {
                throw VideoEditException.Validation($"Scene {scene.Id} has no bound narration derivative.");
            }

            var audio = ResolveProjectPath(_store.ProjectDirectory, scene.NarrationPath);
            if (!File.Exists(audio))
            {
                throw VideoEditException.Validation($"Bound narration is missing: {scene.NarrationPath}");
            }

            var current = FingerprintFile(audio);
            if (!string.Equals(current, scene.NarrationFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw VideoEditException.Validation($"Scene {scene.Id} narration changed; fingerprint no longer matches.");
            }
        }
    }

    public static string FingerprintFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<double> ProbeDurationAsync(string path, CancellationToken token)
    {
        var result = await _processRunner.RunAsync(
            _options.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "json", path],
            TimeSpan.FromSeconds(30),
            token);
        if (result.ExitCode != 0)
        {
            throw VideoEditException.Validation($"Could not measure duration for {path}.");
        }

        using var metadata = JsonDocument.Parse(result.StandardOutput);
        var duration = double.Parse(
            metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw VideoEditException.Validation($"Source {path} has no usable duration.");
        }

        return duration;
    }

    public string SpliceHash(Scene scene, SceneEdit edit) =>
        CompositionHasher.SpliceFingerprint(scene, edit, _options.SpliceContextSeconds);

    public static IReadOnlyList<RenderRange> SpliceRanges(Scene scene, SceneEdit edit, double contextSeconds) =>
        CompositionResolver.SpliceRanges(scene, edit, contextSeconds);

    public static IReadOnlyList<RenderRange> OriginalRanges(Scene scene, SceneEdit edit, double contextSeconds) =>
        CompositionResolver.OriginalRanges(scene, edit, contextSeconds);

    public static TimeRange OriginalRange(Scene scene, SceneEdit edit, double contextSeconds) =>
        CompositionResolver.SpliceWindow(scene, edit, contextSeconds);

    public static string BuildFilterGraph(IReadOnlyList<RenderRange> ranges, OutputSettings output)
    {
        var parts = new List<string>();
        var width = output.Width;
        var height = output.Height;
        var fps = output.FrameRate.ToString(CultureInfo.InvariantCulture);
        var crossJoin = new bool[ranges.Count];
        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            var start = N(range.Start);
            var end = N(range.End);
            var duration = range.Duration;
            var joinLeft = i > 0 && ranges[i - 1].CrossfadeOut && range.CrossfadeIn;
            var joinRight = i < ranges.Count - 1 && range.CrossfadeOut && ranges[i + 1].CrossfadeIn;
            crossJoin[i] = joinLeft;
            var fadeIn = joinLeft ? 0 : Math.Min(range.FadeInSeconds, Math.Max(0, duration / 3));
            var fadeOut = joinRight ? 0 : Math.Min(range.FadeOutSeconds, Math.Max(0, duration / 3));
            var fadeOutStart = Math.Max(0, duration - fadeOut);
            var scale = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
            var input = range.InputIndex;
            parts.Add(
                $"[{input}:v]trim=start={start}:end={end},setpts=PTS-STARTPTS,fps={fps}:eof_action=pass,{scale},format=yuv420p,trim=duration={N(duration)},settb=1/{fps},setpts=PTS-STARTPTS[v{i}]");
            var audioRange = range.AudioRange ?? range.Range;
            var audioInput = string.IsNullOrEmpty(range.AudioSourceAssetId) ? input : range.AudioInputIndex;
            IReadOnlyList<TimeRange> audioKeeps = range.AudioKeeps is null
                ? [audioRange]
                : range.AudioKeeps;
            string audio;
            if (audioKeeps.Count == 0)
            {
                audio = $"anullsrc=r={OutputSampleRate}:cl=stereo:d={N(Math.Max(0.02, duration))}";
            }
            else
            {
                audio = AudioTrim(audioInput, audioKeeps, i, parts);
            }
            if (Math.Abs(range.AudioGainDb) > 0.0001)
            {
                audio += $",volume={N(range.AudioGainDb)}dB";
            }

            if (range.RoomInputIndex >= 0 && !string.IsNullOrEmpty(range.AudioSourceAssetId))
            {
                parts.Add(
                    $"{audio},apad=whole_dur={N(duration)},asetpts=PTS-STARTPTS,{OutputAudioFormat}[s{i}]");
                var room = $"[{range.RoomInputIndex}:a]atrim=start={N(AudioTreatment.RoomToneSkipSeconds)},asetpts=PTS-STARTPTS,aloop=loop=-1:size=20000000";
                if (Math.Abs(range.RoomGainDb) > 0.0001)
                {
                    room += $",volume={N(range.RoomGainDb)}dB";
                }

                parts.Add(
                    $"{room},atrim=0:{N(duration)},asetpts=PTS-STARTPTS,{OutputAudioFormat}[r{i}]");
                audio = $"[s{i}][r{i}]amix=inputs=2:duration=first:normalize=0:dropout_transition=0,atrim=duration={N(duration)},asetpts=PTS-STARTPTS";
            }
            else
            {
                audio += $",atrim=duration={N(duration)},apad=whole_dur={N(duration)},asetpts=PTS-STARTPTS";
            }

            if (fadeIn > 0)
            {
                audio += $",afade=t=in:st=0:d={N(fadeIn)}";
            }

            if (fadeOut > 0)
            {
                audio += $",afade=t=out:st={N(fadeOutStart)}:d={N(fadeOut)}";
            }

            audio += $",{OutputAudioFormat}";
            parts.Add($"{audio}[a{i}]");
        }

        if (ranges.Count == 1)
        {
            parts.Add("[v0]null[v]");
            parts.Add("[a0]anull[a]");
            return string.Join(';', parts);
        }

        if (!crossJoin.Any(join => join))
        {
            var concat = new StringBuilder();
            for (var i = 0; i < ranges.Count; i++)
            {
                concat.Append(CultureInfo.InvariantCulture, $"[v{i}][a{i}]");
            }

            parts.Add($"{concat}concat=n={ranges.Count}:v=1:a=1[v][a]");
            return string.Join(';', parts);
        }

        var groups = new List<List<int>>();
        var current = new List<int> { 0 };
        for (var i = 1; i < ranges.Count; i++)
        {
            if (crossJoin[i])
            {
                groups.Add(current);
                current = [i];
                continue;
            }

            current.Add(i);
        }

        groups.Add(current);

        var labels = new (string Video, string Audio, double Duration)[groups.Count];
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var duration = group.Sum(index => ranges[index].Duration);
            if (group.Count == 1)
            {
                labels[g] = ($"v{group[0]}", $"a{group[0]}", duration);
                continue;
            }

            var concat = new StringBuilder();
            foreach (var index in group)
            {
                concat.Append(CultureInfo.InvariantCulture, $"[v{index}][a{index}]");
            }

            parts.Add($"{concat}concat=n={group.Count}:v=1:a=1[vg{g}raw][ag{g}raw]");
            parts.Add($"[vg{g}raw]fps={fps}:eof_action=pass,trim=duration={N(duration)},settb=1/{fps},setpts=PTS-STARTPTS[vg{g}]");
            parts.Add($"[ag{g}raw]atrim=duration={N(duration)},asetpts=PTS-STARTPTS[ag{g}]");
            labels[g] = ($"vg{g}", $"ag{g}", duration);
        }

        var video = labels[0].Video;
        var audioLabel = labels[0].Audio;
        var running = labels[0].Duration;
        for (var g = 1; g < groups.Count; g++)
        {
            var left = groups[g][0];
            var fade = Math.Min(
                Math.Min(ranges[left - 1].FadeOutSeconds, ranges[left].FadeInSeconds),
                Math.Min(Math.Max(0, running - 0.001), Math.Max(0, labels[g].Duration - 0.001)));
            var last = g == groups.Count - 1;
            var nextVideo = last ? "v" : $"vx{g}";
            var nextAudio = last ? "a" : $"ax{g}";
            var offset = running - fade;
            parts.Add($"[{video}][{labels[g].Video}]xfade=transition=fade:duration={N(fade)}:offset={N(offset)}[{nextVideo}xf]");
            parts.Add($"[{nextVideo}xf]settb=1/{fps},setpts=PTS-STARTPTS[{nextVideo}]");
            parts.Add($"[{audioLabel}][{labels[g].Audio}]acrossfade=d={N(fade)}[{nextAudio}]");
            running += labels[g].Duration - fade;
            video = nextVideo;
            audioLabel = nextAudio;
        }

        return string.Join(';', parts);
    }

    public async Task<IReadOnlyList<string>> RenderCompositionAsync(
        ProjectDocument project,
        ResolvedComposition composition,
        string directory,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var roomGain = project.BoundRender.RoomGainDb;
        if (preview && roomGain is null && RenderIdentity.HasAudioOverlay(project))
        {
            roomGain = await MeasureRoomBedGainAsync(project, cancellationToken);
        }
        foreach (var scene in composition.Scenes)
        {
            var path = Path.Combine(directory, $"scene-{scene.SceneId}.mp4");
            if (scene.IsGenerated)
            {
                var visual = project.Sources.First(source => source.Id == scene.VisualAssetId);
                var audio = ResolveProjectPath(_store.ProjectDirectory, scene.NarrationPath
                    ?? throw VideoEditException.Validation($"Scene {scene.SceneId} has no bound narration derivative."));
                await RenderStillAsync(visual.Path, audio, scene.DurationSeconds, path, preview, timeout, cancellationToken, project.Output);
            }
            else
            {
                var inputs = BindInputs(project, scene);
                var ranges = ApplyRoomBedGain(inputs.Ranges, roomGain);
                await RenderRangesAsync(inputs.Paths, ranges, path, preview, timeout, cancellationToken, project.Output);
            }

            files.Add(path);
        }

        return files;
    }

    public async Task ExtractStillAsync(
        string sourcePath,
        double atSeconds,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".tmp.jpg";
        DeleteIfExists(temp);
        var result = await _processRunner.RunAsync(
            _options.FfmpegPath,
            [
                "-hide_banner", "-y",
                "-ss", N(atSeconds),
                "-i", sourcePath,
                "-frames:v", "1",
                "-q:v", "2",
                temp,
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            DeleteIfExists(temp);
            throw VideoEditException.Conflict($"ffmpeg failed ({result.ExitCode}). {Trim(result.StandardError)}");
        }

        File.Move(temp, destinationPath, overwrite: true);
    }

    public static string ResolveProjectPath(string projectDirectory, string stored)
    {
        return Path.IsPathRooted(stored) ? stored : Path.Combine(projectDirectory, stored.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string ProjectRelative(string projectDirectory, string fullPath)
    {
        var relative = Path.GetRelativePath(projectDirectory, fullPath);
        return relative.Replace('\\', '/');
    }

    public static void PublishFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".tmp";
        DeleteIfExists(temp);
        try
        {
            File.Copy(source, temp, overwrite: true);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                throw VideoEditException.Conflict($"Could not publish {destination}.");
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            DeleteIfExists(temp);
            throw;
        }
    }

    private async Task RenderStillAsync(
        string imagePath,
        string audioPath,
        double duration,
        string destinationPath,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        OutputSettings output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temp = destinationPath + ".tmp.mp4";
        DeleteIfExists(temp);
        var scale = $"scale={output.Width}:{output.Height}:force_original_aspect_ratio=decrease,pad={output.Width}:{output.Height}:(ow-iw)/2:(oh-ih)/2";
        var fps = output.FrameRate.ToString(CultureInfo.InvariantCulture);
        var filter =
            $"[0:v]{scale},fps={fps}:eof_action=pass,format=yuv420p,trim=duration={N(duration)},setpts=PTS-STARTPTS[v];" +
            $"[1:a]atrim=0:{N(duration)},asetpts=PTS-STARTPTS,{OutputAudioFormat}[a]";
        var arguments = new List<string>
        {
            "-hide_banner", "-y",
            "-loop", "1", "-i", imagePath,
            "-i", audioPath,
            "-filter_complex", filter,
            "-map", "[v]", "-map", "[a]",
            "-c:v", output.VideoCodec,
            "-preset", preview ? "veryfast" : "medium",
            "-crf", preview ? "28" : "18",
            "-pix_fmt", "yuv420p",
            "-r", fps,
            "-c:a", output.AudioCodec,
            "-b:a", preview ? "128k" : "192k",
            "-ar", OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", OutputChannels.ToString(CultureInfo.InvariantCulture),
            "-t", N(duration),
            "-movflags", "+faststart",
            temp,
        };
        var result = await _processRunner.RunAsync(_options.FfmpegPath, arguments, timeout, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            MarkIncomplete(destinationPath, temp);
            throw VideoEditException.Conflict($"ffmpeg failed ({result.ExitCode}). {Trim(result.StandardError)}");
        }

        File.Move(temp, destinationPath, overwrite: true);
    }

    private async Task ConcatScenesAsync(
        IReadOnlyList<string> sceneFiles,
        string destinationPath,
        ProjectDocument project,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var policyGap = preview
            ? EditingPolicy.LoadOrDefault(Path.Combine(_store.ProjectDirectory, "edit", EditingPolicy.FileName)).SceneBoundaryGapS
            : project.BoundRender.SceneBoundaryGapS;
        var parts = new List<string>();
        for (var i = 0; i < sceneFiles.Count; i++)
        {
            parts.Add(sceneFiles[i]);
            if (i == sceneFiles.Count - 1 || policyGap <= 0)
            {
                continue;
            }

            var gapPath = Path.Combine(
                Path.GetDirectoryName(sceneFiles[i]) ?? _store.ProjectDirectory,
                $".scene-gap-{i}.mp4");
            await RenderBoundaryGapAsync(
                sceneFiles[i],
                project,
                policyGap,
                gapPath,
                preview,
                timeout,
                cancellationToken);
            parts.Add(gapPath);
        }

        await ConcatAsync(parts, destinationPath, preview, timeout, cancellationToken, project.Output);
        foreach (var gap in parts.Where(path => Path.GetFileName(path).StartsWith(".scene-gap-", StringComparison.Ordinal)))
        {
            DeleteIfExists(gap);
        }
    }

    private async Task RenderBoundaryGapAsync(
        string previousScenePath,
        ProjectDocument project,
        double gapSeconds,
        string destinationPath,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var output = project.Output;
        var fps = output.FrameRate.ToString(CultureInfo.InvariantCulture);
        var room = project.Narration.RoomTone is { Path: var roomPath }
            ? ResolveProjectPath(_store.ProjectDirectory, roomPath)
            : null;
        if (room is not null && !File.Exists(room))
        {
            room = null;
        }

        var framePath = destinationPath + ".last.png";
        var temp = destinationPath + ".tmp.mp4";
        DeleteIfExists(framePath);
        DeleteIfExists(temp);
        await ExtractLastFrameAsync(previousScenePath, framePath, timeout, cancellationToken);

        var arguments = new List<string>
        {
            "-hide_banner", "-y",
            "-loop", "1", "-framerate", fps, "-i", framePath,
        };
        if (room is not null)
        {
            arguments.AddRange(["-ss", N(AudioTreatment.RoomToneSkipSeconds), "-i", room]);
        }
        else
        {
            arguments.AddRange(
            [
                "-f", "lavfi", "-i",
                $"anullsrc=r={OutputSampleRate}:cl=stereo",
            ]);
        }

        var filter =
            $"[0:v]scale={output.Width}:{output.Height}:force_original_aspect_ratio=decrease," +
            $"pad={output.Width}:{output.Height}:(ow-iw)/2:(oh-ih)/2,fps={fps},format=yuv420p,setpts=PTS-STARTPTS[v];" +
            $"[1:a]aloop=loop=-1:size=20000000,atrim=0:{N(gapSeconds)},asetpts=PTS-STARTPTS,{OutputAudioFormat}[a]";
        arguments.AddRange(
        [
            "-filter_complex", filter,
            "-map", "[v]", "-map", "[a]",
            "-c:v", output.VideoCodec,
            "-preset", preview ? "veryfast" : "medium",
            "-crf", preview ? "28" : "18",
            "-pix_fmt", "yuv420p",
            "-r", fps,
            "-c:a", output.AudioCodec,
            "-b:a", preview ? "128k" : "192k",
            "-ar", OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", OutputChannels.ToString(CultureInfo.InvariantCulture),
            "-t", N(gapSeconds),
            "-movflags", "+faststart",
            temp,
        ]);
        var result = await _processRunner.RunAsync(_options.FfmpegPath, arguments, timeout, cancellationToken);
        DeleteIfExists(framePath);
        if (result.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            MarkIncomplete(destinationPath, temp);
            throw VideoEditException.Conflict($"ffmpeg failed ({result.ExitCode}). {Trim(result.StandardError)}");
        }

        File.Move(temp, destinationPath, overwrite: true);
    }

    private async Task ExtractLastFrameAsync(
        string videoPath,
        string destinationPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (await TryDecodeLastFrameAsync(videoPath, destinationPath, seekEof: true, timeout, cancellationToken))
        {
            return;
        }

        if (await TryDecodeLastFrameAsync(videoPath, destinationPath, seekEof: false, timeout, cancellationToken))
        {
            return;
        }

        throw VideoEditException.Conflict("Could not decode the last frame for the scene-boundary hold.");
    }

    private async Task<bool> TryDecodeLastFrameAsync(
        string videoPath,
        string destinationPath,
        bool seekEof,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DeleteIfExists(destinationPath);
        var arguments = new List<string> { "-hide_banner", "-y" };
        if (seekEof)
        {
            arguments.AddRange(["-sseof", "-1"]);
        }

        arguments.AddRange(
        [
            "-i", videoPath,
            "-vf", "reverse,select=eq(n\\,0)",
            "-frames:v", "1",
            "-update", "1",
            destinationPath,
        ]);
        var result = await _processRunner.RunAsync(_options.FfmpegPath, arguments, timeout, cancellationToken);
        if (result.ExitCode == 0 && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return true;
        }

        DeleteIfExists(destinationPath);
        return false;
    }

    private async Task ConcatAsync(
        IReadOnlyList<string> parts,
        string destinationPath,
        bool preview,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        OutputSettings output)
    {
        if (parts.Count == 1)
        {
            PublishFile(parts[0], destinationPath);
            return;
        }

        var list = destinationPath + ".concat.txt";
        File.WriteAllLines(list, parts.Select(path => $"file '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'"));
        var temp = destinationPath + ".tmp.mp4";
        DeleteIfExists(temp);
        var arguments = new List<string>
        {
            "-hide_banner", "-y",
            "-f", "concat", "-safe", "0", "-i", list,
            "-c:v", output.VideoCodec,
            "-preset", preview ? "veryfast" : "medium",
            "-crf", preview ? "28" : "18",
            "-pix_fmt", "yuv420p",
            "-r", output.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", output.AudioCodec,
            "-b:a", preview ? "128k" : "192k",
            "-ar", OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", OutputChannels.ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            temp,
        };
        var result = await _processRunner.RunAsync(_options.FfmpegPath, arguments, timeout, cancellationToken);
        File.Delete(list);
        if (result.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            MarkIncomplete(destinationPath, temp);
            throw VideoEditException.Conflict($"ffmpeg failed ({result.ExitCode}). {Trim(result.StandardError)}");
        }

        File.Move(temp, destinationPath, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private void SaveManifest(PreviewManifest manifest)
    {
        Directory.CreateDirectory(PreviewsDirectory);
        File.WriteAllText(
            Path.Combine(PreviewsDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, ProjectStore.JsonOptions));
    }

    private static (List<string> Paths, List<RenderRange> Ranges) BindInputs(ProjectDocument project, ResolvedScene scene)
    {
        var paths = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var ranges = new List<RenderRange>();
        foreach (var range in scene.KeepRanges)
        {
            var id = string.IsNullOrEmpty(range.SourceAssetId) ? scene.SourceAssetId : range.SourceAssetId;
            if (!index.TryGetValue(id, out var input))
            {
                input = paths.Count;
                index[id] = input;
                paths.Add(project.Sources.First(source => source.Id == id).Path);
            }

            var audioId = string.IsNullOrEmpty(range.AudioSourceAssetId) ? id : range.AudioSourceAssetId;
            if (!index.TryGetValue(audioId, out var audioInput))
            {
                audioInput = paths.Count;
                index[audioId] = audioInput;
                paths.Add(project.Sources.First(source => source.Id == audioId).Path);
            }

            var roomIndex = -1;
            if (RenderIdentity.UsesRoomBed(project)
                && !string.IsNullOrEmpty(range.AudioSourceAssetId)
                && project.Narration.RoomTone is { Path: var roomPath }
                && File.Exists(roomPath))
            {
                roomIndex = paths.Count;
                paths.Add(roomPath);
            }

            ranges.Add(range with { InputIndex = input, AudioInputIndex = audioInput, RoomInputIndex = roomIndex });
        }

        return (paths, ranges);
    }

    private static List<RenderRange> ApplyRoomBedGain(List<RenderRange> ranges, double? gain) =>
        gain is not double roomGain
            ? ranges
            : ranges
                .Select(range => range.RoomInputIndex >= 0 ? range with { RoomGainDb = roomGain } : range)
                .ToList();

    private async Task<double?> MeasureRoomBedGainAsync(ProjectDocument project, CancellationToken cancellationToken)
    {
        if (project.Narration.RoomTone is not { Path: var roomPath } || !File.Exists(roomPath))
        {
            return null;
        }

        var target = MeasureHostNoiseFloor();
        var result = await _processRunner.RunAsync(
            _options.FfmpegPath,
            [
                "-hide_banner",
                "-ss", N(AudioTreatment.RoomToneSkipSeconds),
                "-t", "5",
                "-i", roomPath,
                "-af", "astats=metadata=1:reset=0",
                "-f", "null",
                "-",
            ],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        var measured = AudioLoudness.ParseRmsDb(result.StandardError + result.StandardOutput);
        return AudioLoudness.FloorGainDb(measured, target);
    }

    private double MeasureHostNoiseFloor()
    {
        var analysis = Path.Combine(_store.ProjectDirectory, "edit", "analysis");
        var wavPath = Path.Combine(analysis, "audio16k.wav");
        var vadPath = Path.Combine(analysis, VadSpeechMap.FileName);
        if (!File.Exists(wavPath) || !File.Exists(vadPath))
        {
            throw VideoEditException.Validation(
                "Host noise floor needs edit/analysis/audio16k.wav and the VAD speech map.");
        }

        var (samples, sampleRate) = RoomFloor.ReadMono16(wavPath);
        var silences = VadSpeechMap.Load(vadPath).ToSilences(samples.Length / (double)sampleRate);
        return RoomFloor.NoiseFloorDb(samples, sampleRate, silences);
    }

    private static void MarkIncomplete(string destinationPath, string tempPath)
    {
        DeleteIfExists(destinationPath);
        if (File.Exists(tempPath))
        {
            File.Move(tempPath, destinationPath + ".incomplete", overwrite: true);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public const int OutputSampleRate = 48000;

    public const int OutputChannels = 2;

    public static string OutputAudioFormat =>
        $"aformat=sample_fmts=fltp:sample_rates={OutputSampleRate}:channel_layouts=stereo";

    private static string AudioTrim(int audioInput, IReadOnlyList<TimeRange> keeps, int clip, List<string> parts)
    {
        if (keeps.Count == 1)
        {
            return $"[{audioInput}:a]atrim=start={N(keeps[0].Start)}:end={N(keeps[0].End)},asetpts=PTS-STARTPTS";
        }

        var labels = new List<string>();
        for (var k = 0; k < keeps.Count; k++)
        {
            var label = $"ak{clip}_{k}";
            parts.Add(
                $"[{audioInput}:a]atrim=start={N(keeps[k].Start)}:end={N(keeps[k].End)},asetpts=PTS-STARTPTS,{OutputAudioFormat}[{label}]");
            labels.Add($"[{label}]");
        }

        return $"{string.Join("", labels)}concat=n={keeps.Count}:v=0:a=1";
    }

    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Trim(string text) =>
        text.Length <= 400 ? text : text[^400..];
}

public sealed class PreviewManifest
{
    public PreviewEntry? RoughCut { get; set; }

    public Dictionary<string, PreviewEntry> Splices { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, PreviewEntry> Scenes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PreviewEntry
{
    public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalPath { get; set; }

    public string CompositionHash { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProposalRevision { get; set; }
}

public sealed record ExportResult(string FileName, string SnapshotName, string CompositionHash);

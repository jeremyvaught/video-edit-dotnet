using System.Globalization;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class ProjectService
{
    private readonly ProjectStore _store;
    private readonly MediaGate _mediaGate;
    private readonly MediaRenderer _renderer;

    public ProjectService(ProjectStore store, MediaGate mediaGate, MediaRenderer renderer)
    {
        _store = store;
        _mediaGate = mediaGate;
        _renderer = renderer;
    }

    public ProjectDocument Load() => _store.Load();

    public ProjectDocument SetNarrationSettings(string voiceId, string modelId, int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(voiceId) || !voiceId.All(char.IsAsciiLetterOrDigit))
        {
            throw VideoEditException.Validation("Choose an ElevenLabs voice ID from the voice list.");
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw VideoEditException.Validation("A narration model ID is required.");
        }

        return _store.Mutate(expectedRevision, project =>
        {
            if (string.Equals(project.Narration.VoiceId, voiceId, StringComparison.Ordinal)
                && string.Equals(project.Narration.ModelId, modelId, StringComparison.Ordinal))
            {
                return false;
            }

            project.Narration.VoiceId = voiceId;
            project.Narration.ModelId = modelId;
            return true;
        });
    }

    public ProjectDocument RecordDecision(string editId, string decision, string? note, int expectedRevision)
    {
        if (!DecisionStates.All.Contains(decision))
        {
            throw VideoEditException.Validation($"Unknown decision '{decision}'.");
        }

        return MutateComposition(expectedRevision, project =>
        {
            var scene = FindSceneByEdit(project, editId);
            var edit = scene.Edits.First(item => item.Id == editId);
            if (!edit.DecisionMatchesProposal && edit.DecisionProposalRevision is not null)
            {
                throw VideoEditException.Validation(
                    $"Edit {editId} proposal changed; review the current proposal before deciding.");
            }

            var before = CompositionHasher.SceneHash(project, scene);
            var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            edit.Note = trimmed;
            if (decision == DecisionStates.Rejected && trimmed is not null)
            {
                ReproposeFromNote(project, scene, edit);
            }
            else
            {
                edit.Decision = decision;
                edit.DecisionProposalRevision = edit.ProposalRevision;
            }

            var after = CompositionHasher.SceneHash(project, scene);
            InvalidateAcceptance(project, scene, before, after);
            return true;
        });
    }

    public ProjectDocument ResolveTiming(string findingId, string decision, int expectedRevision)
    {
        if (!TimingDecisions.All.Contains(decision))
        {
            throw VideoEditException.Validation(
                "A timing finding must be resolved by extend, trim, or reject.");
        }

        return MutateComposition(expectedRevision, project =>
        {
            var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
                ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
            if (finding.Kind != PickupFindingKinds.Timing)
            {
                throw VideoEditException.Validation($"Finding {findingId} is not a timing finding.");
            }

            var scene = project.Scenes.FirstOrDefault(item =>
                item.Selections.Any(selection =>
                    string.Equals(selection.AudioSourceAssetId, finding.SourceAssetId, StringComparison.Ordinal)
                    && selection.AudioRange is TimeRange audio
                    && audio.Overlaps(finding.Range)))
                ?? throw VideoEditException.Validation($"Finding {findingId} is not bound to a pickup overlay.");
            var selection = scene.Selections.First(item =>
                string.Equals(item.AudioSourceAssetId, finding.SourceAssetId, StringComparison.Ordinal)
                && item.AudioRange is TimeRange audio
                && audio.Overlaps(finding.Range));
            CompositionResolver.EnsureTimingDecisionAllowed(project, selection, scene, decision);
            var before = CompositionHasher.SceneHash(project, scene);
            if (decision == TimingDecisions.Extend)
            {
                ExtendHostPicture(project, selection, scene);
            }
            else if (decision == TimingDecisions.Trim)
            {
                TrimPickupToHost(project, scene, selection);
            }
            else
            {
                RejectPickup(scene, selection);
                finding.Decision = DecisionStates.Rejected;
                var afterReject = CompositionHasher.SceneHash(project, scene);
                InvalidateAcceptance(project, scene, before, afterReject);
                return true;
            }

            EnsureTimingFits(scene, selection);
            finding.Decision = DecisionStates.Approved;

            var after = CompositionHasher.SceneHash(project, scene);
            InvalidateAcceptance(project, scene, before, after);
            return true;
        });
    }

    public ProjectDocument UpdateProposal(
        string editId,
        double start,
        double end,
        string kind,
        string reason,
        int expectedRevision)
    {
        return MutateComposition(expectedRevision, project =>
        {
            var scene = FindSceneByEdit(project, editId);
            var edit = scene.Edits.First(item => item.Id == editId);
            if (edit.Start == start
                && edit.End == end
                && string.Equals(edit.Kind, kind, StringComparison.Ordinal)
                && string.Equals(edit.Reason, reason, StringComparison.Ordinal))
            {
                return false;
            }

            edit.Start = start;
            edit.End = end;
            edit.Kind = kind;
            edit.Reason = reason;
            edit.ProposalRevision += 1;
            edit.Decision = DecisionStates.Pending;
            edit.DecisionProposalRevision = null;
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });
    }

    public ProjectDocument SaveNote(string editId, string? note, int expectedRevision)
    {
        return _store.Mutate(expectedRevision, project =>
        {
            var scene = FindSceneByEdit(project, editId);
            var edit = scene.Edits.First(item => item.Id == editId);
            var next = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (string.Equals(edit.Note, next, StringComparison.Ordinal))
            {
                return false;
            }

            edit.Note = next;
            if (edit.Decision == DecisionStates.Rejected && next is not null)
            {
                ReproposeFromNote(project, scene, edit);
            }

            return true;
        });
    }

    public ProjectDocument SnapPendingEdits(
        int expectedRevision,
        IReadOnlySet<string>? includeRejectedIds = null,
        bool includeApproved = false,
        Func<double, double?>? energyMinimum = null)
    {
        var policy = LoadPolicy();
        var analysis = Path.Combine(_store.ProjectDirectory, "edit", "analysis");
        var vadPath = Path.Combine(analysis, VadSpeechMap.FileName);
        if (!File.Exists(vadPath))
        {
            throw VideoEditException.NotFound($"VAD speech map is missing at {vadPath}.");
        }

        var diagnosisPath = Path.Combine(analysis, EditingPolicy.DiagnosisFileName);
        if (!File.Exists(diagnosisPath))
        {
            throw VideoEditException.NotFound($"Silence map is missing at {diagnosisPath}.");
        }

        var diagnosis = SilenceSnapper.ParseFfmpegLog(File.ReadAllText(diagnosisPath), policy);
        var map = VadSpeechMap.Load(vadPath);
        return MutateComposition(expectedRevision, project =>
        {
            var snapSilences = map.ToSilences(project.Sources[0].DurationSeconds);
            var changed = false;
            foreach (var scene in project.Scenes)
            {
                foreach (var edit in scene.Edits)
                {
                    if (edit.Decision == DecisionStates.Approved && !includeApproved)
                    {
                        continue;
                    }

                    if (edit.Decision == DecisionStates.Rejected
                        && !IsReproposalRequest(edit)
                        && (includeRejectedIds is null || !includeRejectedIds.Contains(edit.Id)))
                    {
                        continue;
                    }

                    if (IsReproposalRequest(edit))
                    {
                        if (!ApplyNoteProposal(project, scene, edit))
                        {
                            edit.ProposalRevision += 1;
                            edit.Decision = DecisionStates.Pending;
                            edit.DecisionProposalRevision = null;
                            scene.Acceptance = null;
                            project.FinalApproval = null;
                            changed = true;
                            continue;
                        }
                    }

                    var (range, noClean) = SilenceSnapper.SnapEdit(
                        edit.Range,
                        edit.Kind,
                        snapSilences,
                        policy,
                        diagnosis,
                        map.Onsets());
                    if (OverlapsSibling(scene, edit, range))
                    {
                        continue;
                    }

                    if (edit.Kind == EditKinds.Postroll)
                    {
                        var duration = project.Sources[0].DurationSeconds;
                        if (range.End > duration)
                        {
                            range = new TimeRange(range.Start, duration);
                        }
                    }

                    var sameTimes = Math.Abs(edit.Start - range.Start) < 0.000001
                        && Math.Abs(edit.End - range.End) < 0.000001
                        && edit.NoCleanBoundary == noClean;
                    if (sameTimes && !IsReproposalRequest(edit))
                    {
                        continue;
                    }

                    if (!sameTimes)
                    {
                        var newlyUnclean = noClean && !edit.NoCleanBoundary;
                        edit.Start = range.Start;
                        edit.End = range.End;
                        edit.NoCleanBoundary = noClean;
                        if (newlyUnclean)
                        {
                            edit.Confidence = LowerConfidence(edit.Confidence);
                            if (!edit.Reason.Contains("no clean", StringComparison.OrdinalIgnoreCase))
                            {
                                edit.Reason = edit.Reason.TrimEnd() + " No clean silence boundary within 150 ms.";
                            }
                        }
                    }

                    edit.ProposalRevision += 1;
                    if (edit.Decision == DecisionStates.Flagged)
                    {
                        edit.DecisionProposalRevision = edit.ProposalRevision;
                    }
                    else
                    {
                        edit.Decision = DecisionStates.Pending;
                        edit.DecisionProposalRevision = null;
                    }

                    scene.Acceptance = null;
                    project.FinalApproval = null;
                    changed = true;
                }
            }

            return changed;
        });
    }

    public ProjectDocument ApplyProposalTimes(
        IReadOnlyList<ProposalTimeUpdate> updates,
        int expectedRevision)
    {
        return MutateComposition(expectedRevision, project =>
        {
            var changed = false;
            foreach (var update in updates)
            {
                var scene = FindSceneByEdit(project, update.EditId);
                var edit = scene.Edits.First(item => item.Id == update.EditId);
                var sameTimes = Math.Abs(edit.Start - update.Start) < 0.000001
                    && Math.Abs(edit.End - update.End) < 0.000001
                    && (update.Reason is null || string.Equals(edit.Reason, update.Reason, StringComparison.Ordinal))
                    && (update.RemovedText is null || string.Equals(edit.RemovedText, update.RemovedText, StringComparison.Ordinal))
                    && (update.Decision is null || string.Equals(edit.Decision, update.Decision, StringComparison.Ordinal))
                    && (update.NoCleanBoundary is null || edit.NoCleanBoundary == update.NoCleanBoundary)
                    && (update.Crossfade is null || edit.Crossfade == update.Crossfade)
                    && (update.FadeSeconds is null || edit.FadeSeconds == update.FadeSeconds);
                if (sameTimes && !update.Force)
                {
                    continue;
                }

                if (!sameTimes)
                {
                    edit.Start = update.Start;
                    edit.End = update.End;
                    if (update.RemovedText is not null)
                    {
                        edit.RemovedText = update.RemovedText;
                    }

                    if (update.Reason is not null)
                    {
                        edit.Reason = update.Reason;
                    }

                    if (update.NoCleanBoundary is bool noClean)
                    {
                        edit.NoCleanBoundary = noClean;
                    }

                    if (update.Crossfade is bool crossfade)
                    {
                        edit.Crossfade = crossfade;
                    }

                    if (update.FadeSeconds is double fadeSeconds)
                    {
                        edit.FadeSeconds = fadeSeconds;
                    }

                    if (update.Confidence is double confidence)
                    {
                        edit.Confidence = confidence;
                    }
                }

                edit.ProposalRevision += 1;
                edit.Decision = update.Decision ?? DecisionStates.Pending;
                edit.DecisionProposalRevision = null;
                scene.Acceptance = null;
                project.FinalApproval = null;
                changed = true;
            }

            return changed;
        });
    }

    public ProjectDocument AcceptScene(string sceneId, int expectedRevision) =>
        RecordSceneDecision(sceneId, DecisionStates.Approved, null, expectedRevision);

    public ProjectDocument RecordSceneDecision(string sceneId, string decision, string? note, int expectedRevision)
    {
        if (decision is not (DecisionStates.Approved or DecisionStates.Rejected or DecisionStates.Flagged))
        {
            throw VideoEditException.Validation("A scene can be accepted, rejected, or flagged.");
        }

        return _store.Mutate(expectedRevision, project =>
        {
            var scene = project.Scenes.FirstOrDefault(item => item.Id == sceneId)
                ?? throw VideoEditException.NotFound($"Scene {sceneId} was not found.");
            if (decision == DecisionStates.Approved && CompositionResolver.HasBlockingEdits(scene))
            {
                throw VideoEditException.Validation($"Scene {sceneId} still has pending, flagged, or stale proposal decisions.");
            }

            if (decision == DecisionStates.Approved
                && scene.Selections.Any(selection =>
                    CompositionResolver.HostCutsAffectPickup(selection, scene, approvedOnly: false)))
            {
                throw VideoEditException.Validation(CompositionResolver.HostCutRestriction);
            }

            if (decision == DecisionStates.Approved
                && CompositionResolver.HasUnresolvedTiming(project, scene))
            {
                throw VideoEditException.Validation(
                    $"Scene {sceneId} has unresolved pickup timing. Extend the host picture, trim the pickup, or reject the timing finding.");
            }

            FreezeBoundRender(project);
            scene.Acceptance = new SceneAcceptance
            {
                CompositionHash = CompositionHasher.SceneHash(project, scene),
                Decision = decision,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            };
            if (decision != DecisionStates.Approved)
            {
                project.FinalApproval = null;
            }

            return true;
        });
    }

    public ProjectDocument ApproveFinal(int expectedRevision)
    {
        return _store.Mutate(expectedRevision, project =>
        {
            var blocks = CompositionResolver.ExportBlocks(project, requireFinalApproval: false).ToList();
            if (blocks.Count > 0)
            {
                throw VideoEditException.Validation(string.Join(" ", blocks));
            }

            FreezeBoundRender(project);
            var current = CompositionHasher.ProposedHash(project);
            var manifest = _renderer.LoadManifest();
            if (manifest.RoughCut is null
                || !string.Equals(manifest.RoughCut.CompositionHash, current, StringComparison.Ordinal))
            {
                throw VideoEditException.Validation("Final approval requires a current rough-cut preview.");
            }

            var previewPath = Path.Combine(_renderer.PreviewsDirectory, manifest.RoughCut.Path);
            if (!File.Exists(previewPath))
            {
                throw VideoEditException.Validation("Final approval requires a current rough-cut preview.");
            }

            project.FinalApproval = new FinalApproval
            {
                CompositionHash = CompositionHasher.FullHash(project),
            };
            return true;
        });
    }

    public ProjectDocument InsertGeneratedScene(string recordedSceneId, SourceAsset still, int expectedRevision)
    {
        var project = _store.Load();
        var assetId = project.Narration.GeneratedAssetId;
        var derivative = project.Narration.Derivative;
        if (string.IsNullOrWhiteSpace(assetId)
            || derivative is null
            || string.IsNullOrWhiteSpace(derivative.Path)
            || string.IsNullOrWhiteSpace(derivative.Fingerprint)
            || derivative.DurationSeconds <= 0)
        {
            throw VideoEditException.Validation("Generate and match narration before inserting the scene.");
        }

        return InsertGeneratedScene(
            recordedSceneId,
            assetId,
            derivative.Path,
            derivative.Fingerprint,
            derivative.DurationSeconds,
            still,
            expectedRevision);
    }

    public ProjectDocument InsertGeneratedScene(
        string recordedSceneId,
        string narrationAssetId,
        string narrationPath,
        string narrationFingerprint,
        double narrationDurationSeconds,
        SourceAsset still,
        int expectedRevision)
    {
        if (narrationDurationSeconds <= 0
            || string.IsNullOrWhiteSpace(narrationPath)
            || string.IsNullOrWhiteSpace(narrationFingerprint))
        {
            throw VideoEditException.Validation("Generated scene duration must come from a measured, fingerprinted derivative.");
        }

        return MutateComposition(expectedRevision, project =>
        {
            if (project.Scenes.Any(item => item.Id == "scene-generated"))
            {
                throw VideoEditException.Validation("A generated scene is already in the project.");
            }

            var recorded = project.Scenes.FirstOrDefault(item => item.Id == recordedSceneId)
                ?? throw VideoEditException.NotFound($"Scene {recordedSceneId} was not found.");
            var instruction = recorded.Edits.FirstOrDefault(edit => edit.Kind == EditKinds.Instruction)
                ?? throw VideoEditException.Validation("The recorded scene has no instruction cut to insert the generated passage into.");
            var selection = recorded.Selections.First();
            if (!project.Sources.Any(source => source.Id == still.Id))
            {
                project.Sources.Add(still);
            }

            recorded.Selections =
            [
                new SourceSelection
                {
                    SourceAssetId = selection.SourceAssetId,
                    SourceFingerprint = selection.SourceFingerprint,
                    Start = selection.Start,
                    End = instruction.Start,
                }
            ];
            var later = recorded.Edits.Where(edit => edit.Start >= instruction.End).ToList();
            recorded.Edits = recorded.Edits
                .Where(edit => edit.End <= instruction.Start)
                .ToList();
            recorded.Acceptance = null;

            var generated = new Scene
            {
                Id = "scene-generated",
                Title = "Generated narration",
                Position = recorded.Position + 1,
                NarrationAssetId = narrationAssetId,
                NarrationPath = narrationPath,
                NarrationFingerprint = narrationFingerprint,
                VisualAssetId = still.Id,
                NarrationDurationSeconds = narrationDurationSeconds,
            };
            var close = new Scene
            {
                Id = "scene-close",
                Title = "Talking head close",
                Position = recorded.Position + 2,
                Selections =
                [
                    new SourceSelection
                    {
                        SourceAssetId = selection.SourceAssetId,
                        SourceFingerprint = selection.SourceFingerprint,
                        Start = instruction.End,
                        End = selection.End,
                    }
                ],
                Edits = later.Select(edit =>
                {
                    edit.SceneId = "scene-close";
                    return edit;
                }).ToList(),
            };

            foreach (var scene in project.Scenes.Where(item => item.Position > recorded.Position))
            {
                scene.Position += 2;
            }

            project.Scenes.Add(generated);
            project.Scenes.Add(close);
            project.Scenes = project.Scenes.OrderBy(item => item.Position).ToList();
            RecordDerivative(project, narrationAssetId, narrationPath, narrationFingerprint, narrationDurationSeconds);
            project.FinalApproval = null;
            return true;
        });
    }

    public ProjectDocument AddSource(SourceAsset source, int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(source.Id)
            || string.IsNullOrWhiteSpace(source.Path)
            || string.IsNullOrWhiteSpace(source.Fingerprint)
            || source.DurationSeconds <= 0)
        {
            throw VideoEditException.Validation("A source must have an id, path, fingerprint, and measured duration.");
        }

        return _store.Mutate(expectedRevision, project =>
        {
            if (project.Sources.Any(item => string.Equals(item.Id, source.Id, StringComparison.Ordinal)))
            {
                throw VideoEditException.Validation($"Source {source.Id} already exists.");
            }

            if (project.Sources.Any(item =>
                string.Equals(item.Path, source.Path, StringComparison.Ordinal)
                && string.Equals(item.Fingerprint, source.Fingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            project.Sources.Add(source);
            return true;
        });
    }

    public ProjectDocument AddStagedPickup(
        string pickupId,
        string stagedPath,
        double durationSeconds,
        bool hasVideo,
        int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(pickupId) || durationSeconds <= 0)
        {
            throw VideoEditException.Validation("A pickup must have an id and a measured duration.");
        }

        var staged = Path.GetFullPath(stagedPath);
        if (!File.Exists(staged))
        {
            throw VideoEditException.Validation($"Source file is missing: {stagedPath}");
        }

        var extension = Path.GetExtension(staged);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".mov";
        }

        var dest = Path.GetFullPath(Path.Combine(_store.ProjectDirectory, "sources", "pickups", pickupId + extension));
        var moved = false;
        try
        {
            return _store.Mutate(expectedRevision, project =>
            {
                if (project.Sources.Any(item => string.Equals(item.Id, pickupId, StringComparison.Ordinal)))
                {
                    throw VideoEditException.Validation($"Source {pickupId} already exists.");
                }

                if (File.Exists(dest))
                {
                    if (project.Sources.Any(item => string.Equals(item.Path, dest, StringComparison.Ordinal)))
                    {
                        throw VideoEditException.Validation($"Source {pickupId} already exists.");
                    }

                    File.Delete(dest);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(staged, dest);
                moved = true;
                project.Sources.Add(new SourceAsset
                {
                    Id = pickupId,
                    Path = dest,
                    Fingerprint = MediaRenderer.FingerprintFile(dest),
                    DurationSeconds = durationSeconds,
                    Kind = hasVideo ? "video" : "audio",
                });
                return true;
            });
        }
        catch
        {
            if (moved)
            {
                _store.WithWriteLock(project =>
                {
                    if (!project.Sources.Any(item => string.Equals(item.Path, dest, StringComparison.Ordinal))
                        && File.Exists(dest))
                    {
                        File.Delete(dest);
                    }

                    return 0;
                });
            }

            throw;
        }
    }

    public ProjectDocument ApplyPickup(
        string pickupSourceId,
        double pickupStart,
        double pickupEnd,
        int expectedRevision,
        string? sceneId = null,
        string? findingId = null,
        double? replaceStart = null,
        double? replaceEnd = null,
        bool audioOnly = false)
    {
        var pickupRange = new TimeRange(pickupStart, pickupEnd);
        if (!pickupRange.IsValid)
        {
            throw VideoEditException.Validation("The pickup range must have an end after its start.");
        }

        return MutateComposition(expectedRevision, project =>
        {
            var pickup = project.Sources.FirstOrDefault(item => item.Id == pickupSourceId)
                ?? throw VideoEditException.NotFound($"Source {pickupSourceId} was not found.");
            if (pickupRange.End > pickup.DurationSeconds + 0.000001)
            {
                throw VideoEditException.Validation($"Pickup {pickupRange} is outside source duration {pickup.DurationSeconds}.");
            }

            Scene scene;
            TimeRange replace;
            if (!string.IsNullOrWhiteSpace(findingId))
            {
                var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
                    ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
                replace = replaceStart is double start && replaceEnd is double end
                    ? new TimeRange(start, end)
                    : finding.Range;
                if (!replace.IsValid)
                {
                    throw VideoEditException.Validation("The replaced range must have an end after its start.");
                }

                if (!replace.Overlaps(finding.Range))
                {
                    throw VideoEditException.Validation(
                        $"Replace range {replace} does not cover finding {findingId}.");
                }

                scene = project.Scenes.FirstOrDefault(item =>
                        item.Selections.Any(selection =>
                            selection.SourceAssetId == finding.SourceAssetId
                            && selection.Range.Contains(replace)))
                    ?? throw VideoEditException.Validation(
                        $"Finding {findingId} is not inside a selected range of any scene.");
            }
            else
            {
                scene = project.Scenes.FirstOrDefault(item => item.Id == sceneId)
                    ?? throw VideoEditException.NotFound($"Scene {sceneId} was not found.");
                if (replaceStart is null || replaceEnd is null)
                {
                    throw VideoEditException.Validation("A pickup needs --replace-start and --replace-end, or --finding.");
                }

                replace = new TimeRange(replaceStart.Value, replaceEnd.Value);
                if (!replace.IsValid)
                {
                    throw VideoEditException.Validation("The replaced range must have an end after its start.");
                }
            }

            var matches = scene.Selections
                .Select((selection, index) => (selection, index))
                .Where(item =>
                    item.selection.Range.Contains(replace)
                    && (string.IsNullOrWhiteSpace(findingId)
                        || item.selection.SourceAssetId == project.Findings.First(finding => finding.Id == findingId).SourceAssetId))
                .ToList();
            if (matches.Count != 1)
            {
                throw VideoEditException.Validation(
                    matches.Count == 0
                        ? $"Range {replace} is not wholly inside a selected range of scene {scene.Id}."
                        : $"Range {replace} matches more than one selection in scene {scene.Id}; name the source.");
            }

            var hostIndex = matches[0].index;

            var host = scene.Selections[hostIndex];
            var next = scene.Selections.Take(hostIndex).ToList();
            if (host.Start < replace.Start)
            {
                next.Add(CopyVideo(host, host.Start, replace.Start));
            }

            next.Add(audioOnly
                ? new SourceSelection
                {
                    SourceAssetId = host.SourceAssetId,
                    SourceFingerprint = host.SourceFingerprint,
                    Start = replace.Start,
                    End = replace.End,
                    AudioSourceAssetId = pickup.Id,
                    AudioSourceFingerprint = pickup.Fingerprint,
                    AudioStart = pickupRange.Start,
                    AudioEnd = pickupRange.End,
                }
                : new SourceSelection
                {
                    SourceAssetId = pickup.Id,
                    SourceFingerprint = pickup.Fingerprint,
                    Start = pickupRange.Start,
                    End = pickupRange.End,
                });
            if (replace.End < host.End)
            {
                next.Add(CopyVideo(host, replace.End, host.End));
            }

            next.AddRange(scene.Selections.Skip(hostIndex + 1));
            scene.Selections = next;
            scene.Edits = scene.Edits
                .Where(edit => !replace.Contains(edit.Range)
                    && next.Any(selection =>
                        selection.SourceAssetId == edit.SourceAssetId && selection.Range.Contains(edit.Range)))
                .ToList();
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });
    }

    private static SourceSelection CopyVideo(SourceSelection host, double start, double end) =>
        new()
        {
            SourceAssetId = host.SourceAssetId,
            SourceFingerprint = host.SourceFingerprint,
            Start = start,
            End = end,
        };

    public ProjectDocument ImportFindings(string seedPath)
    {
        if (!File.Exists(seedPath))
        {
            throw VideoEditException.NotFound($"Findings fixture is missing at {seedPath}.");
        }

        var seed = System.Text.Json.JsonSerializer.Deserialize<FindingsSeed>(
            File.ReadAllText(seedPath),
            ProjectStore.JsonOptions)
            ?? throw VideoEditException.Validation("Findings fixture is empty.");
        var project = _store.Load();
        var imported = Path.Combine(_store.ProjectDirectory, "edit", "analysis", "findings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
        File.WriteAllText(imported, File.ReadAllText(seedPath));
        return MutateComposition(project.Revision, doc => ApplyFindingsFixture(doc, seed));
    }

    private static bool ApplyFindingsFixture(ProjectDocument project, FindingsSeed seed)
    {
        var source = project.Sources[0];
        var scene = project.Scenes[0];
        var added = false;
        foreach (var finding in seed.Findings)
        {
            if (project.Findings.Any(item => item.Id == finding.Id))
            {
                continue;
            }

            if (finding.Start < 0
                || finding.End > source.DurationSeconds
                || finding.End <= finding.Start)
            {
                throw VideoEditException.Validation(
                    $"Finding {finding.Id} has an invalid range [{finding.Start}, {finding.End}) for a {source.DurationSeconds:0.###}s source.");
            }

            finding.SourceAssetId = source.Id;
            finding.SourceFingerprint = source.Fingerprint;
            project.Findings.Add(finding);
            if (EditKinds.All.Contains(finding.Kind))
            {
                var editId = "e-" + finding.Id;
                if (!scene.Edits.Any(item => item.Id == editId))
                {
                    scene.Edits.Add(new SceneEdit
                    {
                        Id = editId,
                        SceneId = scene.Id,
                        SourceAssetId = source.Id,
                        SourceFingerprint = source.Fingerprint,
                        FindingId = finding.Id,
                        ProposalRevision = 1,
                        Start = finding.Start,
                        End = finding.End,
                        Kind = finding.Kind,
                        RemovedText = finding.TranscriptEvidence,
                        Reason = finding.Reason,
                        Confidence = finding.Confidence,
                        Decision = DecisionStates.Pending,
                    });
                }
            }

            added = true;
        }

        foreach (var question in seed.Questions)
        {
            if (project.Questions.Any(item => item.Id == question.Id))
            {
                continue;
            }

            question.SceneId = scene.Id;
            project.Questions.Add(question);
            added = true;
        }

        if (!added)
        {
            return false;
        }

        scene.Edits = scene.Edits.OrderBy(edit => edit.Start).ToList();
        scene.Acceptance = null;
        project.FinalApproval = null;
        return true;
    }

    public ProjectDocument ReplaceScriptDriftFindings(IReadOnlyList<SourceFinding> findings, int expectedRevision)
    {
        return _store.Mutate(expectedRevision, project =>
        {
            var next = findings.ToList();
            foreach (var finding in next)
            {
                if (!ScriptDriftKinds.IsDrift(finding.Kind))
                {
                    throw VideoEditException.Validation($"Finding {finding.Id} is not a script-drift finding.");
                }
            }

            var existing = project.Findings.Where(finding => ScriptDriftKinds.IsDrift(finding.Kind)).ToList();
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fresh in next)
            {
                var match = existing.FirstOrDefault(item => SameDrift(item, fresh));
                if (match is null)
                {
                    continue;
                }

                fresh.Id = match.Id;
                fresh.Decision = match.Decision;
                fresh.Note = match.Note;
                reserved.Add(match.Id);
            }

            var keep = existing.Where(item =>
                !string.IsNullOrEmpty(item.Decision)
                && item.Decision != DecisionStates.Pending
                && next.All(fresh => !SameDrift(item, fresh))).ToList();
            foreach (var kept in keep)
            {
                reserved.Add(kept.Id);
            }

            foreach (var fresh in next)
            {
                if (reserved.Contains(fresh.Id)
                    && existing.Any(item => item.Id == fresh.Id && SameDrift(item, fresh)))
                {
                    continue;
                }

                fresh.Id = NextDriftId(reserved);
                reserved.Add(fresh.Id);
            }

            var merged = next.Concat(keep).ToList();
            if (existing.Count == merged.Count
                && existing.All(item => merged.Any(fresh =>
                    SameDrift(item, fresh)
                    && item.Id == fresh.Id
                    && item.Decision == fresh.Decision
                    && item.Note == fresh.Note
                    && item.Severity == fresh.Severity
                    && item.ScriptTokenIndex == fresh.ScriptTokenIndex)))
            {
                return false;
            }

            project.Findings.RemoveAll(finding => ScriptDriftKinds.IsDrift(finding.Kind));
            project.Findings.AddRange(merged);
            return true;
        });
    }

    private static string NextDriftId(HashSet<string> reserved)
    {
        for (var n = 1; ; n++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"drift-{n:00}");
            if (!reserved.Contains(id))
            {
                return id;
            }
        }
    }

    private static bool SameDrift(SourceFinding left, SourceFinding right) =>
        left.Kind == right.Kind
        && left.Start == right.Start
        && left.End == right.End
        && left.ScriptText == right.ScriptText
        && left.SpokenText == right.SpokenText;

    public ProjectDocument BindGeneratedNarration(
        string assetId,
        string audioPath,
        string fingerprint,
        double durationSeconds,
        int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(assetId)
            || string.IsNullOrWhiteSpace(audioPath)
            || string.IsNullOrWhiteSpace(fingerprint)
            || durationSeconds <= 0)
        {
            throw VideoEditException.Validation("A generated scene must bind a measured narration derivative.");
        }

        return MutateComposition(expectedRevision, project =>
        {
            var scene = project.Scenes.FirstOrDefault(item => item.IsGenerated);
            var sameScene = scene is not null
                && string.Equals(scene.NarrationAssetId, assetId, StringComparison.Ordinal)
                && string.Equals(scene.NarrationPath, audioPath, StringComparison.Ordinal)
                && string.Equals(scene.NarrationFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
                && scene.NarrationDurationSeconds == durationSeconds;
            var sameRecord = SameDerivative(project, assetId, audioPath, fingerprint, durationSeconds);
            if (sameScene && sameRecord)
            {
                return false;
            }

            RecordDerivative(project, assetId, audioPath, fingerprint, durationSeconds);
            if (scene is null || sameScene)
            {
                return true;
            }

            scene.NarrationAssetId = assetId;
            scene.NarrationPath = audioPath;
            scene.NarrationFingerprint = fingerprint;
            scene.NarrationDurationSeconds = durationSeconds;
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });
    }

    private static void RecordDerivative(
        ProjectDocument project,
        string assetId,
        string path,
        string fingerprint,
        double durationSeconds)
    {
        project.Narration.GeneratedAssetId = assetId;
        project.Narration.Derivative = new NarrationDerivative
        {
            Path = path,
            Fingerprint = fingerprint,
            DurationSeconds = durationSeconds,
        };
    }

    private static bool SameDerivative(
        ProjectDocument project,
        string assetId,
        string path,
        string fingerprint,
        double durationSeconds)
    {
        return string.Equals(project.Narration.GeneratedAssetId, assetId, StringComparison.Ordinal)
            && project.Narration.Derivative is { } derivative
            && string.Equals(derivative.Path, path, StringComparison.Ordinal)
            && string.Equals(derivative.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)
            && derivative.DurationSeconds == durationSeconds;
    }

    private ProjectDocument MutateComposition(int expectedRevision, Func<ProjectDocument, bool> mutate)
    {
        return _store.Mutate(expectedRevision, project =>
        {
            if (_mediaGate.IsExporting)
            {
                throw VideoEditException.Conflict("Composition-changing operations are blocked while an export is rendering.");
            }

            return mutate(project);
        });
    }

    public ProjectDocument AddMissingHostCuts(IReadOnlyList<HostCutProposal> proposals, int expectedRevision)
    {
        return MutateComposition(expectedRevision, project =>
        {
            var source = project.Sources[0];
            var scene = project.Scenes[0];
            var added = false;
            foreach (var proposal in proposals)
            {
                if (project.Findings.Any(item => item.Id == proposal.FindingId)
                    || scene.Edits.Any(item => item.Id == proposal.EditId))
                {
                    continue;
                }

                project.Findings.Add(new SourceFinding
                {
                    Id = proposal.FindingId,
                    SourceAssetId = source.Id,
                    SourceFingerprint = source.Fingerprint,
                    Start = proposal.Range.Start,
                    End = proposal.Range.End,
                    Kind = proposal.Kind,
                    TranscriptEvidence = proposal.TranscriptEvidence,
                    Reason = proposal.Reason,
                    Confidence = proposal.Confidence,
                });
                scene.Edits.Add(new SceneEdit
                {
                    Id = proposal.EditId,
                    SceneId = scene.Id,
                    SourceAssetId = source.Id,
                    SourceFingerprint = source.Fingerprint,
                    FindingId = proposal.FindingId,
                    ProposalRevision = 1,
                    Start = proposal.Range.Start,
                    End = proposal.Range.End,
                    Kind = proposal.Kind,
                    RemovedText = proposal.TranscriptEvidence,
                    Reason = proposal.Reason,
                    Confidence = proposal.Confidence,
                    Decision = DecisionStates.Pending,
                });
                added = true;
            }

            if (!added)
            {
                return false;
            }

            scene.Edits = scene.Edits.OrderBy(edit => edit.Start).ToList();
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });
    }

    public ProjectDocument AddMissingDriftCuts(int expectedRevision)
    {
        return MutateComposition(expectedRevision, project =>
        {
            var scene = project.Scenes[0];
            var added = MergeRetakeCuts(project, scene);
            foreach (var finding in project.Findings.Where(item => ScriptDriftKinds.IsCutProposal(item.Kind)))
            {
                if (finding.Decision == DecisionStates.Rejected
                    || scene.Edits.Any(edit =>
                        edit.FindingId == finding.Id
                        || string.Equals(edit.Id, "e-" + finding.Id, StringComparison.Ordinal)
                        || (edit.Decision != DecisionStates.Rejected
                            && edit.SourceAssetId == finding.SourceAssetId
                            && edit.Range.Overlaps(finding.Range)))
                    || CoveredByExistingCut(scene, finding))
                {
                    continue;
                }

                scene.Edits.Add(new SceneEdit
                {
                    Id = "e-" + finding.Id,
                    SceneId = scene.Id,
                    SourceAssetId = finding.SourceAssetId,
                    SourceFingerprint = finding.SourceFingerprint,
                    FindingId = finding.Id,
                    ProposalRevision = 1,
                    Start = finding.Start,
                    End = finding.End,
                    Kind = finding.Kind,
                    RemovedText = finding.SpokenText ?? finding.TranscriptEvidence,
                    Reason = finding.Reason,
                    Confidence = finding.Confidence,
                    Decision = DecisionStates.Pending,
                });
                added = true;
            }

            if (!added)
            {
                return false;
            }

            scene.Edits = scene.Edits.OrderBy(edit => edit.Start).ToList();
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });
    }

    public ProjectDocument DismissFinding(string findingId, int expectedRevision)
    {
        return _store.Mutate(expectedRevision, project =>
        {
            var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
                ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
            if (finding.Kind != ScriptDriftKinds.Substitution)
            {
                throw VideoEditException.Validation("Only substitutions can be dismissed as spoken.");
            }

            if (finding.Decision == DecisionStates.Rejected)
            {
                return false;
            }

            finding.Decision = DecisionStates.Rejected;
            return true;
        });
    }

    public ProjectDocument FlagFinding(string findingId, int expectedRevision)
    {
        return _store.Mutate(expectedRevision, project =>
        {
            var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
                ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
            if (!ScriptDriftKinds.IsDrift(finding.Kind))
            {
                throw VideoEditException.Validation("Only script drifts can be flagged.");
            }

            if (finding.Decision == DecisionStates.Flagged)
            {
                return false;
            }

            finding.Decision = DecisionStates.Flagged;
            return true;
        });
    }

    public ProjectDocument SaveFindingNote(string findingId, string? note, int expectedRevision)
    {
        var current = _store.Load();
        var existing = current.Findings.FirstOrDefault(item => item.Id == findingId)
            ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
        var next = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var changesComposition = FindingNoteChangesComposition(current, existing, next);
        bool Mutate(ProjectDocument project)
        {
            var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
                ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
            if (string.Equals(finding.Note, next, StringComparison.Ordinal))
            {
                return false;
            }

            finding.Note = next;
            if (next is not null && ScriptDriftKinds.IsDrift(finding.Kind))
            {
                ApplyFindingNote(project, finding);
            }

            return true;
        }

        var updated = changesComposition
            ? MutateComposition(expectedRevision, Mutate)
            : _store.Mutate(expectedRevision, Mutate);

        var analysis = Path.Combine(_store.ProjectDirectory, "edit", "analysis");
        if (File.Exists(Path.Combine(analysis, VadSpeechMap.FileName))
            && File.Exists(Path.Combine(analysis, EditingPolicy.DiagnosisFileName)))
        {
            updated = SnapPendingEdits(updated.Revision);
        }

        return updated;
    }

    public ProjectDocument ProposeClauseCut(string findingId, int expectedRevision)
    {
        var updated = MutateComposition(expectedRevision, project =>
        {
            var finding = project.Findings.FirstOrDefault(item => item.Id == findingId)
                ?? throw VideoEditException.NotFound($"Finding {findingId} was not found.");
            if (!ScriptDriftKinds.IsDrift(finding.Kind))
            {
                throw VideoEditException.Validation("Only script drifts can become clause cuts.");
            }

            var scene = project.Scenes.FirstOrDefault(item =>
                    item.Selections.Any(selection =>
                        selection.SourceAssetId == finding.SourceAssetId
                        && selection.Range.Contains(finding.Range)))
                ?? throw VideoEditException.Validation(
                    $"Finding {findingId} is not inside a selected range of any scene.");
            if (HasOpenCut(project, finding)
                || scene.Edits.Any(edit =>
                    string.Equals(edit.Id, "e-" + finding.Id, StringComparison.Ordinal)))
            {
                return false;
            }

            var range = ClauseRange(_store.ProjectDirectory, project, finding);
            if (OverlapsAny(scene, finding.SourceAssetId, range))
            {
                range = finding.Range;
            }

            if (OverlapsAny(scene, finding.SourceAssetId, range))
            {
                throw VideoEditException.Validation(
                    $"Clause cut for {findingId} overlaps an existing edit.");
            }

            scene.Edits.Add(new SceneEdit
            {
                Id = "e-" + finding.Id,
                SceneId = scene.Id,
                SourceAssetId = finding.SourceAssetId,
                SourceFingerprint = finding.SourceFingerprint,
                FindingId = finding.Id,
                ProposalRevision = 1,
                Start = range.Start,
                End = range.End,
                Kind = EditKindFor(finding),
                RemovedText = finding.SpokenText ?? finding.TranscriptEvidence,
                Reason = finding.Reason,
                Confidence = finding.Confidence,
                Decision = DecisionStates.Pending,
            });
            scene.Edits = scene.Edits.OrderBy(edit => edit.Start).ToList();
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });

        var analysis = Path.Combine(_store.ProjectDirectory, "edit", "analysis");
        if (File.Exists(Path.Combine(analysis, VadSpeechMap.FileName))
            && File.Exists(Path.Combine(analysis, EditingPolicy.DiagnosisFileName)))
        {
            updated = SnapPendingEdits(updated.Revision);
        }

        return updated;
    }

    public static TimeRange ClauseRange(
        string projectDirectory,
        ProjectDocument project,
        SourceFinding finding)
    {
        var host = project.Sources.FirstOrDefault(source => source.Kind != "image");
        if (host is null)
        {
            return finding.Range;
        }

        var wordsPath = SourceAnalysis.TryWordsPath(
            SourceAnalysis.Directory(projectDirectory, host, project.Sources));
        if (wordsPath is null)
        {
            return finding.Range;
        }

        return WhisperWords.ExpandToSentence(WhisperWords.Parse(File.ReadAllText(wordsPath)), finding.Range);
    }

    private static bool OverlapsAny(Scene scene, string sourceAssetId, TimeRange range) =>
        scene.Edits.Any(edit =>
            edit.Decision != DecisionStates.Rejected
            && edit.SourceAssetId == sourceAssetId
            && edit.Range.Overlaps(range));

    public static bool HasOpenCut(ProjectDocument project, SourceFinding finding) =>
        project.Scenes.Any(scene => scene.Edits.Any(edit =>
            edit.FindingId == finding.Id
            && edit.Decision != DecisionStates.Rejected));

    public static string NextPickupId(ProjectDocument project)
    {
        var n = 1;
        while (project.Sources.Any(source => source.Id == $"pickup-{n}"))
        {
            n++;
        }

        return $"pickup-{n}";
    }

    public static string BindPickupFile(string projectDirectory, string sourceId, string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw VideoEditException.Validation($"Source file is missing: {path}");
        }

        var pickupsDir = Path.GetFullPath(Path.Combine(projectDirectory, "sources", "pickups"));
        Directory.CreateDirectory(pickupsDir);
        if (full.StartsWith(pickupsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return full;
        }

        var extension = Path.GetExtension(full);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".mov";
        }

        var dest = Path.Combine(pickupsDir, sourceId + extension);
        File.Copy(full, dest, overwrite: false);
        return dest;
    }

    private static string EditKindFor(SourceFinding finding) =>
        finding.Kind switch
        {
            ScriptDriftKinds.Insertion => EditKinds.Insertion,
            ScriptDriftKinds.Repeat => EditKinds.Repeat,
            ScriptDriftKinds.Substitution => EditKinds.Substitution,
            ScriptDriftKinds.Retake => EditKinds.Retake,
            _ => EditKinds.Flub,
        };

    private bool MergeRetakeCuts(ProjectDocument project, Scene scene)
    {
        var words = LoadWords(project, project.Sources[0].Id);
        var added = false;
        var drifts = project.Findings
            .Where(item => ScriptDriftKinds.IsDrift(item.Kind))
            .OrderBy(item => item.Start)
            .ToList();
        foreach (var substitution in drifts.Where(item => item.Kind == ScriptDriftKinds.Substitution))
        {
            var insertion = RetakeDetector.NextInsertion(drifts, substitution);
            if (!RetakeDetector.TryPropose(substitution, insertion, words, out var retake)
                || insertion is null)
            {
                continue;
            }

            if (ApplyRetakeCut(scene, insertion, retake, substitution))
            {
                added = true;
            }
        }

        return added;
    }

    private bool FindingNoteChangesComposition(
        ProjectDocument project,
        SourceFinding finding,
        string? note)
    {
        if (string.IsNullOrWhiteSpace(note) || !ScriptDriftKinds.IsDrift(finding.Kind))
        {
            return false;
        }

        var words = LoadWords(project, finding.SourceAssetId);
        var proposal = EditNoteReader.ReadFinding(finding, note, words);
        if (!proposal.Understood)
        {
            return false;
        }

        var insertion = RetakeDetector.NextInsertion(project.Findings, finding);
        var retake = RetakeDetector.TryPropose(finding, insertion, words, out var detected)
            ? detected
            : null;
        var range = retake is not null && proposal.Range == finding.Range
            ? retake.Range
            : proposal.Range;
        return range != finding.Range
            || proposal.Kind != EditKinds.Retake
            || retake is not null;
    }

    private void ApplyFindingNote(ProjectDocument project, SourceFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.Note))
        {
            return;
        }

        var scene = project.Scenes.FirstOrDefault(item =>
                item.Selections.Any(selection =>
                    selection.SourceAssetId == finding.SourceAssetId
                    && selection.Range.Contains(finding.Range)))
            ?? project.Scenes[0];
        var words = LoadWords(project, finding.SourceAssetId);
        var proposal = EditNoteReader.ReadFinding(finding, finding.Note, words);
        var insertion = RetakeDetector.NextInsertion(project.Findings, finding);
        var retake = RetakeDetector.TryPropose(finding, insertion, words, out var detected)
            ? detected
            : null;
        if (!proposal.Understood)
        {
            return;
        }

        var range = retake is not null && proposal.Range == finding.Range
            ? retake.Range
            : proposal.Range;
        if (range == finding.Range && proposal.Kind == EditKinds.Retake && retake is null)
        {
            return;
        }

        var host = insertion is not null && range.Overlaps(insertion.Range) ? insertion : finding;
        ApplyRetakeCut(
            scene,
            host,
            new RetakeProposal(
                range,
                retake?.Phrase ?? finding.ScriptText ?? "",
                proposal.Reason,
                finding.SpokenText ?? finding.TranscriptEvidence),
            finding,
            finding.Note);
    }

    private static bool ApplyRetakeCut(
        Scene scene,
        SourceFinding host,
        RetakeProposal retake,
        SourceFinding? alsoCovered,
        string? note = null)
    {
        var existing = scene.Edits.FirstOrDefault(edit =>
            edit.FindingId == host.Id
            || string.Equals(edit.Id, "e-" + host.Id, StringComparison.Ordinal)
            || (alsoCovered is not null
                && (edit.FindingId == alsoCovered.Id
                    || string.Equals(edit.Id, "e-" + alsoCovered.Id, StringComparison.Ordinal))));
        if (existing is not null
            && existing.Kind == EditKinds.Retake
            && alsoCovered is not null
            && existing.Start <= alsoCovered.Start + 0.4
            && existing.End >= retake.Range.End - 0.05
            && existing.End <= host.End + 0.05)
        {
            return false;
        }

        if (existing is null)
        {
            if (scene.Edits.Any(edit =>
                    edit.Decision != DecisionStates.Rejected
                    && edit.SourceAssetId == host.SourceAssetId
                    && edit.Range.Overlaps(retake.Range)))
            {
                return false;
            }

            scene.Edits.Add(new SceneEdit
            {
                Id = "e-" + host.Id,
                SceneId = scene.Id,
                SourceAssetId = host.SourceAssetId,
                SourceFingerprint = host.SourceFingerprint,
                FindingId = host.Id,
                ProposalRevision = note is null ? 1 : 2,
                Start = retake.Range.Start,
                End = retake.Range.End,
                Kind = EditKinds.Retake,
                RemovedText = retake.RemovedText,
                Reason = retake.Reason,
                Confidence = host.Confidence,
                Decision = DecisionStates.Pending,
                Note = note ?? host.Note,
            });
            scene.Edits = scene.Edits.OrderBy(edit => edit.Start).ToList();
            scene.Acceptance = null;
            return true;
        }

        var same = Math.Abs(existing.Start - retake.Range.Start) < 0.000001
            && Math.Abs(existing.End - retake.Range.End) < 0.000001
            && existing.Kind == EditKinds.Retake
            && string.Equals(existing.Reason, retake.Reason, StringComparison.Ordinal)
            && (note is null || string.Equals(existing.Note, note, StringComparison.Ordinal));
        if (same)
        {
            return false;
        }

        existing.Start = retake.Range.Start;
        existing.End = retake.Range.End;
        existing.Kind = EditKinds.Retake;
        existing.RemovedText = retake.RemovedText;
        existing.Reason = retake.Reason;
        if (note is not null)
        {
            existing.Note = note;
        }

        existing.ProposalRevision += 1;
        if (existing.Decision == DecisionStates.Flagged)
        {
            existing.DecisionProposalRevision = existing.ProposalRevision;
        }
        else
        {
            existing.Decision = DecisionStates.Pending;
            existing.DecisionProposalRevision = null;
        }

        scene.Acceptance = null;
        return true;
    }

    public static bool CoveredByExistingCut(Scene scene, SourceFinding finding) =>
        scene.Edits.Any(edit =>
            edit.Decision != DecisionStates.Rejected
            && edit.SourceAssetId == finding.SourceAssetId
            && edit.Range.Contains(finding.Range));

    public static bool CoveredByOpenCut(ProjectDocument project, SourceFinding finding) =>
        project.Scenes.Any(scene => CoveredByExistingCut(scene, finding));

    private static bool OverlapsSibling(Scene scene, SceneEdit edit, TimeRange range) =>
        scene.Edits.Any(other =>
            other.Id != edit.Id
            && other.SourceAssetId == edit.SourceAssetId
            && other.Decision != DecisionStates.Rejected
            && other.Range.Overlaps(range));

    public static bool CoveredByApprovedCut(ProjectDocument project, SourceFinding finding) =>
        project.Scenes.Any(scene =>
            scene.Edits.Any(edit =>
                edit.SourceAssetId == finding.SourceAssetId
                && edit.Decision == DecisionStates.Approved
                && edit.DecisionMatchesProposal
                && edit.Range.Contains(finding.Range)));

    public ProjectDocument AddSceneEdits(
        string sceneId,
        IReadOnlyList<NewSceneEdit> edits,
        IReadOnlyList<SourceFinding>? findings,
        int expectedRevision)
    {
        return MutateComposition(expectedRevision, project =>
        {
            var scene = project.Scenes.FirstOrDefault(item => item.Id == sceneId)
                ?? throw VideoEditException.NotFound($"Scene {sceneId} was not found.");
            var source = project.Sources[0];
            if (findings is not null)
            {
                foreach (var finding in findings)
                {
                    if (project.Findings.Any(item => item.Id == finding.Id))
                    {
                        throw VideoEditException.Validation($"Finding {finding.Id} already exists.");
                    }

                    finding.SourceAssetId = source.Id;
                    finding.SourceFingerprint = source.Fingerprint;
                    project.Findings.Add(finding);
                }
            }

            foreach (var add in edits)
            {
                if (scene.Edits.Any(item => item.Id == add.Id))
                {
                    throw VideoEditException.Validation($"Edit {add.Id} already exists.");
                }

                scene.Edits.Add(new SceneEdit
                {
                    Id = add.Id,
                    SceneId = scene.Id,
                    SourceAssetId = source.Id,
                    SourceFingerprint = source.Fingerprint,
                    FindingId = add.FindingId,
                    ProposalRevision = 1,
                    Start = add.Start,
                    End = add.End,
                    Kind = add.Kind,
                    RemovedText = add.RemovedText,
                    Reason = add.Reason,
                    Confidence = add.Confidence,
                    Decision = add.Decision,
                    NoCleanBoundary = add.NoCleanBoundary,
                });
            }

            scene.Edits = scene.Edits.OrderBy(edit => edit.Start).ToList();
            scene.Acceptance = null;
            project.FinalApproval = null;
            return true;
        });
    }

    public static bool IsReproposalRequest(SceneEdit edit) =>
        edit.Decision == DecisionStates.Rejected && !string.IsNullOrWhiteSpace(edit.Note);

    public static bool ShowsReproposalNote(SceneEdit edit) =>
        !string.IsNullOrWhiteSpace(edit.Note)
        && edit.Decision == DecisionStates.Pending
        && edit.ProposalRevision > 1
        && !ShowsNoteNotUnderstood(edit);

    public static bool ShowsNoteNotUnderstood(SceneEdit edit) =>
        !string.IsNullOrWhiteSpace(edit.Note)
        && edit.Decision == DecisionStates.Pending
        && edit.Reason.Contains(EditNoteReader.NotUnderstood, StringComparison.OrdinalIgnoreCase);

    private void ReproposeFromNote(ProjectDocument project, Scene scene, SceneEdit edit)
    {
        ApplyNoteProposal(project, scene, edit);
        edit.ProposalRevision += 1;
        edit.Decision = DecisionStates.Pending;
        edit.DecisionProposalRevision = null;
        scene.Acceptance = null;
        project.FinalApproval = null;
    }

    private bool ApplyNoteProposal(ProjectDocument project, Scene scene, SceneEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.Note))
        {
            return false;
        }

        var proposal = EditNoteReader.Read(edit, edit.Note, LoadWords(project, edit.SourceAssetId));
        edit.Reason = proposal.Reason;
        if (!proposal.Understood)
        {
            return false;
        }

        var clamped = ClampToSelection(scene, edit, proposal.Range);
        edit.Start = clamped.Start;
        edit.End = clamped.End;
        if (proposal.Kind is string kind)
        {
            edit.Kind = kind;
        }

        edit.Crossfade = proposal.Crossfade;
        edit.FadeSeconds = proposal.FadeSeconds;
        return true;
    }

    private static TimeRange ClampToSelection(Scene scene, SceneEdit edit, TimeRange proposed)
    {
        var selection = scene.Selections.FirstOrDefault(item =>
                item.SourceAssetId == edit.SourceAssetId && item.Range.Contains(edit.Range))
            ?? scene.Selections.FirstOrDefault(item =>
                string.Equals(item.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
                && item.AudioRange is TimeRange audio
                && audio.Contains(edit.Range));
        if (selection is null)
        {
            return edit.Range;
        }

        var bounds = string.Equals(selection.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
            && selection.AudioRange is TimeRange audioBounds
            ? audioBounds
            : selection.Range;
        var start = Math.Max(bounds.Start, proposed.Start);
        var end = Math.Min(bounds.End, proposed.End);
        return end > start ? new TimeRange(start, end) : edit.Range;
    }

    private IReadOnlyList<SpokenWord> LoadWords(ProjectDocument project, string sourceAssetId)
    {
        var source = project.Sources.FirstOrDefault(item => item.Id == sourceAssetId);
        if (source is null)
        {
            return [];
        }

        var path = SourceAnalysis.TryWordsPath(
            SourceAnalysis.Directory(_store.ProjectDirectory, source, project.Sources));
        return path is null ? [] : WhisperWords.Parse(File.ReadAllText(path));
    }

    private EditingPolicy LoadPolicy() =>
        EditingPolicy.LoadOrDefault(Path.Combine(_store.ProjectDirectory, "edit", EditingPolicy.FileName));

    private static void ExtendHostPicture(ProjectDocument project, SourceSelection selection, Scene scene)
    {
        var extra = CompositionResolver.RetainedAudioDuration(selection, scene, approvedOnly: false)
            - CompositionResolver.EffectivePictureDuration(selection, scene, approvedOnly: false);
        if (extra <= 0.02)
        {
            return;
        }

        var source = project.Sources.FirstOrDefault(item => item.Id == selection.SourceAssetId);
        var limit = source?.DurationSeconds ?? selection.End + extra;
        var oldEnd = selection.End;
        var newEnd = Math.Min(limit, selection.End + extra);
        if (newEnd <= selection.End + 0.02)
        {
            throw VideoEditException.Validation(
                "Extend picture cannot grow the host window; the source ends here. Trim the pickup or reject it.");
        }

        ReconcileFollowing(scene, selection, oldEnd, newEnd);
        selection.End = newEnd;
        if (selection.ReplaceEnd is double leftover && leftover < newEnd)
        {
            selection.ReplaceEnd = newEnd;
        }
    }

    private static void TrimPickupToHost(ProjectDocument project, Scene scene, SourceSelection selection)
    {
        if (selection.AudioRange is not TimeRange audio)
        {
            return;
        }

        var keeps = CompositionResolver.RetainedAudioKeeps(selection, scene, approvedOnly: false);
        var donor = keeps is null ? new List<TimeRange> { audio } : keeps.ToList();
        var fitted = RangeMath.CapDuration(donor, selection.Range.Duration);
        var added = donor
            .SelectMany(keep => RangeMath.Subtract(keep, fitted))
            .Where(item => item.Duration > 0.02)
            .ToList();
        var pickup = project.Sources.First(source => source.Id == selection.AudioSourceAssetId);
        foreach (var cut in added)
        {
            scene.Edits.RemoveAll(edit =>
                string.Equals(edit.SourceAssetId, pickup.Id, StringComparison.Ordinal)
                && edit.Decision == DecisionStates.Rejected
                && edit.Range.Overlaps(cut));
            if (scene.Edits.Any(edit =>
                    string.Equals(edit.SourceAssetId, pickup.Id, StringComparison.Ordinal)
                    && edit.Decision != DecisionStates.Rejected
                    && edit.Range.Overlaps(cut)))
            {
                continue;
            }

            var id = NextTrimId(scene, cut.Start);
            scene.Edits.Add(new SceneEdit
            {
                Id = id,
                SceneId = scene.Id,
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                Start = cut.Start,
                End = cut.End,
                Kind = EditKinds.Pause,
                Reason = "Trim pickup audio to the host picture.",
                ProposalRevision = 1,
                Decision = DecisionStates.Approved,
                DecisionProposalRevision = 1,
            });
        }
    }

    private static string NextTrimId(Scene scene, double start)
    {
        var prefix = $"e-pickup-trim-{start:0.###}";
        if (scene.Edits.All(edit => edit.Id != prefix))
        {
            return prefix;
        }

        var n = 2;
        while (scene.Edits.Any(edit => edit.Id == $"{prefix}-{n}"))
        {
            n++;
        }

        return $"{prefix}-{n}";
    }

    private static void RejectPickup(Scene scene, SourceSelection selection)
    {
        if (selection.AudioRange is TimeRange audio && !string.IsNullOrEmpty(selection.AudioSourceAssetId))
        {
            scene.Edits.RemoveAll(edit =>
                string.Equals(edit.SourceAssetId, selection.AudioSourceAssetId, StringComparison.Ordinal)
                && audio.Overlaps(edit.Range));
        }

        var restoreEnd = selection.ReplaceEnd ?? selection.End;
        if (restoreEnd > selection.End + 0.02)
        {
            ReconcileFollowing(scene, selection, selection.End, restoreEnd);
            selection.End = restoreEnd;
        }

        selection.AudioSourceAssetId = null;
        selection.AudioSourceFingerprint = null;
        selection.AudioStart = null;
        selection.AudioEnd = null;
        selection.AudioGainDb = null;
        selection.NeedsCutaway = false;
        selection.ReplaceEnd = null;
    }

    private static void ReconcileFollowing(Scene scene, SourceSelection selection, double from, double to)
    {
        var followers = scene.Selections
            .Where(item =>
                !ReferenceEquals(item, selection)
                && item.SourceAssetId == selection.SourceAssetId
                && item.Start < to
                && item.End > from - 0.000001)
            .OrderBy(item => item.Start)
            .ToList();
        foreach (var next in followers)
        {
            if (next.HasAudioOverlay)
            {
                throw VideoEditException.Validation(
                    $"The change would overlap the following pickup at {next.Start:0.###}s. Trim this pickup or reject it first.");
            }

            if (next.Start < from - 0.000001)
            {
                throw VideoEditException.Validation(
                    $"The change would overlap host material already selected at {next.Start:0.###}s.");
            }

            next.Start = to;
            if (next.Range.Duration <= 0.02)
            {
                scene.Selections.Remove(next);
            }
        }
    }

    private static void EnsureTimingFits(Scene scene, SourceSelection selection)
    {
        if (!CompositionResolver.TimingMismatch(selection, scene, approvedOnly: false))
        {
            return;
        }

        var retained = CompositionResolver.RetainedAudioDuration(selection, scene, approvedOnly: false);
        var picture = CompositionResolver.EffectivePictureDuration(selection, scene, approvedOnly: false);
        throw VideoEditException.Validation(
            $"Pickup still does not fit the host picture ({retained:0.###}s audio vs {picture:0.###}s picture). Trim the pickup or reject it.");
    }

    private void FreezeBoundRender(ProjectDocument project)
    {
        var live = project.LiveRender ?? RenderIdentity.FromLive(project, _store.ProjectDirectory);
        project.BoundRender = new BoundRenderSettings
        {
            SceneBoundaryGapS = live.SceneBoundaryGapS,
            RoomToneFillMs = live.RoomToneFillMs,
            RoomPath = live.RoomPath,
            RoomFingerprint = live.RoomFingerprint,
            RoomGainDb = live.RoomGainDb,
        };
        project.LiveRender = live;
    }

    private static void InvalidateAcceptance(
        ProjectDocument project,
        Scene scene,
        string beforeHash,
        string afterHash)
    {
        if (!string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
        {
            scene.Acceptance = null;
            project.FinalApproval = null;
        }
    }

    private static Scene FindSceneByEdit(ProjectDocument project, string editId)
    {
        foreach (var scene in project.Scenes)
        {
            if (scene.Edits.Any(edit => edit.Id == editId))
            {
                return scene;
            }
        }

        throw VideoEditException.NotFound($"Edit {editId} was not found.");
    }

    private static double LowerConfidence(double confidence) =>
        Math.Round(Math.Max(0.25, confidence * 0.7), 2);
}

public sealed record ProposalTimeUpdate(
    string EditId,
    double Start,
    double End,
    string? Reason = null,
    string? RemovedText = null,
    string? Decision = null,
    bool? NoCleanBoundary = null,
    double? Confidence = null,
    bool Force = false,
    bool? Crossfade = null,
    double? FadeSeconds = null);

public sealed record NewSceneEdit(
    string Id,
    string? FindingId,
    double Start,
    double End,
    string Kind,
    string RemovedText,
    string Reason,
    double Confidence,
    string Decision = DecisionStates.Pending,
    bool NoCleanBoundary = true);

using System.Globalization;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class PickupMixService(
    ProjectStore store,
    IProcessRunner runner,
    VadAnalyzer analyzer,
    VideoEditOptions options)
{
    public async Task<ProjectDocument> PrepareAsync(int expectedRevision, CancellationToken token)
    {
        var project = store.Load();
        var policy = EditingPolicy.LoadOrDefault(Path.Combine(store.ProjectDirectory, "edit", EditingPolicy.FileName));
        var host = project.Sources.First(source => source.Kind != "image");
        var hostWords = LoadWords(SourceAnalysis.WordsPath(SourceAnalysis.Directory(store.ProjectDirectory, host, project.Sources)));
        var hostMap = await analyzer.EnsureMapAsync(host, project.Sources, policy, token);
        var pickups = project.Sources.Where(source => source.Kind != "image" && source.Id != host.Id).ToList();
        var pickupWords = new Dictionary<string, IReadOnlyList<SpokenWord>>(StringComparer.Ordinal);
        var pickupSilences = new Dictionary<string, IReadOnlyList<SilenceInterval>>(StringComparer.Ordinal)
        {
            [host.Id] = hostMap.ToSilences(host.DurationSeconds),
        };
        foreach (var pickup in pickups)
        {
            var map = await analyzer.EnsureMapAsync(pickup, project.Sources, policy, token);
            pickupWords[pickup.Id] = LoadWords(SourceAnalysis.WordsPath(SourceAnalysis.Directory(store.ProjectDirectory, pickup, project.Sources)));
            pickupSilences[pickup.Id] = map.ToSilences(pickup.DurationSeconds);
        }

        var widened = project.Scenes
            .Select(scene => (
                scene.Id,
                Overlays: WidenDetailed(scene, hostWords, pickupWords, pickupSilences, policy)))
            .ToList();
        if (widened.All(item => item.Overlays.Count == 0))
        {
            return project;
        }

        var gains = new Dictionary<(string SceneId, double Start, double End), (double Gain, bool Cutaway)>();
        foreach (var (sceneId, overlays) in widened)
        {
            foreach (var selection in overlays.Select(item => item.Selection))
            {
                var pickup = project.Sources.First(source => source.Id == selection.AudioSourceAssetId);
                var hostWindow = WhisperWords.ExpandToSentence(hostWords, selection.Range);
                var pickupWindow = selection.AudioRange ?? hostWindow;
                var silences = pickupSilences[pickup.Id];
                var keeps = RangeMath.Subtract(
                    pickupWindow,
                    PickupBounds.PauseCutsInside(pickupWindow, silences, policy));
                var spoken = RangeMath.TotalDuration(keeps);
                var measure = keeps.OrderByDescending(item => item.Duration).FirstOrDefault();
                var hostLufs = await MeasureAsync(host.Path, hostWindow, token);
                var pickupLufs = await MeasureAsync(
                    pickup.Path,
                    measure.Duration > 0 ? measure : pickupWindow,
                    token);
                gains[(sceneId, selection.Start, selection.End)] = (
                    Math.Round(AudioLoudness.GainDb(pickupLufs, hostLufs), 2),
                    selection.Range.Duration >= 1.0
                        || selection.Range.Duration - spoken >= 0.9);
            }
        }

        return store.Mutate(expectedRevision, current =>
        {
            current.Findings = current.Findings
                .Where(item => !item.Id.StartsWith("pickup-retake-", StringComparison.Ordinal)
                    && !item.Id.StartsWith("pickup-timing-", StringComparison.Ordinal)
                    && !item.Id.StartsWith("pickup-pause-", StringComparison.Ordinal))
                .ToList();
            foreach (var (sceneId, overlays) in widened)
            {
                var scene = current.Scenes.First(item => item.Id == sceneId);
                scene.Edits = scene.Edits
                    .Where(item => !item.Id.StartsWith("e-pickup-retake-", StringComparison.Ordinal)
                        && !item.Id.StartsWith("e-pickup-pause-", StringComparison.Ordinal))
                    .ToList();
                var snaps = overlays.ToDictionary(item => (item.Selection.Start, item.Selection.End), item => item.Snap);
                ApplyOverlays(
                    scene,
                    overlays.Select(item => item.Selection).ToList(),
                    current,
                    pickupSilences,
                    policy,
                    snaps);
                foreach (var selection in scene.Selections.Where(item => item.HasAudioOverlay))
                {
                    if (gains.TryGetValue((sceneId, selection.Start, selection.End), out var prepared))
                    {
                        selection.AudioGainDb = prepared.Gain;
                        selection.NeedsCutaway = prepared.Cutaway;
                    }
                }
            }

            current.FinalApproval = null;
            return true;
        });
    }

    public static IReadOnlyList<SourceSelection> WidenOverlays(
        Scene scene,
        IReadOnlyList<SpokenWord> hostWords,
        IReadOnlyList<SpokenWord> pickupWords)
    {
        return WidenOverlays(
            scene,
            hostWords,
            new Dictionary<string, IReadOnlyList<SpokenWord>> { ["pickup-1"] = pickupWords },
            new Dictionary<string, IReadOnlyList<SilenceInterval>>(),
            EditingPolicy.Default);
    }

    public static IReadOnlyList<SourceSelection> WidenOverlays(
        Scene scene,
        IReadOnlyList<SpokenWord> hostWords,
        IReadOnlyDictionary<string, IReadOnlyList<SpokenWord>> pickupWordsBySource,
        IReadOnlyDictionary<string, IReadOnlyList<SilenceInterval>> silencesBySource,
        EditingPolicy policy) =>
        WidenDetailed(scene, hostWords, pickupWordsBySource, silencesBySource, policy)
            .Select(item => item.Selection)
            .ToList();

    private sealed record PreparedPickup(SourceSelection Selection, PickupSnap Snap);

    private static List<PreparedPickup> WidenDetailed(
        Scene scene,
        IReadOnlyList<SpokenWord> hostWords,
        IReadOnlyDictionary<string, IReadOnlyList<SpokenWord>> pickupWordsBySource,
        IReadOnlyDictionary<string, IReadOnlyList<SilenceInterval>> silencesBySource,
        EditingPolicy policy)
    {
        var overlays = new List<PreparedPickup>();
        foreach (var selection in scene.Selections.Where(item => item.HasAudioOverlay))
        {
            var pickupId = selection.AudioSourceAssetId ?? "";
            var pickupWords = pickupWordsBySource.TryGetValue(pickupId, out var words) ? words : [];
            var pickupSeed = selection.AudioRange ?? selection.Range;
            var pickupSentence = WhisperWords.SnapIntoPauses(
                pickupWords,
                WhisperWords.ExpandToSentence(pickupWords, pickupSeed));
            var aligned = WhisperWords.AlignSentence(
                hostWords,
                WordsIn(pickupWords, pickupSentence),
                selection.Range);
            var hostSentence = WhisperWords.CompleteSentence(hostWords, aligned);
            var spokenWords = WordsIn(pickupWords, pickupSentence);
            var lastWordEnd = spokenWords.Count > 0 ? spokenWords[^1].End : (double?)null;
            var snap = silencesBySource.TryGetValue(pickupId, out var silences)
                ? PickupBounds.Snap(pickupSentence, silences, policy, lastWordEnd, hostSentence.Duration, spokenWords)
                : new PickupSnap(
                    pickupSentence,
                    [],
                    [],
                    hostSentence.Duration > 0 && pickupSentence.Duration > hostSentence.Duration);
            var audio = PickupBounds.FoldLeadingPauses(snap.Range, snap.PauseCuts);
            var spoken = PickupBounds.SpokenDuration(audio, snap.PauseCuts);
            var host = PickupBounds.FitHost(hostSentence, spoken, policy);
            overlays.Add(new PreparedPickup(
                new SourceSelection
                {
                    SourceAssetId = selection.SourceAssetId,
                    SourceFingerprint = selection.SourceFingerprint,
                    Start = host.Start,
                    End = host.End,
                    ReplaceEnd = host.End < hostSentence.End ? hostSentence.End : null,
                    AudioSourceAssetId = selection.AudioSourceAssetId,
                    AudioSourceFingerprint = selection.AudioSourceFingerprint,
                    AudioStart = audio.Start,
                    AudioEnd = audio.End,
                },
                snap));
        }

        return BalanceSeams(
            overlays.OrderBy(item => item.Selection.Start).ToList(),
            silencesBySource,
            policy);
    }

    private static List<PreparedPickup> BalanceSeams(
        List<PreparedPickup> overlays,
        IReadOnlyDictionary<string, IReadOnlyList<SilenceInterval>> silencesBySource,
        EditingPolicy policy)
    {
        for (var i = 1; i < overlays.Count; i++)
        {
            var previous = overlays[i - 1].Selection;
            var current = overlays[i].Selection;
            var previousThrough = previous.ReplaceEnd ?? previous.End;
            if (previousThrough < current.Start - 0.001)
            {
                continue;
            }

            if (previous.AudioRange is not TimeRange previousAudio
                || current.AudioRange is not TimeRange currentAudio
                || current.AudioSourceAssetId is not string pickupId
                || !silencesBySource.TryGetValue(pickupId, out var silences))
            {
                continue;
            }

            var trail = PickupBounds.TrailingPad(previous.Range, previousAudio);
            var lead = PickupBounds.LeadingSilence(currentAudio, silences);
            var trim = PickupBounds.TrimLeading(lead, trail, policy.PauseTargetS);
            if (trim <= 0.02)
            {
                continue;
            }

            var audio = new TimeRange(currentAudio.Start + trim, currentAudio.End);
            var originalEnd = current.ReplaceEnd ?? current.End;
            var fitted = PickupBounds.FitHost(new TimeRange(current.Start, originalEnd), audio.Duration, policy);
            current.AudioStart = audio.Start;
            current.AudioEnd = audio.End;
            current.End = fitted.End;
            current.ReplaceEnd = fitted.End < originalEnd ? originalEnd : current.ReplaceEnd;
        }

        return overlays;
    }

    public static void FlattenOverlays(Scene scene)
    {
        if (scene.Selections.Count == 0 || scene.Selections.All(item => !item.HasAudioOverlay))
        {
            return;
        }

        var host = scene.Selections[0];
        scene.Selections =
        [
            new SourceSelection
            {
                SourceAssetId = host.SourceAssetId,
                SourceFingerprint = host.SourceFingerprint,
                Start = scene.Selections[0].Start,
                End = scene.Selections[^1].End,
            }
        ];
        scene.Acceptance = null;
    }

    public static void ApplyOverlays(Scene scene, IReadOnlyList<SourceSelection> overlays) =>
        ApplyOverlays(scene, overlays, project: null, silencesBySource: null, policy: null);

    public static void ApplyOverlays(
        Scene scene,
        IReadOnlyList<SourceSelection> overlays,
        ProjectDocument? project,
        IReadOnlyDictionary<string, IReadOnlyList<SilenceInterval>>? silencesBySource,
        EditingPolicy? policy,
        IReadOnlyDictionary<(double Start, double End), PickupSnap>? snaps = null)
    {
        if (overlays.Count == 0 || scene.Selections.Count == 0)
        {
            return;
        }

        var hostId = scene.Selections[0].SourceAssetId;
        var hostFingerprint = scene.Selections[0].SourceFingerprint;
        var start = scene.Selections[0].Start;
        var end = scene.Selections[^1].End;
        var hostSilences = silencesBySource is not null
            && silencesBySource.TryGetValue(hostId, out var loadedHost)
            ? loadedHost
            : [];
        var hostRemovals = scene.Edits
            .Where(edit =>
                edit.SourceAssetId == hostId
                && edit.Decision != DecisionStates.Rejected)
            .Select(edit => edit.Range)
            .ToList();
        var next = new List<SourceSelection>();
        var cursor = start;
        var ordered = overlays.OrderBy(item => item.Start).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var overlay = ordered[i];
            if (snaps is not null
                && snaps.TryGetValue((overlay.Start, overlay.End), out var prepared)
                && overlay.AudioRange is TimeRange current)
            {
                var folded = PickupBounds.FoldLeadingPauses(current, prepared.PauseCuts);
                overlay.AudioStart = folded.Start;
                overlay.AudioEnd = folded.End;
            }

            if (cursor < overlay.Start)
            {
                if (overlay.HasAudioOverlay
                    && overlay.AudioRange is TimeRange incoming
                    && overlay.AudioSourceAssetId is string incomingId
                    && silencesBySource is not null
                    && silencesBySource.TryGetValue(incomingId, out var incomingSilences)
                    && policy is not null)
                {
                    var hostTrail = PickupBounds.HostTrailingSilence(
                        cursor,
                        overlay.Start,
                        hostSilences,
                        hostRemovals);
                    var lead = PickupBounds.LeadingSilence(incoming, incomingSilences);
                    var trim = PickupBounds.TrimLeading(lead, hostTrail, policy.PauseTargetS);
                    if (trim > 0.02)
                    {
                        overlay.AudioStart = incoming.Start + trim;
                    }
                }

                next.Add(new SourceSelection
                {
                    SourceAssetId = hostId,
                    SourceFingerprint = hostFingerprint,
                    Start = cursor,
                    End = overlay.Start,
                });
            }

            next.Add(overlay);
            cursor = Math.Max(cursor, overlay.ReplaceEnd ?? overlay.End);
            if (overlay.HasAudioOverlay && policy is not null && hostSilences.Count > 0)
            {
                var trail = overlay.AudioRange is TimeRange audio
                    ? PickupBounds.TrailingPad(overlay.Range, audio)
                    : 0;
                var limit = i + 1 < ordered.Count ? ordered[i + 1].Start : end;
                cursor = PickupBounds.SkipLeadingSilence(
                    cursor,
                    limit,
                    hostSilences,
                    hostRemovals,
                    Math.Max(0, policy.PauseTargetS - trail));
            }
        }

        if (cursor < end)
        {
            next.Add(new SourceSelection
            {
                SourceAssetId = hostId,
                SourceFingerprint = hostFingerprint,
                Start = cursor,
                End = end,
            });
        }

        var kept = new List<SceneEdit>();
        foreach (var edit in scene.Edits)
        {
            var overlay = overlays.FirstOrDefault(item =>
                item.Range.Overlaps(edit.Range)
                || (item.ReplaceEnd is double replace
                    && replace > item.End
                    && new TimeRange(item.End, replace).Overlaps(edit.Range)));
            if (overlay is null)
            {
                kept.Add(edit);
                continue;
            }

            if (edit.Kind == EditKinds.Pause && project is not null)
            {
                kept.AddRange(TransferHostPause(edit, overlay, project, silencesBySource, policy ?? EditingPolicy.Default));
            }
        }

        if (project is not null)
        {
            foreach (var overlay in overlays)
            {
                PickupSnap? snap = null;
                snaps?.TryGetValue((overlay.Start, overlay.End), out snap);
                foreach (var edit in ProposePickupCuts(overlay, project, snap, policy ?? EditingPolicy.Default, scene.Id))
                {
                    if (kept.Any(item =>
                            string.Equals(item.SourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
                            && item.Range.Overlaps(edit.Range)))
                    {
                        continue;
                    }

                    kept.Add(edit);
                }
            }
        }

        scene.Selections = next;
        scene.Edits = kept
            .Where(edit => next.Any(selection =>
                (selection.SourceAssetId == edit.SourceAssetId && selection.Range.Contains(edit.Range))
                || (string.Equals(selection.AudioSourceAssetId, edit.SourceAssetId, StringComparison.Ordinal)
                    && selection.AudioRange is TimeRange audio
                    && audio.Contains(edit.Range))))
            .ToList();
        scene.Acceptance = null;
    }

    private static IEnumerable<SceneEdit> TransferHostPause(
        SceneEdit hostPause,
        SourceSelection overlay,
        ProjectDocument project,
        IReadOnlyDictionary<string, IReadOnlyList<SilenceInterval>>? silencesBySource,
        EditingPolicy policy)
    {
        var pickup = project.Sources.FirstOrDefault(source => source.Id == overlay.AudioSourceAssetId);
        if (pickup is null || overlay.AudioRange is not TimeRange audio)
        {
            yield break;
        }

        var findingId = $"pickup-from-{hostPause.Id}";
        TimeRange? cut = null;
        if (silencesBySource is not null && silencesBySource.TryGetValue(pickup.Id, out var silences))
        {
            var relative = overlay.Range.Duration <= 0
                ? audio.Start
                : audio.Start + (hostPause.Start - overlay.Start) / overlay.Range.Duration * audio.Duration;
            var matched = PickupBounds.PauseCutsInside(audio, silences, policy)
                .OrderBy(item => Math.Abs(item.Start - relative))
                .FirstOrDefault();
            if (matched.Duration > 0)
            {
                cut = matched;
            }
        }

        if (project.Findings.All(item => item.Id != findingId))
        {
            project.Findings.Add(new SourceFinding
            {
                Id = findingId,
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                Start = cut?.Start ?? audio.Start,
                End = cut?.End ?? audio.End,
                Kind = EditKinds.Pause,
                Reason = $"Host pause {hostPause.Id} fell inside the replaced range; keep that pacing on the pickup.",
                Confidence = hostPause.Confidence,
                TranscriptEvidence = hostPause.RemovedText,
            });
        }

        if (cut is not TimeRange pauseCut)
        {
            yield break;
        }

        yield return new SceneEdit
        {
            Id = $"e-{findingId}",
            SceneId = hostPause.SceneId,
            SourceAssetId = pickup.Id,
            SourceFingerprint = pickup.Fingerprint,
            FindingId = findingId,
            ProposalRevision = 1,
            Start = pauseCut.Start,
            End = pauseCut.End,
            Kind = EditKinds.Pause,
            RemovedText = hostPause.RemovedText,
            Reason = $"Transferred approved host pause {hostPause.Id} onto the pickup range.",
            Confidence = hostPause.Confidence,
            Decision = hostPause.Decision,
            DecisionProposalRevision = hostPause.DecisionProposalRevision,
        };
    }

    private static IEnumerable<SceneEdit> ProposePickupCuts(
        SourceSelection overlay,
        ProjectDocument project,
        PickupSnap? snap,
        EditingPolicy policy,
        string sceneId)
    {
        var pickup = project.Sources.FirstOrDefault(source => source.Id == overlay.AudioSourceAssetId);
        if (pickup is null || overlay.AudioRange is not TimeRange audio)
        {
            yield break;
        }

        foreach (var dropped in snap?.DroppedTakes ?? [])
        {
            AddFindingOnce(project, new SourceFinding
            {
                Id = $"pickup-retake-{dropped.Start:0.###}",
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                Start = dropped.Start,
                End = dropped.End,
                Kind = EditKinds.FalseStart,
                Reason = "Retake preamble before the kept pickup sentence.",
                Confidence = 0.9,
                TranscriptEvidence = dropped.ToString(),
            });
        }

        foreach (var flagged in snap?.FlaggedTakes ?? [])
        {
            AddFindingOnce(project, new SourceFinding
            {
                Id = $"pickup-retake-{flagged.Start:0.###}",
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                Start = flagged.Start,
                End = flagged.End,
                Kind = PickupFindingKinds.Retake,
                Reason = "Repeated words are too short to drop automatically. Confirm whether this is a retake.",
                Confidence = 0.6,
                TranscriptEvidence = flagged.ToString(),
            });
        }

        if (snap is { TimingMismatch: true } && overlay.AudioRange is TimeRange longAudio)
        {
            AddFindingOnce(project, new SourceFinding
            {
                Id = $"pickup-timing-{longAudio.Start:0.###}",
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                Start = longAudio.Start,
                End = longAudio.End,
                Kind = PickupFindingKinds.Timing,
                Reason = "Pickup speech is longer than the host picture. Extend the host, trim the pickup, or reject the pickup.",
                Confidence = 0.9,
                TranscriptEvidence = longAudio.ToString(),
            });
        }

        var cuts = new List<TimeRange>();
        if (snap is not null)
        {
            cuts.AddRange(snap.PauseCuts);
        }

        foreach (var finding in project.Findings.Where(item =>
                     string.Equals(item.SourceAssetId, pickup.Id, StringComparison.Ordinal)
                     && item.Kind == EditKinds.Pause
                     && audio.Overlaps(item.Range)
                     && item.Range.Duration > 0.05
                     && item.Range.Duration < audio.Duration - 0.05))
        {
            var clamped = new TimeRange(Math.Max(audio.Start, finding.Start), Math.Min(audio.End, finding.End));
            if (clamped.Duration > 0.05)
            {
                cuts.Add(clamped);
            }
        }

        foreach (var cut in MergeCuts(cuts))
        {
            var findingId = $"pickup-pause-{cut.Start:0.###}";
            if (project.Scenes.SelectMany(item => item.Edits).Any(item =>
                    item.FindingId == findingId
                    || (string.Equals(item.SourceAssetId, pickup.Id, StringComparison.Ordinal)
                        && item.Range.Overlaps(cut))))
            {
                continue;
            }

            AddFindingOnce(project, new SourceFinding
            {
                Id = findingId,
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                Start = cut.Start,
                End = cut.End,
                Kind = EditKinds.Pause,
                Reason = $"Pause of {cut.Duration + policy.PauseTargetS:0.##}s shortened to {policy.PauseTargetS:0.##}s on the pickup range.",
                Confidence = 0.85,
            });
            if (!audio.Contains(cut))
            {
                continue;
            }

            yield return new SceneEdit
            {
                Id = $"e-{findingId}",
                SceneId = sceneId,
                SourceAssetId = pickup.Id,
                SourceFingerprint = pickup.Fingerprint,
                FindingId = findingId,
                ProposalRevision = 1,
                Start = cut.Start,
                End = cut.End,
                Kind = EditKinds.Pause,
                Reason = $"Pause of {cut.Duration + policy.PauseTargetS:0.##}s shortened to {policy.PauseTargetS:0.##}s on the pickup range.",
                Confidence = 0.85,
                Decision = DecisionStates.Pending,
            };
        }
    }

    private static List<TimeRange> MergeCuts(IEnumerable<TimeRange> cuts)
    {
        var merged = new List<TimeRange>();
        foreach (var cut in cuts.Where(item => item.Duration > 0.05).OrderBy(item => item.Start))
        {
            if (merged.Count == 0 || cut.Start >= merged[^1].End - 0.000001)
            {
                merged.Add(cut);
                continue;
            }

            merged[^1] = new TimeRange(merged[^1].Start, Math.Max(merged[^1].End, cut.End));
        }

        return merged;
    }

    private static void AddFindingOnce(ProjectDocument project, SourceFinding finding)
    {
        if (project.Findings.Any(item => item.Id == finding.Id))
        {
            return;
        }

        project.Findings.Add(finding);
    }

    private static List<SourceSelection> MergeOverlays(List<SourceSelection> overlays)
    {
        var ordered = overlays.OrderBy(item => item.Start).ToList();
        var merged = new List<SourceSelection>();
        foreach (var overlay in ordered)
        {
            if (merged.Count == 0 || overlay.Start >= merged[^1].End - 0.000001)
            {
                merged.Add(overlay);
                continue;
            }

            var previous = merged[^1];
            previous.End = Math.Max(previous.End, overlay.End);
            previous.AudioStart = Math.Min(previous.AudioStart ?? overlay.AudioStart ?? 0, overlay.AudioStart ?? 0);
            previous.AudioEnd = Math.Max(previous.AudioEnd ?? overlay.AudioEnd ?? 0, overlay.AudioEnd ?? 0);
        }

        return merged;
    }

    private static IReadOnlyList<SpokenWord> WordsIn(IReadOnlyList<SpokenWord> words, TimeRange range) =>
        words.Where(word => word.Start < range.End && word.End > range.Start).ToList();

    private async Task<double> MeasureAsync(string path, TimeRange range, CancellationToken token)
    {
        var result = await runner.RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner",
                "-ss", N(range.Start),
                "-to", N(range.End),
                "-i", path,
                "-af", "ebur128",
                "-f", "null",
                "-",
            ],
            TimeSpan.FromMinutes(2),
            token);
        return AudioLoudness.ParseIntegrated(result.StandardError + result.StandardOutput);
    }

    private static IReadOnlyList<SpokenWord> LoadWords(string path) =>
        File.Exists(path) ? WhisperWords.Parse(File.ReadAllText(path)) : [];

    private static string N(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}

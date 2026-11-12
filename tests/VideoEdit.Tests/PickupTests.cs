using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class PickupTests
{
    [Fact]
    public void ResolverKeepsPickupOrderInsteadOfSortingByStart()
    {
        var project = MixedScene();
        var resolved = CompositionResolver.ResolveScene(project, project.Scenes[0]);

        Assert.Equal(
            [
                ("source-1", new TimeRange(0, 10)),
                ("pickup-1", new TimeRange(2, 2.62)),
                ("source-1", new TimeRange(10, 20)),
            ],
            resolved.KeepRanges.Select(item => (item.SourceAssetId, item.Range)));
        Assert.Equal(20.62, resolved.DurationSeconds, 3);
    }

    [Fact]
    public void HasherIncludesPickupIdentityAndListOrder()
    {
        var project = MixedScene();
        var first = CompositionHasher.SceneHash(project, project.Scenes[0]);
        project.Scenes[0].Selections.Reverse();
        var reversed = CompositionHasher.SceneHash(project, project.Scenes[0]);
        project.Scenes[0].Selections.Reverse();
        project.Sources[1].Fingerprint = new string('c', 64);
        var reprinted = CompositionHasher.SceneHash(project, project.Scenes[0]);

        Assert.NotEqual(first, reversed);
        Assert.NotEqual(first, reprinted);
    }

    [Fact]
    public void AddingAnUnusedSourceDoesNotClearAcceptance()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        service.RecordDecision("e-1", DecisionStates.Rejected, null, 1);
        var accepted = service.AcceptScene("scene-1", 2);
        var before = CompositionHasher.FullHash(accepted);

        var updated = service.AddSource(
            new SourceAsset
            {
                Id = "pickup-1",
                Path = "/tmp/pickup.mov",
                Fingerprint = new string('b', 64),
                DurationSeconds = 8,
            },
            accepted.Revision);

        Assert.Equal(before, CompositionHasher.FullHash(updated));
        Assert.True(CompositionResolver.SceneAcceptanceValid(
            updated,
            updated.Scenes[0],
            CompositionHasher.SceneHash(updated, updated.Scenes[0])));
        Assert.Equal(accepted.Revision + 1, updated.Revision);
    }

    [Fact]
    public void ApplyingAPickupSplitsTheHostAndDropsCoveredEdits()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Acceptance = new SceneAcceptance
        {
            CompositionHash = CompositionHasher.SceneHash(project, project.Scenes[0]),
        };
        project.FinalApproval = new FinalApproval { CompositionHash = CompositionHasher.FullHash(project) };
        store.SaveNew(project);
        service.AddSource(
            new SourceAsset
            {
                Id = "pickup-1",
                Path = "/tmp/pickup.mov",
                Fingerprint = new string('b', 64),
                DurationSeconds = 8,
            },
            1);

        var updated = service.ApplyPickup("pickup-1", 2, 2.62, 2, sceneId: "scene-1", replaceStart: 5, replaceEnd: 8);
        var scene = updated.Scenes[0];

        Assert.Equal(
            [
                ("source-1", 0, 5),
                ("pickup-1", 2, 2.62),
                ("source-1", 8, 20),
            ],
            scene.Selections.Select(item => (item.SourceAssetId, item.Start, item.End)));
        Assert.Empty(scene.Edits);
        Assert.Null(scene.Acceptance);
        Assert.Null(updated.FinalApproval);
        Assert.Equal(
            [
                ("source-1", new TimeRange(0, 5)),
                ("pickup-1", new TimeRange(2, 2.62)),
                ("source-1", new TimeRange(8, 20)),
            ],
            CompositionResolver.ResolveScene(updated, scene).KeepRanges.Select(item => (item.SourceAssetId, item.Range)));
        Assert.Empty(ProjectValidator.Collect(updated));
    }

    [Fact]
    public void ApplyingAPickupFromAFindingUsesTheFindingRange()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 5,
            End = 8,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "We",
            SpokenText = "They",
            Severity = DriftSeverity.Rerecord,
        });
        store.SaveNew(project);
        service.AddSource(
            new SourceAsset
            {
                Id = "pickup-1",
                Path = "/tmp/pickup.mov",
                Fingerprint = new string('b', 64),
                DurationSeconds = 8,
            },
            1);

        var updated = service.ApplyPickup("pickup-1", 1.2, 1.82, 2, findingId: "drift-1");
        var scene = updated.Scenes[0];

        Assert.Equal("pickup-1", scene.Selections[1].SourceAssetId);
        Assert.Equal(1.2, scene.Selections[1].Start);
        Assert.Equal(1.82, scene.Selections[1].End);
        Assert.Equal(5, scene.Selections[0].End);
        Assert.Equal(8, scene.Selections[2].Start);
        Assert.Empty(ProjectValidator.Collect(updated));
    }

    [Fact]
    public void BindPickupFileCopiesIntoSourcesPickups()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-tests", Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(root, "incoming.wav");
        Directory.CreateDirectory(root);
        File.WriteAllText(incoming, "audio");

        var bound = ProjectService.BindPickupFile(root, "pickup-1", incoming);

        Assert.Equal(Path.Combine(root, "sources", "pickups", "pickup-1.wav"), bound);
        Assert.True(File.Exists(bound));
        Assert.Equal(bound, ProjectService.BindPickupFile(root, "pickup-1", bound));
    }

    [Fact]
    public async Task RecordedPickupReplacesTheClauseAndLeavesTheRerecordList()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-12th",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 11.6,
            End = 11.8,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "12",
            SpokenText = "12th",
            Severity = DriftSeverity.Rerecord,
        });
        store.SaveNew(project);
        WriteWords(Path.Combine(root, "edit", "analysis"), 11000, 13000, "lesson.");
        var incoming = Path.Combine(root, "take.mov");
        File.WriteAllText(incoming, "pickup");
        SeedPickupAnalysis(root, "pickup-1");

        var runner = new ProbeRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new VideoEditOptions
        {
            ProjectDirectory = root,
            FfprobePath = "ffprobe",
            FfmpegPath = "ffmpeg",
        });
        var pickups = new RerecordPickupService(
            store,
            service,
            new PickupMixService(store, runner, new VadAnalyzer(store, runner, options), options.Value),
            new TranscriptService(store, runner, options),
            new VadAnalyzer(store, runner, options),
            new NarrationService(new MissingVoiceProvider(), runner, gate, options.Value),
            runner,
            options);

        var updated = await pickups.ApplyFileAsync("drift-12th", incoming, project.Revision, cutaway: false, CancellationToken.None);
        var pickup = Assert.Single(updated.Sources, source => source.Id == "pickup-1");
        Assert.Equal(Path.Combine(root, "sources", "pickups", "pickup-1.mov"), pickup.Path);
        Assert.Equal("pickup-1", updated.Scenes[0].Selections[1].SourceAssetId);
        Assert.Equal(11.0, updated.Scenes[0].Selections[0].End);
        Assert.Equal(13.0, updated.Scenes[0].Selections[2].Start);
        Assert.Empty(ProjectValidator.Collect(updated));

        var renderer = TestSupport.Renderer(root, new ProbeRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        Assert.DoesNotContain(review.RerecordList, item => item.Id == "drift-12th");
    }

    [Fact]
    public async Task StalePickupUploadThenValidRetrySucceeds()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-12th",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 11.6,
            End = 11.8,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "12",
            SpokenText = "12th",
            Severity = DriftSeverity.Rerecord,
        });
        store.SaveNew(project);
        WriteWords(Path.Combine(root, "edit", "analysis"), 11000, 13000, "lesson.");
        var incoming = Path.Combine(root, "take.mov");
        File.WriteAllText(incoming, "pickup");
        SeedPickupAnalysis(root, "pickup-1");
        var pickupsDir = Path.Combine(root, "sources", "pickups");
        var runner = new ProbeRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new VideoEditOptions
        {
            ProjectDirectory = root,
            FfprobePath = "ffprobe",
            FfmpegPath = "ffmpeg",
        });
        var pickups = new RerecordPickupService(
            store,
            service,
            new PickupMixService(store, runner, new VadAnalyzer(store, runner, options), options.Value),
            new TranscriptService(store, runner, options),
            new VadAnalyzer(store, runner, options),
            new NarrationService(new MissingVoiceProvider(), runner, gate, options.Value),
            runner,
            options);

        var stale = await Assert.ThrowsAsync<VideoEditException>(() =>
            pickups.ApplyFileAsync("drift-12th", incoming, 0, cutaway: false, CancellationToken.None));
        Assert.Equal("stale_write", stale.Code);
        Assert.Equal(project.Revision, store.Load().Revision);
        Assert.DoesNotContain(store.Load().Sources, source => source.Id == "pickup-1");
        Assert.False(File.Exists(Path.Combine(pickupsDir, "pickup-1.mov")));
        Assert.False(Directory.Exists(pickupsDir) && Directory.EnumerateFiles(pickupsDir).Any());

        var updated = await pickups.ApplyFileAsync(
            "drift-12th",
            incoming,
            project.Revision,
            cutaway: false,
            CancellationToken.None);
        Assert.Contains(updated.Sources, source => source.Id == "pickup-1");
        Assert.True(File.Exists(Path.Combine(pickupsDir, "pickup-1.mov")));
        Assert.Equal("pickup-1", updated.Scenes[0].Selections[1].SourceAssetId);

        var renderer = TestSupport.Renderer(root, new ProbeRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        Assert.DoesNotContain(review.RerecordList, item => item.Id == "drift-12th");
    }

    [Fact]
    public async Task OverlappingPickupUploadsCannotOverwriteTheRegisteredTake()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-12th",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 11.6,
            End = 11.8,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "12",
            SpokenText = "12th",
            Severity = DriftSeverity.Rerecord,
        });
        store.SaveNew(project);
        WriteWords(Path.Combine(root, "edit", "analysis"), 11000, 13000, "lesson.");
        SeedPickupAnalysis(root, "pickup-1");
        var firstFile = Path.Combine(root, "first.mov");
        var secondFile = Path.Combine(root, "second.mov");
        File.WriteAllText(firstFile, "first take");
        File.WriteAllText(secondFile, "second take");
        var options = Microsoft.Extensions.Options.Options.Create(new VideoEditOptions
        {
            ProjectDirectory = root,
            FfprobePath = "ffprobe",
            FfmpegPath = "ffmpeg",
        });
        RerecordPickupService CreatePickup(IProcessRunner runner) => new(
            store,
            service,
            new PickupMixService(store, runner, new VadAnalyzer(store, runner, options), options.Value),
            new TranscriptService(store, runner, options),
            new VadAnalyzer(store, runner, options),
            new NarrationService(new MissingVoiceProvider(), runner, gate, options.Value),
            runner,
            options);

        var firstRunner = new PausedProbeRunner();
        var secondRunner = new PausedProbeRunner();
        var firstTask = CreatePickup(firstRunner).ApplyFileAsync(
            "drift-12th",
            firstFile,
            project.Revision,
            cutaway: false,
            CancellationToken.None);
        await firstRunner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = CreatePickup(secondRunner).ApplyFileAsync(
            "drift-12th",
            secondFile,
            project.Revision,
            cutaway: false,
            CancellationToken.None);
        await secondRunner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        firstRunner.Release.SetResult();
        var firstDone = await firstTask;
        var registered = Assert.Single(firstDone.Sources, source => source.Id == "pickup-1");
        var beforeSecond = File.ReadAllBytes(registered.Path);
        var beforeHash = registered.Fingerprint;

        secondRunner.Release.SetResult();
        var stale = await Assert.ThrowsAsync<VideoEditException>(() => secondTask);
        Assert.Equal("stale_write", stale.Code);

        var after = store.Load();
        var kept = Assert.Single(after.Sources, source => source.Id == "pickup-1");
        Assert.Equal(beforeHash, kept.Fingerprint);
        Assert.Equal(beforeSecond, File.ReadAllBytes(kept.Path));
        Assert.Equal("first take", File.ReadAllText(kept.Path));
        Assert.Equal(beforeHash, MediaRenderer.FingerprintFile(kept.Path));
        Assert.Contains(after.Scenes[0].Selections, selection => selection.SourceAssetId == "pickup-1");
    }

    [Fact]
    public async Task FailedPickupProbeDoesNotDeleteANewlyRegisteredTake()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-12th",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 11.6,
            End = 11.8,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "12",
            SpokenText = "12th",
            Severity = DriftSeverity.Rerecord,
        });
        store.SaveNew(project);
        WriteWords(Path.Combine(root, "edit", "analysis"), 11000, 13000, "lesson.");
        SeedPickupAnalysis(root, "pickup-1");
        var successFile = Path.Combine(root, "success.mov");
        var failFile = Path.Combine(root, "fail.mov");
        File.WriteAllText(successFile, "kept take");
        File.WriteAllText(failFile, "failed take");
        var options = Microsoft.Extensions.Options.Options.Create(new VideoEditOptions
        {
            ProjectDirectory = root,
            FfprobePath = "ffprobe",
            FfmpegPath = "ffmpeg",
        });
        RerecordPickupService CreatePickup(IProcessRunner runner) => new(
            store,
            service,
            new PickupMixService(store, runner, new VadAnalyzer(store, runner, options), options.Value),
            new TranscriptService(store, runner, options),
            new VadAnalyzer(store, runner, options),
            new NarrationService(new MissingVoiceProvider(), runner, gate, options.Value),
            runner,
            options);

        var successRunner = new PausedProbeRunner();
        var failRunner = new PausedProbeRunner { Fail = true };
        var failTask = CreatePickup(failRunner).ApplyFileAsync(
            "drift-12th",
            failFile,
            project.Revision,
            cutaway: false,
            CancellationToken.None);
        await failRunner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var successTask = CreatePickup(successRunner).ApplyFileAsync(
            "drift-12th",
            successFile,
            project.Revision,
            cutaway: false,
            CancellationToken.None);
        await successRunner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        successRunner.Release.SetResult();
        var registered = await successTask;
        var pickup = Assert.Single(registered.Sources, source => source.Id == "pickup-1");
        var hash = pickup.Fingerprint;
        Assert.True(File.Exists(pickup.Path));

        failRunner.Release.SetResult();
        var failed = await Assert.ThrowsAsync<VideoEditException>(() => failTask);
        Assert.Equal("validation", failed.Code);

        var after = store.Load();
        var kept = Assert.Single(after.Sources, source => source.Id == "pickup-1");
        Assert.True(File.Exists(kept.Path));
        Assert.Equal("kept take", File.ReadAllText(kept.Path));
        Assert.Equal(hash, kept.Fingerprint);
        Assert.Equal(hash, MediaRenderer.FingerprintFile(kept.Path));
        Assert.Contains(after.Scenes[0].Selections, selection => selection.SourceAssetId == "pickup-1");
    }

    [Fact]
    public async Task GeneratedPickupRequiresAVoiceId()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 12,
            End = 13,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "12",
        });
        store.SaveNew(project);
        var runner = new ProbeRunner();
        var options = Microsoft.Extensions.Options.Options.Create(new VideoEditOptions
        {
            ProjectDirectory = root,
        });
        var pickups = new RerecordPickupService(
            store,
            service,
            new PickupMixService(store, runner, new VadAnalyzer(store, runner, options), options.Value),
            new TranscriptService(store, runner, options),
            new VadAnalyzer(store, runner, options),
            new NarrationService(new MissingVoiceProvider(), runner, gate, options.Value),
            runner,
            options);

        var voice = await Assert.ThrowsAsync<VideoEditException>(() =>
            pickups.ApplyGeneratedAsync("drift-1", 1, CancellationToken.None));
        Assert.Contains("voice ID", voice.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteWords(string analysis, int fromMs, int toMs, string last)
    {
        Directory.CreateDirectory(analysis);
        File.WriteAllText(
            Path.Combine(analysis, "words.json"),
            "{\"transcription\":["
            + $"{{\"text\":\"As\",\"offsets\":{{\"from\":{fromMs},\"to\":{fromMs + 200}}}}},"
            + $"{{\"text\":\"of\",\"offsets\":{{\"from\":{fromMs + 200},\"to\":{fromMs + 300}}}}},"
            + $"{{\"text\":\"the\",\"offsets\":{{\"from\":{fromMs + 300},\"to\":{fromMs + 400}}}}},"
            + $"{{\"text\":\"May\",\"offsets\":{{\"from\":{fromMs + 400},\"to\":{fromMs + 600}}}}},"
            + $"{{\"text\":\"12\",\"offsets\":{{\"from\":{fromMs + 600},\"to\":{fromMs + 800}}}}},"
            + $"{{\"text\":\"{last}\",\"offsets\":{{\"from\":{fromMs + 800},\"to\":{toMs}}}}}"
            + "]}");
    }

    private static void SeedPickupAnalysis(string root, string pickupId)
    {
        var analysis = Path.Combine(root, "edit", "analysis", pickupId);
        Directory.CreateDirectory(analysis);
        File.WriteAllText(Path.Combine(analysis, "audio16k.wav"), "wav");
        WriteWords(analysis, 200, 1800, "lesson.");
        VadSpeechMap.Write(
            Path.Combine(analysis, VadSpeechMap.FileName),
            new VadSpeechMap { Segments = [new SpeechSegment { Start = 0.2, End = 1.8 }] });
        File.WriteAllText(Path.Combine(analysis, EditingPolicy.DiagnosisFileName), "silence_start: 0\nsilence_end: 0.2\n");
    }

    private sealed class PausedProbeRunner : IProcessRunner
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; init; }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Ready.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (Fail)
            {
                return new ProcessResult(1, "", "probe failed");
            }

            return new ProcessResult(
                0,
                """{"format":{"duration":"2.0"},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""",
                "");
        }
    }

    private sealed class ProbeRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(
                0,
                """{"format":{"duration":"2.0"},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""",
                ""));
    }

    private sealed class MissingVoiceProvider : INarrationProvider
    {
        public Task<IReadOnlyList<NarrationVoice>> ListVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NarrationVoice>>([]);

        public Task<NarrationAudio> GenerateAsync(NarrationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test does not call ElevenLabs.");
    }

    [Fact]
    public void FilterGraphUsesEachRangeInput()
    {
        var filter = MediaRenderer.BuildFilterGraph(
            [
                new RenderRange(new TimeRange(0, 1), 0, 0, SourceAssetId: "source-1", InputIndex: 0),
                new RenderRange(new TimeRange(2, 2.62), 0, 0, SourceAssetId: "pickup-1", InputIndex: 1),
                new RenderRange(new TimeRange(8, 9), 0, 0, SourceAssetId: "source-1", InputIndex: 0),
            ],
            new OutputSettings());

        Assert.Contains("[0:v]trim=start=0:end=1", filter, StringComparison.Ordinal);
        Assert.Contains("[1:v]trim=start=2:end=2.62", filter, StringComparison.Ordinal);
        Assert.Contains("[0:v]trim=start=8:end=9", filter, StringComparison.Ordinal);
        Assert.Contains("concat=n=3:v=1:a=1", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyingAnAudioOnlyPickupKeepsHostVideo()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        store.SaveNew(project);
        service.AddSource(
            new SourceAsset
            {
                Id = "pickup-1",
                Path = "/tmp/pickup.mov",
                Fingerprint = new string('b', 64),
                DurationSeconds = 8,
            },
            1);

        var updated = service.ApplyPickup(
            "pickup-1",
            2,
            2.62,
            2,
            sceneId: "scene-1",
            replaceStart: 5,
            replaceEnd: 8,
            audioOnly: true);
        var scene = updated.Scenes[0];
        var overlay = scene.Selections[1];

        Assert.Equal(
            [
                ("source-1", 0, 5),
                ("source-1", 5, 8),
                ("source-1", 8, 20),
            ],
            scene.Selections.Select(item => (item.SourceAssetId, item.Start, item.End)));
        Assert.Equal("pickup-1", overlay.AudioSourceAssetId);
        Assert.Equal(2, overlay.AudioStart);
        Assert.Equal(2.62, overlay.AudioEnd);
        Assert.Empty(scene.Edits);
        var keep = CompositionResolver.ResolveScene(updated, scene).KeepRanges[1];
        Assert.Equal("source-1", keep.SourceAssetId);
        Assert.Equal("pickup-1", keep.AudioSourceAssetId);
        Assert.Equal(new TimeRange(2, 2.62), keep.AudioRange);
        Assert.Empty(ProjectValidator.Collect(updated));
    }

    [Fact]
    public void AudioOnlyFilterUsesHostVideoAndPickupAudio()
    {
        var filter = MediaRenderer.BuildFilterGraph(
            [
                new RenderRange(
                    new TimeRange(81.21, 81.6),
                    0,
                    0,
                    SourceAssetId: "source-1",
                    InputIndex: 0,
                    AudioSourceAssetId: "pickup-1",
                    AudioRange: new TimeRange(44.1, 44.45),
                    AudioInputIndex: 1),
            ],
            new OutputSettings());

        Assert.Contains("[0:v]trim=start=81.21:end=81.6", filter, StringComparison.Ordinal);
        Assert.Contains("[1:a]atrim=start=44.1:end=44.45", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("[1:v]", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioOnlyPickupSeamsUseTwentyFiveMillisecondCrossfades()
    {
        var project = TestSupport.SampleProject();
        project.Sources.Add(new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 8,
        });
        project.Scenes[0].Edits.Clear();
        project.Scenes[0].Selections =
        [
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 0, End = 5 },
            new SourceSelection
            {
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 5,
                End = 8,
                AudioSourceAssetId = "pickup-1",
                AudioSourceFingerprint = new string('b', 64),
                AudioStart = 2,
                AudioEnd = 2.62,
                AudioGainDb = 3.2,
            },
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 8, End = 20 },
        ];

        var resolved = CompositionResolver.ResolveScene(project, project.Scenes[0]);
        Assert.True(resolved.KeepRanges[0].CrossfadeOut);
        Assert.True(resolved.KeepRanges[1].CrossfadeIn);
        Assert.True(resolved.KeepRanges[1].CrossfadeOut);
        Assert.True(resolved.KeepRanges[2].CrossfadeIn);
        Assert.Equal(AudioTreatment.PickupCrossfadeSeconds, resolved.KeepRanges[1].FadeInSeconds);
        Assert.Equal(3.2, resolved.KeepRanges[1].AudioGainDb);

        var filter = MediaRenderer.BuildFilterGraph(
            [
                resolved.KeepRanges[1] with
                {
                    InputIndex = 0,
                    AudioInputIndex = 1,
                    RoomInputIndex = 2,
                    RoomGainDb = 25.6,
                },
            ],
            new OutputSettings());
        Assert.Contains("volume=3.2dB", filter, StringComparison.Ordinal);
        Assert.Contains("volume=25.6dB", filter, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2:duration=first:normalize=0:dropout_transition=0", filter, StringComparison.Ordinal);
        Assert.Contains("apad=whole_dur=3", filter, StringComparison.Ordinal);
        Assert.Contains("[2:a]atrim=start=1", filter, StringComparison.Ordinal);
        Assert.Contains("aloop=loop=-1", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandToUtteranceStopsAtAPeriod()
    {
        var words = NurseryWords();
        var span = WhisperWords.ExpandToUtterance(words, new TimeRange(74.01, 74.4));
        Assert.Equal(73.37, span.Start, 3);
        Assert.Equal(75.0, span.End, 3);
    }

    [Fact]
    public void SentencePickupReplacesTheAlignedHostSentenceNotTheWord()
    {
        var host = new[]
        {
            new SpokenWord("That", "that", 62.68, 62.9),
            new SpokenWord("Blossoms,", "happens", 70.0, 70.5),
            new SpokenWord("guided", "guided", 73.37, 74.01),
            new SpokenWord("by", "by", 74.01, 74.58),
            new SpokenWord("NURSERY", "nursery", 81.21, 81.6),
            new SpokenWord("sunlight.", "sunlight", 83.6, 84.42),
            new SpokenWord("I'm", "i'm", 84.42, 85.14),
        };
        var pickup = new[]
        {
            new SpokenWord("Guided", "guided", 32.33, 35.71),
            new SpokenWord("by", "by", 35.71, 36.84),
            new SpokenWord("NURSERY", "nursery", 44.1, 44.45),
            new SpokenWord("sunlight.", "sunlight", 46.08, 46.72),
            new SpokenWord("You", "you", 51.82, 51.89),
        };
        var pickupSentence = WhisperWords.ExpandToSentence(pickup, new TimeRange(44.1, 44.45));
        var hostSentence = WhisperWords.AlignSentence(host, pickup.Where(word => pickupSentence.Contains(word.Range)).ToList(), new TimeRange(81.21, 81.6));

        Assert.Equal(32.33, pickupSentence.Start, 3);
        Assert.Equal(46.72, pickupSentence.End, 3);
        Assert.Equal(73.37, hostSentence.Start, 3);
        Assert.Equal(84.42, hostSentence.End, 3);
        var completed = WhisperWords.CompleteSentence(host, hostSentence);
        Assert.Equal(73.37, completed.Start, 3);
        Assert.Equal(84.42, completed.End, 3);
    }

    [Fact]
    public void CombinedPickupSentenceCoversBothHostSentences()
    {
        var host = new[]
        {
            new SpokenWord("You", "you", 102.64, 102.91),
            new SpokenWord("stop", "stop", 102.91, 103.25),
            new SpokenWord("treating", "treating", 103.25, 103.86),
            new SpokenWord("the", "the", 103.86, 104.16),
            new SpokenWord("current", "current", 104.16, 104.56),
            new SpokenWord("row", "row", 104.66, 104.89),
            new SpokenWord("is", "is", 104.89, 105.11),
            new SpokenWord("truth.", "truth", 105.11, 106.0),
            new SpokenWord("The", "the", 106.0, 106.24),
            new SpokenWord("truth", "truth", 106.24, 106.64),
            new SpokenWord("happened.", "happened", 108.88, 109.28),
            new SpokenWord("The", "the", 109.28, 110.4),
            new SpokenWord("current", "current", 110.4, 110.81),
            new SpokenWord("rebuild.", "rebuild", 114.76, 115.97),
            new SpokenWord("That", "that", 115.97, 116.41),
        };
        var pickup = new[]
        {
            new SpokenWord("The", "the", 55.61, 56.83),
            new SpokenWord("truth", "truth", 56.83, 58.88),
            new SpokenWord("happened,", "happened", 60.4, 61.28),
            new SpokenWord("and", "and", 61.28, 61.5),
            new SpokenWord("current", "current", 61.5, 62.01),
            new SpokenWord("rebuild.", "rebuild", 64.65, 65.44),
        };
        var pickupSentence = WhisperWords.ExpandToSentence(pickup, new TimeRange(61.28, 61.5));
        var hostSentence = WhisperWords.AlignSentence(host, pickup, new TimeRange(109.28, 110.4));

        Assert.Equal(55.61, pickupSentence.Start, 3);
        Assert.Equal(65.44, pickupSentence.End, 3);
        Assert.Equal(106.0, hostSentence.Start, 3);
        Assert.Equal(115.97, hostSentence.End, 3);

        var youStop = WhisperWords.AlignSentence(
            host,
            [
                new SpokenWord("You", "you", 51.82, 51.89),
                new SpokenWord("stop", "stop", 51.89, 52.17),
                new SpokenWord("treating", "treating", 52.17, 52.74),
                new SpokenWord("the", "the", 52.74, 52.95),
                new SpokenWord("current", "current", 52.95, 53.45),
                new SpokenWord("row", "row", 53.45, 53.66),
                new SpokenWord("as", "as", 53.66, 53.8),
                new SpokenWord("the", "the", 53.8, 54.01),
                new SpokenWord("truth.", "truth", 54.01, 55.61),
            ],
            new TimeRange(104.89, 105.11));
        Assert.Equal(102.64, youStop.Start, 3);
        Assert.Equal(106.0, WhisperWords.CompleteSentence(host, youStop).End, 3);
    }

    [Fact]
    public void WideningPickupsDropsEditsInsideTheNewSentence()
    {
        var project = TestSupport.SampleProject();
        project.Sources.Add(new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 80,
        });
        project.Scenes[0].Edits =
        [
            new SceneEdit
            {
                Id = "e-inside",
                SourceAssetId = "source-1",
                Start = 6,
                End = 7,
                Kind = EditKinds.Flub,
                Decision = DecisionStates.Approved,
            },
            new SceneEdit
            {
                Id = "e-outside",
                SourceAssetId = "source-1",
                Start = 16,
                End = 17,
                Kind = EditKinds.Pause,
                Decision = DecisionStates.Approved,
            },
        ];
        project.Scenes[0].Selections =
        [
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 0, End = 5.5 },
            new SourceSelection
            {
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 5.5,
                End = 5.8,
                AudioSourceAssetId = "pickup-1",
                AudioSourceFingerprint = new string('b', 64),
                AudioStart = 2.0,
                AudioEnd = 2.2,
            },
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 5.8, End = 20 },
        ];

        var host = new[]
        {
            new SpokenWord("Hello.", "hello", 0, 1),
            new SpokenWord("You", "you", 5.0, 5.2),
            new SpokenWord("stop", "stop", 5.2, 5.5),
            new SpokenWord("truth.", "truth", 5.5, 8.0),
            new SpokenWord("Next.", "next", 8.0, 9.0),
        };
        var pickup = new[]
        {
            new SpokenWord("You", "you", 1.8, 1.9),
            new SpokenWord("stop", "stop", 1.9, 2.0),
            new SpokenWord("truth.", "truth", 2.0, 3.4),
        };

        var overlays = PickupMixService.WidenOverlays(project.Scenes[0], host, pickup);
        PickupMixService.ApplyOverlays(project.Scenes[0], overlays);

        Assert.Equal(5.0, overlays[0].Start, 3);
        Assert.Equal(7.2, overlays[0].End, 3);
        Assert.Equal(8.0, overlays[0].ReplaceEnd);
        Assert.Equal(1.8, overlays[0].AudioStart);
        Assert.Equal(3.4, overlays[0].AudioEnd);
        Assert.DoesNotContain(project.Scenes[0].Edits, edit => edit.Id == "e-inside");
        Assert.Contains(project.Scenes[0].Edits, edit => edit.Id == "e-outside");
        Assert.Equal(
            [
                ("source-1", 0, 5.0),
                ("source-1", 5.0, 7.2),
                ("source-1", 8.0, 20),
            ],
            project.Scenes[0].Selections.Select(item => (item.SourceAssetId, item.Start, item.End)));
    }

    [Fact]
    public void PickupVadDropsARetakePreambleAndSnapsTheEndToTheLastWordGap()
    {
        var silences = new[]
        {
            new SilenceInterval(0, 31.90),
            new SilenceInterval(35.04, 37.50),
            new SilenceInterval(43.58, 44.03),
            new SilenceInterval(46.91, 51.84),
            new SilenceInterval(54.62, 58.27),
            new SilenceInterval(95.14, 100.99),
        };
        var retakeWords = new SpokenWord[]
        {
            new("wrong", "wrong", 32.4, 33.2),
            new("take", "take", 33.3, 34.0),
            new("again", "again", 34.1, 34.8),
            new("wrong", "wrong", 37.6, 38.2),
            new("take", "take", 38.3, 38.9),
            new("again", "again", 39.0, 39.6),
        };
        var nursery = PickupBounds.Snap(new TimeRange(32.33, 46.72), silences, spokenWords: retakeWords);
        Assert.Equal(37.50, nursery.Range.Start, 2);
        Assert.InRange(nursery.Range.End, 46.70, 46.90);
        Assert.Contains(nursery.DroppedTakes, take => take.Start <= 32.33 && take.End >= 35.04);
        Assert.Empty(nursery.PauseCuts);

        var youStop = PickupBounds.Snap(new TimeRange(51.82, 55.61), silences);
        Assert.InRange(youStop.Range.End, 54.55, 54.70);

        var planter = PickupBounds.Snap(new TimeRange(89.74, 94.96), silences);
        Assert.InRange(planter.Range.End, 94.96, 95.20);
    }

    [Fact]
    public void PickupPauseCutsStayInsideTheAudioWindow()
    {
        var silences = new[]
        {
            new SilenceInterval(54.62, 58.27),
            new SilenceInterval(60.93, 61.44),
        };
        var snap = PickupBounds.Snap(new TimeRange(55.61, 65.44), silences);
        Assert.Equal(55.61, snap.Range.Start, 2);
        Assert.Single(snap.PauseCuts);
        Assert.Equal(55.61, snap.PauseCuts[0].Start, 2);
        Assert.InRange(snap.PauseCuts[0].End, 57.60, 57.75);
        Assert.True(snap.Range.Contains(snap.PauseCuts[0]));
        var folded = PickupBounds.FoldLeadingPauses(snap.Range, snap.PauseCuts);
        Assert.InRange(folded.Start, 57.60, 57.75);
        Assert.False(folded.Contains(snap.PauseCuts[0]));
    }

    [Fact]
    public void FitHostShortensToPickupPlusPauseTarget()
    {
        var host = PickupBounds.FitHost(new TimeRange(73.37, 84.42), 9.25);
        Assert.Equal(73.37, host.Start, 2);
        Assert.Equal(83.22, host.End, 2);
        Assert.Equal(new TimeRange(102.64, 106.0), PickupBounds.FitHost(new TimeRange(102.64, 106.0), 3.15));
    }

    [Fact]
    public void SeamTrimLeavesOnePauseTargetAcrossAPickupHostJoin()
    {
        Assert.Equal(0.56, PickupBounds.TrimLeading(0.60, 0.56, 0.6), 2);
        Assert.Equal(0, PickupBounds.TrimLeading(0.20, 0.30, 0.6), 2);
        var start = PickupBounds.SkipLeadingSilence(
            139.46,
            170.15,
            [new SilenceInterval(139.58, 142.11)],
            [new TimeRange(139.65, 141.51)],
            keepSilence: 0);
        Assert.InRange(start, 142.05, 142.20);
    }

    [Fact]
    public void HostAfterAPickupSkipsLeadingSilenceThePickupAlreadyPaid()
    {
        var project = TestSupport.SampleProject();
        var pickup = new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 80,
        };
        project.Sources.Add(pickup);
        var overlay = new SourceSelection
        {
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 5,
            End = 7.2,
            ReplaceEnd = 8.0,
            AudioSourceAssetId = "pickup-1",
            AudioSourceFingerprint = pickup.Fingerprint,
            AudioStart = 1.8,
            AudioEnd = 3.4,
        };
        project.Scenes[0].Selections =
        [
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 0, End = 5 },
            overlay,
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 8, End = 20 },
        ];
        project.Scenes[0].Edits =
        [
            new SceneEdit
            {
                Id = "e-pause-tail",
                SceneId = "scene-1",
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 8.1,
                End = 9.5,
                Kind = EditKinds.Pause,
                Decision = DecisionStates.Approved,
            },
        ];
        var silences = new Dictionary<string, IReadOnlyList<SilenceInterval>>
        {
            ["source-1"] = [new SilenceInterval(8.0, 10.0)],
            ["pickup-1"] = [],
        };

        PickupMixService.ApplyOverlays(project.Scenes[0], [overlay], project, silences, EditingPolicy.Default);

        Assert.Equal(7.2, project.Scenes[0].Selections[1].End, 3);
        Assert.Equal(10.0, project.Scenes[0].Selections[2].Start, 3);
        Assert.DoesNotContain(project.Scenes[0].Edits, edit => edit.Id == "e-pause-tail");
        Assert.Empty(ProjectValidator.Collect(project));
    }

    [Fact]
    public void PickupAudioLongerThanTheHostIsATimingMismatch()
    {
        var silences = new[]
        {
            new SilenceInterval(0, 1.0),
            new SilenceInterval(8.0, 12.0),
        };
        var snap = PickupBounds.Snap(new TimeRange(1.0, 8.0), silences, maxDuration: 3.36);
        Assert.Equal(1.0, snap.Range.Start, 2);
        Assert.Equal(8.0, snap.Range.End, 2);
        Assert.True(snap.TimingMismatch);
        Assert.Empty(snap.DroppedTakes);
    }

    [Fact]
    public void HostPauseInsideAPickupBecomesAPickupFinding()
    {
        var project = TestSupport.SampleProject();
        var pickup = new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 80,
        };
        project.Sources.Add(pickup);
        var overlay = new SourceSelection
        {
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 5,
            End = 8,
            AudioSourceAssetId = "pickup-1",
            AudioSourceFingerprint = pickup.Fingerprint,
            AudioStart = 2,
            AudioEnd = 5,
        };
        project.Scenes[0].Selections =
        [
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 0, End = 5 },
            overlay,
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 8, End = 20 },
        ];
        project.Scenes[0].Edits =
        [
            new SceneEdit
            {
                Id = "e-pause-inside",
                SceneId = "scene-1",
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 6,
                End = 7,
                Kind = EditKinds.Pause,
                Decision = DecisionStates.Approved,
                Confidence = 0.9,
            },
        ];
        var silences = new Dictionary<string, IReadOnlyList<SilenceInterval>>
        {
            ["pickup-1"] = [new SilenceInterval(3.0, 4.2)],
        };

        PickupMixService.ApplyOverlays(project.Scenes[0], [overlay], project, silences, EditingPolicy.Default);

        Assert.DoesNotContain(project.Scenes[0].Edits, edit => edit.Id == "e-pause-inside");
        Assert.Contains(project.Findings, finding => finding.Id == "pickup-from-e-pause-inside" && finding.SourceAssetId == "pickup-1");
        Assert.Contains(project.Scenes[0].Edits, edit => edit.SourceAssetId == "pickup-1" && edit.Kind == EditKinds.Pause && edit.Decision == DecisionStates.Approved);
        Assert.Empty(ProjectValidator.Collect(project));
    }

    [Fact]
    public void PickupPauseEditsTargetTheAudioRangeAndAreSubtractedFromKeeps()
    {
        var project = TestSupport.SampleProject();
        var pickup = new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 80,
        };
        project.Sources.Add(pickup);
        project.Scenes[0].Edits.Clear();
        project.Scenes[0].Selections =
        [
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 0, End = 5 },
            new SourceSelection
            {
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 5,
                End = 14.97,
                AudioSourceAssetId = "pickup-1",
                AudioSourceFingerprint = pickup.Fingerprint,
                AudioStart = 55.61,
                AudioEnd = 65.47,
            },
        ];
        var overlay = project.Scenes[0].Selections[1];
        var silences = new Dictionary<string, IReadOnlyList<SilenceInterval>>
        {
            ["pickup-1"] = [new SilenceInterval(54.62, 58.27)],
        };
        var snap = PickupBounds.Snap(new TimeRange(55.61, 65.44), silences["pickup-1"]);
        PickupMixService.ApplyOverlays(
            project.Scenes[0],
            [overlay],
            project,
            silences,
            EditingPolicy.Default,
            new Dictionary<(double Start, double End), PickupSnap> { [(overlay.Start, overlay.End)] = snap });

        Assert.DoesNotContain(project.Scenes[0].Edits, edit => edit.SourceAssetId == "pickup-1");
        var finding = Assert.Single(project.Findings, item => item.Id.StartsWith("pickup-pause-", StringComparison.Ordinal));
        Assert.Equal("pickup-1", finding.SourceAssetId);
        Assert.Equal(55.61, finding.Start, 2);
        Assert.InRange(overlay.AudioStart ?? 0, 57.60, 57.75);
        Assert.Equal(RerecordStatuses.ResolvedByCut, ReviewAssembler.StatusForFinding(project, finding));
        Assert.Empty(ProjectValidator.Collect(project));

        var resolved = CompositionResolver.ResolveProposedScene(project, project.Scenes[0]);
        var audio = Assert.Single(resolved.KeepRanges, item => item.AudioSourceAssetId == "pickup-1");
        Assert.InRange(audio.AudioRange!.Value.Start, 57.60, 57.75);
        Assert.Null(audio.AudioKeeps);
    }

    [Fact]
    public void PickupPauseFindingsBecomeAudioEditsOnApply()
    {
        var project = TestSupport.SampleProject();
        var pickup = new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 80,
        };
        project.Sources.Add(pickup);
        project.Findings.Add(new SourceFinding
        {
            Id = "pickup-pause-3",
            SourceAssetId = "pickup-1",
            SourceFingerprint = pickup.Fingerprint,
            Start = 3.0,
            End = 3.6,
            Kind = EditKinds.Pause,
            Reason = "Existing pickup pause finding.",
        });
        var overlay = new SourceSelection
        {
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 5,
            End = 8,
            AudioSourceAssetId = "pickup-1",
            AudioSourceFingerprint = pickup.Fingerprint,
            AudioStart = 2,
            AudioEnd = 5,
        };
        project.Scenes[0].Selections =
        [
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 0, End = 5 },
            overlay,
            new SourceSelection { SourceAssetId = "source-1", SourceFingerprint = TestSupport.Fingerprint, Start = 8, End = 20 },
        ];
        project.Scenes[0].Edits.Clear();

        PickupMixService.ApplyOverlays(project.Scenes[0], [overlay], project, new Dictionary<string, IReadOnlyList<SilenceInterval>>(), EditingPolicy.Default);

        var pause = Assert.Single(project.Scenes[0].Edits, edit => edit.SourceAssetId == "pickup-1");
        Assert.Equal(3.0, pause.Start, 2);
        Assert.Equal(3.6, pause.End, 2);
        Assert.Empty(ProjectValidator.Collect(project));
    }

    [Fact]
    public void AudioOnlyFilterCapsPickupAudioToTheHostDuration()
    {
        var filter = MediaRenderer.BuildFilterGraph(
            [
                new RenderRange(
                    new TimeRange(102.64, 106.0),
                    0,
                    0,
                    SourceAssetId: "source-1",
                    InputIndex: 0,
                    AudioSourceAssetId: "pickup-1",
                    AudioRange: new TimeRange(51.82, 55.61),
                    AudioInputIndex: 1),
            ],
            new OutputSettings());

        Assert.Contains("[1:a]atrim=start=51.82:end=55.61", filter, StringComparison.Ordinal);
    }

    private static SpokenWord[] NurseryWords() =>
    [
        new SpokenWord("gardeners.", "gardeners", 72.5, 73.3),
        new SpokenWord("guided", "guided", 73.37, 74.01),
        new SpokenWord("NURSERY", "nursery", 74.01, 74.4),
        new SpokenWord("sunlight.", "sunlight", 74.4, 75.0),
        new SpokenWord("I", "i", 75.1, 75.3),
    ];

    private static ProjectDocument MixedScene()
    {
        var source = new SourceAsset
        {
            Id = "source-1",
            Path = "/tmp/source.mov",
            Fingerprint = TestSupport.Fingerprint,
            DurationSeconds = 20,
        };
        var pickup = new SourceAsset
        {
            Id = "pickup-1",
            Path = "/tmp/pickup.mov",
            Fingerprint = new string('b', 64),
            DurationSeconds = 8,
        };
        return new ProjectDocument
        {
            Name = "pickup",
            Revision = 1,
            Sources = [source, pickup],
            Scenes =
            [
                new Scene
                {
                    Id = "scene-1",
                    Title = "Talking head",
                    Position = 1,
                    Selections =
                    [
                        new SourceSelection { SourceAssetId = source.Id, SourceFingerprint = source.Fingerprint, Start = 0, End = 10 },
                        new SourceSelection { SourceAssetId = pickup.Id, SourceFingerprint = pickup.Fingerprint, Start = 2, End = 2.62 },
                        new SourceSelection { SourceAssetId = source.Id, SourceFingerprint = source.Fingerprint, Start = 10, End = 20 },
                    ],
                }
            ],
            Output = new OutputSettings(),
        };
    }
}

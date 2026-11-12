using System.Globalization;
using Microsoft.Extensions.Options;
using VideoEdit;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public sealed class LatestValidationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "video-edit-latest-" + Guid.NewGuid().ToString("N"));
    private readonly ProcessRunner runner = new();
    private string? hostPath;
    private string? pickupPath;
    private string hostFp = "";
    private string pickupFp = "";

    [Fact]
    public async Task ExportUsesTheFrozenRoomGain()
    {
        var room = Path.Combine(root, "room-gain.wav");
        await RoomAsync(room, 880);
        var fixture = await OverlayFixtureAsync(roomPath: room);
        await WriteAnalysisAsync(fixture.Store.ProjectDirectory, "0.5");
        await ApproveAsync(fixture);

        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        var previewAmp = Amplitude(preview, 1, 0.2, 880);
        await WriteAnalysisAsync(fixture.Store.ProjectDirectory, "0.05");
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var exportPath = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        var exportAmp = Amplitude(exportPath, 1, 0.2, 880);
        var changeDb = 20 * Math.Log10(exportAmp / previewAmp);
        Assert.InRange(changeDb, -1.5, 1.5);
    }

    [Fact]
    public async Task BoundaryRoomIsBoundIntoApprovalAndExport()
    {
        var room = Path.Combine(root, "room-gap.wav");
        await RoomAsync(room, 880);
        var fixture = await TwoSceneFixtureAsync(roomPath: room);
        await ApproveAsync(fixture);

        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        Assert.True(Amplitude(preview, 1.08, 0.1, 880) > 0.05);
        Assert.True(Amplitude(preview, 1.08, 0.1, 1760) < 0.01);
        await RoomAsync(room, 1760);
        var loaded = fixture.Store.Load();
        Assert.True(RenderIdentity.UsesRoomBed(loaded));
        Assert.NotEmpty(CompositionResolver.ExportBlocks(loaded));
        var blocked = await Record.ExceptionAsync(() => fixture.Renderer.ExportAsync(CancellationToken.None));
        Assert.NotNull(blocked);
        Assert.True(Amplitude(preview, 1.08, 0.1, 880) > 0.05);
    }

    [Fact]
    public async Task CompressedPickupAudioKeepsTheRetainedTail()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 2,
            edits:
            [
                new SceneEdit
                {
                    Id = "cut",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    SourceFingerprint = pickupFp,
                    Start = 0.5,
                    End = 1.5,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                }
            ]);
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);
        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        Assert.True(Rms(preview, 0.2, 0.2) > 0.01);
        Assert.True(Rms(preview, 1.5, 0.2) > 0.01);
    }

    [Fact]
    public async Task TimingFindingBlocksExportUntilAnExplicitDecision()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 1,
            findings:
            [
                new SourceFinding
                {
                    Id = "timing",
                    SourceAssetId = "pickup",
                    SourceFingerprint = pickupFp,
                    Start = 0,
                    End = 3,
                    Kind = PickupFindingKinds.Timing,
                    Reason = "Pickup speech is longer than host picture.",
                }
            ]);
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);

        var review = new ReviewAssembler(fixture.Store, fixture.Renderer, fixture.Gate).Assemble();
        Assert.Contains(review.RerecordList, item => item.Kind == PickupFindingKinds.Timing);
        Assert.False(review.CanExport);
        Assert.Throws<VideoEditException>(() => fixture.Service.AcceptScene("scene", fixture.Store.Load().Revision));

        fixture.Service.ResolveTiming("timing", TimingDecisions.Extend, fixture.Store.Load().Revision);
        await ApproveAsync(fixture);
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var path = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(Amplitude(path, 2.2, 0.2, 660) > 0.02);
    }

    [Fact]
    public async Task ExtendPictureShiftsTheFollowingHostSelection()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 1,
            extraSelections:
            [
                new SourceSelection { SourceAssetId = "host", Start = 1, End = 3 }
            ],
            findings: [TimingFinding()]);
        fixture.Service.ResolveTiming("timing", TimingDecisions.Extend, fixture.Store.Load().Revision);
        var selections = fixture.Store.Load().Scenes[0].Selections;
        var remaining = Assert.Single(selections);
        Assert.Equal(0, remaining.Start, 3);
        Assert.Equal(3, remaining.End, 3);
        await ApproveAsync(fixture);
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var path = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(Amplitude(path, 2.2, 0.2, 660) > 0.02);
    }

    [Fact]
    public async Task TrimPickupAddsOnlyTheRetainedTail()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 1,
            edits:
            [
                new SceneEdit
                {
                    Id = "cut",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    Start = 0.5,
                    End = 1,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                }
            ],
            findings: [TimingFinding()]);
        fixture.Service.ResolveTiming("timing", TimingDecisions.Trim, fixture.Store.Load().Revision);
        var edits = fixture.Store.Load().Scenes[0].Edits;
        var existing = Assert.Single(edits, edit => edit.Id == "cut");
        Assert.Equal(DecisionStates.Approved, existing.Decision);
        Assert.Equal(0.5, existing.Start, 3);
        Assert.Equal(1, existing.End, 3);
        Assert.DoesNotContain(edits, edit => edit.Id != "cut" && edit.Range.Overlaps(existing.Range));
        var added = Assert.Single(edits, edit => edit.Id != "cut");
        Assert.Equal(1.5, added.Start, 3);
        Assert.Equal(3, added.End, 3);
        await ApproveAsync(fixture);
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var path = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(Amplitude(path, 0.2, 0.2, 660) > 0.02);
        Assert.True(Rms(path, 0.2, 0.6) > 0.01);
    }

    [Fact]
    public async Task ExtendPictureRefusesWhenHostCutsAffectTheSelection()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 2,
            audioEnd: 2.5,
            edits: [HostPause(0.5, 1)],
            findings: [TimingFinding()]);
        AssertTimingRefused(fixture, TimingDecisions.Extend);
        var project = fixture.Store.Load();
        Assert.Equal(2, project.Scenes[0].Selections[0].End, 3);
        Assert.Contains(project.Scenes[0].Edits, edit => edit.Id == "host-cut");
        Assert.NotEqual(DecisionStates.Approved, project.Findings[0].Decision);
    }

    [Fact]
    public async Task TrimPickupRefusesWhenHostCutsAffectTheSelection()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 2,
            edits: [HostPause(0.5, 1)],
            findings: [TimingFinding()]);
        AssertTimingRefused(fixture, TimingDecisions.Trim);
        Assert.Equal(2, fixture.Store.Load().Scenes[0].Selections[0].End, 3);
        Assert.Single(fixture.Store.Load().Scenes[0].Edits);
        Assert.NotEqual(DecisionStates.Approved, fixture.Store.Load().Findings[0].Decision);
    }

    [Fact]
    public async Task ExtendPictureRefusesWhenConsumedHostMaterialHasCuts()
    {
        var fixture = await OverlayFixtureAsync(
            hostStart: 1,
            hostEnd: 2,
            audioEnd: 2,
            extraSelections: [new SourceSelection { SourceAssetId = "host", Start = 2, End = 3 }],
            edits:
            [
                new SceneEdit
                {
                    Id = "pickup-cut",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    Start = 0.5,
                    End = 1,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                },
                HostPause(2.1, 2.2),
            ],
            findings: [TimingFinding()]);
        AssertTimingRefused(fixture, TimingDecisions.Extend);
        var project = fixture.Store.Load();
        Assert.Equal(1, project.Scenes[0].Selections[0].Start, 3);
        Assert.Equal(2, project.Scenes[0].Selections[0].End, 3);
        Assert.Equal(2, project.Scenes[0].Selections[1].Start, 3);
        Assert.Contains(project.Scenes[0].Edits, edit => edit.Id == "host-cut");
        Assert.NotEqual(DecisionStates.Approved, project.Findings[0].Decision);
    }

    [Fact]
    public async Task OverlayWithHostCutsBlocksAcceptanceAndExport()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 3,
            audioEnd: 2.5,
            edits: [HostPause(0.5, 1)]);
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);
        var before = File.ReadAllText(fixture.Store.ProjectPath);
        var error = Assert.Throws<VideoEditException>(
            () => fixture.Service.AcceptScene("scene", fixture.Store.Load().Revision));
        Assert.Equal(CompositionResolver.HostCutRestriction, error.Message);
        Assert.Equal(before, File.ReadAllText(fixture.Store.ProjectPath));
        Assert.Null(fixture.Store.Load().Scenes[0].Acceptance);
        var blocks = CompositionResolver.ExportBlocks(fixture.Store.Load());
        Assert.Contains(blocks, block => block.Contains(CompositionResolver.HostCutRestriction, StringComparison.Ordinal));
        var export = await Record.ExceptionAsync(() => fixture.Renderer.ExportAsync(CancellationToken.None));
        Assert.NotNull(export);
        Assert.Contains(CompositionResolver.HostCutRestriction, export.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrimPickupSupersedesRejectedOverlaps()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 1,
            edits:
            [
                new SceneEdit
                {
                    Id = "rejected-tail",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    Start = 2,
                    End = 2.5,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Rejected,
                }
            ],
            findings: [TimingFinding()]);
        fixture.Service.ResolveTiming("timing", TimingDecisions.Trim, fixture.Store.Load().Revision);
        var project = fixture.Store.Load();
        Assert.DoesNotContain(project.Scenes[0].Edits, edit => edit.Id == "rejected-tail");
        var trim = Assert.Single(project.Scenes[0].Edits);
        Assert.Equal(1, trim.Start, 3);
        Assert.Equal(3, trim.End, 3);
        Assert.Equal(DecisionStates.Approved, trim.Decision);
        Assert.Equal(DecisionStates.Approved, project.Findings[0].Decision);
        Assert.False(CompositionResolver.HasUnresolvedTiming(project, project.Scenes[0]));
        await ApproveAsync(fixture);
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var path = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(Amplitude(path, 0.2, 0.2, 660) > 0.02);
    }

    [Fact]
    public async Task RejectPickupRestoresThePreparedHostInterval()
    {
        var fixture = await OverlayFixtureAsync(
            hostEnd: 1,
            extraSelections: [new SourceSelection { SourceAssetId = "host", Start = 2, End = 3 }],
            findings: [TimingFinding()]);
        fixture.Store.Mutate(fixture.Store.Load().Revision, project =>
        {
            project.Scenes[0].Selections[0].ReplaceEnd = 2;
            return true;
        });
        fixture.Service.ResolveTiming("timing", TimingDecisions.Reject, fixture.Store.Load().Revision);
        var project = fixture.Store.Load();
        Assert.Equal(
            [(0d, 2d, false), (2d, 3d, false)],
            project.Scenes[0].Selections.Select(item => (item.Start, item.End, item.HasAudioOverlay)));
        Assert.Null(project.Scenes[0].Selections[0].ReplaceEnd);
        await ApproveAsync(fixture);
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var path = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(Amplitude(path, 1.3, 0.2, 440) > 0.02);
        Assert.True(Amplitude(path, 1.3, 0.2, 660) < 0.01);
    }

    [Fact]
    public async Task RejectPickupRestoresHostAudio()
    {
        var fixture = await OverlayFixtureAsync(hostEnd: 1, findings: [TimingFinding()]);
        fixture.Service.ResolveTiming("timing", TimingDecisions.Reject, fixture.Store.Load().Revision);
        var selection = Assert.Single(fixture.Store.Load().Scenes[0].Selections);
        Assert.False(selection.HasAudioOverlay);
        await ApproveAsync(fixture);
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var path = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(Amplitude(path, 0.2, 0.2, 440) > 0.02);
        Assert.True(Amplitude(path, 0.2, 0.2, 660) < 0.01);
    }

    [Fact]
    public async Task VideoAfterDonorEndRendersSilence()
    {
        var fixture = await OverlayFixtureAsync(
            edits:
            [
                new SceneEdit
                {
                    Id = "cut",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    Start = 0,
                    End = 2,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                },
                new SceneEdit
                {
                    Id = "host-cut",
                    SceneId = "scene",
                    SourceAssetId = "host",
                    Start = 1,
                    End = 2,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                }
            ]);
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);
        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        var keeps = CompositionResolver.Resolve(fixture.Store.Load()).Scenes[0].KeepRanges;
        Assert.Equal(2, keeps.Count);
        Assert.NotNull(keeps[1].AudioKeeps);
        Assert.Empty(keeps[1].AudioKeeps!);
        Assert.True(Rms(preview, 0.2, 0.2) > 0.01);
        Assert.True(Rms(preview, 1.5, 0.2) < 0.01);
    }

    [Fact]
    public async Task NeverSayNeverIsNotADroppedRetake()
    {
        var fixture = await PhrasePickupFixtureAsync();
        var words = new List<SpokenWord>
        {
            new("Never", "never", 0, 1),
            new("say", "say", 2, 2.4),
            new("never", "never", 2.4, 3),
        };
        var snap = PickupBounds.Snap(
            new TimeRange(0, 3),
            [new SilenceInterval(1, 2)],
            lastWordEnd: 3,
            maxDuration: 3,
            spokenWords: words);
        Assert.Empty(snap.DroppedTakes);
        Assert.NotEmpty(snap.FlaggedTakes ?? []);
        Assert.NotEmpty(snap.PauseCuts);

        fixture.Store.Mutate(fixture.Store.Load().Revision, project =>
        {
            foreach (var cut in snap.PauseCuts)
            {
                project.Scenes[0].Edits.Add(new SceneEdit
                {
                    Id = $"pause-{cut.Start:0.###}",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    SourceFingerprint = pickupFp,
                    Start = cut.Start,
                    End = cut.End,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                });
            }

            return true;
        });
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);
        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        Assert.True(Amplitude(preview, 0.2, 0.2, 440) > 0.02);
        Assert.True(Amplitude(preview, 1.7, 0.2, 660) > 0.02);
    }

    [Fact]
    public async Task SharedWordAcrossAPauseIsNotADroppedRetake()
    {
        var fixture = await PhrasePickupFixtureAsync();
        var words = new List<SpokenWord>
        {
            new("The", "the", 0, 0.2),
            new("planter", "planter", 0.2, 0.5),
            new("matches", "matches", 0.5, 0.9),
            new("the", "the", 2, 2.2),
            new("script", "script", 2.2, 3),
        };
        var snap = PickupBounds.Snap(
            new TimeRange(0, 3),
            [new SilenceInterval(1, 2)],
            lastWordEnd: 3,
            maxDuration: 3,
            spokenWords: words);
        Assert.Empty(snap.DroppedTakes);
        Assert.NotEmpty(snap.PauseCuts);

        fixture.Store.Mutate(fixture.Store.Load().Revision, project =>
        {
            foreach (var cut in snap.PauseCuts)
            {
                project.Scenes[0].Edits.Add(new SceneEdit
                {
                    Id = $"pause-{cut.Start:0.###}",
                    SceneId = "scene",
                    SourceAssetId = "pickup",
                    SourceFingerprint = pickupFp,
                    Start = cut.Start,
                    End = cut.End,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                });
            }

            return true;
        });
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);
        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        Assert.True(Amplitude(preview, 0.2, 0.2, 440) > 0.02);
        Assert.True(Amplitude(preview, 1.7, 0.2, 660) > 0.02);
    }

    [Fact]
    public async Task BoundaryHoldUsesTheLastDecodedFrame()
    {
        var fixture = await ThreeSceneFixtureAsync();
        await ApproveAsync(fixture);
        var preview = Path.Combine(fixture.Renderer.PreviewsDirectory, "rough-cut.mp4");
        var exported = await fixture.Renderer.ExportAsync(CancellationToken.None);
        var exportPath = Path.Combine(fixture.Renderer.ExportsDirectory, exported.FileName);
        Assert.True(IsBlue(Pixel(hostPath!, 2.9)));
        Assert.True(IsBlue(Pixel(preview, 2.9)));
        Assert.True(IsBlue(Pixel(preview, 3.1)));
        Assert.True(IsBlue(Pixel(exportPath, 3.1)));
    }

    private async Task<Fixture> OverlayFixtureAsync(
        double hostEnd = 3,
        double hostStart = 0,
        double audioEnd = 3,
        string? roomPath = null,
        IReadOnlyList<SceneEdit>? edits = null,
        IReadOnlyList<SourceFinding>? findings = null,
        IReadOnlyList<SourceSelection>? extraSelections = null)
    {
        await EnsureMediaAsync();
        foreach (var edit in edits ?? [])
        {
            edit.SourceFingerprint = edit.SourceAssetId == "pickup" ? pickupFp : hostFp;
        }

        foreach (var extra in extraSelections ?? [])
        {
            extra.SourceFingerprint = extra.SourceAssetId == "pickup" ? pickupFp : hostFp;
        }

        foreach (var finding in findings ?? [])
        {
            if (finding.SourceAssetId == "pickup")
            {
                finding.SourceFingerprint = pickupFp;
            }
        }

        var dir = Path.Combine(root, "p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "edit"));
        var options = new VideoEditOptions { ProjectDirectory = dir };
        var store = new ProjectStore(options);
        var gate = new MediaGate();
        var renderer = new MediaRenderer(store, gate, runner, Options.Create(options));
        store.SaveNew(new ProjectDocument
        {
            Name = "latest",
            Revision = 1,
            Sources =
            [
                new SourceAsset { Id = "host", Path = hostPath!, Fingerprint = hostFp, DurationSeconds = 3 },
                new SourceAsset { Id = "pickup", Path = pickupPath!, Fingerprint = pickupFp, DurationSeconds = 3, Kind = "audio" },
            ],
            Scenes =
            [
                new Scene
                {
                    Id = "scene",
                    Position = 1,
                    Selections =
                    [
                        new SourceSelection
                        {
                            SourceAssetId = "host",
                            SourceFingerprint = hostFp,
                            Start = hostStart,
                            End = hostEnd,
                            AudioSourceAssetId = "pickup",
                            AudioSourceFingerprint = pickupFp,
                            AudioStart = 0,
                            AudioEnd = audioEnd,
                        },
                        .. extraSelections ?? [],
                    ],
                    Edits = edits?.ToList() ?? [],
                }
            ],
            Findings = findings?.ToList() ?? [],
            Narration = roomPath is null ? new() : new() { RoomTone = new RoomToneClip { Path = roomPath } },
            Output = new OutputSettings { Width = 128, Height = 72, FrameRate = 30 },
        });
        return new Fixture(store, renderer, new ProjectService(store, gate, renderer), gate);
    }

    private static SourceFinding TimingFinding() =>
        new()
        {
            Id = "timing",
            SourceAssetId = "pickup",
            Start = 0,
            End = 3,
            Kind = PickupFindingKinds.Timing,
            Reason = "Pickup speech is longer than host picture.",
        };

    private static SceneEdit HostPause(double start, double end) =>
        new()
        {
            Id = "host-cut",
            SceneId = "scene",
            SourceAssetId = "host",
            Start = start,
            End = end,
            Kind = EditKinds.Pause,
            ProposalRevision = 1,
            DecisionProposalRevision = 1,
            Decision = DecisionStates.Approved,
        };

    private static void AssertTimingRefused(Fixture fixture, string decision)
    {
        var before = File.ReadAllText(fixture.Store.ProjectPath);
        var revision = fixture.Store.Load().Revision;
        var error = Assert.Throws<VideoEditException>(
            () => fixture.Service.ResolveTiming("timing", decision, revision));
        Assert.Equal(CompositionResolver.HostCutRestriction, error.Message);
        Assert.Equal(before, File.ReadAllText(fixture.Store.ProjectPath));
        Assert.Equal(revision, fixture.Store.Load().Revision);
    }

    private async Task<Fixture> TwoSceneFixtureAsync(string? roomPath = null)
    {
        await EnsureMediaAsync();
        var dir = Path.Combine(root, "p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "edit"));
        var options = new VideoEditOptions { ProjectDirectory = dir };
        var store = new ProjectStore(options);
        var gate = new MediaGate();
        var renderer = new MediaRenderer(store, gate, runner, Options.Create(options));
        store.SaveNew(new ProjectDocument
        {
            Name = "latest",
            Revision = 1,
            Sources = [new SourceAsset { Id = "host", Path = hostPath!, Fingerprint = hostFp, DurationSeconds = 3 }],
            Scenes =
            [
                new Scene
                {
                    Id = "scene",
                    Position = 1,
                    Selections = [new SourceSelection { SourceAssetId = "host", SourceFingerprint = hostFp, Start = 0, End = 1 }],
                },
                new Scene
                {
                    Id = "scene2",
                    Position = 2,
                    Selections = [new SourceSelection { SourceAssetId = "host", SourceFingerprint = hostFp, Start = 1, End = 2 }],
                }
            ],
            Narration = roomPath is null ? new() : new() { RoomTone = new RoomToneClip { Path = roomPath } },
            Output = new OutputSettings { Width = 128, Height = 72, FrameRate = 30 },
        });
        return new Fixture(store, renderer, new ProjectService(store, gate, renderer), gate);
    }

    private async Task<Fixture> ThreeSceneFixtureAsync()
    {
        await EnsureMediaAsync();
        var dir = Path.Combine(root, "p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "edit"));
        var options = new VideoEditOptions { ProjectDirectory = dir };
        var store = new ProjectStore(options);
        var gate = new MediaGate();
        var renderer = new MediaRenderer(store, gate, runner, Options.Create(options));
        store.SaveNew(new ProjectDocument
        {
            Name = "latest",
            Revision = 1,
            Sources = [new SourceAsset { Id = "host", Path = hostPath!, Fingerprint = hostFp, DurationSeconds = 3 }],
            Scenes =
            [
                new Scene
                {
                    Id = "scene",
                    Position = 1,
                    Selections = [new SourceSelection { SourceAssetId = "host", SourceFingerprint = hostFp, Start = 0, End = 3 }],
                },
                new Scene
                {
                    Id = "scene2",
                    Position = 2,
                    Selections = [new SourceSelection { SourceAssetId = "host", SourceFingerprint = hostFp, Start = 0, End = 1 }],
                },
                new Scene
                {
                    Id = "scene3",
                    Position = 3,
                    Selections = [new SourceSelection { SourceAssetId = "host", SourceFingerprint = hostFp, Start = 1, End = 2 }],
                }
            ],
            Output = new OutputSettings { Width = 128, Height = 72, FrameRate = 30 },
        });
        return new Fixture(store, renderer, new ProjectService(store, gate, renderer), gate);
    }

    private async Task<Fixture> PhrasePickupFixtureAsync()
    {
        Directory.CreateDirectory(root);
        hostPath = Path.Combine(root, "host-phrase.mp4");
        pickupPath = Path.Combine(root, "pickup-phrase.wav");
        await Ffmpeg(
            "-f", "lavfi", "-i", "color=c=red:s=128x72:r=30",
            "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=48000",
            "-t", "3", "-ac", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
            hostPath);
        await Ffmpeg(
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=1",
            "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000:duration=1",
            "-filter_complex", "[0:a][1:a][2:a]concat=n=3:v=0:a=1,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a]",
            "-map", "[a]",
            pickupPath);
        hostFp = MediaRenderer.FingerprintFile(hostPath);
        pickupFp = MediaRenderer.FingerprintFile(pickupPath);
        return await OverlayFixtureAsync();
    }

    private async Task EnsureMediaAsync()
    {
        if (hostPath is not null)
        {
            return;
        }

        Directory.CreateDirectory(root);
        hostPath = Path.Combine(root, "host.mp4");
        pickupPath = Path.Combine(root, "pickup.wav");
        await Ffmpeg(
            "-f", "lavfi", "-i", "color=c=red:s=128x72:r=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-vf", "drawbox=color=blue:t=fill:enable='gte(t,2.5)'",
            "-t", "3", "-ac", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
            hostPath);
        await Ffmpeg("-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000", "-t", "3", "-ac", "2", pickupPath);
        hostFp = MediaRenderer.FingerprintFile(hostPath);
        pickupFp = MediaRenderer.FingerprintFile(pickupPath);
    }

    private async Task ApproveAsync(Fixture fixture)
    {
        await fixture.Renderer.BuildRoughCutAsync(CancellationToken.None);
        foreach (var scene in fixture.Store.Load().Scenes)
        {
            fixture.Service.AcceptScene(scene.Id, fixture.Store.Load().Revision);
        }

        fixture.Service.ApproveFinal(fixture.Store.Load().Revision);
    }

    private async Task WriteAnalysisAsync(string projectDirectory, string volume)
    {
        var analysis = Path.Combine(projectDirectory, "edit", "analysis");
        Directory.CreateDirectory(analysis);
        await Ffmpeg(
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000",
            "-af", "volume=" + volume,
            "-t", "3", "-ac", "1", "-c:a", "pcm_s16le",
            Path.Combine(analysis, "audio16k.wav"));
        VadSpeechMap.Write(Path.Combine(analysis, VadSpeechMap.FileName), new VadSpeechMap());
    }

    private async Task RoomAsync(string path, int frequency)
    {
        Directory.CreateDirectory(root);
        await Ffmpeg("-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000", "-t", "6", "-ac", "2", path);
    }

    private async Task Ffmpeg(params string[] arguments)
    {
        var result = await runner.RunAsync(
            "ffmpeg",
            ["-hide_banner", "-y", .. arguments],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private float[] ReadPcm(string path, double start, double duration)
    {
        var pcm = Path.Combine(root, $"pcm-{Guid.NewGuid():N}.f32");
        var result = runner.RunAsync(
            "ffmpeg",
            [
                "-v", "error", "-y",
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path,
                "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
                "-f", "f32le",
                "-ac", "1",
                "-ar", "48000",
                pcm,
            ],
            TimeSpan.FromSeconds(20),
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, result.ExitCode);
        var bytes = File.ReadAllBytes(pcm);
        File.Delete(pcm);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private double Amplitude(string path, double start, double duration, double frequency)
    {
        var samples = ReadPcm(path, start, duration);
        var real = 0.0;
        var imag = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var angle = 2 * Math.PI * frequency * i / 48000.0;
            real += samples[i] * Math.Cos(angle);
            imag -= samples[i] * Math.Sin(angle);
        }

        return 2 * Math.Sqrt((real * real) + (imag * imag)) / samples.Length;
    }

    private double Rms(string path, double start, double duration)
    {
        var samples = ReadPcm(path, start, duration);
        return Math.Sqrt(samples.Sum(sample => sample * sample) / samples.Length);
    }

    private int[] Pixel(string path, double time)
    {
        var png = Path.Combine(root, $"pix-{Guid.NewGuid():N}.rgb");
        var result = runner.RunAsync(
            "ffmpeg",
            [
                "-v", "error", "-y",
                "-ss", time.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path,
                "-frames:v", "1",
                "-vf", "scale=1:1",
                "-f", "rawvideo",
                "-pix_fmt", "rgb24",
                png,
            ],
            TimeSpan.FromSeconds(15),
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, result.ExitCode);
        var bytes = File.ReadAllBytes(png);
        File.Delete(png);
        return [bytes[0], bytes[1], bytes[2]];
    }

    private static bool IsBlue(int[] pixel) => pixel[2] >= 200 && pixel[0] < 40 && pixel[1] < 40;

    public void Dispose()
    {
        runner.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(ProjectStore Store, MediaRenderer Renderer, ProjectService Service, MediaGate Gate);
}

using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class M2ValidationTests
{
    private const string HostFp = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PickupFp = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void AudioKeepsArePartOfTheApprovalIdentity()
    {
        var project = OverlayProject();
        project.Scenes[0].Edits =
        [
            new SceneEdit
            {
                Id = "audio-cut",
                SceneId = "scene",
                SourceAssetId = "pickup",
                SourceFingerprint = PickupFp,
                Start = 0.8,
                End = 1.2,
                Kind = EditKinds.Pause,
                ProposalRevision = 1,
                Decision = DecisionStates.Rejected,
            }
        ];
        var before = CompositionHasher.FullHash(project);
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        var after = CompositionResolver.Resolve(project).Scenes[0].KeepRanges[0];
        Assert.NotEqual(before, CompositionHasher.FullHash(project));
        Assert.Equal(
            [new TimeRange(0, 0.8), new TimeRange(1.2, 3)],
            after.AudioKeeps);
    }

    [Fact]
    public void EmptyAudioKeepsStayEmptyAndRenderAsSilence()
    {
        var project = OverlayProject();
        project.Scenes[0].Edits =
        [
            new SceneEdit
            {
                Id = "audio-cut",
                SceneId = "scene",
                SourceAssetId = "pickup",
                SourceFingerprint = PickupFp,
                Start = 0,
                End = 3,
                Kind = EditKinds.Pause,
                ProposalRevision = 1,
                DecisionProposalRevision = 1,
                Decision = DecisionStates.Approved,
            }
        ];
        var keep = Assert.Single(CompositionResolver.Resolve(project).Scenes[0].KeepRanges);
        Assert.NotNull(keep.AudioKeeps);
        Assert.Empty(keep.AudioKeeps);
        var filter = MediaRenderer.BuildFilterGraph([keep], project.Output);
        Assert.Contains("anullsrc", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("atrim=start=0:end=3", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneGapPolicyIsHashedFromTheLiveFile()
    {
        var (store, _, _, root) = TestSupport.CreateStore();
        var project = OverlayProject();
        project.Scenes[0].Selections[0].AudioSourceAssetId = null;
        project.Scenes[0].Selections[0].AudioStart = null;
        project.Scenes[0].Selections[0].AudioEnd = null;
        project.Scenes.Add(new Scene
        {
            Id = "scene-2",
            Position = 2,
            Selections =
            [
                new SourceSelection { SourceAssetId = "host", SourceFingerprint = HostFp, Start = 1, End = 2 }
            ],
        });
        project.Scenes[0].Selections[0].End = 1;
        store.SaveNew(project);
        var path = Path.Combine(root, "edit", EditingPolicy.FileName);
        var policy = EditingPolicy.Clone(EditingPolicy.Default);
        policy.SceneBoundaryGapS = 0.25;
        EditingPolicy.Write(path, policy);
        var before = CompositionHasher.FullHash(store.Load());
        Assert.Equal(2.25, CompositionResolver.Resolve(store.Load()).DurationSeconds, 3);

        policy.SceneBoundaryGapS = 1;
        EditingPolicy.Write(path, policy);
        var loaded = store.Load();
        Assert.NotEqual(before, CompositionHasher.FullHash(loaded));
        Assert.Equal(3, CompositionResolver.Resolve(loaded).DurationSeconds, 3);
    }

    [Fact]
    public void OrdinaryPauseIsNotADroppedRetake()
    {
        var snap = PickupBounds.Snap(new TimeRange(0, 3), [new SilenceInterval(1, 2)], lastWordEnd: 3, maxDuration: 3);
        Assert.Equal(0, snap.Range.Start, 3);
        Assert.Equal(3, snap.Range.End, 3);
        Assert.Empty(snap.DroppedTakes);
        Assert.NotEmpty(snap.PauseCuts);
        Assert.False(snap.TimingMismatch);
    }

    [Fact]
    public void LongPickupIsATimingFindingNotASilentCap()
    {
        var snap = PickupBounds.Snap(new TimeRange(0, 3), [], lastWordEnd: 3, maxDuration: 1);
        Assert.Equal(0, snap.Range.Start, 3);
        Assert.Equal(3, snap.Range.End, 3);
        Assert.True(snap.TimingMismatch);
        Assert.Empty(snap.DroppedTakes);
    }

    [Fact]
    public void SpliceWindowIsSourceQualified()
    {
        var scene = new Scene
        {
            Selections =
            [
                new SourceSelection { SourceAssetId = "host", Start = 0, End = 3 },
                new SourceSelection { SourceAssetId = "other", Start = 0, End = 3 },
            ],
            Edits =
            [
                new SceneEdit
                {
                    Id = "audio-cut",
                    SourceAssetId = "other",
                    Start = 1,
                    End = 1.2,
                    Kind = EditKinds.Pause,
                    Decision = DecisionStates.Pending,
                    ProposalRevision = 1,
                }
            ],
        };
        var ranges = CompositionResolver.SpliceRanges(scene, scene.Edits[0], 0.5);
        Assert.All(ranges, range => Assert.Equal("other", range.SourceAssetId));
        Assert.Equal(
            [new TimeRange(0.5, 1), new TimeRange(1.2, 1.7)],
            ranges.Select(range => range.Range));
    }

    [Fact]
    public void OriginalPreviewExcludesAnApprovedTarget()
    {
        var scene = new Scene
        {
            Selections = [new SourceSelection { SourceAssetId = "host", Start = 0, End = 3 }],
            Edits =
            [
                new SceneEdit
                {
                    Id = "audio-cut",
                    SourceAssetId = "host",
                    Start = 1,
                    End = 1.2,
                    Kind = EditKinds.Pause,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                    Decision = DecisionStates.Approved,
                }
            ],
        };
        var original = CompositionResolver.OriginalRanges(scene, scene.Edits[0], 0.5);
        var edited = CompositionResolver.SpliceRanges(scene, scene.Edits[0], 0.5);
        Assert.False(original.SequenceEqual(edited));
        Assert.Contains(original, range => range.Range.Contains(scene.Edits[0].Range));
        Assert.DoesNotContain(edited, range => range.Range.Overlaps(scene.Edits[0].Range));
    }

    [Fact]
    public void OverlayAudioOffsetFollowsCutsAndWindowClips()
    {
        var project = OverlayProject();
        project.Scenes[0].Edits =
        [
            new SceneEdit
            {
                Id = "cut",
                SceneId = "scene",
                SourceAssetId = "host",
                SourceFingerprint = HostFp,
                Start = 1,
                End = 2,
                Kind = EditKinds.Flub,
                ProposalRevision = 1,
                DecisionProposalRevision = 1,
                Decision = DecisionStates.Approved,
            }
        ];
        var resolved = CompositionResolver.ResolveScene(project, project.Scenes[0]);
        Assert.Equal(
            [(0, 1, 0, 1), (2, 3, 2, 3)],
            resolved.KeepRanges.Select(range => (
                range.Start,
                range.End,
                range.AudioRange?.Start ?? -1,
                range.AudioRange?.End ?? -1)));

        project.Scenes[0].Edits[0].Decision = DecisionStates.Pending;
        project.Scenes[0].Edits[0].Start = 2;
        project.Scenes[0].Edits[0].End = 2.2;
        var clipped = CompositionResolver.SpliceRanges(project.Scenes[0], project.Scenes[0].Edits[0], 0.2);
        Assert.Equal(2, clipped.Count);
        Assert.Equal(1.8, clipped[0].Start, 3);
        Assert.Equal(2, clipped[0].End, 3);
        Assert.Equal(1.8, clipped[0].AudioRange?.Start ?? 0, 3);
        Assert.Equal(2, clipped[0].AudioRange?.End ?? 0, 3);
        Assert.Equal(2.2, clipped[1].Start, 3);
        Assert.Equal(2.4, clipped[1].End, 3);
        Assert.Equal(2.2, clipped[1].AudioRange?.Start ?? 0, 3);
        Assert.Equal(2.4, clipped[1].AudioRange?.End ?? 0, 3);
    }

    [Fact]
    public void FindingPickupReplacesTheFindingSource()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = OverlayProject();
        project.Sources.Add(new SourceAsset { Id = "other", Path = "/tmp/other.mp4", Fingerprint = HostFp, DurationSeconds = 3 });
        project.Scenes[0].Selections[0].AudioSourceAssetId = null;
        project.Scenes[0].Selections[0].AudioStart = null;
        project.Scenes[0].Selections[0].AudioEnd = null;
        project.Scenes[0].Selections.Add(new SourceSelection { SourceAssetId = "other", SourceFingerprint = HostFp, Start = 0, End = 3 });
        project.Findings =
        [
            new SourceFinding
            {
                Id = "finding",
                SourceAssetId = "other",
                SourceFingerprint = HostFp,
                Start = 1,
                End = 2,
                Kind = ScriptDriftKinds.Substitution,
                ScriptText = "correct",
                SpokenText = "wrong",
            }
        ];
        store.SaveNew(project);
        var updated = service.ApplyPickup("pickup", 0, 1, 1, findingId: "finding", audioOnly: true);
        Assert.Contains(updated.Scenes[0].Selections, item =>
            item.SourceAssetId == "other" && item.AudioSourceAssetId == "pickup" && item.Start == 1 && item.End == 2);
        Assert.DoesNotContain(updated.Scenes[0].Selections, item =>
            item.SourceAssetId == "host" && item.AudioSourceAssetId == "pickup");
        Assert.Empty(ProjectValidator.Collect(updated));
    }

    [Fact]
    public void HeadingMatchesOnlyAtTheOpening()
    {
        var spoken = WhisperWords.TokenizeScript("I painted a planter today")
            .Select((token, i) => new SpokenWord(token.Text, token.Normalized, i, i + 1))
            .ToList();
        var project = new ProjectDocument
        {
            Sources = [new SourceAsset { Id = "host", Fingerprint = HostFp, DurationSeconds = spoken.Count }],
        };
        var findings = ScriptAligner.Align("# planter\n\nI painted a planter today.", spoken, project, "host", HostFp);
        Assert.Empty(findings);
    }

    [Fact]
    public void UnusedRoomMetadataDoesNotCreateARoomDependency()
    {
        var project = OverlayProject();
        project.Narration.RoomTone = new RoomToneClip { Path = "/tmp/room.wav", Start = 0, End = 6 };
        Assert.True(RenderIdentity.UsesRoomBed(project));
        project.Scenes[0].Selections[0].AudioSourceAssetId = null;
        project.Scenes[0].Selections[0].AudioStart = null;
        project.Scenes[0].Selections[0].AudioEnd = null;
        Assert.False(RenderIdentity.UsesRoomBed(project));
    }

    [Fact]
    public void RoomFingerprintIsPartOfTheLiveIdentity()
    {
        var (store, _, _, root) = TestSupport.CreateStore();
        var room = Path.Combine(root, "room.wav");
        File.WriteAllBytes(room, [1, 2, 3, 4]);
        var project = OverlayProject();
        project.Narration.RoomTone = new RoomToneClip { Path = room, Start = 0, End = 6 };
        store.SaveNew(project);
        var before = CompositionHasher.FullHash(store.Load());
        File.WriteAllBytes(room, [9, 9, 9, 9]);
        Assert.NotEqual(before, CompositionHasher.FullHash(store.Load()));
    }

    private static ProjectDocument OverlayProject() =>
        new()
        {
            Name = "m2",
            Revision = 1,
            Sources =
            [
                new SourceAsset { Id = "host", Path = "/tmp/host.mp4", Fingerprint = HostFp, DurationSeconds = 3 },
                new SourceAsset { Id = "pickup", Path = "/tmp/pickup.wav", Fingerprint = PickupFp, DurationSeconds = 3, Kind = "audio" },
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
                            SourceFingerprint = HostFp,
                            Start = 0,
                            End = 3,
                            AudioSourceAssetId = "pickup",
                            AudioSourceFingerprint = PickupFp,
                            AudioStart = 0,
                            AudioEnd = 3,
                        }
                    ],
                }
            ],
            Output = new OutputSettings { Width = 128, Height = 72, FrameRate = 30 },
        };
}

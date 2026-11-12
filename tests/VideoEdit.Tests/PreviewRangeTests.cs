using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class PreviewRangeTests
{
    [Fact]
    public void OriginalRangeUsesTheSameContextWindowAsTheSplice()
    {
        var scene = new Scene
        {
            Selections = [new SourceSelection { SourceAssetId = "source-1", Start = 0, End = 20 }],
            Edits =
            [
                new SceneEdit { SourceAssetId = "source-1", Start = 10, End = 12 }
            ],
        };
        var edit = scene.Edits[0];
        var original = MediaRenderer.OriginalRange(scene, edit, 1.5);
        var splice = MediaRenderer.SpliceRanges(scene, edit, 1.5);

        Assert.Equal(new TimeRange(8.5, 13.5), original);
        Assert.Equal(new TimeRange(8.5, 10), splice[0].Range);
        Assert.Equal(new TimeRange(12, 13.5), splice[1].Range);
        Assert.Equal(original.Duration, splice[0].Duration + edit.Range.Duration + splice[1].Duration, 6);
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, splice[0].FadeOutSeconds);
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, splice[1].FadeInSeconds);
    }

    [Fact]
    public void OriginalRangeStaysInsideTheSelection()
    {
        var scene = new Scene
        {
            Selections = [new SourceSelection { SourceAssetId = "source-1", Start = 0, End = 5 }],
            Edits = [new SceneEdit { SourceAssetId = "source-1", Start = 0, End = 1 }],
        };
        Assert.Equal(new TimeRange(0, 2.5), MediaRenderer.OriginalRange(scene, scene.Edits[0], 1.5));
    }

    [Fact]
    public void SpliceAppliesApprovedNeighborsAndLeavesPendingInTheWindow()
    {
        var scene = ContextScene();
        var flub = scene.Edits[0];
        var splice = CompositionResolver.SpliceRanges(scene, flub, 1.5);
        var original = CompositionResolver.OriginalRanges(scene, flub, 1.5);

        Assert.Equal(
            [new TimeRange(8.5, 8.8), new TimeRange(9.2, 10), new TimeRange(12, 13.5)],
            splice.Select(range => range.Range));
        Assert.Equal(
            [new TimeRange(8.5, 8.8), new TimeRange(9.2, 13.5)],
            original.Select(range => range.Range));
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, splice[0].FadeOutSeconds);
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, splice[1].FadeInSeconds);
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, splice[1].FadeOutSeconds);
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, splice[2].FadeInSeconds);
    }

    [Fact]
    public void ApprovingTheFirstEditChangesTheSecondSpliceHashAndDropsItsRemovedAudio()
    {
        var first = new SceneEdit
        {
            Id = "e-first",
            SourceAssetId = "source-1",
            Start = 10,
            End = 10.4,
            Kind = EditKinds.Flub,
            RemovedText = "PLA",
            Reason = "flub",
            Decision = DecisionStates.Pending,
            ProposalRevision = 1,
        };
        var second = new SceneEdit
        {
            Id = "e-second",
            SourceAssetId = "source-1",
            Start = 11.2,
            End = 11.5,
            Kind = EditKinds.Flub,
            Reason = "later flub",
            Decision = DecisionStates.Pending,
            ProposalRevision = 1,
        };
        var scene = new Scene
        {
            Selections = [new SourceSelection { SourceAssetId = "source-1", Start = 0, End = 20 }],
            Edits = [first, second],
        };

        Assert.InRange(second.Start - first.End, 0, 1.5);
        var beforeHash = CompositionHasher.SpliceFingerprint(scene, second, 1.5);
        var before = CompositionResolver.SpliceRanges(scene, second, 1.5);
        Assert.Contains(before, range => range.Range.Overlaps(first.Range));

        first.Decision = DecisionStates.Approved;
        first.DecisionProposalRevision = 1;

        var afterHash = CompositionHasher.SpliceFingerprint(scene, second, 1.5);
        Assert.NotEqual(beforeHash, afterHash);

        var after = CompositionResolver.SpliceRanges(scene, second, 1.5);
        Assert.DoesNotContain(after, range => range.Range.Overlaps(first.Range));
        Assert.Equal(
            [new TimeRange(9.7, 10), new TimeRange(10.4, 11.2), new TimeRange(11.5, 13)],
            after.Select(range => range.Range));

        var filter = MediaRenderer.BuildFilterGraph(after, new OutputSettings());
        Assert.DoesNotContain("trim=start=10:end=10.4", filter, StringComparison.Ordinal);
        Assert.Contains("trim=start=10.4:end=11.2", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovingThroughRecordDecisionInvalidatesTheNeighborSplice()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-2",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 9,
            End = 9.3,
            Kind = EditKinds.Flub,
            Reason = "later",
            Decision = DecisionStates.Pending,
            ProposalRevision = 1,
        });
        store.SaveNew(project);
        var scene = store.Load().Scenes[0];
        var second = scene.Edits.First(item => item.Id == "e-2");
        var before = CompositionHasher.SpliceFingerprint(scene, second, 1.5);

        var updated = service.RecordDecision("e-1", DecisionStates.Approved, null, 1);
        scene = updated.Scenes[0];
        second = scene.Edits.First(item => item.Id == "e-2");
        Assert.NotEqual(before, CompositionHasher.SpliceFingerprint(scene, second, 1.5));
        Assert.DoesNotContain(
            CompositionResolver.SpliceRanges(scene, second, 1.5),
            range => range.Range.Overlaps(scene.Edits.First(item => item.Id == "e-1").Range));
    }

    [Fact]
    public void SpliceFingerprintChangesWhenAnApprovedNeighborInTheWindowChanges()
    {
        var scene = ContextScene();
        var flub = scene.Edits[0];
        var before = CompositionHasher.SpliceFingerprint(scene, flub, 1.5);

        scene.Edits[1].Start = 8.7;
        Assert.NotEqual(before, CompositionHasher.SpliceFingerprint(scene, flub, 1.5));

        scene.Edits[1].Start = 8.8;
        scene.Edits[2].Start = 12.5;
        Assert.Equal(before, CompositionHasher.SpliceFingerprint(scene, flub, 1.5));

        scene.Edits[2].Start = 12.4;
        scene.Edits[3].End = 3.2;
        Assert.Equal(before, CompositionHasher.SpliceFingerprint(scene, flub, 1.5));
    }

    [Fact]
    public void CrossfadeUsesOverlapInsteadOfFadeToSilence()
    {
        var scene = new Scene
        {
            Selections = [new SourceSelection { SourceAssetId = "source-1", Start = 0, End = 20 }],
            Edits =
            [
                new SceneEdit
                {
                    SourceAssetId = "source-1",
                    Start = 10,
                    End = 12,
                    NoCleanBoundary = true,
                    Crossfade = true,
                    FadeSeconds = AudioTreatment.CrossfadeSeconds,
                }
            ],
        };
        var splice = CompositionResolver.SpliceRanges(scene, scene.Edits[0], 1.5);
        var filter = MediaRenderer.BuildFilterGraph(splice, new OutputSettings());

        Assert.Equal(2, splice.Count);
        Assert.True(splice[0].CrossfadeOut);
        Assert.True(splice[1].CrossfadeIn);
        Assert.Equal(AudioTreatment.CrossfadeSeconds, splice[0].FadeOutSeconds);
        Assert.Equal(AudioTreatment.CrossfadeSeconds, splice[1].FadeInSeconds);
        Assert.Contains("xfade", filter, StringComparison.Ordinal);
        Assert.Contains("acrossfade", filter, StringComparison.Ordinal);
        Assert.Contains("settb=1/30", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("afade=t=", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcatOnlyFilterUsesASingleConcat()
    {
        var filter = MediaRenderer.BuildFilterGraph(
            [
                new RenderRange(new TimeRange(0, 1), 0.01, 0.01),
                new RenderRange(new TimeRange(2, 3), 0.01, 0.01),
                new RenderRange(new TimeRange(4, 5), 0.01, 0.01),
            ],
            new OutputSettings());

        Assert.Contains("concat=n=3:v=1:a=1", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("xfade", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("acrossfade", filter, StringComparison.Ordinal);
    }

    private static Scene ContextScene() =>
        new()
        {
            Selections = [new SourceSelection { SourceAssetId = "source-1", Start = 0, End = 20 }],
            Edits =
            [
                new SceneEdit
                {
                    Id = "e-flub",
                    SourceAssetId = "source-1",
                    Start = 10,
                    End = 12,
                    Kind = EditKinds.Flub,
                    Reason = "flub",
                    Decision = DecisionStates.Pending,
                },
                new SceneEdit
                {
                    Id = "e-approved",
                    SourceAssetId = "source-1",
                    Start = 8.8,
                    End = 9.2,
                    Kind = EditKinds.Pause,
                    Reason = "pause",
                    Decision = DecisionStates.Approved,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                },
                new SceneEdit
                {
                    Id = "e-pending",
                    SourceAssetId = "source-1",
                    Start = 12.4,
                    End = 12.8,
                    Kind = EditKinds.Pause,
                    Reason = "later",
                    Decision = DecisionStates.Pending,
                },
                new SceneEdit
                {
                    Id = "e-far",
                    SourceAssetId = "source-1",
                    Start = 2,
                    End = 3,
                    Kind = EditKinds.Pause,
                    Reason = "far",
                    Decision = DecisionStates.Approved,
                    ProposalRevision = 1,
                    DecisionProposalRevision = 1,
                },
            ],
        };
}

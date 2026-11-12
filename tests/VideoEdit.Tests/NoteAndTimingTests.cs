using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class NoteAndTimingTests
{
    [Fact]
    public void SaveNotePersistsWithoutChangingComposition()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        var before = CompositionHasher.FullHash(store.Load());

        var updated = service.SaveNote("e-1", "starts mid-word", 1);

        Assert.Equal("starts mid-word", updated.Scenes[0].Edits[0].Note);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(before, CompositionHasher.FullHash(updated));
        Assert.Equal(DecisionStates.Pending, updated.Scenes[0].Edits[0].Decision);
    }

    [Fact]
    public void ApplyProposalTimesResetsOnlyChangedDecisions()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-2",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 12,
            End = 13,
            Kind = EditKinds.Pause,
            Reason = "Pause",
            Decision = DecisionStates.Pending,
            ProposalRevision = 1,
        });
        store.SaveNew(project);
        service.RecordDecision("e-1", DecisionStates.Approved, null, 1);

        var updated = service.ApplyProposalTimes(
            [new ProposalTimeUpdate("e-1", 5.2, 8, "Snapped to silence")],
            2);

        Assert.Equal(DecisionStates.Pending, updated.Scenes[0].Edits[0].Decision);
        Assert.Equal(2, updated.Scenes[0].Edits[0].ProposalRevision);
        Assert.Equal(5.2, updated.Scenes[0].Edits[0].Start);
        Assert.Equal(12, updated.Scenes[0].Edits[1].Start);
    }

    [Fact]
    public void SnapPendingEditsLeavesApprovedCutsAndResnapsIncludedRejectedPauses()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-pause-24",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 9.05,
            End = 11.4,
            Kind = EditKinds.Pause,
            Reason = "Pause",
            Decision = DecisionStates.Rejected,
            DecisionProposalRevision = 1,
            ProposalRevision = 1,
        });
        store.SaveNew(project);
        Directory.CreateDirectory(Path.Combine(root, "edit", "analysis"));
        File.WriteAllText(
            Path.Combine(root, "edit", "analysis", "silences-40db.txt"),
            """
            silence_start: 5.0
            silence_end: 8.5
            silence_start: 8.98
            silence_end: 9.05
            silence_start: 9.2
            silence_end: 12.0
            """);
        VadSpeechMap.Write(
            Path.Combine(root, "edit", "analysis", VadSpeechMap.FileName),
            new VadSpeechMap
            {
                Model = "test",
                Segments =
                [
                    new SpeechSegment { Start = 0, End = 5 },
                    new SpeechSegment { Start = 8.5, End = 9.2 },
                    new SpeechSegment { Start = 12, End = 20 },
                ],
            });

        var updated = service.SnapPendingEdits(
            1,
            includeRejectedIds: new HashSet<string>(StringComparer.Ordinal) { "e-pause-24" });

        Assert.Equal(5, updated.Scenes[0].Edits[0].Start);
        Assert.Equal(8, updated.Scenes[0].Edits[0].End);
        Assert.Equal(DecisionStates.Approved, updated.Scenes[0].Edits[0].Decision);
        Assert.Equal(DecisionStates.Pending, updated.Scenes[0].Edits[1].Decision);
        Assert.InRange(updated.Scenes[0].Edits[1].Start, 9.26, 9.28);
        Assert.Equal(11.4, updated.Scenes[0].Edits[1].End, 3);
        Assert.Null(updated.Scenes[0].Acceptance);
        Assert.Null(updated.FinalApproval);
    }

    [Fact]
    public void RejectedEditWithANoteIsAReproposalRequest()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Scenes[0].Edits[0].Note = "Cut off the last half of the word consider.";
        store.SaveNew(project);
        Directory.CreateDirectory(Path.Combine(root, "edit", "analysis"));
        File.WriteAllText(
            Path.Combine(root, "edit", "analysis", "words2.json"),
            """
            {"transcription":[{"text":"consider.","offsets":{"from":5000,"to":5800}}]}
            """);
        File.WriteAllText(Path.Combine(root, "edit", "analysis", "silences-40db.txt"), "silence_start: 4.8\nsilence_end: 8.2\n");
        VadSpeechMap.Write(
            Path.Combine(root, "edit", "analysis", VadSpeechMap.FileName),
            new VadSpeechMap
            {
                Segments =
                [
                    new SpeechSegment { Start = 0, End = 4.8 },
                    new SpeechSegment { Start = 8.2, End = 20 },
                ],
            });

        Assert.True(ProjectService.IsReproposalRequest(project.Scenes[0].Edits[0]));
        var updated = service.SnapPendingEdits(1);
        Assert.Equal(DecisionStates.Pending, updated.Scenes[0].Edits[0].Decision);
        Assert.Equal(2, updated.Scenes[0].Edits[0].ProposalRevision);
        Assert.Equal("Cut off the last half of the word consider.", updated.Scenes[0].Edits[0].Note);
        Assert.Contains("Re-proposed from note", updated.Scenes[0].Edits[0].Reason, StringComparison.Ordinal);
        Assert.True(ProjectService.ShowsReproposalNote(updated.Scenes[0].Edits[0]));
    }

    [Fact]
    public void RejectingWithANoteReproposesImmediately()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());

        var updated = service.RecordDecision("e-1", DecisionStates.Rejected, "start 6.2", 1);
        var edit = updated.Scenes[0].Edits[0];

        Assert.Equal(DecisionStates.Pending, edit.Decision);
        Assert.Equal(2, edit.ProposalRevision);
        Assert.Equal(6.2, edit.Start, 3);
        Assert.Equal(8, edit.End, 3);
        Assert.Equal("start 6.2", edit.Note);
        Assert.Contains("Re-proposed from note: start 6.2", edit.Reason, StringComparison.Ordinal);
        Assert.True(ProjectService.ShowsReproposalNote(edit));
    }

    [Fact]
    public void RejectingWithoutANoteKeepsTheCutRejected()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());

        var updated = service.RecordDecision("e-1", DecisionStates.Rejected, "  ", 1);
        var edit = updated.Scenes[0].Edits[0];

        Assert.Equal(DecisionStates.Rejected, edit.Decision);
        Assert.Equal(1, edit.ProposalRevision);
        Assert.Equal(5, edit.Start);
        Assert.Null(edit.Note);
        Assert.False(ProjectService.ShowsReproposalNote(edit));
    }

    [Fact]
    public void SavingANoteOnARejectedEditReproposes()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        store.SaveNew(project);

        var updated = service.SaveNote("e-1", "language, not a pause", 1);
        var edit = updated.Scenes[0].Edits[0];

        Assert.Equal(DecisionStates.Pending, edit.Decision);
        Assert.Equal(2, edit.ProposalRevision);
        Assert.Equal(EditKinds.Flub, edit.Kind);
        Assert.Equal("language, not a pause", edit.Note);
    }

    [Fact]
    public void EditNoteReaderUsesAnExplicitRangeAndANamedWord()
    {
        var edit = new SceneEdit
        {
            Start = 5,
            End = 8,
            Kind = EditKinds.Pause,
            Reason = "Pause",
        };

        var ranged = EditNoteReader.Read(edit, "use 6.1-7.4 instead");
        Assert.Equal(6.1, ranged.Range.Start, 3);
        Assert.Equal(7.4, ranged.Range.End, 3);

        var half = EditNoteReader.Read(
            edit,
            "Cut off the last half of the word consider.",
            [new SpokenWord("consider.", "consider", 5.0, 5.8)]);
        Assert.Equal(5.4, half.Range.Start, 3);
        Assert.Equal(8, half.Range.End, 3);

        var kind = EditNoteReader.Read(edit, "language, not a pause");
        Assert.Equal(EditKinds.Flub, kind.Kind);
        Assert.True(kind.Understood);
        Assert.Contains("Re-proposed from note", kind.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognizedNoteLeavesBoundariesAndAsksForAManualReproposal()
    {
        const string note = "You kept 'a late', but you should have kept what came next";
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject(firstEdit: new TimeRange(160.30, 160.63));
        project.Sources[0].DurationSeconds = 216.66;
        project.Scenes[0].Selections[0].End = 216.66;
        store.SaveNew(project);

        var read = EditNoteReader.Read(project.Scenes[0].Edits[0], note);
        Assert.False(read.Understood);
        Assert.Equal(160.30, read.Range.Start, 3);
        Assert.Equal(160.63, read.Range.End, 3);

        var updated = service.RecordDecision("e-1", DecisionStates.Rejected, note, 1);
        var edit = updated.Scenes[0].Edits[0];

        Assert.Equal(DecisionStates.Pending, edit.Decision);
        Assert.Equal(2, edit.ProposalRevision);
        Assert.Equal(160.30, edit.Start, 3);
        Assert.Equal(160.63, edit.End, 3);
        Assert.Equal(note, edit.Note);
        Assert.Contains(EditNoteReader.NotUnderstood, edit.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-proposed from note", edit.Reason, StringComparison.Ordinal);
        Assert.True(ProjectService.ShowsNoteNotUnderstood(edit));
        Assert.False(ProjectService.ShowsReproposalNote(edit));
    }

    [Fact]
    public void ForceReproposeLeavesTimesAndSetsPending()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].Note = "language, not a pause";
        store.SaveNew(project);

        var updated = service.ApplyProposalTimes(
            [new ProposalTimeUpdate("e-1", 5, 8, Force: true)],
            1);

        Assert.Equal(5, updated.Scenes[0].Edits[0].Start);
        Assert.Equal(8, updated.Scenes[0].Edits[0].End);
        Assert.Equal(DecisionStates.Pending, updated.Scenes[0].Edits[0].Decision);
        Assert.Equal(2, updated.Scenes[0].Edits[0].ProposalRevision);
    }

    [Fact]
    public void ApplyProposalTimesCanSetCrossfadeAndKeepFlagged()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());

        var updated = service.ApplyProposalTimes(
            [
                new ProposalTimeUpdate(
                    "e-1",
                    5.06,
                    8,
                    "Crossfade into the kept side.",
                    Decision: DecisionStates.Flagged,
                    NoCleanBoundary: true,
                    Crossfade: true,
                    FadeSeconds: AudioTreatment.CrossfadeSeconds)
            ],
            1);

        var edit = updated.Scenes[0].Edits[0];
        Assert.Equal(5.06, edit.Start);
        Assert.True(edit.Crossfade);
        Assert.Equal(AudioTreatment.CrossfadeSeconds, edit.FadeSeconds);
        Assert.Equal(DecisionStates.Flagged, edit.Decision);
    }
}

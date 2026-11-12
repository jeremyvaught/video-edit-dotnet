using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class RetakeTests
{
    [Fact]
    public void SyntheticWindowSnapsToThePauseBeforeTheWrongPhraseAndTheKeptTake()
    {
        var words = FixtureWords();
        Assert.True(RetakeDetector.TryPropose(Substitution(), Insertion(), words, out var retake));
        var diagnosis = new List<SilenceInterval>
        {
            new(54.061, 54.164),
            new(54.164, 54.250),
            new(54.988, 55.967),
            new(56.392, 57.199),
            new(57.586, 57.801),
        };
        var vad = new VadSpeechMap
        {
            Segments =
            [
                new SpeechSegment { Start = 49.020, End = 55.100 },
                new SpeechSegment { Start = 55.970, End = 56.480 },
                new SpeechSegment { Start = 57.180, End = 60.740 },
            ],
        };
        var (snapped, noClean) = SilenceSnapper.SnapEdit(
            retake.Range,
            EditKinds.Retake,
            vad.ToSilences(84.8),
            EditingPolicy.Default,
            diagnosis,
            vad.Onsets());

        Assert.False(noClean);
        Assert.InRange(snapped.Start, 54.22, 54.28);
        Assert.InRange(snapped.End, 56.55, 56.60);
        Assert.True(snapped.End <= 56.570 + 0.000001);
    }

    [Fact]
    public void RetakeEndKeepsAValidOnsetAndDoesNotCutTheKeptTake()
    {
        var snap = SilenceSnapper.SnapEnd(
            5,
            EditKinds.Retake,
            [new SilenceInterval(4.7, 5), new SilenceInterval(5.5, 5.8)],
            EditingPolicy.Default,
            [new SilenceInterval(4.7, 5), new SilenceInterval(5.5, 5.8)],
            [5.0, 5.8]);

        Assert.Equal(5.0, snap.Time, 3);
        Assert.False(snap.NoCleanBoundary);
        Assert.True(snap.Time <= 5.0);
    }

    [Fact]
    public void SubstitutionPlusInsertionIsOneRetakeCut()
    {
        var words = FixtureWords();
        var substitution = Substitution();
        var insertion = Insertion();

        Assert.True(RetakeDetector.TryPropose(substitution, insertion, words, out var retake));
        Assert.Equal(54.510, retake.Range.Start, 3);
        Assert.Equal(56.570, retake.Range.End, 3);
        Assert.Equal("in the way", retake.Phrase);
        Assert.Equal("Retake of “in the way”.", retake.Reason);
    }

    [Fact]
    public void ApprovedInsertionBecomesAPendingRetake()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Substitution());
        project.Findings.Add(Insertion());
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-drift-03",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            FindingId = "drift-03",
            ProposalRevision = 1,
            Start = 55.390,
            End = 57.740,
            Kind = EditKinds.Insertion,
            RemovedText = "I'm sorry in the way",
            Reason = "Ad-lib not in the script: “I'm sorry in the way”.",
            Confidence = 0.65,
            Decision = DecisionStates.Approved,
            DecisionProposalRevision = 1,
        });
        store.SaveNew(project);
        WriteFixtureWords(root);

        var updated = service.AddMissingDriftCuts(project.Revision);
        var cut = Assert.Single(updated.Scenes[0].Edits, edit => edit.Id == "e-drift-03");
        Assert.Equal(EditKinds.Retake, cut.Kind);
        Assert.Equal(54.510, cut.Start, 3);
        Assert.Equal(56.570, cut.End, 3);
        Assert.Equal(DecisionStates.Pending, cut.Decision);
        Assert.Null(cut.DecisionProposalRevision);
        Assert.True(cut.ProposalRevision > 1);
    }

    [Fact]
    public void FindingNoteReproposalClearsAnInheritedApproval()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Substitution());
        project.Findings.Add(Insertion());
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-drift-03",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            FindingId = "drift-03",
            ProposalRevision = 1,
            Start = 55.390,
            End = 57.740,
            Kind = EditKinds.Insertion,
            Reason = "Ad-lib",
            Confidence = 0.65,
            Decision = DecisionStates.Approved,
            DecisionProposalRevision = 1,
        });
        store.SaveNew(project);
        WriteFixtureWords(root);

        var updated = service.SaveFindingNote(
            "drift-02",
            "the first \"in a way\" should be edited out. \"in the way\" was a replacement.",
            project.Revision);
        var cut = Assert.Single(updated.Scenes[0].Edits, edit => edit.Id == "e-drift-03");
        Assert.Equal(EditKinds.Retake, cut.Kind);
        Assert.Equal(54.510, cut.Start, 3);
        Assert.Equal(56.570, cut.End, 3);
        Assert.Equal(DecisionStates.Pending, cut.Decision);
        Assert.Null(cut.DecisionProposalRevision);
        Assert.True(cut.ProposalRevision > 1);
    }

    [Fact]
    public void FlaggedInsertionBecomesTheMergedRetakeCard()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Substitution());
        project.Findings.Add(Insertion());
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-drift-03",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            FindingId = "drift-03",
            ProposalRevision = 1,
            Start = 55.390,
            End = 57.740,
            Kind = EditKinds.Insertion,
            RemovedText = "I'm sorry in the way",
            Reason = "Ad-lib not in the script: “I'm sorry in the way”.",
            Confidence = 0.65,
            Decision = DecisionStates.Flagged,
            DecisionProposalRevision = 1,
            Note = "the first \"in a way\" should be edited out. \"in the way\" was a replacement.",
        });
        store.SaveNew(project);
        WriteFixtureWords(root);

        var updated = service.AddMissingDriftCuts(project.Revision);
        var cut = Assert.Single(updated.Scenes[0].Edits, edit => edit.Id == "e-drift-03");
        Assert.Equal(EditKinds.Retake, cut.Kind);
        Assert.Equal(54.510, cut.Start, 3);
        Assert.Equal(56.570, cut.End, 3);
        Assert.Equal(DecisionStates.Flagged, cut.Decision);
        Assert.Equal("the first \"in a way\" should be edited out. \"in the way\" was a replacement.", cut.Note);
        Assert.Contains("in the way", cut.Reason, StringComparison.Ordinal);
        Assert.Equal(updated.Revision, service.AddMissingDriftCuts(updated.Revision).Revision);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        Assert.DoesNotContain(review.RerecordList, item => item.Id is "drift-02" or "drift-03");
        Assert.Equal(EditKinds.Retake, review.Scenes[0].Edits.Single(edit => edit.Id == "e-drift-03").Kind);
    }

    [Fact]
    public void FindingNoteIsAReproposalRequest()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Substitution());
        project.Findings.Add(Insertion());
        store.SaveNew(project);
        WriteFixtureWords(root);

        var updated = service.SaveFindingNote(
            "drift-02",
            "the first \"in a way\" should be edited out. \"in the way\" was a replacement.",
            project.Revision);
        var cut = Assert.Single(updated.Scenes[0].Edits, edit => edit.Kind == EditKinds.Retake);
        Assert.Equal(54.510, cut.Start, 3);
        Assert.Equal(56.570, cut.End, 3);
        Assert.Equal(
            "the first \"in a way\" should be edited out. \"in the way\" was a replacement.",
            cut.Note);
        Assert.Contains("Re-proposed from note", cut.Reason, StringComparison.Ordinal);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        Assert.Empty(review.RerecordList);
        Assert.True(review.Scenes[0].Edits.Single(edit => edit.Kind == EditKinds.Retake).ReproposedFromNote);
    }

    [Fact]
    public void EditorialFindingNoteIsBlockedWhileExporting()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Findings.Add(Substitution());
        project.Findings.Add(Insertion());
        store.SaveNew(project);
        WriteFixtureWords(root);
        gate.Begin("export", true);
        try
        {
            var error = Assert.Throws<VideoEditException>(() =>
                service.SaveFindingNote(
                    "drift-02",
                    "the first \"in a way\" should be edited out. \"in the way\" was a replacement.",
                    project.Revision));
            Assert.Equal("conflict", error.Code);
            Assert.Contains("export", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            gate.End();
        }

        var after = store.Load();
        Assert.Equal(project.Revision, after.Revision);
        Assert.DoesNotContain(after.Scenes[0].Edits, edit => edit.Kind == EditKinds.Retake);
        Assert.Null(after.Findings.Single(item => item.Id == "drift-02").Note);
    }

    [Fact]
    public void OrdinaryFindingNoteSavesDuringExport()
    {
        var (store, service, gate, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Findings.Add(Substitution());
        store.SaveNew(project);
        gate.Begin("export", true);
        try
        {
            var updated = service.SaveFindingNote("drift-02", "I want to think about this take", project.Revision);
            Assert.Equal("I want to think about this take", updated.Findings.Single(item => item.Id == "drift-02").Note);
            Assert.DoesNotContain(updated.Scenes[0].Edits, edit => edit.FindingId == "drift-02");
        }
        finally
        {
            gate.End();
        }
    }

    [Fact]
    public void UnrecognizedFindingNoteStaysVisible()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Sources[0].DurationSeconds = 84.8;
        project.Scenes[0].Selections[0].End = 84.8;
        project.Findings.Add(Substitution());
        store.SaveNew(project);

        var updated = service.SaveFindingNote("drift-02", "I want to think about this take", project.Revision);
        Assert.Equal("I want to think about this take", updated.Findings.Single(item => item.Id == "drift-02").Note);
        Assert.DoesNotContain(updated.Scenes[0].Edits, edit => edit.FindingId == "drift-02");

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        var item = Assert.Single(review.RerecordList, entry => entry.Id == "drift-02");
        Assert.True(item.NoteNotUnderstood);
        Assert.Equal("I want to think about this take", item.Note);
    }

    [Fact]
    public void EditNoteReaderReadsCutAndKeepQuotes()
    {
        var edit = new SceneEdit
        {
            Start = 54.51,
            End = 54.95,
            Kind = ScriptDriftKinds.Substitution,
            Reason = "Said “in a”; script says “the”.",
        };
        var read = EditNoteReader.Read(
            edit,
            "the first \"in a way\" should be edited out. \"in the way\" was a replacement.",
            FixtureWords());

        Assert.True(read.Understood);
        Assert.Equal(EditKinds.Retake, read.Kind);
        Assert.Equal(54.510, read.Range.Start, 3);
        Assert.Equal(56.570, read.Range.End, 3);
    }

    private static void WriteFixtureWords(string root)
    {
        var analysis = Path.Combine(root, "edit", "analysis");
        Directory.CreateDirectory(analysis);
        File.WriteAllText(
            Path.Combine(analysis, "words.json"),
            """
            {"transcription":[
              {"text":"public","offsets":{"from":53620,"to":54510}},
              {"text":"in","offsets":{"from":54510,"to":54800}},
              {"text":"a","offsets":{"from":54800,"to":54950}},
              {"text":"way","offsets":{"from":54950,"to":55390}},
              {"text":"I'm","offsets":{"from":55390,"to":55830}},
              {"text":"sorry","offsets":{"from":55830,"to":56570}},
              {"text":"in","offsets":{"from":56570,"to":56860}},
              {"text":"the","offsets":{"from":56860,"to":57300}},
              {"text":"way","offsets":{"from":57300,"to":57740}},
              {"text":"a","offsets":{"from":57740,"to":57890}},
              {"text":"board","offsets":{"from":57890,"to":58630}}
            ]}
            """);
    }

    private static IReadOnlyList<SpokenWord> FixtureWords() =>
    [
        new("public", "public", 53.620, 54.510),
        new("in", "in", 54.510, 54.800),
        new("a", "a", 54.800, 54.950),
        new("way", "way", 54.950, 55.390),
        new("I'm", "i'm", 55.390, 55.830),
        new("sorry", "sorry", 55.830, 56.570),
        new("in", "in", 56.570, 56.860),
        new("the", "the", 56.860, 57.300),
        new("way", "way", 57.300, 57.740),
        new("a", "a", 57.740, 57.890),
        new("board", "board", 57.890, 58.630),
    ];

    private static SourceFinding Substitution() =>
        new()
        {
            Id = "drift-02",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 54.510,
            End = 54.950,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "the",
            SpokenText = "in a",
            TranscriptEvidence = "in a",
            Reason = "Said “in a”; script says “the”.",
            Severity = DriftSeverity.Rerecord,
            Confidence = 0.8,
        };

    private static SourceFinding Insertion() =>
        new()
        {
            Id = "drift-03",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 55.390,
            End = 57.740,
            Kind = ScriptDriftKinds.Insertion,
            SpokenText = "I'm sorry in the way",
            TranscriptEvidence = "I'm sorry in the way",
            Reason = "Ad-lib not in the script: “I'm sorry in the way”.",
            Severity = DriftSeverity.Rerecord,
            Confidence = 0.65,
        };

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", ""));
    }
}

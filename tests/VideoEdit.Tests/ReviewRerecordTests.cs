using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ReviewRerecordTests
{
    [Fact]
    public void FindingInsideAnApprovedCutIsResolved()
    {
        var project = ProjectWithCut(5, 8, DecisionStates.Approved);
        var finding = Drift(5.2, 7.8, "um");

        Assert.Equal(RerecordStatuses.ResolvedByCut, ReviewAssembler.StatusForFinding(project, finding));
    }

    [Fact]
    public void FindingInsideAPendingCutIsResolvedAndARejectedCutLeavesItPending()
    {
        var pending = ProjectWithCut(5, 8, DecisionStates.Pending);
        var rejected = ProjectWithCut(5, 8, DecisionStates.Rejected);
        var finding = Drift(5.2, 7.8, "um");

        Assert.Equal(RerecordStatuses.ResolvedByCut, ReviewAssembler.StatusForFinding(pending, finding));
        Assert.Equal(RerecordStatuses.Pending, ReviewAssembler.StatusForFinding(rejected, finding));
    }

    [Fact]
    public void FindingThatOnlyPartlyOverlapsACutStaysPending()
    {
        var project = ProjectWithCut(5, 8, DecisionStates.Approved);
        var finding = Drift(4.9, 5.2, "it's");

        Assert.Equal(RerecordStatuses.Pending, ReviewAssembler.StatusForFinding(project, finding));
    }

    [Fact]
    public void FindingInAnUnselectedGapIsResolved()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Selections[0].End = 10;
        project.Scenes[0].Edits.Clear();
        project.Scenes.Add(new Scene
        {
            Id = "scene-close",
            Title = "Close",
            Position = 2,
            Selections =
            [
                new SourceSelection
                {
                    SourceAssetId = "source-1",
                    SourceFingerprint = TestSupport.Fingerprint,
                    Start = 16,
                    End = 20,
                }
            ],
        });
        var inGap = Drift(12, 13, "2092");
        var inKeep = Drift(17, 18, "thanks");

        Assert.Equal(RerecordStatuses.ResolvedByCut, ReviewAssembler.StatusForFinding(project, inGap));
        Assert.Equal(RerecordStatuses.Pending, ReviewAssembler.StatusForFinding(project, inKeep));
    }

    [Fact]
    public void AssembleHidesResolvedAndCuttableFindings()
    {
        var (store, _, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Drift(5.2, 7.8, "um"));
        project.Findings.Add(Substitution(12, 13, "planter", "basket"));
        store.SaveNew(project);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();

        Assert.DoesNotContain(review.RerecordList, item => item.SpokenText == "um");
        var remaining = Assert.Single(review.RerecordList);
        Assert.Equal("basket", remaining.SpokenText);
        Assert.Equal(RerecordStatuses.Pending, remaining.Status);
        Assert.Equal(project.Revision, review.Revision);
    }

    [Fact]
    public void DismissedSubstitutionLeavesTheList()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Findings.Add(Substitution(12, 13, "12", "12th"));
        store.SaveNew(project);

        service.DismissFinding("drift-12th", project.Revision);
        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();

        Assert.Empty(review.RerecordList);
        Assert.Equal(DecisionStates.Rejected, store.Load().Findings.Single(item => item.Id == "drift-12th").Decision);
    }

    [Fact]
    public void InsertionAndRepeatBecomePendingCutsUnlessCovered()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Drift(5.2, 7.8, "you"));
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-sorry",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 12,
            End = 13.5,
            Kind = ScriptDriftKinds.Insertion,
            SpokenText = "I'm sorry in the way",
            TranscriptEvidence = "I'm sorry in the way",
            Reason = "Ad-lib",
            Severity = DriftSeverity.Rerecord,
            Confidence = 0.65,
        });
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-repeat",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 14,
            End = 15,
            Kind = ScriptDriftKinds.Repeat,
            SpokenText = "I could not",
            TranscriptEvidence = "I could not",
            Reason = "Repeated take",
            Severity = DriftSeverity.Rerecord,
            Confidence = 0.65,
        });
        store.SaveNew(project);

        var updated = service.AddMissingDriftCuts(store.Load().Revision);
        Assert.DoesNotContain(updated.Scenes[0].Edits, edit => edit.FindingId == "drift-you");
        var insertion = Assert.Single(updated.Scenes[0].Edits, edit => edit.Id == "e-drift-sorry");
        Assert.Equal(EditKinds.Insertion, insertion.Kind);
        Assert.Equal(DecisionStates.Pending, insertion.Decision);
        var repeat = Assert.Single(updated.Scenes[0].Edits, edit => edit.Id == "e-drift-repeat");
        Assert.Equal(EditKinds.Repeat, repeat.Kind);
        Assert.Equal(store.Load().Revision, service.AddMissingDriftCuts(store.Load().Revision).Revision);
    }

    [Fact]
    public void ClauseCutBecomesAPendingSceneEditAndLeavesTheList()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Findings.Add(Substitution(11.6, 11.8, "12", "12th"));
        store.SaveNew(project);
        WriteHostWords(root);

        var updated = service.ProposeClauseCut("drift-12th", project.Revision);
        var cut = Assert.Single(updated.Scenes[0].Edits, edit => edit.Id == "e-drift-12th");
        Assert.Equal(EditKinds.Substitution, cut.Kind);
        Assert.Equal(DecisionStates.Pending, cut.Decision);
        Assert.Equal("drift-12th", cut.FindingId);
        Assert.Equal(11.0, cut.Start);
        Assert.Equal(13.0, cut.End);
        Assert.Equal(updated.Revision, service.ProposeClauseCut("drift-12th", updated.Revision).Revision);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        Assert.DoesNotContain(review.RerecordList, item => item.Id == "drift-12th");
        Assert.Contains(review.Scenes[0].Edits, edit => edit.Id == "e-drift-12th");
    }

    [Fact]
    public void FlaggedSubstitutionStaysOnTheListAndDoesNotBlockExport()
    {
        var (store, service, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Findings.Add(Substitution(12, 13, "12", "12th"));
        store.SaveNew(project);

        var flagged = service.FlagFinding("drift-12th", project.Revision);
        Assert.Equal(DecisionStates.Flagged, flagged.Findings.Single(item => item.Id == "drift-12th").Decision);
        Assert.Equal(flagged.Revision, service.FlagFinding("drift-12th", flagged.Revision).Revision);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        var item = Assert.Single(review.RerecordList);
        Assert.Equal(RerecordStatuses.Flagged, item.Status);
        Assert.DoesNotContain(
            CompositionResolver.ExportBlocks(store.Load(), requireFinalApproval: false),
            block => block.Contains("drift-12th", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewCardIncludesTheScriptSentenceToSay()
    {
        var (store, _, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Findings.Add(Substitution(12, 13, "12", "12th"));
        store.SaveNew(project);
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        File.WriteAllText(
            Path.Combine(root, "sources", "script.md"),
            """
            <!-- preface -->
            # Title

            For the May 12, 2042 lesson we have 24 seed trays.
            """);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        var item = Assert.Single(review.RerecordList);
        Assert.Equal("For the May 12, 2042 lesson we have 24 seed trays.", item.ScriptSentence);
    }

    [Fact]
    public void ReviewTimesAreOutputTimesForTheProposedCut()
    {
        var (store, _, gate, root) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject(firstEdit: new TimeRange(0, 5));
        project.Scenes[0].Edits[0].Kind = EditKinds.Preroll;
        project.Findings.Add(Substitution(12, 13, "12", "12th"));
        store.SaveNew(project);

        var renderer = TestSupport.Renderer(root, new NoopRunner());
        var review = new ReviewAssembler(store, renderer, gate).Assemble();
        var preroll = Assert.Single(review.Scenes[0].Edits);
        Assert.Equal(0, preroll.Start);
        Assert.Equal(0, preroll.End);
        Assert.Equal(0, preroll.SourceStart);
        Assert.Equal(5, preroll.SourceEnd);
        var drift = Assert.Single(review.RerecordList);
        Assert.Equal(7, drift.Start);
        Assert.Equal(8, drift.End);
        Assert.Equal(12, drift.SourceStart);
        Assert.Equal(13, drift.SourceEnd);
    }

    private static ProjectDocument ProjectWithCut(double start, double end, string decision)
    {
        var project = TestSupport.SampleProject(firstEdit: new TimeRange(start, end));
        project.Scenes[0].Edits[0].Decision = decision;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        return project;
    }

    private static SourceFinding Drift(double start, double end, string spoken) =>
        new()
        {
            Id = "drift-" + spoken,
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = start,
            End = end,
            Kind = ScriptDriftKinds.Insertion,
            SpokenText = spoken,
            Severity = DriftSeverity.Rerecord,
        };

    private static void WriteHostWords(string root)
    {
        var analysis = Path.Combine(root, "edit", "analysis");
        Directory.CreateDirectory(analysis);
        File.WriteAllText(
            Path.Combine(analysis, "words.json"),
            """
            {"transcription":[
              {"text":"As","offsets":{"from":11000,"to":11200}},
              {"text":"of","offsets":{"from":11200,"to":11300}},
              {"text":"the","offsets":{"from":11300,"to":11400}},
              {"text":"May","offsets":{"from":11400,"to":11600}},
              {"text":"12","offsets":{"from":11600,"to":11800}},
              {"text":"lesson.","offsets":{"from":11800,"to":13000}}
            ]}
            """);
    }

    private static SourceFinding Substitution(double start, double end, string script, string spoken) =>
        new()
        {
            Id = "drift-" + spoken,
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = start,
            End = end,
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = script,
            SpokenText = spoken,
            Severity = DriftSeverity.Rerecord,
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

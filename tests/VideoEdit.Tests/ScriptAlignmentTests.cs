using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ScriptAlignmentTests
{
    [Fact]
    public void AuthoritativeScriptIgnoresCommentsAndTheFirstHeading()
    {
        var text = ScriptPassage.ReadAuthoritative(
            """
            # Title

            <!-- An example script for the recorded take. This is the authoritative text. -->

            I painted a planter.

            <!-- generate -->
            On May 12, 2042, we planted.
            <!-- /generate -->
            """);
        Assert.Equal("I painted a planter.\n\nOn May 12, 2042, we planted.", text);
        Assert.Equal("Title", ScriptPassage.ReadHeading("# Title\n\n<!-- preface -->\n\nI painted a planter."));
    }

    [Fact]
    public void AuthoritativeScriptDoesNotSkipASpokenPrefaceByPhrase()
    {
        var text = ScriptPassage.ReadAuthoritative(
            """
            # Title

            An example script for the recorded take.

            I painted a planter.
            """);
        Assert.StartsWith("An example script", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CardinalDigitsMatchNumberWordsAndOrdinalsStayVisible()
    {
        var findings = ScriptAligner.Align(
            "I do not have ten gardeners for eighteen months on the 12th.",
            Words(
                (0, 0.2, "I"),
                (0.2, 0.4, "do"),
                (0.4, 0.6, "not"),
                (0.6, 0.8, "have"),
                (0.8, 1.0, "10"),
                (1.0, 1.2, "gardeners"),
                (1.2, 1.4, "for"),
                (1.4, 1.6, "18"),
                (1.6, 1.8, "months"),
                (1.8, 2.0, "on"),
                (2.0, 2.2, "the"),
                (2.2, 2.4, "12")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        Assert.DoesNotContain(
            findings,
            finding => (finding.ScriptText ?? "").Contains("ten", StringComparison.Ordinal)
                || (finding.SpokenText ?? "").Contains("10", StringComparison.Ordinal));
        Assert.DoesNotContain(
            findings,
            finding => (finding.ScriptText ?? "").Contains("eighteen", StringComparison.Ordinal)
                || (finding.SpokenText ?? "").Contains("18", StringComparison.Ordinal));
        var ordinal = Assert.Single(
            findings,
            finding => finding.Kind == ScriptDriftKinds.Substitution
                && finding.ScriptText == "12th"
                && finding.SpokenText == "12");
        Assert.Equal(DriftSeverity.Rerecord, ordinal.Severity);
    }

    [Fact]
    public void AlignsInsertionsAndSubstitutions()
    {
        var script = "I painted a planter in 2042.";
        var words = Words(
            (0, 0.2, "Hello"),
            (0.2, 0.4, "I"),
            (0.4, 0.6, "painted"),
            (0.6, 0.8, "a"),
            (0.8, 1.0, "basket"),
            (1.0, 1.2, "in"),
            (1.2, 1.4, "2092"));
        var findings = ScriptAligner.Align(script, words, EmptyProject(), "source-1", TestSupport.Fingerprint);

        Assert.Equal(
            [ScriptDriftKinds.Insertion, ScriptDriftKinds.Substitution, ScriptDriftKinds.Substitution],
            findings.Select(finding => finding.Kind));
        Assert.Equal("Hello", findings[0].SpokenText);
        Assert.Equal("basket", findings[1].SpokenText);
        Assert.Equal("planter", findings[1].ScriptText);
        Assert.Equal("2092", findings[2].SpokenText);
        Assert.Equal("2042", findings[2].ScriptText);
        Assert.Equal(0.8, findings[1].Start);
        Assert.Equal(1.0, findings[1].End);
        Assert.All(findings, finding => Assert.Equal(DriftSeverity.Rerecord, finding.Severity));
    }

    [Fact]
    public void ExtraScriptPhraseIsARepeatTake()
    {
        var findings = ScriptAligner.Align(
            "I could not list it from notes. I sat down, and I could not.",
            Words(
                (0, 0.2, "I"),
                (0.2, 0.4, "could"),
                (0.4, 0.6, "not"),
                (0.6, 0.8, "list"),
                (0.8, 1.0, "it"),
                (1.0, 1.2, "from"),
                (1.2, 1.4, "notes"),
                (1.4, 1.6, "I"),
                (1.6, 1.8, "sat"),
                (1.8, 2.0, "down"),
                (2.0, 2.2, "and"),
                (2.2, 2.4, "I"),
                (2.4, 2.6, "could"),
                (2.6, 2.8, "not"),
                (2.8, 3.0, "I"),
                (3.0, 3.2, "could"),
                (3.2, 3.4, "not")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        var repeat = Assert.Single(findings, finding => finding.Kind == ScriptDriftKinds.Repeat);
        Assert.Equal("I could not", repeat.SpokenText);
        Assert.Contains("Repeated take", repeat.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleThatIsAlsoTheOpeningSentenceIsSpokenScript()
    {
        var findings = ScriptAligner.Align(
            """
            # The title is wrong

            The title is wrong. I left it that way on purpose.
            """,
            Words(
                (0.0, 0.2, "The"),
                (0.2, 0.4, "title"),
                (0.4, 0.6, "is"),
                (0.6, 0.8, "wrong"),
                (0.8, 1.0, "I"),
                (1.0, 1.2, "left"),
                (1.2, 1.4, "it"),
                (1.4, 1.6, "that"),
                (1.6, 1.8, "way"),
                (1.8, 2.0, "on"),
                (2.0, 2.2, "purpose")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == ScriptDriftKinds.Omission
                && (finding.ScriptText ?? "").Contains("The title is wrong", StringComparison.Ordinal));
        Assert.Empty(findings);
    }

    [Fact]
    public void SpokenHeadingDoesNotMisAnchorTheOpeningSentence()
    {
        var script = """
            # A garden guide that visitors can read today

            I painted a planter.
            """;
        var findings = ScriptAligner.Align(
            script,
            Words(
                (0.0, 0.2, "All"),
                (0.2, 0.4, "right"),
                (21.2, 21.3, "A"),
                (21.3, 22.0, "garden"),
                (22.0, 22.8, "guide"),
                (22.8, 23.0, "that"),
                (23.0, 23.2, "visitors"),
                (23.2, 23.5, "can"),
                (23.5, 23.8, "read"),
                (23.8, 24.1, "today"),
                (24.1, 24.4, "I"),
                (24.4, 24.7, "painted"),
                (24.7, 25.0, "a"),
                (25.0, 25.3, "planter")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        Assert.DoesNotContain(
            findings,
            finding => finding.SpokenText == "can read today" && finding.ScriptText == "I");
        Assert.Contains(
            findings,
            finding => finding.Kind == ScriptDriftKinds.Insertion
                && (finding.SpokenText ?? "").Contains("All right", StringComparison.Ordinal));
        Assert.DoesNotContain(
            findings,
            finding => finding.Kind == ScriptDriftKinds.Substitution && finding.ScriptText == "I");
    }

    [Fact]
    public void ContractionsAndNumberAgreementAreMinor()
    {
        var findings = ScriptAligner.Align(
            "I am labeling flower baskets.",
            Words(
                (0, 0.2, "I'm"),
                (0.2, 0.4, "labeling"),
                (0.4, 0.6, "flowers"),
                (0.6, 0.8, "baskets")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(DriftSeverity.Minor, finding.Severity));
    }

    [Fact]
    public void OneWordSwapWinsWhenAPhraseRepeats()
    {
        var findings = ScriptAligner.Align(
            "a smaller planter. This garden corner needs the smaller planter.",
            Words(
                (1.0, 1.1, "a"),
                (1.1, 1.2, "smaller"),
                (1.2, 1.3, "basket"),
                (1.3, 1.4, "This"),
                (1.4, 1.5, "garden"),
                (1.5, 1.6, "corner"),
                (1.6, 1.7, "needs"),
                (1.7, 1.8, "the"),
                (1.8, 1.9, "smaller"),
                (1.9, 2.0, "planter")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        var swap = Assert.Single(findings);
        Assert.Equal(ScriptDriftKinds.Substitution, swap.Kind);
        Assert.Equal("planter", swap.ScriptText);
        Assert.Equal("basket", swap.SpokenText);
    }

    [Fact]
    public void AlignsAnOmissionBetweenMatchingNeighbors()
    {
        var findings = ScriptAligner.Align(
            "I painted a planter quickly in 2042.",
            Words(
                (0.2, 0.4, "I"),
                (0.4, 0.6, "painted"),
                (0.6, 0.8, "a"),
                (0.8, 1.0, "planter"),
                (1.0, 1.2, "in"),
                (1.2, 1.4, "2042")),
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        var omitted = Assert.Single(findings);
        Assert.Equal(ScriptDriftKinds.Omission, omitted.Kind);
        Assert.Equal("quickly", omitted.ScriptText);
    }

    [Fact]
    public void InstructionCutWordsStayOnTheList()
    {
        var project = EmptyProject();
        project.Findings.Add(new SourceFinding
        {
            Id = "instruction-last-paragraph",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 1.0,
            End = 2.0,
            Kind = EditKinds.Instruction,
        });
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-flub",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            ProposalRevision = 1,
            Start = 0.2,
            End = 0.4,
            Kind = EditKinds.Flub,
            Decision = DecisionStates.Approved,
            DecisionProposalRevision = 1,
        });

        var words = Words(
            (0.0, 0.2, "I"),
            (0.2, 0.4, "um"),
            (0.4, 0.6, "painted"),
            (0.6, 0.8, "a"),
            (0.8, 1.0, "planter"),
            (1.0, 1.2, "in"),
            (1.2, 1.4, "2092"));
        var findings = ScriptAligner.Align("I painted a planter in 2042.", words, project, "source-1", TestSupport.Fingerprint);
        Assert.Contains(findings, finding => finding.SpokenText == "um" && finding.Kind == ScriptDriftKinds.Insertion);
        var year = findings.Single(finding => finding.Kind == ScriptDriftKinds.Substitution);
        Assert.Equal("2092", year.SpokenText);
        Assert.Equal("2042", year.ScriptText);
        Assert.Equal(DriftSeverity.Rerecord, year.Severity);
    }

    [Fact]
    public void SyntheticRawWordsYieldTheKnownRerecordDrifts()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "script-alignment");
        var words = WhisperWords.Parse(File.ReadAllText(Path.Combine(root, "words2.json")));
        var findings = ScriptAligner.Align(
            File.ReadAllText(Path.Combine(root, "script.md")),
            words,
            EmptyProject(),
            "source-1",
            TestSupport.Fingerprint);
        var rerecord = findings.Where(finding => finding.Severity == DriftSeverity.Rerecord).ToList();
        var minor = findings.Where(finding => finding.Severity == DriftSeverity.Minor).ToList();

        Assert.Equal(4, rerecord.Count);
        Assert.Contains(rerecord, finding => finding.ScriptText == "2042" && finding.SpokenText == "2092");
        Assert.Contains(rerecord, finding => finding.ScriptText == "blue" && finding.SpokenText == "green");
        Assert.Contains(rerecord, finding => finding.ScriptText == "water" && finding.SpokenText == "sand");
        var instruction = Assert.Single(rerecord, finding => finding.Kind == ScriptDriftKinds.Insertion);
        Assert.Equal("Please remove this sentence and begin again", instruction.SpokenText);
        Assert.Equal(2, minor.Count);
        Assert.Contains(minor, finding => finding.ScriptText == "I am" && finding.SpokenText == "I'm");
        Assert.Contains(minor, finding => finding.ScriptText == "flower" && finding.SpokenText == "flowers");
    }

    [Theory]
    [InlineData("inserted_negation", "We ship today.", "We don't ship today")]
    [InlineData("optional_title_with_preamble", "# Project overview\n\nWe ship today.", "Hello We ship today")]
    [InlineData("optional_title_with_opening_error", "# Project overview\n\nWe ship today.", "They ship today")]
    [InlineData("optional_title_without_preamble", "# Project overview\n\nWe ship today.", "We ship today")]
    [InlineData("trailing_omission", "We ship today and tomorrow.", "We ship today")]
    public void ReviewerSyntheticCases(string name, string script, string spoken)
    {
        var tokens = WhisperWords.TokenizeScript(spoken);
        var words = tokens.Select((token, i) => new SpokenWord(token.Text, token.Normalized, i, i + 1)).ToList();
        var project = new ProjectDocument
        {
            Name = "Synthetic alignment",
            Revision = 1,
            Sources =
            [
                new SourceAsset
                {
                    Id = "source",
                    Path = "/unused",
                    Fingerprint = TestSupport.Fingerprint,
                    DurationSeconds = tokens.Count,
                    Kind = "video",
                }
            ],
        };
        var findings = ScriptAligner.Align(script, words, project, "source", TestSupport.Fingerprint);
        project.Findings = findings.ToList();
        Assert.Empty(ProjectValidator.Collect(project));

        switch (name)
        {
            case "inserted_negation":
                var inserted = Assert.Single(findings);
                Assert.Equal(ScriptDriftKinds.Insertion, inserted.Kind);
                Assert.Equal("don't", inserted.SpokenText);
                Assert.Equal(DriftSeverity.Rerecord, inserted.Severity);
                break;
            case "optional_title_with_preamble":
                var preamble = Assert.Single(findings);
                Assert.Equal(ScriptDriftKinds.Insertion, preamble.Kind);
                Assert.Equal("Hello", preamble.SpokenText);
                Assert.DoesNotContain("Project overview", preamble.ScriptText ?? "", StringComparison.Ordinal);
                break;
            case "optional_title_with_opening_error":
                var opening = Assert.Single(findings);
                Assert.Equal(ScriptDriftKinds.Substitution, opening.Kind);
                Assert.Equal("They", opening.SpokenText);
                Assert.Equal("We", opening.ScriptText);
                break;
            case "optional_title_without_preamble":
                Assert.Empty(findings);
                break;
            case "trailing_omission":
                var omitted = Assert.Single(findings);
                Assert.Equal(ScriptDriftKinds.Omission, omitted.Kind);
                Assert.Equal("and tomorrow", omitted.ScriptText);
                Assert.InRange(omitted.Start, 0, tokens.Count);
                Assert.InRange(omitted.End, omitted.Start + 0.000001, tokens.Count);
                break;
        }
    }

    [Fact]
    public void OmissionFindingsAreNotValidSceneEdits()
    {
        var project = TestSupport.SampleProject();
        project.Findings.Add(new SourceFinding
        {
            Id = "drift-01",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 1,
            End = 2,
            Kind = ScriptDriftKinds.Omission,
            ScriptText = "planter",
        });
        Assert.Empty(ProjectValidator.Collect(project));

        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-drift",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            ProposalRevision = 1,
            Start = 1,
            End = 2,
            Kind = ScriptDriftKinds.Omission,
        });
        Assert.Contains(ProjectValidator.Collect(project), error => error.Contains("unknown kind"));
    }

    [Fact]
    public void DuplicateFindingIdsAreRejected()
    {
        var project = EmptyProject();
        project.Findings =
        [
            new SourceFinding
            {
                Id = "drift-02",
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 0.4,
                End = 0.5,
                Kind = ScriptDriftKinds.Substitution,
                ScriptText = "wrongB",
                SpokenText = "three",
            },
            new SourceFinding
            {
                Id = "drift-02",
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 0.8,
                End = 0.9,
                Kind = ScriptDriftKinds.Substitution,
                ScriptText = "wrongC",
                SpokenText = "five",
            }
        ];
        Assert.Contains(ProjectValidator.Collect(project), error => error.Contains("Duplicate finding id drift-02"));
    }

    [Fact]
    public void ReplacingDriftsReservesPreservedIdsForTheOneTwoThreeCase()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = EmptyProject();
        store.SaveNew(project);
        var words = Words(
            (0.0, 0.1, "one"),
            (0.2, 0.3, "two"),
            (0.4, 0.5, "three"),
            (0.6, 0.7, "four"),
            (0.8, 0.9, "five"));

        var first = ScriptAligner.Align("wrongA two wrongB four five", words, project, "source-1", TestSupport.Fingerprint);
        Assert.Equal(2, first.Count);
        var saved = service.ReplaceScriptDriftFindings(first, 1);
        Assert.Equal(["drift-01", "drift-02"], saved.Findings.Select(finding => finding.Id));
        var retained = Assert.Single(saved.Findings, finding => finding.ScriptText == "wrongB");
        Assert.Equal("drift-02", retained.Id);

        var second = ScriptAligner.Align("one two wrongB four wrongC", words, saved, "source-1", TestSupport.Fingerprint);
        var merged = service.ReplaceScriptDriftFindings(second, saved.Revision);
        Assert.Empty(ProjectValidator.Collect(merged));
        Assert.Equal(merged.Findings.Count, merged.Findings.Select(finding => finding.Id).Distinct().Count());
        var preserved = Assert.Single(merged.Findings, finding => finding.ScriptText == "wrongB" && finding.SpokenText == "three");
        Assert.Equal("drift-02", preserved.Id);
        var added = Assert.Single(merged.Findings, finding => finding.ScriptText == "wrongC");
        Assert.NotEqual(preserved.Id, added.Id);
        Assert.DoesNotContain(merged.Findings, finding => finding.ScriptText == "wrongA");
    }

    [Fact]
    public void ReplacingDriftFindingsDoesNotClearAcceptance()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        store.SaveNew(project);
        service.AcceptScene("scene-1", 1);
        var accepted = store.Load();
        accepted.FinalApproval = new FinalApproval { CompositionHash = CompositionHasher.FullHash(accepted) };
        File.WriteAllText(store.ProjectPath, System.Text.Json.JsonSerializer.Serialize(accepted, ProjectStore.JsonOptions));

        var updated = service.ReplaceScriptDriftFindings(
            [
                new SourceFinding
                {
                    Id = "drift-01",
                    SourceAssetId = "source-1",
                    SourceFingerprint = TestSupport.Fingerprint,
                    Start = 1,
                    End = 2,
                    Kind = ScriptDriftKinds.Substitution,
                    ScriptText = "planter",
                    SpokenText = "basket",
                }
            ],
            store.Load().Revision);

        Assert.NotNull(updated.Scenes[0].Acceptance);
        Assert.NotNull(updated.FinalApproval);
        Assert.True(CompositionResolver.FinalApprovalValid(updated, CompositionHasher.FullHash(updated)));
        Assert.Single(updated.Findings, finding => finding.Id == "drift-01");
    }

    [Fact]
    public void ParsesWhisperMillisecondOffsets()
    {
        var words = WhisperWords.Parse(
            """
            {"transcription":[
              {"text":"","offsets":{"from":0,"to":100}},
              {"text":" basket.","offsets":{"from":138840,"to":139460}}
            ]}
            """);
        Assert.Equal("basket", words[0].Text);
        Assert.Equal("basket", words[0].Normalized);
        Assert.Equal(138.84, words[0].Start);
        Assert.Equal(139.46, words[0].End);
    }

    private static List<SpokenWord> Words(params (double Start, double End, string Text)[] items) =>
        items.Select(item => new SpokenWord(item.Text, WhisperWords.Normalize(item.Text), item.Start, item.End)).ToList();

    private static ProjectDocument EmptyProject()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits.Clear();
        return project;
    }
}

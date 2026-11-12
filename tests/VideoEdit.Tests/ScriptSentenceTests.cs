using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ScriptSentenceTests
{
    private const string TwoPlanterScript =
        "# Script\nWe put the planter in the office. Later we moved the planter into storage.";

    [Fact]
    public void ForFindingUsesTheAlignedScriptTokenSentence()
    {
        var spokenTokens = "We put the planter in the office Later we moved the basket into storage".Split(' ');
        var spokenWords = spokenTokens
            .Select((token, index) => new SpokenWord(token, token.ToLowerInvariant(), index, index + 0.8))
            .ToList();
        var aligned = ScriptAligner.Align(
            TwoPlanterScript,
            spokenWords,
            TestSupport.SampleProject(),
            "source-1",
            TestSupport.Fingerprint);
        var drift = Assert.Single(aligned, finding => finding.Kind == ScriptDriftKinds.Substitution);

        Assert.Equal(11.0, drift.Start, 3);
        Assert.Equal(11.8, drift.End, 3);
        Assert.Equal("planter", drift.ScriptText);
        Assert.Equal("basket", drift.SpokenText);
        Assert.Equal(
            "Later we moved the planter into storage.",
            ScriptSentence.ForFinding(TwoPlanterScript, drift));
    }

    [Fact]
    public void RealignPersistsScriptTokenIndexOnExistingFindings()
    {
        var spokenTokens = "We put the planter in the office Later we moved the basket into storage".Split(' ');
        var spokenWords = spokenTokens
            .Select((token, index) => new SpokenWord(token, token.ToLowerInvariant(), index, index + 0.8))
            .ToList();
        var aligned = ScriptAligner.Align(
            TwoPlanterScript,
            spokenWords,
            TestSupport.SampleProject(),
            "source-1",
            TestSupport.Fingerprint);
        var drift = Assert.Single(aligned, finding => finding.Kind == ScriptDriftKinds.Substitution);
        Assert.Equal(11, drift.ScriptTokenIndex);

        var (store, service, _, _) = TestSupport.CreateStore();
        var prior = TestSupport.SampleProject();
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SourceFinding>(
            System.Text.Json.JsonSerializer.Serialize(drift))!;
        legacy.ScriptTokenIndex = null;
        prior.Findings.Add(legacy);
        store.SaveNew(prior);

        var refreshed = service.ReplaceScriptDriftFindings(aligned, prior.Revision);
        var fromDisk = store.Load().Findings.Single(finding => finding.Kind == ScriptDriftKinds.Substitution);
        Assert.Equal(11, fromDisk.ScriptTokenIndex);
        Assert.Equal(
            "Later we moved the planter into storage.",
            ScriptSentence.ForFinding(TwoPlanterScript, fromDisk));
        Assert.True(refreshed.Revision > prior.Revision);

        var again = service.ReplaceScriptDriftFindings(aligned, refreshed.Revision);
        Assert.Equal(refreshed.Revision, again.Revision);
        Assert.Equal(11, store.Load().Findings.Single(finding => finding.Kind == ScriptDriftKinds.Substitution).ScriptTokenIndex);
    }

    [Fact]
    public void ForFindingRequiresAChoiceWhenThePhraseIsAmbiguous()
    {
        var finding = new SourceFinding
        {
            Kind = ScriptDriftKinds.Substitution,
            ScriptText = "planter",
            SpokenText = "basket",
        };

        var error = Assert.Throws<VideoEditException>(() => ScriptSentence.ForFinding(TwoPlanterScript, finding));
        Assert.Equal("validation", error.Code);
        Assert.Contains("more than once", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

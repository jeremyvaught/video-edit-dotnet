using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class SceneReviewTests
{
    [Fact]
    public void RejectingASceneBlocksExportAndClearsFinalApproval()
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

        var rejected = service.RecordSceneDecision("scene-1", DecisionStates.Rejected, "Still looks wrong.", store.Load().Revision);
        var acceptance = Assert.IsType<SceneAcceptance>(rejected.Scenes[0].Acceptance);
        Assert.Equal(DecisionStates.Rejected, acceptance.ReviewDecision);
        Assert.Equal("Still looks wrong.", acceptance.Note);
        Assert.Null(rejected.FinalApproval);
        Assert.False(CompositionResolver.SceneAcceptanceValid(
            rejected,
            rejected.Scenes[0],
            CompositionHasher.SceneHash(rejected, rejected.Scenes[0])));
        Assert.Contains(
            CompositionResolver.ExportBlocks(rejected),
            block => block.Contains("rejected", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingAcceptanceWithoutDecisionStillCountsAsApproved()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        project.Scenes[0].Acceptance = new SceneAcceptance
        {
            CompositionHash = CompositionHasher.SceneHash(project, project.Scenes[0]),
        };

        Assert.True(CompositionResolver.SceneAcceptanceValid(
            project,
            project.Scenes[0],
            CompositionHasher.SceneHash(project, project.Scenes[0])));
    }
}

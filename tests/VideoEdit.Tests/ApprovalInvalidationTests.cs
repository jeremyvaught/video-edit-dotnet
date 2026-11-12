using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ApprovalInvalidationTests
{
    [Fact]
    public void ChangingARejectedProposalResetsItsDecisionWithoutChangingResolvedOutput()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());

        service.RecordDecision("e-1", DecisionStates.Rejected, null, 1);
        var accepted = service.AcceptScene("scene-1", 2);
        var beforeHash = CompositionHasher.FullHash(accepted);
        Assert.True(CompositionResolver.SceneAcceptanceValid(accepted, accepted.Scenes[0], CompositionHasher.SceneHash(accepted, accepted.Scenes[0])));

        var updated = service.UpdateProposal("e-1", 5, 9, EditKinds.Flub, "Moved rejected cut", 3);
        var afterHash = CompositionHasher.FullHash(updated);
        var edit = updated.Scenes[0].Edits[0];

        Assert.Equal(beforeHash, afterHash);
        Assert.Equal(DecisionStates.Pending, edit.Decision);
        Assert.Equal(2, edit.ProposalRevision);
        Assert.Null(edit.DecisionProposalRevision);
        Assert.Null(updated.Scenes[0].Acceptance);
        Assert.Null(updated.FinalApproval);
        Assert.Equal([new TimeRange(0, 20)], CompositionResolver.ResolveScene(updated, updated.Scenes[0]).KeepRanges.Select(item => item.Range));
    }

    [Fact]
    public void MatchingHashesDoNotBypassPendingEdits()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Acceptance = new SceneAcceptance
        {
            CompositionHash = CompositionHasher.SceneHash(project, project.Scenes[0]),
        };
        project.FinalApproval = new FinalApproval
        {
            CompositionHash = CompositionHasher.FullHash(project),
        };

        Assert.True(CompositionResolver.HasBlockingEdits(project.Scenes[0]));
        Assert.False(CompositionResolver.SceneAcceptanceValid(
            project,
            project.Scenes[0],
            CompositionHasher.SceneHash(project, project.Scenes[0])));
        Assert.Contains(CompositionResolver.ExportBlocks(project), block => block.Contains("pending", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordingAcceptanceDoesNotInvalidateItself()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        service.RecordDecision("e-1", DecisionStates.Rejected, null, 1);
        var accepted = service.AcceptScene("scene-1", 2);
        var hash = CompositionHasher.SceneHash(accepted, accepted.Scenes[0]);

        Assert.Equal(hash, accepted.Scenes[0].Acceptance?.CompositionHash);
        Assert.True(CompositionResolver.SceneAcceptanceValid(accepted, accepted.Scenes[0], hash));
    }

    [Fact]
    public void ApprovingACutChangesCompositionIdentity()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        service.RecordDecision("e-1", DecisionStates.Rejected, null, 1);
        service.AcceptScene("scene-1", 2);
        var rejectedHash = CompositionHasher.FullHash(store.Load());

        var approved = service.RecordDecision("e-1", DecisionStates.Approved, null, 3);
        Assert.NotEqual(rejectedHash, CompositionHasher.FullHash(approved));
        Assert.Null(approved.Scenes[0].Acceptance);
        Assert.Equal([new TimeRange(0, 5), new TimeRange(8, 20)], CompositionResolver.ResolveScene(approved, approved.Scenes[0]).KeepRanges.Select(item => item.Range));
    }
}

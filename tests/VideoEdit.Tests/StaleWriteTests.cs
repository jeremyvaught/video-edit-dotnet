using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class StaleWriteTests
{
    [Fact]
    public void RejectsAWriteThatSuppliesAnOldRevision()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        service.RecordDecision("e-1", DecisionStates.Approved, null, 1);

        var error = Assert.Throws<VideoEditException>(() =>
            service.RecordDecision("e-1", DecisionStates.Rejected, null, 1));

        Assert.Equal("stale_write", error.Code);
        Assert.Equal(DecisionStates.Approved, store.Load().Scenes[0].Edits[0].Decision);
        Assert.Equal(2, store.Load().Revision);
    }
}

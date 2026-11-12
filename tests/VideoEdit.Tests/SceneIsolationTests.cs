using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class SceneIsolationTests
{
    [Fact]
    public void ApprovingAnEditLeavesAnotherSceneUsingTheSameSourceUnchanged()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject(secondSceneSelection: new TimeRange(0, 20));
        store.SaveNew(project);

        service.RecordDecision("e-1", DecisionStates.Approved, null, 1);
        var loaded = store.Load();
        var scene1 = CompositionResolver.ResolveScene(loaded, loaded.Scenes[0]);
        var scene2 = CompositionResolver.ResolveScene(loaded, loaded.Scenes[1]);

        Assert.Equal([new TimeRange(0, 5), new TimeRange(8, 20)], scene1.KeepRanges.Select(item => item.Range));
        Assert.Equal([new TimeRange(0, 20)], scene2.KeepRanges.Select(item => item.Range));
        Assert.Equal(20, scene2.DurationSeconds);
    }
}

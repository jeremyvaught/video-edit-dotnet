using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class OverlappingCutTests
{
    [Fact]
    public void RejectsOverlappingRemovalsInOneScene()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-2",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 7,
            End = 10,
            Kind = EditKinds.Pause,
            Reason = "Overlap",
            Decision = DecisionStates.Pending,
        });

        var errors = ProjectValidator.Collect(project);
        Assert.Contains(errors, error => error.Contains("overlapping removals", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnEditThatCrossesASelectionBoundary()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Selections[0].End = 10;
        project.Scenes[0].Edits[0].Start = 8;
        project.Scenes[0].Edits[0].End = 12;

        var errors = ProjectValidator.Collect(project);
        Assert.Contains(errors, error => error.Contains("not wholly inside", StringComparison.Ordinal));
    }
}

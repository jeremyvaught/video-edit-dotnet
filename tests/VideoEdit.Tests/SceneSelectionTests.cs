using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class SceneSelectionTests
{
    [Fact]
    public void RejectsOverlappingSelectionsOfTheSameSourceInOneScene()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Selections.Add(new SourceSelection
        {
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 10,
            End = 15,
        });
        project.Scenes[0].Selections[0].End = 12;

        var errors = ProjectValidator.Collect(project);
        Assert.Contains(errors, error => error.Contains("repeats source", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowsAdjacentSelectionsOfTheSameSource()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Selections[0].End = 10;
        project.Scenes[0].Selections.Add(new SourceSelection
        {
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 10,
            End = 20,
        });

        Assert.Empty(ProjectValidator.Collect(project));
    }
}

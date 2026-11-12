using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ProjectStoreTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingProjectDirectoryIsRejectedBeforeOpeningTheStore(string directory)
    {
        var error = Assert.Throws<VideoEditException>(() =>
            new ProjectStore(new VideoEditOptions { ProjectDirectory = directory }));

        Assert.Equal("validation", error.Code);
        Assert.Contains("VideoEdit__ProjectDirectory", error.Message, StringComparison.Ordinal);
    }
}

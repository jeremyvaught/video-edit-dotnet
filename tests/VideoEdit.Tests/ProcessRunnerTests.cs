using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task AlreadyCanceledRequestDoesNotStartAProcess()
    {
        using var runner = new ProcessRunner();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        // A nonexistent executable distinguishes cancellation before launch from
        // cancellation after launch, without risking any process side effects.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "video-edit-nonexistent-executable", [], TimeSpan.FromSeconds(5), cancel.Token));
    }

    [Fact]
    public async Task InvalidTimeoutIsRejectedBeforeStartingAProcess()
    {
        using var runner = new ProcessRunner();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(
            "video-edit-nonexistent-executable", [], TimeSpan.FromSeconds(-2), CancellationToken.None));
    }
}

using Microsoft.Extensions.Options;
using VideoEdit;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class ValidationContractTests
{
    [Fact]
    public void ApproveFinalRefusesWithoutACurrentRoughCut()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        service.RecordDecision("e-1", DecisionStates.Rejected, null, 1);
        service.AcceptScene("scene-1", 2);

        var error = Assert.Throws<VideoEditException>(() => service.ApproveFinal(3));
        Assert.Contains("rough-cut", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.Load().FinalApproval);
    }

    [Fact]
    public void SceneCorrectionDoesNotRewriteSourceFindings()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Findings.Add(new SourceFinding
        {
            Id = "f-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 5,
            End = 8,
            Kind = EditKinds.Flub,
            Reason = "evidence",
        });
        project.Scenes[0].Edits[0].FindingId = "f-1";
        store.SaveNew(project);

        var updated = service.ApplyProposalTimes([new ProposalTimeUpdate("e-1", 5, 9, "scene correction")], 1);

        Assert.Equal(9, updated.Scenes[0].Edits[0].End);
        Assert.Equal(8, updated.Findings[0].End);
        Assert.Equal("evidence", updated.Findings[0].Reason);
    }

    [Fact]
    public void UnsupportedOutputSettingsAreRejected()
    {
        var project = TestSupport.SampleProject();
        project.Output.VideoCodec = "prores";
        var errors = ProjectValidator.Collect(project);
        Assert.Contains(errors, error => error.Contains("prores", StringComparison.Ordinal));
    }

    [Fact]
    public void FadeTreatmentIsHashedOnTheResolvedComposition()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Approved;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        var clean = CompositionHasher.FullHash(project);
        project.Scenes[0].Edits[0].NoCleanBoundary = true;
        Assert.NotEqual(clean, CompositionHasher.FullHash(project));
    }

    [Fact]
    public async Task MutationWaitingOnTheStoreIsRejectedOnceExportStarts()
    {
        var (store, service, gate, _) = TestSupport.CreateStore();
        store.SaveNew(TestSupport.SampleProject());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var rev = store.Load().Revision;
        var holder = Task.Run(() => store.Mutate(rev, _ =>
        {
            entered.Set();
            release.Wait();
            return false;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));

        var savedDuringExport = false;
        Exception? mutationError = null;
        var thread = new Thread(() =>
        {
            try
            {
                service.UpdateProposal("e-1", 5, 10, EditKinds.Flub, "race test", rev);
                savedDuringExport = gate.IsExporting;
            }
            catch (Exception ex)
            {
                mutationError = ex;
            }
        });
        thread.Start();
        Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 3000));

        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var export = gate.RunAsync("export", true, _ => finish.Task, CancellationToken.None);
        release.Set();
        await holder;
        Assert.True(thread.Join(3000));
        finish.SetResult(true);
        await export;
        Assert.False(savedDuringExport);
        Assert.IsType<VideoEditException>(mutationError);
    }

    [Fact]
    public async Task DisposeKillsEveryOwnedProcess()
    {
        using var cancel = new CancellationTokenSource();
        using var runner = new ProcessRunner();
        var longRunning = runner.RunAsync("/bin/sleep", ["10"], TimeSpan.FromSeconds(15), cancel.Token);
        await runner.RunAsync("/usr/bin/true", [], TimeSpan.FromSeconds(5), CancellationToken.None);
        runner.Dispose();
        var result = await longRunning.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEqual(0, result.ExitCode);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            runner.RunAsync("/usr/bin/true", [], TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}

using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class GeneratedSceneTests
{
    [Fact]
    public void InsertSplitsTheInstructionCutAndUsesMeasuredDuration()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = SampleWithInstruction();
        store.SaveNew(project);

        var still = new SourceAsset
        {
            Id = "still-generated",
            Path = "/tmp/still.jpg",
            Fingerprint = new string('b', 64),
            Kind = "image",
        };
        var updated = service.InsertGeneratedScene(
            "scene-1",
            "narration-test",
            "generated/narration-test/bedded.wav",
            new string('c', 64),
            7.84,
            still,
            1);

        Assert.Equal(["scene-1", "scene-generated", "scene-close"], updated.Scenes.Select(scene => scene.Id));
        Assert.Equal(0, updated.Scenes[0].Selections[0].Start);
        Assert.Equal(10, updated.Scenes[0].Selections[0].End);
        Assert.Equal(["e-1"], updated.Scenes[0].Edits.Select(edit => edit.Id));
        Assert.Null(updated.Scenes[0].Acceptance);
        Assert.Null(updated.FinalApproval);

        var generated = updated.Scenes[1];
        Assert.Equal("narration-test", generated.NarrationAssetId);
        Assert.Equal("generated/narration-test/bedded.wav", generated.NarrationPath);
        Assert.Equal(new string('c', 64), generated.NarrationFingerprint);
        Assert.Equal("still-generated", generated.VisualAssetId);
        Assert.Equal(7.84, generated.NarrationDurationSeconds);
        Assert.Equal("generated/narration-test/bedded.wav", updated.Narration.Derivative!.Path);
        Assert.Equal(7.84, updated.Narration.Derivative.DurationSeconds);
        Assert.Empty(generated.Selections);
        Assert.Empty(generated.Edits);

        var close = updated.Scenes[2];
        Assert.Equal(16, close.Selections[0].Start);
        Assert.Equal(20, close.Selections[0].End);
        Assert.Equal(["e-postroll"], close.Edits.Select(edit => edit.Id));
        Assert.Equal("scene-close", close.Edits[0].SceneId);

        var resolved = CompositionResolver.ResolveProposed(updated);
        Assert.Equal(7.84, resolved.Scenes[1].DurationSeconds);
        Assert.Empty(resolved.Scenes[1].KeepRanges);
        Assert.Contains(updated.Findings, finding => finding.Id == "instruction-last-paragraph");
    }

    [Fact]
    public void ChangingTheNarrationAssetChangesTheSceneHash()
    {
        var project = SampleWithInstruction();
        project.Sources.Add(new SourceAsset
        {
            Id = "still-generated",
            Path = "/tmp/still.jpg",
            Fingerprint = new string('b', 64),
            Kind = "image",
        });
        project.Scenes.Add(new Scene
        {
            Id = "scene-generated",
            Title = "Generated",
            Position = 2,
            NarrationAssetId = "narration-a",
            NarrationPath = "generated/narration-a/bedded.wav",
            NarrationFingerprint = new string('c', 64),
            VisualAssetId = "still-generated",
            NarrationDurationSeconds = 7.84,
        });

        var first = CompositionHasher.SceneHash(project, project.Scenes[1]);
        project.Scenes[1].NarrationAssetId = "narration-b";
        var second = CompositionHasher.SceneHash(project, project.Scenes[1]);
        project.Scenes[1].NarrationAssetId = "narration-a";
        project.Scenes[1].NarrationDurationSeconds = 8.1;
        var third = CompositionHasher.SceneHash(project, project.Scenes[1]);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void GeneratedScenesMayHaveNoSelectedMaterial()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Selections.Clear();
        Assert.Contains(ProjectValidator.Collect(project), error => error.Contains("no selected material"));

        project.Scenes[0].Selections =
        [
            new SourceSelection
            {
                SourceAssetId = "source-1",
                SourceFingerprint = TestSupport.Fingerprint,
                Start = 0,
                End = 20,
            }
        ];
        project.Scenes.Add(new Scene
        {
            Id = "scene-generated",
            Title = "Generated",
            Position = 2,
            NarrationAssetId = "narration-a",
            NarrationPath = "generated/narration-a/bedded.wav",
            NarrationFingerprint = new string('c', 64),
            VisualAssetId = "still-generated",
            NarrationDurationSeconds = 7.84,
        });
        project.Sources.Add(new SourceAsset
        {
            Id = "still-generated",
            Path = "/tmp/still.jpg",
            Fingerprint = new string('b', 64),
            Kind = "image",
        });

        Assert.Empty(ProjectValidator.Collect(project));
    }

    [Fact]
    public void BindingANewNarrationAssetInvalidatesAcceptanceAndFinalApproval()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = SampleWithInstruction();
        store.SaveNew(project);
        var still = new SourceAsset
        {
            Id = "still-generated",
            Path = "/tmp/still.jpg",
            Fingerprint = new string('b', 64),
            Kind = "image",
        };
        service.InsertGeneratedScene(
            "scene-1",
            "narration-a",
            "generated/narration-a/bedded.wav",
            new string('c', 64),
            7.84,
            still,
            1);
        project = store.Load();
        project.Scenes[0].Edits.ForEach(edit =>
        {
            edit.Decision = DecisionStates.Approved;
            edit.DecisionProposalRevision = edit.ProposalRevision;
        });
        project.Scenes[2].Edits.ForEach(edit =>
        {
            edit.Decision = DecisionStates.Approved;
            edit.DecisionProposalRevision = edit.ProposalRevision;
        });
        File.WriteAllText(store.ProjectPath, System.Text.Json.JsonSerializer.Serialize(project, ProjectStore.JsonOptions));
        project = store.Load();
        service.AcceptScene("scene-1", project.Revision);
        project = store.Load();
        service.AcceptScene("scene-generated", project.Revision);
        project = store.Load();
        service.AcceptScene("scene-close", project.Revision);
        project = store.Load();
        project.FinalApproval = new FinalApproval { CompositionHash = CompositionHasher.FullHash(project) };
        File.WriteAllText(store.ProjectPath, System.Text.Json.JsonSerializer.Serialize(project, ProjectStore.JsonOptions));

        var rebound = service.BindGeneratedNarration(
            "narration-b",
            "generated/narration-b/bedded.wav",
            new string('d', 64),
            8.2,
            store.Load().Revision);
        var generated = rebound.Scenes.First(scene => scene.Id == "scene-generated");
        Assert.Equal("narration-b", generated.NarrationAssetId);
        Assert.Equal(8.2, generated.NarrationDurationSeconds);
        Assert.Null(generated.Acceptance);
        Assert.Null(rebound.FinalApproval);
        Assert.NotNull(rebound.Scenes[0].Acceptance);
        Assert.NotNull(rebound.Scenes[2].Acceptance);
        Assert.Equal("narration-b", rebound.Narration.GeneratedAssetId);
    }

    [Fact]
    public void BindingTheSameAssetDoesNotClearAcceptance()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = SampleWithInstruction();
        store.SaveNew(project);
        service.InsertGeneratedScene(
            "scene-1",
            "narration-a",
            "generated/narration-a/bedded.wav",
            new string('c', 64),
            7.84,
            new SourceAsset
            {
                Id = "still-generated",
                Path = "/tmp/still.jpg",
                Fingerprint = new string('b', 64),
                Kind = "image",
            },
            1);
        project = store.Load();
        service.AcceptScene("scene-generated", project.Revision);
        var accepted = store.Load();
        var same = service.BindGeneratedNarration(
            "narration-a",
            "generated/narration-a/bedded.wav",
            new string('c', 64),
            7.84,
            accepted.Revision);
        Assert.Equal(accepted.Revision, same.Revision);
        Assert.NotNull(same.Scenes.First(scene => scene.Id == "scene-generated").Acceptance);
    }

    [Fact]
    public void AGeneratedOnlyProjectIsValid()
    {
        var project = new ProjectDocument
        {
            Name = "generated-only",
            Revision = 1,
            Sources =
            [
                new SourceAsset
                {
                    Id = "still-generated",
                    Path = "/tmp/still.jpg",
                    Fingerprint = new string('b', 64),
                    Kind = "image",
                }
            ],
            Scenes =
            [
                new Scene
                {
                    Id = "scene-generated",
                    Title = "Generated",
                    Position = 1,
                    NarrationAssetId = "narration-a",
                    NarrationPath = "generated/narration-a/bedded.wav",
                    NarrationFingerprint = new string('c', 64),
                    VisualAssetId = "still-generated",
                    NarrationDurationSeconds = 7.84,
                }
            ],
        };

        Assert.Empty(ProjectValidator.Collect(project));
        var resolved = CompositionResolver.ResolveProposed(project);
        Assert.Equal(7.84, resolved.DurationSeconds);
        Assert.True(resolved.Scenes[0].IsGenerated);
    }

    [Fact]
    public void BindingAMixResultWithoutAScenePersistsTheDerivative()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        store.SaveNew(SampleWithInstruction());
        var older = Path.Combine(root, "generated", "narration-test", "bedded-ffffffffffffffffffffffffffffffff.wav");
        var newer = Path.Combine(root, "generated", "narration-test", "bedded-00000000000000000000000000000000.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        File.WriteAllText(older, "older-take");
        File.WriteAllText(newer, "newer-take");

        var bound = service.BindGeneratedNarration(
            "narration-test",
            "generated/narration-test/bedded-00000000000000000000000000000000.wav",
            new string('e', 64),
            8.2,
            1);

        Assert.Null(bound.Scenes.FirstOrDefault(scene => scene.IsGenerated));
        Assert.Equal("narration-test", bound.Narration.GeneratedAssetId);
        Assert.Equal("generated/narration-test/bedded-00000000000000000000000000000000.wav", bound.Narration.Derivative!.Path);
        Assert.Equal(new string('e', 64), bound.Narration.Derivative.Fingerprint);
        Assert.Equal(8.2, bound.Narration.Derivative.DurationSeconds);
    }

    [Fact]
    public void InsertionBindsTheRecordedDerivativeWhenAnOlderFileSortsHigher()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        store.SaveNew(SampleWithInstruction());
        var older = Path.Combine(root, "generated", "narration-test", "bedded-ffffffffffffffffffffffffffffffff.wav");
        var newer = Path.Combine(root, "generated", "narration-test", "bedded-00000000000000000000000000000000.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        File.WriteAllText(older, "older-take");
        File.WriteAllText(newer, "newer-take");
        service.BindGeneratedNarration(
            "narration-test",
            "generated/narration-test/bedded-00000000000000000000000000000000.wav",
            new string('e', 64),
            8.2,
            1);

        var scanned = Directory.GetFiles(Path.Combine(root, "generated", "narration-test"), "bedded*.wav")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .First();
        Assert.Equal(older, scanned);

        var updated = service.InsertGeneratedScene(
            "scene-1",
            new SourceAsset
            {
                Id = "still-generated",
                Path = "/tmp/still.jpg",
                Fingerprint = new string('b', 64),
                Kind = "image",
            },
            store.Load().Revision);

        var generated = updated.Scenes.Single(scene => scene.IsGenerated);
        Assert.Equal("generated/narration-test/bedded-00000000000000000000000000000000.wav", generated.NarrationPath);
        Assert.Equal(new string('e', 64), generated.NarrationFingerprint);
        Assert.Equal(8.2, generated.NarrationDurationSeconds);
        Assert.Equal(8.2, updated.Narration.Derivative!.DurationSeconds);
    }

    [Fact]
    public void InsertionRefusesToScanWhenNoDerivativeIsRecorded()
    {
        var (store, service, _, root) = TestSupport.CreateStore();
        var project = SampleWithInstruction();
        project.Narration.GeneratedAssetId = "narration-test";
        store.SaveNew(project);
        Directory.CreateDirectory(Path.Combine(root, "generated", "narration-test"));
        File.WriteAllText(
            Path.Combine(root, "generated", "narration-test", "bedded-ffffffffffffffffffffffffffffffff.wav"),
            "older-take");

        var error = Assert.Throws<VideoEditException>(() => service.InsertGeneratedScene(
            "scene-1",
            new SourceAsset
            {
                Id = "still-generated",
                Path = "/tmp/still.jpg",
                Fingerprint = new string('b', 64),
                Kind = "image",
            },
            1));
        Assert.Contains("match narration", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectDocument SampleWithInstruction()
    {
        var project = TestSupport.SampleProject();
        project.Scenes[0].Acceptance = new SceneAcceptance { CompositionHash = "stale" };
        project.FinalApproval = new FinalApproval { CompositionHash = "stale" };
        project.Findings.Add(new SourceFinding
        {
            Id = "instruction-last-paragraph",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            Start = 10,
            End = 16,
            Kind = EditKinds.Instruction,
        });
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-instruction",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            FindingId = "instruction-last-paragraph",
            ProposalRevision = 1,
            Start = 10,
            End = 16,
            Kind = EditKinds.Instruction,
            Decision = DecisionStates.Approved,
            DecisionProposalRevision = 1,
        });
        project.Scenes[0].Edits.Add(new SceneEdit
        {
            Id = "e-postroll",
            SceneId = "scene-1",
            SourceAssetId = "source-1",
            SourceFingerprint = TestSupport.Fingerprint,
            ProposalRevision = 1,
            Start = 18,
            End = 20,
            Kind = EditKinds.Postroll,
            Decision = DecisionStates.Approved,
            DecisionProposalRevision = 1,
        });
        return project;
    }
}

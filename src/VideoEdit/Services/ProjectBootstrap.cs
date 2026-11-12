using Microsoft.Extensions.Options;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class ProjectBootstrap(
    ProjectStore store,
    IOptions<VideoEditOptions> options,
    IWebHostEnvironment environment,
    IProcessRunner runner)
{
    public async Task<ProjectDocument> EnsureProjectAsync(CancellationToken cancellationToken = default)
    {
        EnsurePolicy();
        if (store.Exists())
        {
            return store.Load();
        }

        var root = options.Value.ProjectDirectory;
        var sourcePath = Path.Combine(root, "sources", "source.mov");
        if (!File.Exists(sourcePath))
        {
            throw VideoEditException.NotFound($"Source is missing at {sourcePath}.");
        }

        var analysis = Path.Combine(root, "edit", "analysis");
        var probe = await SourceMediaProbe.InspectAsync(
            runner,
            options.Value.FfprobePath,
            sourcePath,
            analysis,
            cancellationToken);
        var fingerprint = MediaRenderer.FingerprintFile(sourcePath);
        var source = new SourceAsset
        {
            Id = "source-1",
            Path = sourcePath,
            Fingerprint = fingerprint,
            DurationSeconds = probe.DurationSeconds,
            CodecName = probe.CodecName,
            AverageFrameRate = probe.AverageFrameRate,
            Width = probe.Width,
            Height = probe.Height,
        };

        var project = new ProjectDocument
        {
            Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Sources = [source],
            Findings = [],
            Scenes =
            [
                new Scene
                {
                    Id = "scene-1",
                    Title = "Talking head",
                    Position = 1,
                    Selections =
                    [
                        new SourceSelection
                        {
                            SourceAssetId = source.Id,
                            SourceFingerprint = source.Fingerprint,
                            Start = 0,
                            End = probe.DurationSeconds,
                        }
                    ],
                }
            ],
            Output = new OutputSettings(),
        };

        ProjectValidator.Validate(project);
        return store.SaveNew(project);
    }

    public void EnsurePolicy()
    {
        var dest = Path.Combine(options.Value.ProjectDirectory, "edit", EditingPolicy.FileName);
        if (File.Exists(dest))
        {
            EditingPolicy.LoadOrDefault(dest);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var seedPath = Path.Combine(environment.ContentRootPath, "seed", EditingPolicy.FileName);
        if (File.Exists(seedPath))
        {
            File.Copy(seedPath, dest);
            return;
        }

        EditingPolicy.Write(dest, EditingPolicy.Default);
    }

    public string SeedFindingsPath => Path.Combine(environment.ContentRootPath, "seed", "findings.json");
}

public sealed class FindingsSeed
{
    public List<SourceFinding> Findings { get; set; } = [];

    public List<ReviewQuestion> Questions { get; set; } = [];
}

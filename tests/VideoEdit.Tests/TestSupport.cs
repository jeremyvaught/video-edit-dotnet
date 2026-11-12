using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using VideoEdit;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

internal static class TestSupport
{
    public static readonly string Fingerprint = new('a', 64);

    public static (ProjectStore Store, ProjectService Service, MediaGate Gate, string Root) CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "edit"));
        var options = new VideoEditOptions { ProjectDirectory = root };
        var store = new ProjectStore(options);
        var gate = new MediaGate();
        var renderer = new MediaRenderer(
            store,
            gate,
            new UnusedRunner(),
            Options.Create(options));
        var service = new ProjectService(store, gate, renderer);
        return (store, service, gate, root);
    }

    public static ProjectDocument SampleProject(
        TimeRange? secondSceneSelection = null,
        TimeRange? firstEdit = null)
    {
        var source = new SourceAsset
        {
            Id = "source-1",
            Path = "/tmp/source.mov",
            Fingerprint = Fingerprint,
            DurationSeconds = 20,
        };

        var scene1 = new Scene
        {
            Id = "scene-1",
            Title = "One",
            Position = 1,
            Selections =
            [
                new SourceSelection
                {
                    SourceAssetId = source.Id,
                    SourceFingerprint = source.Fingerprint,
                    Start = 0,
                    End = 20,
                }
            ],
            Edits =
            [
                new SceneEdit
                {
                    Id = "e-1",
                    SceneId = "scene-1",
                    SourceAssetId = source.Id,
                    SourceFingerprint = source.Fingerprint,
                    ProposalRevision = 1,
                    Start = firstEdit?.Start ?? 5,
                    End = firstEdit?.End ?? 8,
                    Kind = EditKinds.Flub,
                    Reason = "Flub",
                    Confidence = 0.8,
                    Decision = DecisionStates.Pending,
                }
            ],
        };

        var scenes = new List<Scene> { scene1 };
        if (secondSceneSelection is TimeRange extra)
        {
            scenes.Add(new Scene
            {
                Id = "scene-2",
                Title = "Two",
                Position = 2,
                Selections =
                [
                    new SourceSelection
                    {
                        SourceAssetId = source.Id,
                        SourceFingerprint = source.Fingerprint,
                        Start = extra.Start,
                        End = extra.End,
                    }
                ],
            });
        }

        return new ProjectDocument
        {
            Name = "sample",
            Revision = 1,
            Sources = [source],
            Scenes = scenes,
            Output = new OutputSettings(),
        };
    }

    public static (ProjectBootstrap Bootstrap, ProjectStore Store, ProjectService Service, string Root) CreateBootstrap(
        IProcessRunner runner,
        string? contentRoot = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        var options = new VideoEditOptions
        {
            ProjectDirectory = root,
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
        };
        var store = new ProjectStore(options);
        var gate = new MediaGate();
        var renderer = new MediaRenderer(store, gate, runner, Options.Create(options));
        var service = new ProjectService(store, gate, renderer);
        var env = new TestWebHostEnvironment { ContentRootPath = contentRoot ?? root };
        var bootstrap = new ProjectBootstrap(store, Options.Create(options), env, runner);
        return (bootstrap, store, service, root);
    }

    public static ProjectPrepareService CreatePrepare(
        ProjectStore store,
        ProjectService service,
        IProcessRunner runner)
    {
        var options = Options.Create(new VideoEditOptions
        {
            ProjectDirectory = store.ProjectDirectory,
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            WhisperCliPath = "whisper-cli",
            WhisperModelPath = "models/test-whisper.bin",
        });
        var gate = new MediaGate();
        var renderer = new MediaRenderer(store, gate, runner, options);
        return new ProjectPrepareService(
            store,
            service,
            new VadAnalyzer(store, runner, options),
            new TranscriptService(store, runner, options),
            renderer,
            runner,
            options);
    }

    public static async Task<ReviewHost> StartReviewAsync(string projectDirectory, string listenUrl)
    {
        var dll = typeof(ProjectService).Assembly.Location;
        var start = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["VideoEdit__ProjectDirectory"] = projectDirectory;
        start.Environment["VideoEdit__ListenUrl"] = listenUrl;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the review host.");
        var host = new ReviewHost(process);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                    throw new InvalidOperationException("Review host exited before listening. " + error);
                }

                if (line.Contains("Now listening on:", StringComparison.Ordinal))
                {
                    return host;
                }
            }
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    internal sealed class ReviewHost : IDisposable
    {
        private readonly Process _process;

        public ReviewHost(Process process) => _process = process;

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }

    public static MediaRenderer Renderer(string root, IProcessRunner runner)
    {
        var options = Options.Create(new VideoEditOptions
        {
            ProjectDirectory = root,
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
        });
        var store = new ProjectStore(options.Value);
        return new MediaRenderer(store, new MediaGate(), runner, options);
    }

    internal sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VideoEdit";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = "";

        public string EnvironmentName { get; set; } = "Development";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = "";
    }

    private sealed class UnusedRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test does not run media processes.");
    }
}

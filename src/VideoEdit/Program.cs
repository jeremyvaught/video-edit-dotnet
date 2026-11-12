using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using VideoEdit;
using VideoEdit.Models;
using VideoEdit.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);
builder.Services.Configure<VideoEditOptions>(builder.Configuration.GetSection(VideoEditOptions.SectionName));
var configuredUrl = builder.Configuration[$"{VideoEditOptions.SectionName}:ListenUrl"] ?? "http://127.0.0.1:5080";
var prepareOnly = args.Contains("--prepare-only", StringComparer.Ordinal);
var listenUrl = configuredUrl;
if (ReviewListen.WillServe(args))
{
    var plan = await ReviewListen.ResolveAsync(configuredUrl);
    if (plan.Conflict is not null)
    {
        Console.WriteLine(ReviewListen.ConflictMessage(plan.Conflict));
    }

    if (string.IsNullOrEmpty(plan.Url))
    {
        Console.Error.WriteLine(
            ReviewListen.TryParse(configuredUrl, out _, out _)
                ? $"No free loopback port available after {configuredUrl}. Nothing was prepared."
                : $"ListenUrl '{configuredUrl}' is not a loopback http URL. Nothing was prepared.");
        return;
    }

    listenUrl = plan.Url;
    if (!string.Equals(listenUrl, configuredUrl, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"review {listenUrl}");
    }
}

builder.WebHost.UseUrls(listenUrl);

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<VideoEditOptions>>().Value);
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddSingleton<MediaGate>();
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<IProcessRunner>(sp => sp.GetRequiredService<ProcessRunner>());
builder.Services.AddSingleton<MediaRenderer>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<ProjectBootstrap>();
builder.Services.AddSingleton<ReviewAssembler>();
builder.Services.AddSingleton<VadAnalyzer>();
builder.Services.AddSingleton<TranscriptService>();
builder.Services.AddSingleton<ProjectPrepareService>();
builder.Services.AddSingleton<PickupMixService>();
builder.Services.AddSingleton<RerecordPickupService>();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 512L * 1024 * 1024);
builder.AddNarration();

await using var app = builder.Build();
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<ProcessRunner>().Dispose());

// Source preparation can run without bootstrapping or modifying the editing project.
if (await NarrationCommand.TryRunAsync(args, app.Services)) return;

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var exception = feature?.Error;
        if (exception is VideoEditException edit)
        {
            context.Response.StatusCode = edit.Code switch
            {
                "stale_write" => StatusCodes.Status409Conflict,
                "not_found" => StatusCodes.Status404NotFound,
                "export_blocked" or "validation" => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status409Conflict,
            };
            await context.Response.WriteAsJsonAsync(new { error = edit.Code, message = edit.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "server", message = exception?.Message ?? "Unexpected error." });
    });
});

app.UseDefaultFiles();
app.UseStaticFiles();

var options = app.Services.GetRequiredService<VideoEditOptions>();
Directory.CreateDirectory(Path.Combine(options.ProjectDirectory, "edit", "previews"));
Directory.CreateDirectory(Path.Combine(options.ProjectDirectory, "edit", "exports"));
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/media/previews",
    FileProvider = new PhysicalFileProvider(Path.Combine(options.ProjectDirectory, "edit", "previews")),
    ServeUnknownFileTypes = true,
});
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/media/exports",
    FileProvider = new PhysicalFileProvider(Path.Combine(options.ProjectDirectory, "edit", "exports")),
    ServeUnknownFileTypes = true,
});
Directory.CreateDirectory(Path.Combine(options.ProjectDirectory, "generated"));
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/media/generated",
    FileProvider = new PhysicalFileProvider(Path.Combine(options.ProjectDirectory, "generated")),
    ServeUnknownFileTypes = true,
});
Directory.CreateDirectory(Path.Combine(options.ProjectDirectory, "sources"));
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/media/sources",
    FileProvider = new PhysicalFileProvider(Path.Combine(options.ProjectDirectory, "sources")),
    ServeUnknownFileTypes = true,
});

var sourcePath = Path.Combine(options.ProjectDirectory, "sources", "source.mov");
app.MapGet("/media/source", async (HttpContext context) =>
{
    if (!File.Exists(sourcePath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "video/quicktime";
    await context.Response.SendFileAsync(sourcePath);
});

await app.Services.GetRequiredService<ProjectBootstrap>().EnsureProjectAsync();

if (args.Contains("--prepare", StringComparer.Ordinal)
    || args.Contains("--prepare-only", StringComparer.Ordinal))
{
    var prepared = await app.Services.GetRequiredService<ProjectPrepareService>().RunAsync(CancellationToken.None);
    var hostCuts = prepared.Scenes.SelectMany(scene => scene.Edits)
        .Count(edit => edit.Kind is EditKinds.Preroll or EditKinds.Pause or EditKinds.Postroll);
    var drifts = prepared.Findings.Count(finding => ScriptDriftKinds.IsDrift(finding.Kind));
    Console.WriteLine($"prepared r{prepared.Revision} host-cuts={hostCuts} drifts={drifts}");
    if (prepareOnly)
    {
        return;
    }

    Console.WriteLine($"review {listenUrl}");
}

if (args.Contains("--import-findings", StringComparer.Ordinal))
{
    var service = app.Services.GetRequiredService<ProjectService>();
    var bootstrap = app.Services.GetRequiredService<ProjectBootstrap>();
    var path = args.SkipWhile(arg => arg != "--import-findings").Skip(1).FirstOrDefault()
        ?? bootstrap.SeedFindingsPath;
    var project = service.ImportFindings(path);
    Console.WriteLine($"imported {project.Findings.Count} findings r{project.Revision}");
    return;
}

if (args.Contains("--set-narration-voice", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var service = app.Services.GetRequiredService<ProjectService>();
    var voiceId = args.SkipWhile(arg => arg != "--set-narration-voice").Skip(1).FirstOrDefault()
        ?? throw VideoEditException.Validation("--set-narration-voice requires a voice ID.");
    var modelId = args.SkipWhile(arg => arg != "--model-id").Skip(1).FirstOrDefault()
        ?? "eleven_multilingual_v2";
    var project = service.SetNarrationSettings(voiceId, modelId, store.Load().Revision);
    Console.WriteLine($"{project.Narration.VoiceId} {project.Narration.ModelId} r{project.Revision}");
    return;
}

if (args.Contains("--align-script", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var service = app.Services.GetRequiredService<ProjectService>();
    var project = store.Load();
    var scriptPath = Path.Combine(store.ProjectDirectory, "sources", "script.md");
    if (!File.Exists(scriptPath))
    {
        throw VideoEditException.Validation("sources/script.md is required to align the transcript.");
    }

    var source = project.Sources.FirstOrDefault(item => item.Kind == "video")
        ?? throw VideoEditException.Validation("A recorded video source is required to align the transcript.");
    var words = WhisperWords.Parse(File.ReadAllText(WhisperWords.FileName(Path.Combine(store.ProjectDirectory, "edit", "analysis"))));
    var findings = ScriptAligner.Align(File.ReadAllText(scriptPath), words, project, source.Id, source.Fingerprint);
    var updated = service.ReplaceScriptDriftFindings(findings, project.Revision);
    Console.WriteLine($"aligned {findings.Count} script drifts r{updated.Revision}");
    return;
}

if (args.Contains("--add-source", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var service = app.Services.GetRequiredService<ProjectService>();
    var renderer = app.Services.GetRequiredService<MediaRenderer>();
    var project = store.Load();
    var path = args.SkipWhile(arg => arg != "--add-source").Skip(1).FirstOrDefault()
        ?? throw VideoEditException.Validation("--add-source requires a media path.");
    var sourceId = args.SkipWhile(arg => arg != "--source-id").Skip(1).FirstOrDefault()
        ?? ProjectService.NextPickupId(project);
    var bound = BindSourceFile(store.ProjectDirectory, sourceId, path);
    var source = new SourceAsset
    {
        Id = sourceId,
        Path = bound,
        Fingerprint = MediaRenderer.FingerprintFile(bound),
        DurationSeconds = await renderer.ProbeDurationAsync(bound, CancellationToken.None),
        Kind = "video",
    };
    var updated = service.AddSource(source, project.Revision);
    Console.WriteLine($"added {source.Id} {source.DurationSeconds:0.###}s r{updated.Revision}");
    return;
}

if (args.Contains("--apply-pickup", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var service = app.Services.GetRequiredService<ProjectService>();
    var pickupId = args.SkipWhile(arg => arg != "--pickup").Skip(1).FirstOrDefault()
        ?? throw VideoEditException.Validation("--apply-pickup requires --pickup <source-id>.");
    var updated = service.ApplyPickup(
        pickupId,
        RequireDouble(args, "--start"),
        RequireDouble(args, "--end"),
        store.Load().Revision,
        args.SkipWhile(arg => arg != "--scene").Skip(1).FirstOrDefault(),
        args.SkipWhile(arg => arg != "--finding").Skip(1).FirstOrDefault(),
        TryDouble(args, "--replace-start"),
        TryDouble(args, "--replace-end"),
        args.Contains("--audio-only", StringComparer.Ordinal));
    var scene = updated.Scenes.First(item => item.Selections.Any(selection =>
        selection.SourceAssetId == pickupId
        || string.Equals(selection.AudioSourceAssetId, pickupId, StringComparison.Ordinal)));
    Console.WriteLine($"pickup {pickupId} in {scene.Id} r{updated.Revision}");
    return;
}

if (args.Contains("--prepare-pickups", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var mix = app.Services.GetRequiredService<PickupMixService>();
    var updated = await mix.PrepareAsync(store.Load().Revision, CancellationToken.None);
    foreach (var selection in updated.Scenes.SelectMany(scene => scene.Selections).Where(item => item.HasAudioOverlay))
    {
        Console.WriteLine(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{selection.Start:0.###}-{selection.End:0.###} audio {selection.AudioStart:0.###}-{selection.AudioEnd:0.###} gain {selection.AudioGainDb:0.##} dB{(selection.NeedsCutaway ? " needs-cutaway" : "")}"));
    }

    Console.WriteLine($"prepared pickups r{updated.Revision}");
    return;
}

if (args.Contains("--insert-generated-scene", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var service = app.Services.GetRequiredService<ProjectService>();
    var renderer = app.Services.GetRequiredService<MediaRenderer>();
    var project = store.Load();
    var recorded = project.Scenes.FirstOrDefault(scene => scene.Id == "scene-1")
        ?? throw VideoEditException.NotFound("Scene scene-1 was not found.");
    var instruction = recorded.Edits.FirstOrDefault(edit => edit.Kind == EditKinds.Instruction)
        ?? throw VideoEditException.Validation("The recorded scene has no instruction cut to insert into.");
    var source = project.Sources.First(item => item.Id == recorded.Selections[0].SourceAssetId);
    var stillPath = Path.Combine(store.ProjectDirectory, "sources", "still-generated.jpg");
    if (!File.Exists(stillPath))
    {
        await renderer.ExtractStillAsync(source.Path, instruction.Start - 0.35, stillPath, CancellationToken.None);
    }

    var still = new SourceAsset
    {
        Id = "still-generated",
        Path = stillPath,
        Fingerprint = MediaRenderer.FingerprintFile(stillPath),
        Kind = "image",
    };
    var updated = service.InsertGeneratedScene("scene-1", still, project.Revision);
    var generated = updated.Scenes.First(scene => scene.IsGenerated);
    Console.WriteLine($"inserted {generated.NarrationAssetId} {generated.NarrationDurationSeconds:0.###}s r{updated.Revision}");
    return;
}

if (args.Contains("--build-rough", StringComparer.Ordinal))
{
    await app.Services.GetRequiredService<MediaRenderer>().BuildRoughCutAsync(CancellationToken.None);
    Console.WriteLine("rough-cut ready");
    return;
}

if (args.Contains("--build-splices", StringComparer.Ordinal))
{
    await app.Services.GetRequiredService<MediaRenderer>().BuildSplicesAsync(CancellationToken.None);
    Console.WriteLine("splices ready");
    return;
}

if (args.Contains("--analyze-vad", StringComparer.Ordinal)
    || args.Contains("--snap-pending", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var policy = EditingPolicy.LoadOrDefault(Path.Combine(store.ProjectDirectory, "edit", EditingPolicy.FileName));
    var analyzer = app.Services.GetRequiredService<VadAnalyzer>();
    if (args.Contains("--analyze-vad", StringComparer.Ordinal)
        || !File.Exists(Path.Combine(store.ProjectDirectory, "edit", "analysis", VadSpeechMap.FileName)))
    {
        await analyzer.AnalyzeAsync(policy, CancellationToken.None);
        if (!args.Contains("--snap-pending", StringComparer.Ordinal))
        {
            return;
        }
    }
}

if (args.Contains("--snap-pending", StringComparer.Ordinal))
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var service = app.Services.GetRequiredService<ProjectService>();
    var runner = app.Services.GetRequiredService<IProcessRunner>();
    var videoOptions = app.Services.GetRequiredService<VideoEditOptions>();
    var project = store.Load();
    var energySource = project.Sources[0].Path;
    var includeRejected = new HashSet<string>(StringComparer.Ordinal);
    if (args.Contains("--rejected-pauses", StringComparer.Ordinal))
    {
        foreach (var edit in project.Scenes.SelectMany(scene => scene.Edits))
        {
            if (edit.Kind == EditKinds.Pause && edit.Decision == DecisionStates.Rejected)
            {
                includeRejected.Add(edit.Id);
            }
        }
    }

    service.SnapPendingEdits(
        project.Revision,
        includeRejectedIds: includeRejected.Count > 0 ? includeRejected : null,
        includeApproved: args.Contains("--approved", StringComparer.Ordinal),
        energyMinimum: proposed => AudioEnergy.FindLocalMinimumAsync(
            runner,
            videoOptions.FfmpegPath,
            energySource,
            proposed,
            CancellationToken.None).GetAwaiter().GetResult());
    return;
}

app.MapGet("/api/project", (ReviewAssembler review) => review.Assemble());
app.MapGet("/api/status", (MediaGate gate) => gate.Status());
app.MapGet("/api/diagnostics", (VideoEditOptions videoOptions, ProjectStore store, IProcessRunner runner) =>
{
    var ffmpeg = runner.RunAsync(videoOptions.FfmpegPath, ["-version"], TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
    var ffprobe = runner.RunAsync(videoOptions.FfprobePath, ["-version"], TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
    return new
    {
        ffmpeg = ffmpeg.StandardOutput.Split('\n').FirstOrDefault(),
        ffprobe = ffprobe.StandardOutput.Split('\n').FirstOrDefault(),
        projectPath = store.ProjectPath,
        listenUrl,
    };
});

app.MapPost("/api/edits/{id}/decision", (string id, DecisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.RecordDecision(id, request.Decision, request.Note, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/findings/{id}/timing", (string id, DecisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.ResolveTiming(id, request.Decision, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/findings/{id}/dismiss", (string id, RevisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.DismissFinding(id, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/findings/{id}/cut", async (string id, RevisionRequest request, ProjectService service, MediaRenderer renderer, ReviewAssembler review, CancellationToken token) =>
{
    service.ProposeClauseCut(id, request.ExpectedRevision);
    await renderer.BuildSplicesAsync(token);
    return review.Assemble();
});

app.MapPost("/api/findings/{id}/flag", (string id, RevisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.FlagFinding(id, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/findings/{id}/note", (string id, NoteRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.SaveFindingNote(id, request.Note, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/findings/{id}/pickup", async (string id, HttpRequest request, RerecordPickupService pickups, MediaRenderer renderer, ReviewAssembler review, CancellationToken token) =>
{
    if (!request.HasFormContentType)
    {
        throw VideoEditException.Validation("A pickup file is required.");
    }

    var form = await request.ReadFormAsync(token);
    var file = form.Files.GetFile("file")
        ?? throw VideoEditException.Validation("A pickup file is required.");
    if (!int.TryParse(form["expectedRevision"], out var expectedRevision))
    {
        throw VideoEditException.Validation("expectedRevision is required.");
    }

    var extension = Path.GetExtension(file.FileName);
    if (string.IsNullOrEmpty(extension))
    {
        extension = ".mov";
    }

    var temp = Path.Combine(Path.GetTempPath(), "video-edit-pickups", Guid.NewGuid().ToString("N") + extension);
    Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
    await using (var stream = File.Create(temp))
    {
        await file.CopyToAsync(stream, token);
    }

    try
    {
        await pickups.ApplyFileAsync(id, temp, expectedRevision, cutaway: false, token);
        await renderer.BuildSplicesAsync(token);
        await renderer.BuildRoughCutAsync(token);
        return review.Assemble();
    }
    finally
    {
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }
}).DisableAntiforgery();

app.MapPost("/api/findings/{id}/generate", async (string id, RevisionRequest request, RerecordPickupService pickups, MediaRenderer renderer, ReviewAssembler review, CancellationToken token) =>
{
    await pickups.ApplyGeneratedAsync(id, request.ExpectedRevision, token);
    await renderer.BuildSplicesAsync(token);
    await renderer.BuildRoughCutAsync(token);
    return review.Assemble();
});

app.MapPost("/api/edits/{id}/note", (string id, NoteRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.SaveNote(id, request.Note, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/scenes/{id}/accept", (string id, RevisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.AcceptScene(id, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/scenes/{id}/decision", (string id, DecisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.RecordSceneDecision(id, request.Decision, request.Note, request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/approve-final", (RevisionRequest request, ProjectService service, ReviewAssembler review) =>
{
    service.ApproveFinal(request.ExpectedRevision);
    return review.Assemble();
});

app.MapPost("/api/previews/splices", async (MediaRenderer renderer, ReviewAssembler review, CancellationToken token) =>
{
    await renderer.BuildSplicesAsync(token);
    return review.Assemble();
});

app.MapPost("/api/previews/rough", async (MediaRenderer renderer, ReviewAssembler review, CancellationToken token) =>
{
    await renderer.BuildRoughCutAsync(token);
    return review.Assemble();
});

app.MapPost("/api/export", async (MediaRenderer renderer, CancellationToken token) =>
    await renderer.ExportAsync(token));

app.Run();

static string BindSourceFile(string projectDirectory, string sourceId, string path)
{
    var full = Path.GetFullPath(path);
    if (!File.Exists(full))
    {
        throw VideoEditException.Validation($"Source file is missing: {path}");
    }

    var sourcesDir = Path.GetFullPath(Path.Combine(projectDirectory, "sources"));
    Directory.CreateDirectory(sourcesDir);
    if (full.StartsWith(sourcesDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        return full;
    }

    var extension = Path.GetExtension(full);
    if (string.IsNullOrEmpty(extension))
    {
        extension = ".mov";
    }

    var dest = Path.Combine(sourcesDir, sourceId + extension);
    File.Copy(full, dest, overwrite: false);
    return dest;
}

static double RequireDouble(string[] args, string name)
{
    return TryDouble(args, name)
        ?? throw VideoEditException.Validation($"{name} is required.");
}

static double? TryDouble(string[] args, string name)
{
    var text = args.SkipWhile(arg => arg != name).Skip(1).FirstOrDefault();
    if (text is null)
    {
        return null;
    }

    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
    {
        throw VideoEditException.Validation($"{name} must be a number.");
    }

    return value;
}

public sealed record DecisionRequest(int ExpectedRevision, string Decision, string? Note);

public sealed record NoteRequest(int ExpectedRevision, string? Note);

public sealed record RevisionRequest(int ExpectedRevision);

public partial class Program;

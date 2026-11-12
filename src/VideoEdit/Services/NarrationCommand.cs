using System.Text.Json;
using VideoEdit.Models;

namespace VideoEdit.Services;

public static class NarrationCommand
{
    public static async Task<bool> TryRunAsync(string[] args, IServiceProvider services)
    {
        var list = args.Contains("--list-narration-voices", StringComparer.Ordinal);
        var generate = args.Contains("--generate-narration", StringComparer.Ordinal);
        var rematch = args.Contains("--match-narration-loudness", StringComparer.Ordinal);
        if (!list && !generate && !rematch) return false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (new[] { list, generate, rematch }.Count(flag => flag) != 1)
                throw VideoEditException.Validation("Choose one narration command.");
            if (list)
            {
                var voices = await services.GetRequiredService<INarrationProvider>().ListVoicesAsync(cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(voices, ProjectStore.JsonOptions));
            }
            else if (generate)
            {
                var scriptPath = RequiredValue(args, "--script-file");
                var project = services.GetRequiredService<ProjectStore>().Load();
                var voiceId = OptionalValue(args, "--voice-id") ?? project.Narration.VoiceId;
                if (string.IsNullOrWhiteSpace(voiceId))
                {
                    throw VideoEditException.Validation("Set a project narration voice ID before generating.");
                }

                var text = ScriptPassage.ReadMarked(await File.ReadAllTextAsync(scriptPath, cancellation.Token));
                var request = new NarrationRequest(text, voiceId);
                if (!string.IsNullOrWhiteSpace(project.Narration.ModelId))
                {
                    request = request with { ModelId = project.Narration.ModelId };
                }

                var asset = await services.GetRequiredService<NarrationService>()
                    .GenerateAsync(request, cancellation.Token);
                var mix = await MatchAsync(services, asset, cancellation.Token);
                BindDerivative(services, asset.Id, mix);
                Console.WriteLine(JsonSerializer.Serialize(new { asset, loudness = mix.Loudness, derivative = mix }, ProjectStore.JsonOptions));
            }
            else
            {
                var store = services.GetRequiredService<ProjectStore>();
                var project = store.Load();
                var id = project.Narration.GeneratedAssetId
                    ?? throw VideoEditException.Validation("No generated narration asset is stored on the project.");
                var assetPath = Path.Combine(store.ProjectDirectory, "generated", id, "asset.json");
                var asset = JsonSerializer.Deserialize<NarrationAsset>(
                    await File.ReadAllTextAsync(assetPath, cancellation.Token), ProjectStore.JsonOptions)
                    ?? throw VideoEditException.Validation("The stored narration asset could not be read.");
                var mix = await MatchAsync(services, asset, cancellation.Token);
                BindDerivative(services, asset.Id, mix);
                Console.WriteLine(JsonSerializer.Serialize(new { asset, loudness = mix.Loudness, derivative = mix }, ProjectStore.JsonOptions));
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Narration operation canceled or timed out. Check retained files and provider usage before retrying.");
            Environment.ExitCode = 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is VideoEditException ? error.Message : "Narration operation failed. Check file paths and permissions; any received audio has been retained.");
            Environment.ExitCode = 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
        return true;
    }

    private static async Task<NarrationMixResult> MatchAsync(
        IServiceProvider services,
        NarrationAsset asset,
        CancellationToken token)
    {
        var options = services.GetRequiredService<VideoEditOptions>();
        var policy = EditingPolicy.LoadOrDefault(Path.Combine(options.ProjectDirectory, "edit", EditingPolicy.FileName));
        var diagnosisPath = Path.Combine(options.ProjectDirectory, "edit", "analysis", EditingPolicy.DiagnosisFileName);
        var silences = File.Exists(diagnosisPath)
            ? SilenceSnapper.ParseFfmpegLog(await File.ReadAllTextAsync(diagnosisPath, token), policy)
            : [];
        var recorded = Directory.GetFiles(Path.Combine(options.ProjectDirectory, "edit", "exports"), "export-*.mp4")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? Path.Combine(options.ProjectDirectory, "sources", "source.mov");
        var sample = Directory.GetFiles(Path.Combine(options.ProjectDirectory, "sources"), "*pvc_sp100_s50_sb75*")
            .FirstOrDefault();
        return await services.GetRequiredService<NarrationMixService>()
            .MatchAsync(asset, recorded, sample, silences, policy, token);
    }

    private static void BindDerivative(IServiceProvider services, string assetId, NarrationMixResult mix)
    {
        var current = services.GetRequiredService<ProjectStore>().Load();
        services.GetRequiredService<ProjectService>()
            .BindGeneratedNarration(assetId, mix.Path, mix.Fingerprint, mix.DurationSeconds, current.Revision);
    }

    private static string RequiredValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw VideoEditException.Validation($"{flag} is required for narration generation.");
        return args[index + 1];
    }

    private static string? OptionalValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[index + 1];
    }
}

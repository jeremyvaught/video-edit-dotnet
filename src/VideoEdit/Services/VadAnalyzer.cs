using System.Globalization;
using Microsoft.Extensions.Options;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class VadAnalyzer(
    ProjectStore store,
    IProcessRunner runner,
    IOptions<VideoEditOptions> options)
{
    public Task<VadSpeechMap> EnsureMapAsync(EditingPolicy policy, CancellationToken cancellationToken)
    {
        var project = store.Load();
        var host = project.Sources.FirstOrDefault(source => source.Kind != "image")
            ?? throw VideoEditException.Validation("A recorded source is required for VAD.");
        return EnsureMapAsync(host, project.Sources, policy, cancellationToken);
    }

    public async Task<VadSpeechMap> EnsureMapAsync(
        SourceAsset source,
        IReadOnlyList<SourceAsset> sources,
        EditingPolicy policy,
        CancellationToken cancellationToken)
    {
        var analysis = SourceAnalysis.Directory(store.ProjectDirectory, source, sources);
        var mapPath = SourceAnalysis.VadPath(analysis);
        await EnsureAudio16kAsync(source, sources, cancellationToken);
        if (File.Exists(mapPath))
        {
            var text = File.ReadAllText(mapPath);
            VadSpeechMap map;
            if (text.Contains("Speech segment", StringComparison.Ordinal))
            {
                map = VadSpeechMap.ParseCli(text, Path.GetFileName(options.Value.VadModelPath), policy);
                VadSpeechMap.Write(mapPath, map);
            }
            else
            {
                map = VadSpeechMap.Load(mapPath);
            }

            await WriteDiagnosisAsync(source.Path, analysis, cancellationToken);
            return map;
        }

        return await AnalyzeAsync(source, sources, policy, cancellationToken);
    }

    public async Task<string> EnsureAudio16kAsync(
        SourceAsset source,
        IReadOnlyList<SourceAsset> sources,
        CancellationToken cancellationToken)
    {
        var analysis = SourceAnalysis.Directory(store.ProjectDirectory, source, sources);
        Directory.CreateDirectory(analysis);
        var wavPath = SourceAnalysis.Audio16kPath(analysis);
        if (File.Exists(wavPath))
        {
            return wavPath;
        }

        var extract = await runner.RunAsync(
            options.Value.FfmpegPath,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", source.Path,
                "-ac", "1", "-ar", "16000",
                wavPath,
            ],
            TimeSpan.FromSeconds(120),
            cancellationToken);
        if (extract.ExitCode != 0 || !File.Exists(wavPath))
        {
            throw VideoEditException.Conflict($"Could not extract 16 kHz audio for {source.Id}.");
        }

        return wavPath;
    }

    public Task<VadSpeechMap> AnalyzeAsync(EditingPolicy policy, CancellationToken cancellationToken)
    {
        var project = store.Load();
        var host = project.Sources.FirstOrDefault(source => source.Kind != "image")
            ?? throw VideoEditException.Validation("A recorded source is required for VAD.");
        return AnalyzeAsync(host, project.Sources, policy, cancellationToken);
    }

    public async Task<VadSpeechMap> AnalyzeAsync(
        SourceAsset source,
        IReadOnlyList<SourceAsset> sources,
        EditingPolicy policy,
        CancellationToken cancellationToken)
    {
        var videoOptions = options.Value;
        var analysis = SourceAnalysis.Directory(store.ProjectDirectory, source, sources);
        var wavPath = await EnsureAudio16kAsync(source, sources, cancellationToken);
        if (!File.Exists(videoOptions.VadModelPath))
        {
            throw VideoEditException.NotFound($"VAD model is missing at {videoOptions.VadModelPath}.");
        }

        var result = await runner.RunAsync(
            videoOptions.WhisperVadPath,
            [
                "-np",
                "-vm", videoOptions.VadModelPath,
                "-vt", policy.VadThreshold.ToString("0.##", CultureInfo.InvariantCulture),
                "-vsd", policy.MinSilenceMs.ToString(CultureInfo.InvariantCulture),
                "-vp", "0",
                "-f", wavPath,
            ],
            TimeSpan.FromSeconds(180),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw VideoEditException.Conflict($"whisper-vad-speech-segments failed for {source.Id}.");
        }

        var map = VadSpeechMap.ParseCli(result.StandardOutput, Path.GetFileName(videoOptions.VadModelPath), policy);
        if (map.Segments.Count == 0)
        {
            throw VideoEditException.Validation($"VAD produced no speech segments for {source.Id}.");
        }

        VadSpeechMap.Write(SourceAnalysis.VadPath(analysis), map);
        await WriteDiagnosisAsync(source.Path, analysis, cancellationToken);
        return map;
    }

    private async Task WriteDiagnosisAsync(string sourcePath, string analysis, CancellationToken cancellationToken)
    {
        var diagnosis = Path.Combine(analysis, EditingPolicy.DiagnosisFileName);
        if (File.Exists(diagnosis))
        {
            return;
        }

        var result = await runner.RunAsync(
            options.Value.FfmpegPath,
            [
                "-hide_banner",
                "-i", sourcePath,
                "-af",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"silencedetect=noise={EditingPolicy.DiagnosisThresholdDb}dB:d={EditingPolicy.DiagnosisMinSilenceSeconds}"),
                "-f", "null",
                "-",
            ],
            TimeSpan.FromMinutes(2),
            cancellationToken);
        File.WriteAllText(diagnosis, result.StandardError + result.StandardOutput);
    }
}

using Microsoft.Extensions.Options;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class TranscriptService(
    ProjectStore store,
    IProcessRunner runner,
    IOptions<VideoEditOptions> options)
{
    public async Task<string> EnsureWordsAsync(
        SourceAsset source,
        IReadOnlyList<SourceAsset> sources,
        string wavPath,
        CancellationToken cancellationToken)
    {
        var analysis = SourceAnalysis.Directory(store.ProjectDirectory, source, sources);
        if (SourceAnalysis.TryWordsPath(analysis) is string existing)
        {
            return existing;
        }

        Directory.CreateDirectory(analysis);
        var videoOptions = options.Value;
        if (!File.Exists(videoOptions.WhisperCliPath))
        {
            throw VideoEditException.NotFound($"whisper-cli is missing at {videoOptions.WhisperCliPath}.");
        }

        if (!File.Exists(videoOptions.WhisperModelPath))
        {
            throw VideoEditException.NotFound($"Whisper model is missing at {videoOptions.WhisperModelPath}.");
        }

        var outputBase = Path.Combine(analysis, "words");
        var result = await runner.RunAsync(
            videoOptions.WhisperCliPath,
            [
                "-m", videoOptions.WhisperModelPath,
                "-f", wavPath,
                "-l", "en",
                "-ml", "1",
                "-sow",
                "-oj",
                "-of", outputBase,
                "-np",
            ],
            TimeSpan.FromMinutes(10),
            cancellationToken);
        var wordsPath = Path.Combine(analysis, "words.json");
        if (result.ExitCode != 0 || !File.Exists(wordsPath))
        {
            throw VideoEditException.Conflict($"whisper-cli failed for {source.Id}.");
        }

        return wordsPath;
    }
}

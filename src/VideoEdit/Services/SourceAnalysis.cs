using VideoEdit.Models;

namespace VideoEdit.Services;

public static class SourceAnalysis
{
    public static string Directory(string projectDirectory, SourceAsset source, IReadOnlyList<SourceAsset> sources)
    {
        var root = Path.Combine(projectDirectory, "edit", "analysis");
        var host = sources.FirstOrDefault(item => item.Kind != "image");
        if (host is null || string.Equals(source.Id, host.Id, StringComparison.Ordinal))
        {
            return root;
        }

        return Path.Combine(root, source.Id);
    }

    public static string WordsPath(string analysisDirectory) =>
        WhisperWords.FileName(analysisDirectory);

    public static string? TryWordsPath(string analysisDirectory) =>
        WhisperWords.TryFileName(analysisDirectory);

    public static string VadPath(string analysisDirectory) =>
        Path.Combine(analysisDirectory, VadSpeechMap.FileName);

    public static string Audio16kPath(string analysisDirectory) =>
        Path.Combine(analysisDirectory, "audio16k.wav");
}

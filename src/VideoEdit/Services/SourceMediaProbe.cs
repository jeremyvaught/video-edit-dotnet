using System.Globalization;
using System.Text.Json;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record SourceMediaInfo(
    double DurationSeconds,
    string? CodecName,
    string? AverageFrameRate,
    int? Width,
    int? Height);

public static class SourceMediaProbe
{
    public static string FileName => "source.probe.json";

    public static async Task<SourceMediaInfo> InspectAsync(
        IProcessRunner runner,
        string ffprobePath,
        string sourcePath,
        string analysisDirectory,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            ffprobePath,
            ["-v", "error", "-show_format", "-show_streams", "-print_format", "json", sourcePath],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw VideoEditException.Validation($"Could not probe {sourcePath}.");
        }

        Directory.CreateDirectory(analysisDirectory);
        File.WriteAllText(Path.Combine(analysisDirectory, FileName), result.StandardOutput);
        return Parse(result.StandardOutput, sourcePath);
    }

    public static SourceMediaInfo Parse(string json, string sourcePath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format)
            || !format.TryGetProperty("duration", out var durationValue)
            || !double.TryParse(
                durationValue.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration)
            || !double.IsFinite(duration)
            || duration <= 0)
        {
            throw VideoEditException.Validation($"Source {sourcePath} has no usable duration.");
        }

        string? codec = null;
        string? frameRate = null;
        int? width = null;
        int? height = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type)
                    || !string.Equals(type.GetString(), "video", StringComparison.Ordinal))
                {
                    continue;
                }

                codec = stream.TryGetProperty("codec_name", out var codecName) ? codecName.GetString() : null;
                frameRate = stream.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString() : null;
                width = stream.TryGetProperty("width", out var widthValue) ? widthValue.GetInt32() : null;
                height = stream.TryGetProperty("height", out var heightValue) ? heightValue.GetInt32() : null;
                break;
            }
        }

        return new SourceMediaInfo(duration, codec, frameRate, width, height);
    }
}

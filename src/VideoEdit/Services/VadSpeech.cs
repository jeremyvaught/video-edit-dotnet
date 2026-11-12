using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class SpeechSegment
{
    public double Start { get; set; }

    public double End { get; set; }
}

public sealed class VadSpeechMap
{
    public string Model { get; set; } = "";

    public double Threshold { get; set; }

    public int MinSilenceMs { get; set; }

    public int SpeechPadMs { get; set; }

    public List<SpeechSegment> Segments { get; set; } = [];

    public static string FileName => "vad-speech-segments.json";

    public static VadSpeechMap ParseCli(string text, string model, EditingPolicy policy)
    {
        var map = new VadSpeechMap
        {
            Model = model,
            Threshold = policy.VadThreshold,
            MinSilenceMs = policy.MinSilenceMs,
            SpeechPadMs = 0,
        };
        foreach (Match match in Regex.Matches(
            text,
            @"Speech segment \d+: start = ([0-9.]+), end = ([0-9.]+)"))
        {
            var start = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0;
            var end = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0;
            if (end > start)
            {
                map.Segments.Add(new SpeechSegment { Start = Math.Round(start, 6), End = Math.Round(end, 6) });
            }
        }

        return map;
    }

    public static VadSpeechMap Load(string path) =>
        JsonSerializer.Deserialize<VadSpeechMap>(File.ReadAllText(path), ProjectStore.JsonOptions)
        ?? throw VideoEditException.Validation("VAD speech map is empty.");

    public static void Write(string path, VadSpeechMap map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(map, ProjectStore.JsonOptions));
    }

    public IReadOnlyList<SilenceInterval> ToSilences(double durationSeconds)
    {
        var silences = new List<SilenceInterval>();
        var cursor = 0d;
        foreach (var speech in Segments.OrderBy(item => item.Start))
        {
            if (speech.Start > cursor)
            {
                silences.Add(new SilenceInterval(cursor, speech.Start));
            }

            cursor = Math.Max(cursor, speech.End);
        }

        if (durationSeconds > cursor)
        {
            silences.Add(new SilenceInterval(cursor, durationSeconds));
        }

        return silences;
    }

    public IReadOnlyList<double> Onsets() =>
        Segments.OrderBy(item => item.Start).Select(item => item.Start).ToList();
}

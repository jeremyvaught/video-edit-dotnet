using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoEdit.Services;

public static class AudioLoudness
{
    private static readonly Regex Integrated = new(
        @"I:\s*(-?[0-9.]+)\s+LUFS",
        RegexOptions.CultureInvariant);

    private static readonly Regex RmsLevel = new(
        @"RMS level dB:\s*(-?[0-9.]+)",
        RegexOptions.CultureInvariant);

    public static double ParseIntegrated(string ffmpegOutput)
    {
        var text = ffmpegOutput ?? "";
        var summary = text.LastIndexOf("Summary:", StringComparison.Ordinal);
        var region = summary >= 0 ? text[summary..] : text;
        var matches = Integrated.Matches(region);
        if (matches.Count == 0
            || !double.TryParse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture, out var lufs)
            || !double.IsFinite(lufs))
        {
            throw VideoEditException.Validation("ffmpeg did not report an integrated loudness.");
        }

        return lufs;
    }

    public static double Average(IReadOnlyList<double> measurements)
    {
        if (measurements.Count == 0)
        {
            throw VideoEditException.Validation("Loudness matching needs at least one recorded-scene measurement.");
        }

        return measurements.Average();
    }

    public static double ParseRmsDb(string ffmpegOutput)
    {
        var matches = RmsLevel.Matches(ffmpegOutput ?? "");
        if (matches.Count == 0
            || !double.TryParse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture, out var rms)
            || !double.IsFinite(rms))
        {
            throw VideoEditException.Validation("ffmpeg did not report an RMS level.");
        }

        return rms;
    }

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values is null || values.Count == 0)
        {
            throw VideoEditException.Validation("A percentile needs at least one value.");
        }

        if (!double.IsFinite(percentile) || percentile < 0 || percentile > 100)
        {
            throw VideoEditException.Validation("A percentile must be between 0 and 100.");
        }

        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var index = percentile / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(index);
        var hi = Math.Min(sorted.Length - 1, lo + 1);
        var mix = index - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * mix;
    }

    public static double FloorGainDb(double measuredDb, double targetDb) =>
        Math.Clamp(Math.Round(targetDb - measuredDb, 2), -40, 40);

    public static double GainDb(double currentLufs, double targetLufs) =>
        targetLufs - currentLufs;

    public static bool WithinTolerance(double lufs, double targetLufs, double toleranceLu) =>
        Math.Abs(lufs - targetLufs) <= toleranceLu;
}

using System.Globalization;

namespace VideoEdit.Services;

public static class AudioEnergy
{
    public const int SampleRate = 8000;
    public const double WindowSeconds = 0.02;
    public const double HopSeconds = 0.01;
    public const double SearchBeforeSeconds = 0.05;
    public const double SearchAfterSeconds = 0.35;

    public static double? FindLocalMinimum(IReadOnlyList<float> samples, double windowStart)
    {
        var window = (int)(WindowSeconds * SampleRate);
        var hop = (int)(HopSeconds * SampleRate);
        if (samples.Count < window)
        {
            return null;
        }

        double? bestDb = null;
        var bestIndex = 0;
        for (var i = 0; i <= samples.Count - window; i += hop)
        {
            var sum = 0d;
            for (var s = 0; s < window; s++)
            {
                var value = samples[i + s];
                sum += value * value;
            }

            var rms = Math.Sqrt(sum / window);
            var db = rms > 1e-12 ? 20 * Math.Log10(rms) : -120;
            if (bestDb is null || db < bestDb)
            {
                bestDb = db;
                bestIndex = i;
            }
        }

        return Math.Round(windowStart + (bestIndex / (double)SampleRate), 6, MidpointRounding.AwayFromZero);
    }

    public static async Task<double?> FindLocalMinimumAsync(
        IProcessRunner runner,
        string ffmpegPath,
        string sourcePath,
        double proposed,
        CancellationToken cancellationToken)
    {
        var start = Math.Max(0, proposed - SearchBeforeSeconds);
        var duration = SearchBeforeSeconds + SearchAfterSeconds;
        var temp = Path.Combine(Path.GetTempPath(), $"video-edit-energy-{Guid.NewGuid():N}.f32");
        try
        {
            var result = await runner.RunAsync(
                ffmpegPath,
                [
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-ss", start.ToString("0.######", CultureInfo.InvariantCulture),
                    "-t", duration.ToString("0.######", CultureInfo.InvariantCulture),
                    "-i", sourcePath,
                    "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                    "-f", "f32le",
                    temp,
                ],
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(temp))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(temp, cancellationToken);
            if (bytes.Length < 4)
            {
                return null;
            }

            var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
            return FindLocalMinimum(samples, start);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

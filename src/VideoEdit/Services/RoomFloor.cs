using System.Buffers.Binary;

namespace VideoEdit.Services;

public static class RoomFloor
{
    public const double WindowSeconds = 0.2;

    public const double FloorPercentile = 5;

    public static double NoiseFloorDb(
        IReadOnlyList<short> samples,
        int sampleRate,
        IReadOnlyList<SilenceInterval> gaps) =>
        AudioLoudness.Percentile(WindowRmsDb(samples, sampleRate, gaps), FloorPercentile);

    public static IReadOnlyList<double> WindowRmsDb(
        IReadOnlyList<short> samples,
        int sampleRate,
        IReadOnlyList<SilenceInterval> gaps,
        double windowSeconds = WindowSeconds)
    {
        if (sampleRate <= 0 || windowSeconds <= 0)
        {
            throw VideoEditException.Validation("Room-floor windows need a positive sample rate and length.");
        }

        var window = (int)Math.Round(windowSeconds * sampleRate);
        if (window <= 0)
        {
            throw VideoEditException.Validation("Room-floor windows are shorter than one sample.");
        }

        var levels = new List<double>();
        foreach (var gap in gaps)
        {
            var start = Math.Clamp((int)Math.Floor(gap.Start * sampleRate), 0, samples.Count);
            var end = Math.Clamp((int)Math.Floor(gap.End * sampleRate), 0, samples.Count);
            for (var i = start; i + window <= end; i += window)
            {
                levels.Add(RmsDb(samples, i, window));
            }
        }

        if (levels.Count == 0)
        {
            throw VideoEditException.Validation("No non-speech 200 ms windows were found for the host noise floor.");
        }

        return levels;
    }

    public static (short[] Samples, int SampleRate) ReadMono16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44
            || bytes[0] != (byte)'R'
            || bytes[1] != (byte)'I'
            || bytes[8] != (byte)'W')
        {
            throw VideoEditException.Validation($"Analysis wav {path} is not a RIFF WAVE file.");
        }

        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        short[]? samples = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunk = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            var data = offset + 8;
            if (chunk == "fmt " && size >= 16)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data + 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(data + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data + 14));
            }
            else if (chunk == "data")
            {
                if (channels != 1 || bits != 16 || sampleRate <= 0)
                {
                    throw VideoEditException.Validation("Host noise-floor audio must be mono 16-bit PCM.");
                }

                var count = size / 2;
                samples = new short[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(data + i * 2));
                }

                break;
            }

            offset = data + size + (size & 1);
        }

        if (samples is null)
        {
            throw VideoEditException.Validation($"Analysis wav {path} has no PCM data.");
        }

        return (samples, sampleRate);
    }

    private static double RmsDb(IReadOnlyList<short> samples, int start, int length)
    {
        var sum = 0d;
        for (var i = 0; i < length; i++)
        {
            var sample = samples[start + i] / 32768.0;
            sum += sample * sample;
        }

        return 20 * Math.Log10(Math.Sqrt(sum / length) + 1e-12);
    }
}

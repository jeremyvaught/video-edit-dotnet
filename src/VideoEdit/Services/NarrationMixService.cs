using System.Globalization;
using System.Text.Json;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record NarrationMixResult(
    LoudnessMeasurements Loudness,
    string Path,
    string Fingerprint,
    double DurationSeconds);

public sealed class NarrationMixService(
    ProjectStore store,
    IProcessRunner runner,
    MediaGate gate,
    VideoEditOptions options)
{
    public async Task<NarrationMixResult> MatchAsync(
        NarrationAsset asset,
        string recordedPath,
        string? samplePath,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy policy,
        CancellationToken cancellationToken)
    {
        return await gate.RunAsync("match narration loudness", false, async token =>
        {
            var recorded = await MeasureAsync(recordedPath, token);
            var generated = await MeasureAsync(asset.AudioPath, token);
            double? sample = samplePath is null ? null : await MeasureAsync(samplePath, token);
            var target = recorded;
            var gain = AudioLoudness.GainDb(generated, target);
            var folder = Path.GetDirectoryName(asset.AudioPath)!;
            var stamp = Guid.NewGuid().ToString("N");
            var work = Path.Combine(folder, $".work-{stamp}");
            Directory.CreateDirectory(work);
            var matchedTemp = Path.Combine(work, "matched.wav");
            await ApplyGainAsync(asset.AudioPath, matchedTemp, gain, token);
            var matched = await MeasureAsync(matchedTemp, token);
            if (!AudioLoudness.WithinTolerance(matched, target, policy.LoudnessToleranceLu))
            {
                throw VideoEditException.Validation(
                    $"Matched loudness {matched:0.0} LUFS is outside ±{policy.LoudnessToleranceLu:0.#} LU of {target:0.0} LUFS.");
            }

            var room = RoomTone.Select(silences, policy.RoomToneMaxS);
            var roomDir = Path.Combine(options.ProjectDirectory, "generated", "room-tone");
            Directory.CreateDirectory(roomDir);
            var roomPath = Path.Combine(roomDir, "room-tone.wav");
            var source = Path.Combine(options.ProjectDirectory, "sources", "source.mov");
            await ExtractAsync(source, room.Start, room.Duration, roomPath, token);
            var floor = await MeasureAsync(roomPath, token);
            var beddedTemp = Path.Combine(work, "bedded.wav");
            await BedRoomToneAsync(matchedTemp, roomPath, asset.DurationSeconds, beddedTemp, token);
            var duration = await ProbeDurationAsync(beddedTemp, token);
            if (Math.Abs(duration - asset.DurationSeconds) > 0.05)
            {
                throw VideoEditException.Validation(
                    $"Bedded narration duration {duration:0.###}s does not match the measured asset {asset.DurationSeconds:0.###}s.");
            }

            var published = Path.Combine(folder, $"bedded-{stamp}.wav");
            MediaRenderer.PublishFile(beddedTemp, published);
            TryDeleteDirectory(work);

            var measurements = new LoudnessMeasurements
            {
                RecordedLufs = recorded,
                GeneratedLufs = generated,
                MatchedLufs = matched,
                SampleLufs = sample,
                TargetLufs = target,
                GainDb = gain,
                ToleranceLu = policy.LoudnessToleranceLu,
            };
            var relative = MediaRenderer.ProjectRelative(options.ProjectDirectory, published);
            var fingerprint = MediaRenderer.FingerprintFile(published);
            store.Mutate(store.Load().Revision, project =>
            {
                project.Narration.GeneratedAssetId = asset.Id;
                project.Narration.Loudness = measurements;
                project.Narration.RoomTone = new RoomToneClip
                {
                    Start = room.Start,
                    End = room.End,
                    Path = roomPath,
                    NoiseFloorLufs = floor,
                };
                project.Narration.Derivative = new NarrationDerivative
                {
                    Path = relative,
                    Fingerprint = fingerprint,
                    DurationSeconds = duration,
                };
                return true;
            });
            return new NarrationMixResult(measurements, relative, fingerprint, duration);
        }, cancellationToken);
    }

    private async Task<double> MeasureAsync(string path, CancellationToken token)
    {
        var result = await runner.RunAsync(
            options.FfmpegPath,
            ["-hide_banner", "-i", path, "-af", "ebur128", "-f", "null", "-"],
            TimeSpan.FromMinutes(2),
            token);
        return AudioLoudness.ParseIntegrated(result.StandardError + result.StandardOutput);
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken token)
    {
        var result = await runner.RunAsync(
            options.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "json", path],
            TimeSpan.FromSeconds(30),
            token);
        if (result.ExitCode != 0)
        {
            throw VideoEditException.Validation("The new narration derivative could not be measured.");
        }

        using var metadata = JsonDocument.Parse(result.StandardOutput);
        var duration = double.Parse(
            metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw VideoEditException.Validation("The new narration derivative has no usable duration.");
        }

        return duration;
    }

    private async Task ApplyGainAsync(string source, string destination, double gainDb, CancellationToken token)
    {
        var result = await runner.RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner", "-y", "-i", source,
                "-af", string.Create(CultureInfo.InvariantCulture, $"volume={gainDb:0.###}dB"),
                destination
            ],
            TimeSpan.FromMinutes(2),
            token);
        if (result.ExitCode != 0 || !File.Exists(destination))
        {
            throw VideoEditException.Conflict("ffmpeg could not apply the loudness match.");
        }
    }

    private async Task ExtractAsync(string source, double start, double duration, string destination, CancellationToken token)
    {
        var temp = destination + ".tmp.wav";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        var result = await runner.RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner", "-y",
                "-ss", N(start), "-t", N(duration), "-i", source,
                "-vn", "-ac", "1", temp
            ],
            TimeSpan.FromMinutes(2),
            token);
        if (result.ExitCode != 0 || !File.Exists(temp))
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw VideoEditException.Conflict("ffmpeg could not extract room tone.");
        }

        MediaRenderer.PublishFile(temp, destination);
        File.Delete(temp);
    }

    private async Task BedRoomToneAsync(
        string speechPath,
        string roomPath,
        double duration,
        string destination,
        CancellationToken token)
    {
        var result = await runner.RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner", "-y",
                "-i", speechPath,
                "-stream_loop", "-1", "-i", roomPath,
                "-filter_complex",
                $"[0:a]{MediaRenderer.OutputAudioFormat}[s];" +
                $"[1:a]{MediaRenderer.OutputAudioFormat},atrim=0:{N(duration)},asetpts=PTS-STARTPTS," +
                $"afade=t=in:st=0:d=0.02,afade=t=out:st={N(Math.Max(0, duration - 0.02))}:d=0.02[r];" +
                $"[s][r]amix=inputs=2:duration=first:normalize=0[a]",
                "-map", "[a]",
                "-ar", MediaRenderer.OutputSampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", MediaRenderer.OutputChannels.ToString(CultureInfo.InvariantCulture),
                destination
            ],
            TimeSpan.FromMinutes(2),
            token);
        if (result.ExitCode != 0 || !File.Exists(destination))
        {
            throw VideoEditException.Conflict("ffmpeg could not bed room tone under the generated audio.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string N(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}

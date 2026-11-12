using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoEdit.Services;

public sealed record NarrationAsset(
    string Id, string AudioPath, string Fingerprint, double DurationSeconds,
    string Provider, string? ProviderGenerationId, DateTimeOffset CreatedAt,
    NarrationRequest Request, string OutputFormat, float Style, bool SpeakerBoost, float Speed);

public sealed class NarrationService(
    INarrationProvider provider, IProcessRunner runner, MediaGate gate, VideoEditOptions options)
{
    public Task<NarrationAsset> GenerateAsync(NarrationRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return gate.RunAsync("generate narration", false, async token =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            token = deadline.Token;
            var id = $"narration-{Guid.NewGuid():N}";
            var folder = Path.Combine(options.ProjectDirectory, "generated", id);
            Directory.CreateDirectory(folder);
            // Retain the request and any downloaded audio even if probing fails.
            // Only asset.json marks a complete asset; never regenerate on export.
            await File.WriteAllTextAsync(Path.Combine(folder, "request.json"),
                JsonSerializer.Serialize(request, ProjectStore.JsonOptions), token);
            var audio = await provider.GenerateAsync(request, token);
            if (audio.Bytes.IsEmpty) throw VideoEditException.Validation("The narration provider returned no audio.");
            var audioPath = Path.Combine(folder, "audio.mp3");
            await using (var output = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(audio.Bytes, token);
            await File.WriteAllTextAsync(Path.Combine(folder, "generation.json"),
                JsonSerializer.Serialize(new
                {
                    provider = "elevenlabs",
                    providerGenerationId = audio.ProviderGenerationId,
                    receivedAt = DateTimeOffset.UtcNow,
                }, ProjectStore.JsonOptions), token);
            var probe = await runner.RunAsync(options.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", audioPath],
                TimeSpan.FromSeconds(30), token);
            if (probe.ExitCode != 0)
                throw VideoEditException.Validation($"Narration was saved at {audioPath}, but ffprobe could not measure it.");
            double duration;
            try
            {
                using var metadata = JsonDocument.Parse(probe.StandardOutput);
                duration = double.Parse(metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                if (!metadata.RootElement.GetProperty("streams").EnumerateArray()
                    .Any(stream => stream.GetProperty("codec_type").GetString() == "audio") || !double.IsFinite(duration) || duration <= 0)
                    throw new FormatException();
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentNullException)
            {
                throw VideoEditException.Validation($"Narration was saved at {audioPath}, but its duration/audio stream could not be validated.");
            }

            var asset = new NarrationAsset(id, audioPath,
                Convert.ToHexStringLower(SHA256.HashData(audio.Bytes.Span)), duration,
                "elevenlabs", audio.ProviderGenerationId, DateTimeOffset.UtcNow, request,
                NarrationRequest.OutputFormat, 0, true, 1);
            var temporary = Path.Combine(folder, "asset.json.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(asset, ProjectStore.JsonOptions), token);
            File.Move(temporary, Path.Combine(folder, "asset.json"));
            return asset;
        }, cancellationToken);
    }
}

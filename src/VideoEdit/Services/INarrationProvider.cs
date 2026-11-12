namespace VideoEdit.Services;

public sealed record NarrationVoice(string Id, string Name, string? Category);

public sealed record NarrationRequest(string Text, string VoiceId)
{
    // This SDK serializes v2 voice settings, so use the long-form v2 model explicitly.
    public string ModelId { get; init; } = "eleven_multilingual_v2";
    public float Stability { get; init; } = 0.5f;
    public float SimilarityBoost { get; init; } = 0.75f;
    public const string OutputFormat = "mp3_44100_128";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Text) || Text.Length > 5000)
            throw VideoEditException.Validation("Narration must contain between 1 and 5000 characters.");
        if (string.IsNullOrWhiteSpace(VoiceId) || !VoiceId.All(char.IsAsciiLetterOrDigit))
            throw VideoEditException.Validation("Choose an ElevenLabs voice ID from the voice list.");
        if (ModelId is not "eleven_multilingual_v2" and not "eleven_v4")
            throw VideoEditException.Validation("This adapter supports eleven_v4 and eleven_multilingual_v2.");
        if (!float.IsFinite(Stability) || Stability < 0 || Stability > 1 ||
            !float.IsFinite(SimilarityBoost) || SimilarityBoost < 0 || SimilarityBoost > 1)
            throw VideoEditException.Validation("Voice settings must be between 0 and 1.");
    }
}

public sealed record NarrationAudio(ReadOnlyMemory<byte> Bytes, string? ProviderGenerationId);

public interface INarrationProvider
{
    Task<IReadOnlyList<NarrationVoice>> ListVoicesAsync(CancellationToken cancellationToken);
    Task<NarrationAudio> GenerateAsync(NarrationRequest request, CancellationToken cancellationToken);
}

using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;

namespace VideoEdit.Services;

public sealed class ElevenLabsNarrationProvider(ElevenLabsClient client) : INarrationProvider
{
    public async Task<IReadOnlyList<NarrationVoice>> ListVoicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var voices = await client.VoicesEndpoint.GetAllVoicesAsync(cancellationToken);
            return voices.Select(voice => new NarrationVoice(voice.Id, voice.Name, voice.Category)).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // SDK errors may contain response bodies. Keep them out of logs and HTTP responses.
            throw new VideoEditException("narration_provider", "ElevenLabs could not list voices. Check the key's voice-read permission and account access.");
        }
    }

    public async Task<NarrationAudio> GenerateAsync(NarrationRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        try
        {
            var speech = new TextToSpeechRequest(
                new Voice(request.VoiceId, request.VoiceId), request.Text,
                voiceSettings: new VoiceSettings(request.Stability, request.SimilarityBoost, style: 0, speakerBoost: true, speed: 1),
                outputFormat: OutputFormat.MP3_44100_128,
                model: new Model(request.ModelId));
            var clip = await client.TextToSpeechEndpoint.TextToSpeechAsync(speech, cancellationToken: cancellationToken);
            if (clip.ClipData.IsEmpty)
                throw new InvalidOperationException("Empty narration response.");
            return new NarrationAudio(clip.ClipData, clip.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            throw new VideoEditException("narration_provider", "ElevenLabs generation failed. Check voice/model access, text-to-speech permission, and quota. No automatic retry was made; check usage before retrying.");
        }
    }
}

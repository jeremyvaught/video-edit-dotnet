using System.Net;
using System.Text.Json;
using ElevenLabs;
using Microsoft.Extensions.Configuration;
using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public sealed class NarrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"video-edit-narration-{Guid.NewGuid():N}");

    [Fact]
    public async Task SdkSendsExplicitVoiceModelAndSettingsAndReturnsAudio()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new ElevenLabsClient(new ElevenLabsAuthentication("test-placeholder"), httpClient: http);
        var provider = new ElevenLabsNarrationProvider(client);
        var result = await provider.GenerateAsync(new NarrationRequest("A test passage.", "voice123"), CancellationToken.None);

        Assert.Equal("POST", handler.Method);
        Assert.Contains("/text-to-speech/voice123", handler.Uri);
        Assert.Contains("output_format=mp3_44100_128", handler.Uri);
        Assert.Equal("test-placeholder", handler.Key);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("A test passage.", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("eleven_multilingual_v2", body.RootElement.GetProperty("model_id").GetString());
        Assert.Equal(0.5, body.RootElement.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Equal(0, body.RootElement.GetProperty("voice_settings").GetProperty("style").GetDouble());
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes.ToArray());
        Assert.Equal("generation-test", result.ProviderGenerationId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ElevenV4IsAnAllowedModel()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new ElevenLabsClient(new ElevenLabsAuthentication("test-placeholder"), httpClient: http);
        var request = new NarrationRequest("A test passage.", "voice123") { ModelId = "eleven_v4" };
        await new ElevenLabsNarrationProvider(client).GenerateAsync(request, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("eleven_v4", body.RootElement.GetProperty("model_id").GetString());
    }

    [Fact]
    public async Task ProviderErrorsAreSanitizedAndNotRetried()
    {
        var handler = new RecordingHandler { Fail = true };
        using var http = new HttpClient(handler);
        using var client = new ElevenLabsClient(new ElevenLabsAuthentication("test-placeholder"), httpClient: http);
        var error = await Assert.ThrowsAsync<VideoEditException>(() =>
            new ElevenLabsNarrationProvider(client).GenerateAsync(new NarrationRequest("Text", "voice123"), CancellationToken.None));
        Assert.DoesNotContain("sensitive-response", error.ToString());
        Assert.DoesNotContain("test-placeholder", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidRequestDoesNotReachProvider()
    {
        var provider = new FakeProvider();
        var service = CreateService(provider, new ProbeRunner());
        await Assert.ThrowsAsync<VideoEditException>(async () =>
            await service.GenerateAsync(new NarrationRequest("", "voice123"), CancellationToken.None));
        Assert.Equal(0, provider.Calls);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task GenerationsAreDistinctMeasuredAndDoNotModifyProject()
    {
        Directory.CreateDirectory(Path.Combine(root, "edit"));
        var projectPath = Path.Combine(root, "edit", "project.json");
        await File.WriteAllTextAsync(projectPath, "sentinel-project");
        var provider = new FakeProvider();
        var gate = new MediaGate();
        var service = CreateService(provider, new ProbeRunner(), gate);
        var request = new NarrationRequest("Test narration", "voice123");
        var first = await service.GenerateAsync(request, CancellationToken.None);
        var second = await service.GenerateAsync(request, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.AudioPath, second.AudioPath);
        Assert.Equal(2.5, first.DurationSeconds);
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal("generation-test", first.ProviderGenerationId);
        Assert.Equal(request, first.Request);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(first.AudioPath)!, "asset.json")));
        Assert.Equal("sentinel-project", await File.ReadAllTextAsync(projectPath));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task ProbeFailureRetainsAudioWithoutPublishingCompletedAsset()
    {
        var gate = new MediaGate();
        var service = CreateService(new FakeProvider(), new ProbeRunner { Fail = true }, gate);
        await Assert.ThrowsAsync<VideoEditException>(() =>
            service.GenerateAsync(new NarrationRequest("Text", "voice123"), CancellationToken.None));
        Assert.Single(Directory.GetFiles(root, "audio.mp3", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(root, "request.json", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(root, "generation.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(root, "asset.json", SearchOption.AllDirectories));
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task BusyGatePreventsChargedRequest()
    {
        var gate = new MediaGate();
        gate.Begin("export", true);
        var provider = new FakeProvider();
        try
        {
            await Assert.ThrowsAsync<VideoEditException>(() => CreateService(provider, new ProbeRunner(), gate)
                .GenerateAsync(new NarrationRequest("Text", "voice123"), CancellationToken.None));
            Assert.Equal(0, provider.Calls);
            Assert.True(gate.IsExporting);
        }
        finally { gate.End(); }
    }

    [Fact]
    public async Task CancellationReleasesGate()
    {
        var gate = new MediaGate();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var provider = new FakeProvider();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(provider, new ProbeRunner(), gate)
            .GenerateAsync(new NarrationRequest("Text", "voice123"), cancel.Token));
        Assert.False(gate.IsBusy);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void LocalKeyResolvesFromRepositoryRootAndEnvironmentTakesPrecedence()
    {
        var contentRoot = Path.Combine(root, "src", "VideoEdit");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(root, "VideoEdit.slnx"), "");
        File.WriteAllText(Path.Combine(root, ".env.local"), "ELEVENLABS_API_KEY='local-test-placeholder'\n");
        Assert.Equal("local-test-placeholder", NarrationSetup.LoadApiKey(new ConfigurationBuilder().Build(), contentRoot));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ELEVENLABS_API_KEY"] = "environment-test-placeholder",
        }).Build();
        Assert.Equal("environment-test-placeholder", NarrationSetup.LoadApiKey(config, contentRoot));
    }

    [Fact]
    public void MalformedEnvDoesNotLeakContents()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".env.local"), "do not expose sensitive-test-content\n");
        var error = Assert.Throws<VideoEditException>(() => NarrationSetup.LoadApiKey(new ConfigurationBuilder().Build(), root));
        Assert.DoesNotContain("sensitive-test-content", error.ToString());
    }

    [Fact]
    public void ScriptPassageRequiresExactlyOneMarkedBlock()
    {
        Assert.Equal("Keep this.", ScriptPassage.ReadMarked("intro\n<!-- generate -->\nKeep this.\n<!-- /generate -->\nend"));
        var missing = Assert.Throws<VideoEditException>(() => ScriptPassage.ReadMarked("no marker"));
        Assert.Contains("no marked passage", missing.Message, StringComparison.Ordinal);
        var extra = Assert.Throws<VideoEditException>(() => ScriptPassage.ReadMarked(
            "<!-- generate -->one<!-- /generate --><!-- generate -->two<!-- /generate -->"));
        Assert.Contains("more than one", extra.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrationSettingsDoNotClearFinalApproval()
    {
        var (store, service, _, _) = TestSupport.CreateStore();
        var project = TestSupport.SampleProject();
        project.Scenes[0].Edits[0].Decision = DecisionStates.Rejected;
        project.Scenes[0].Edits[0].DecisionProposalRevision = 1;
        store.SaveNew(project);
        service.AcceptScene("scene-1", 1);
        var accepted = store.Load();
        accepted.FinalApproval = new FinalApproval { CompositionHash = CompositionHasher.FullHash(accepted) };
        File.WriteAllText(store.ProjectPath, System.Text.Json.JsonSerializer.Serialize(accepted, ProjectStore.JsonOptions));

        var updated = service.SetNarrationSettings("abcdefghijklmnopqrst", "eleven_multilingual_v2", accepted.Revision);
        Assert.Equal("abcdefghijklmnopqrst", updated.Narration.VoiceId);
        Assert.Equal("eleven_multilingual_v2", updated.Narration.ModelId);
        Assert.NotNull(updated.Scenes[0].Acceptance);
        Assert.NotNull(updated.FinalApproval);
        Assert.Equal(accepted.FinalApproval.CompositionHash, updated.FinalApproval!.CompositionHash);
    }

    private NarrationService CreateService(INarrationProvider provider, IProcessRunner runner, MediaGate? gate = null) =>
        new(provider, runner, gate ?? new MediaGate(), new VideoEditOptions { ProjectDirectory = root });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakeProvider : INarrationProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<NarrationVoice>> ListVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NarrationVoice>>([]);
        public Task<NarrationAudio> GenerateAsync(NarrationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new NarrationAudio(new byte[] { 1, 2, 3 }, "generation-test"));
        }
    }

    private sealed class ProbeRunner : IProcessRunner
    {
        public bool Fail { get; init; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(Fail ? 1 : 0, """{"format":{"duration":"2.5"},"streams":[{"codec_type":"audio"}]}""", ""));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Key { get; private set; }
        public string? Uri { get; private set; }
        public string? Method { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Uri = request.RequestUri!.ToString();
            Key = request.Headers.GetValues("xi-api-key").Single();
            Method = request.Method.Method;
            var response = new HttpResponseMessage(Fail ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = Fail ? new StringContent("sensitive-response") : new ByteArrayContent([1, 2, 3]),
                RequestMessage = request,
            };
            response.Headers.Add("history-item-id", "generation-test");
            response.Headers.Add("request-id", "request-test");
            return response;
        }
    }
}

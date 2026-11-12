# ElevenLabs narration preparation

The narration adapter uses `ElevenLabs-DotNet` 3.7.2, the community .NET SDK linked from ElevenLabs' [library documentation](https://elevenlabs.io/docs/eleven-api/resources/libraries/). `INarrationProvider` isolates it for testing. NuGet dependencies are pinned in the project and lockfiles; Node is used only for account onboarding.

`ELEVENLABS_API_KEY` in application configuration (including the process environment) takes precedence. Otherwise, the adapter loads the repository's ignored `.env.local` with DotNetEnv. An explicit `ElevenLabs:EnvFile` path can be configured for a published app. Keys are loaded only when narration is requested, so recorded editing works without ElevenLabs credentials. Do not print the env file or copy its key into code. Use `npx -y @elevenlabs/cli@latest onboard check --answered --env-path .env.local` to check the onboarding key.

From the repository root, list accessible voices without generating speech:

```sh
dotnet run --project src/VideoEdit -- --list-narration-voices
```

Store the selected voice ID and model ID on the project (not the voice name). Generation reads those settings and the single marked passage in `sources/script.md`. Wrap that passage in `<!-- generate -->` … `<!-- /generate -->`. This command makes a paid API request:

```sh
dotnet run --project src/VideoEdit -- --set-narration-voice YOUR_VOICE_ID --model-id eleven_multilingual_v2
dotnet run --project src/VideoEdit -- --generate-narration --script-file /absolute/path/sources/script.md
```

The adapter uses the project's stored model ID (`eleven_v4` when that is what the ElevenLabs web app used, otherwise `eleven_multilingual_v2`), MP3 44.1 kHz/128 kbps, stability 0.5, similarity 0.75, style 0, speaker boost enabled, and speed 1. After generation it measures integrated loudness (ebur128) of the adjacent recorded scene, gain-matches the new asset within `loudness_tolerance_lu` (default ±1 LU), and extracts room tone from the longest diagnosis silence at or under `room_tone_max_s`. The original `audio.mp3` is never overwritten. Scripts are limited to 5000 characters per request. Account access, cloned-voice eligibility, and available credits still depend on ElevenLabs; listing a voice alone does not prove synthesis permission.

Every generation creates a new `generated/narration-<UUID>/` directory inside the configured video project. `request.json` records the request, `audio.mp3` preserves received audio, `generation.json` retains provider identity before probing, and `asset.json` records the completed asset with its fingerprint and ffprobe-measured duration. Failed/canceled attempts can leave an incomplete directory without `asset.json`; retain and inspect it before retrying. Provider requests are not automatically retried. Ctrl-C cancels the command; operations also have a five-minute deadline.

These commands exit before project bootstrap or web hosting. They prepare source assets without changing recorded-scene decisions, approvals, or `project.json`. The existing shared media gate protects against overlapping operations within a host. Do not run a second host/command against a project while it is rendering. Integrating generated audio and a still into the scene model, browser review, and common renderer remains M1 work; this addition does not claim mixed-video rendering is complete.

Offline contract tests use a simulated HTTP handler and a controlled provider/process runner. They cover SDK request serialization, error redaction, credential precedence, unique assets, retained audio after probe failure, and cancellation/gating. They never spend API credits or load the real env file. The onboarding CLI currently checks JavaScript/Python SDK declarations, so a missing-SDK result there must not be interpreted as a failed C# build.

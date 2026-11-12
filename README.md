# VideoEdit

A local ASP.NET Core application for reviewing and editing recorded video. It proposes cuts, lets you review scene decisions, renders previews, and blocks export until the current composition is approved. Optional ElevenLabs narration and recorded pickups support speech repairs.

Install the .NET SDK specified in `global.json`, plus `ffmpeg` and `ffprobe` on your `PATH` (with H.264 and AAC support). Automatic preparation also needs `whisper-cli`, `whisper-vad-speech-segments`, and their model files.

Place your recording at `<project>/sources/source.mov` and your script at `<project>/sources/script.md`. Keep media projects outside this repository. Set the project directory when starting the app:

```sh
dotnet restore --locked-mode
VideoEdit__ProjectDirectory=/absolute/path/to/project dotnet run --project src/VideoEdit
```

Open `http://127.0.0.1:5080`. If the port is occupied, the application prints another available loopback URL. This is a local application with no user authentication; keep it on loopback.

For persistent machine settings, create the ignored `src/VideoEdit/appsettings.Local.json`:

```json
{
  "VideoEdit": {
    "ProjectDirectory": "/absolute/path/to/project",
    "VadModelPath": "/absolute/path/to/ggml-silero-v6.2.0.bin",
    "WhisperModelPath": "/absolute/path/to/ggml-large-v3-turbo.bin"
  }
}
```

Executable paths can also be overridden there. Environment variables such as `VideoEdit__FfmpegPath` and command-line configuration take precedence. Local settings are excluded from build and publish output.

To prepare proposed cuts and review them:

```sh
VideoEdit__ProjectDirectory=/absolute/path/to/project dotnet run --project src/VideoEdit -- --prepare
```

ElevenLabs is optional. See the [narration instructions](src/VideoEdit/Services/Narration.README.md) for credential configuration and commands that generate speech.

Run the tests with:

```sh
dotnet test --configuration Release --no-restore
```

Tests use synthetic media, fixtures, and simulated provider responses. They require FFmpeg but do not require your recordings, Whisper model downloads, or paid API calls. Some process lifecycle tests use Unix utilities, so run the suite on macOS or Linux.

All checked-in script and transcript fixtures are synthetic examples. Keep recordings, provider request logs, and research outputs outside this repository.

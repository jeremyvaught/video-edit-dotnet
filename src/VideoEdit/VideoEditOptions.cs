namespace VideoEdit;

public sealed class VideoEditOptions
{
    public const string SectionName = "VideoEdit";

    public string ProjectDirectory { get; set; } = "";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    public string WhisperVadPath { get; set; } = "whisper-vad-speech-segments";

    public string VadModelPath { get; set; } = "models/ggml-silero-v6.2.0.bin";

    public string WhisperCliPath { get; set; } = "whisper-cli";

    public string WhisperModelPath { get; set; } = "models/ggml-large-v3-turbo.bin";

    public string ListenUrl { get; set; } = "http://127.0.0.1:5080";

    public double AudioFadeSeconds { get; set; } = 0.01;

    public double SpliceContextSeconds { get; set; } = 1.5;

    public int PreviewTimeoutSeconds { get; set; } = 180;

    public int ExportTimeoutSeconds { get; set; } = 600;
}

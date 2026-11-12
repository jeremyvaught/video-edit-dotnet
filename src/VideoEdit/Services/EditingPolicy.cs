using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoEdit.Services;

public sealed class SpeechPadMs
{
    public int Start { get; set; } = 70;

    public int End { get; set; } = 30;
}

public sealed class EditingPolicy
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const double DiagnosisThresholdDb = -40;
    public const double DiagnosisMinSilenceSeconds = 0.06;

    public static EditingPolicy Default { get; } = new();

    public double VadThreshold { get; set; } = 0.5;

    public int MinSilenceMs { get; set; } = 150;

    public SpeechPadMs SpeechPadMs { get; set; } = new();

    public double MarginAfterS { get; set; } = 0.35;

    public double PauseTargetS { get; set; } = 0.6;

    public double MinResidualGapS { get; set; } = 0.5;

    public bool PunchInAlternation { get; set; }

    public double SceneBoundaryGapS { get; set; } = 0.25;

    public int RoomToneFillMs { get; set; } = 40;

    public double LoudnessToleranceLu { get; set; } = 1;

    public double RoomToneMaxS { get; set; } = 10;

    [JsonIgnore]
    public double EndGuardSeconds { get; set; } = 0.025;

    [JsonIgnore]
    public double SearchWindowSeconds { get; set; } = 1.0;

    [JsonIgnore]
    public double CleanProximitySeconds { get; set; } = 0.15;

    [JsonIgnore]
    public double MergeGapSeconds { get; set; } = 0.05;

    [JsonIgnore]
    public double SpeechPadStartSeconds => SpeechPadMs.Start / 1000.0;

    [JsonIgnore]
    public double SpeechPadEndSeconds => SpeechPadMs.End / 1000.0;

    [JsonIgnore]
    public double MinSnapSilenceSeconds => MinSilenceMs / 1000.0;

    [JsonIgnore]
    public double RoomToneFillS => RoomToneFillMs / 1000.0;

    public static string FileName => "editing-policy.json";

    public static string DiagnosisFileName =>
        $"silences{DiagnosisThresholdDb.ToString(System.Globalization.CultureInfo.InvariantCulture)}db.txt";

    public static EditingPolicy LoadOrDefault(string path)
    {
        if (!File.Exists(path))
        {
            return Clone(Default);
        }

        var (policy, migrated) = Read(File.ReadAllText(path));
        policy.Validate();
        if (migrated)
        {
            Write(path, policy);
        }

        return policy;
    }

    public static (EditingPolicy Policy, bool Migrated) Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("vad_threshold", out _) || root.TryGetProperty("vadThreshold", out _))
        {
            var policy = JsonSerializer.Deserialize<EditingPolicy>(json, JsonOptions) ?? Clone(Default);
            policy.SpeechPadMs ??= new SpeechPadMs();
            var complete = Has(root, "scene_boundary_gap_s", "sceneBoundaryGapS")
                && Has(root, "room_tone_fill_ms", "roomToneFillMs");
            return (policy, !complete);
        }

        return (MigrateLegacy(root), true);
    }

    public static void Write(string path, EditingPolicy policy)
    {
        policy.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(policy, JsonOptions));
    }

    public void Validate()
    {
        var errors = CollectErrors();
        if (errors.Count > 0)
        {
            throw VideoEditException.Validation(string.Join(" ", errors));
        }
    }

    public List<string> CollectErrors()
    {
        var errors = new List<string>();
        if (VadThreshold is <= 0 or > 1)
        {
            errors.Add("VAD threshold must be between 0 and 1.");
        }

        if (MinSilenceMs <= 0)
        {
            errors.Add("Minimum silence must be greater than 0 ms.");
        }

        if (SpeechPadMs.Start < 0 || SpeechPadMs.End < 0)
        {
            errors.Add("Speech padding cannot be negative.");
        }

        if (EndGuardSeconds < 0 || SearchWindowSeconds <= 0
            || CleanProximitySeconds <= 0 || MergeGapSeconds < 0)
        {
            errors.Add("Silence search windows cannot be negative.");
        }

        if (PauseTargetS <= 0 || MarginAfterS <= 0 || MinResidualGapS <= 0)
        {
            errors.Add("Pause target, margin after, and residual gap must be greater than 0.");
        }

        if (LoudnessToleranceLu <= 0)
        {
            errors.Add("Loudness tolerance must be greater than 0 LU.");
        }

        if (RoomToneMaxS <= 0)
        {
            errors.Add("Room-tone maximum duration must be greater than 0.");
        }

        if (SceneBoundaryGapS < 0)
        {
            errors.Add("Scene boundary gap cannot be negative.");
        }

        if (RoomToneFillMs < 0)
        {
            errors.Add("Room-tone fill cannot be negative.");
        }

        return errors;
    }

    public static EditingPolicy Clone(EditingPolicy source) => new()
    {
        VadThreshold = source.VadThreshold,
        MinSilenceMs = source.MinSilenceMs,
        SpeechPadMs = new SpeechPadMs { Start = source.SpeechPadMs.Start, End = source.SpeechPadMs.End },
        MarginAfterS = source.MarginAfterS,
        PauseTargetS = source.PauseTargetS,
        MinResidualGapS = source.MinResidualGapS,
        PunchInAlternation = source.PunchInAlternation,
        SceneBoundaryGapS = source.SceneBoundaryGapS,
        RoomToneFillMs = source.RoomToneFillMs,
        LoudnessToleranceLu = source.LoudnessToleranceLu,
        RoomToneMaxS = source.RoomToneMaxS,
        EndGuardSeconds = source.EndGuardSeconds,
        SearchWindowSeconds = source.SearchWindowSeconds,
        CleanProximitySeconds = source.CleanProximitySeconds,
        MergeGapSeconds = source.MergeGapSeconds,
    };

    private static EditingPolicy MigrateLegacy(JsonElement root)
    {
        var policy = Clone(Default);
        if (TryGetDouble(root, "minSnapSilenceSeconds", out var minSnap))
        {
            policy.MinSilenceMs = (int)Math.Round(minSnap * 1000, MidpointRounding.AwayFromZero);
        }

        if (TryGetDouble(root, "startInsetSeconds", out var inset))
        {
            policy.SpeechPadMs.Start = (int)Math.Round(inset * 1000, MidpointRounding.AwayFromZero);
        }

        if (TryGetDouble(root, "keptWordLeadSeconds", out var margin))
        {
            policy.MarginAfterS = margin;
        }

        if (TryGetDouble(root, "pauseKeepSeconds", out var pause))
        {
            policy.PauseTargetS = pause;
        }

        if (TryGetDouble(root, "minResidualGapSeconds", out var residual))
        {
            policy.MinResidualGapS = residual;
        }

        policy.PunchInAlternation = false;
        return policy;
    }

    private static bool Has(JsonElement root, string snake, string camel) =>
        root.TryGetProperty(snake, out _) || root.TryGetProperty(camel, out _);

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        if (root.TryGetProperty(name, out var property) && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}

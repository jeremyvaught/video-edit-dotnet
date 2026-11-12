using System.Text.Json.Serialization;

namespace VideoEdit.Models;

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = 1;

    public int Revision { get; set; }

    public string Name { get; set; } = "";

    public List<SourceAsset> Sources { get; set; } = [];

    public List<SourceFinding> Findings { get; set; } = [];

    public List<Scene> Scenes { get; set; } = [];

    public List<ReviewQuestion> Questions { get; set; } = [];

    public OutputSettings Output { get; set; } = new();

    public NarrationSettings Narration { get; set; } = new();

    public FinalApproval? FinalApproval { get; set; }

    public BoundRenderSettings BoundRender { get; set; } = new();

    [JsonIgnore]
    public BoundRenderSettings? LiveRender { get; set; }
}

public sealed class BoundRenderSettings
{
    public double SceneBoundaryGapS { get; set; }

    public int RoomToneFillMs { get; set; } = 40;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoomPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoomFingerprint { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RoomGainDb { get; set; }
}

public sealed class NarrationSettings
{
    public string VoiceId { get; set; } = "";

    public string ModelId { get; set; } = "";

    public string? GeneratedAssetId { get; set; }

    public NarrationDerivative? Derivative { get; set; }

    public LoudnessMeasurements? Loudness { get; set; }

    public RoomToneClip? RoomTone { get; set; }
}

public sealed class NarrationDerivative
{
    public string Path { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    public double DurationSeconds { get; set; }
}

public sealed class LoudnessMeasurements
{
    public double RecordedLufs { get; set; }

    public double GeneratedLufs { get; set; }

    public double MatchedLufs { get; set; }

    public double? SampleLufs { get; set; }

    public double TargetLufs { get; set; }

    public double GainDb { get; set; }

    public double ToleranceLu { get; set; }
}

public sealed class RoomToneClip
{
    public double Start { get; set; }

    public double End { get; set; }

    public string Path { get; set; } = "";

    public double NoiseFloorLufs { get; set; }
}

public sealed class SourceAsset
{
    public string Id { get; set; } = "";

    public string Path { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    public double DurationSeconds { get; set; }

    public string? CodecName { get; set; }

    public string? AverageFrameRate { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Kind { get; set; } = "video";
}

public sealed class SourceFinding
{
    public string Id { get; set; } = "";

    public string SourceAssetId { get; set; } = "";

    public string SourceFingerprint { get; set; } = "";

    public double Start { get; set; }

    public double End { get; set; }

    public string Kind { get; set; } = "";

    public string TranscriptEvidence { get; set; } = "";

    public string Reason { get; set; } = "";

    public double Confidence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptText { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpokenText { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScriptTokenIndex { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Decision { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonIgnore]
    public TimeRange Range => new(Start, End);
}

public sealed class Scene
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public int Position { get; set; }

    public List<SourceSelection> Selections { get; set; } = [];

    public List<SceneEdit> Edits { get; set; } = [];

    public SceneAcceptance? Acceptance { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NarrationAssetId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NarrationPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NarrationFingerprint { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VisualAssetId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? NarrationDurationSeconds { get; set; }

    [JsonIgnore]
    public bool IsGenerated => !string.IsNullOrEmpty(NarrationAssetId);
}

public sealed class SourceSelection
{
    public string SourceAssetId { get; set; } = "";

    public string SourceFingerprint { get; set; } = "";

    public double Start { get; set; }

    public double End { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioSourceAssetId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioSourceFingerprint { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AudioStart { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AudioEnd { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AudioGainDb { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsCutaway { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ReplaceEnd { get; set; }

    [JsonIgnore]
    public TimeRange Range => new(Start, End);

    [JsonIgnore]
    public bool HasAudioOverlay => !string.IsNullOrEmpty(AudioSourceAssetId);

    [JsonIgnore]
    public TimeRange? AudioRange =>
        AudioStart is double start && AudioEnd is double end ? new TimeRange(start, end) : null;

    [JsonIgnore]
    public double? MapOrigin { get; set; }
}

public sealed class SceneEdit
{
    public string Id { get; set; } = "";

    public string SceneId { get; set; } = "";

    public string SourceAssetId { get; set; } = "";

    public string SourceFingerprint { get; set; } = "";

    public string? FindingId { get; set; }

    public int ProposalRevision { get; set; } = 1;

    public double Start { get; set; }

    public double End { get; set; }

    public string Kind { get; set; } = "";

    public string RemovedText { get; set; } = "";

    public string Reason { get; set; } = "";

    public double Confidence { get; set; }

    public string Decision { get; set; } = DecisionStates.Pending;

    public int? DecisionProposalRevision { get; set; }

    public string? Note { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NoCleanBoundary { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Crossfade { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FadeSeconds { get; set; }

    [JsonIgnore]
    public TimeRange Range => new(Start, End);

    [JsonIgnore]
    public bool DecisionMatchesProposal =>
        DecisionProposalRevision is not int bound || bound == ProposalRevision;
}

public sealed class SceneAcceptance
{
    public string CompositionHash { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Decision { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonIgnore]
    public string ReviewDecision => Decision ?? DecisionStates.Approved;
}

public sealed class FinalApproval
{
    public string CompositionHash { get; set; } = "";
}

public sealed class OutputSettings
{
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int FrameRate { get; set; } = 30;

    public string VideoCodec { get; set; } = "libx264";

    public string AudioCodec { get; set; } = "aac";
}

public sealed class ReviewQuestion
{
    public string Id { get; set; } = "";

    public string SceneId { get; set; } = "";

    public double? Around { get; set; }

    public string Text { get; set; } = "";
}

public static class EditKinds
{
    public const string Preroll = "preroll";
    public const string Flub = "flub";
    public const string FalseStart = "false_start";
    public const string RepeatTake = "repeat_take";
    public const string Pause = "pause";
    public const string Instruction = "instruction";
    public const string Postroll = "postroll";
    public const string Insertion = "insertion";
    public const string Repeat = "repeat";
    public const string Substitution = "substitution";
    public const string Retake = "retake";

    public static readonly HashSet<string> All =
    [
        Preroll, Flub, FalseStart, RepeatTake, Pause, Instruction, Postroll, Insertion, Repeat, Substitution, Retake
    ];
}

public static class ScriptDriftKinds
{
    public const string Substitution = "substitution";
    public const string Insertion = "insertion";
    public const string Omission = "omission";
    public const string Repeat = "repeat";
    public const string Retake = "retake";

    public static readonly HashSet<string> All =
    [
        Substitution, Insertion, Omission, Repeat, Retake
    ];

    public static bool IsDrift(string kind) => All.Contains(kind);

    public static bool IsCutProposal(string kind) => kind is Insertion or Repeat or Retake;
}

public static class PickupFindingKinds
{
    public const string Timing = "timing";
    public const string Retake = "retake";

    public static bool IsPickup(string kind) => kind == Timing || kind == Retake;
}

public static class TimingDecisions
{
    public const string Extend = "extend";
    public const string Trim = "trim";
    public const string Reject = "reject";

    public static readonly HashSet<string> All = [Extend, Trim, Reject];
}

public static class DriftSeverity
{
    public const string Minor = "minor";
    public const string Rerecord = "re-record";
}

public static class RerecordStatuses
{
    public const string Pending = "pending";
    public const string ResolvedByCut = "resolved-by-cut";
    public const string Rejected = "rejected";
    public const string Dismissed = "dismissed";
    public const string Flagged = "flagged";
}

public static class DecisionStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Flagged = "flagged";

    public static readonly HashSet<string> All =
    [
        Pending, Approved, Rejected, Flagged
    ];

    public static bool IsResolved(string decision) =>
        decision is Approved or Rejected;
}

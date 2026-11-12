using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class SilenceSnapperTests
{
    private static readonly SilenceInterval[] Silences =
    [
        new(10.0, 12.0),
        new(20.0, 20.5),
    ];

    [Fact]
    public void SnapStartInsetsFromSilenceStart()
    {
        var snap = SilenceSnapper.SnapStart(10.05, Silences);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(10.07, snap.Time, 3);
    }

    [Fact]
    public void SnapEndLeavesLeadBeforeTheKeptWord()
    {
        var snap = SilenceSnapper.SnapEnd(11.92, EditKinds.Flub, Silences);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(11.65, snap.Time, 3);
    }

    [Fact]
    public void SpeechCutDoesNotJumpToTheFarEdgeOfAContainingSilence()
    {
        var snap = SilenceSnapper.SnapEnd(11.4, EditKinds.FalseStart, Silences);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(11.4, snap.Time);
    }

    [Fact]
    public void JustBeforeSilenceSnapsTheEndToTheKeptWordLead()
    {
        var snap = SilenceSnapper.SnapEnd(19.9, EditKinds.Flub, Silences);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(20.15, snap.Time, 3);
    }

    [Fact]
    public void PostrollEndStaysAtTheFileEnd()
    {
        var snap = SilenceSnapper.SnapEnd(22, EditKinds.Postroll, [new SilenceInterval(20, 22)]);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(22, snap.Time);
    }

    [Fact]
    public void PostrollEndDoesNotRoundTheSourceDuration()
    {
        const double duration = 1.366667;
        var snap = SilenceSnapper.SnapEnd(duration, EditKinds.Postroll, [new SilenceInterval(0.8, duration)]);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(duration, snap.Time);
        Assert.True(snap.Time <= duration);
    }

    [Fact]
    public void PauseEndKeepsSixTenthsOfASecond()
    {
        var snap = SilenceSnapper.SnapEnd(11.9, EditKinds.Pause, Silences);
        Assert.Equal(11.4, snap.Time, 3);
    }

    [Fact]
    public void PrerollStartStaysAtZero()
    {
        var snap = SilenceSnapper.SnapStart(0, EditKinds.Preroll, [new SilenceInterval(0, 2)]);
        Assert.False(snap.NoCleanBoundary);
        Assert.Equal(0, snap.Time);
    }

    [Fact]
    public void MissingNearbySilenceIsMarkedUnclean()
    {
        var snap = SilenceSnapper.SnapStart(15.0, Silences);
        Assert.True(snap.NoCleanBoundary);
        Assert.Equal(15.0, snap.Time);
    }

    [Fact]
    public void AdjacentSilencesCloserThanFiftyMsMerge()
    {
        var merged = SilenceSnapper.MergeAdjacent(
        [
            new SilenceInterval(139.337, 141.463),
            new SilenceInterval(141.463, 142.124),
            new SilenceInterval(54.725, 56.070),
            new SilenceInterval(56.070, 56.666),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(54.725, merged[0].Start, 3);
        Assert.Equal(56.666, merged[0].End, 3);
        Assert.Equal(139.337, merged[1].Start, 3);
        Assert.Equal(142.124, merged[1].End, 3);
    }

    [Fact]
    public void ShortResidualGapMovesTheStartLater()
    {
        var silences = new[]
        {
            new SilenceInterval(115.508, 116.147),
            new SilenceInterval(117.725, 118.017),
        };
        var snapped = SilenceSnapper.SnapEdit(new TimeRange(116.169, 118.1), EditKinds.FalseStart, silences);
        Assert.False(snapped.NoCleanBoundary);
        Assert.InRange(snapped.Range.Start, 115.97, 116.02);
        Assert.InRange(snapped.Range.End, 117.98, 118.00);
    }

    [Fact]
    public void EnergyMinimumPicksTheQuietestWindow()
    {
        var samples = new float[AudioEnergy.SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.2f;
        }

        var dip = (int)(0.4 * AudioEnergy.SampleRate);
        for (var i = dip; i < dip + 80; i++)
        {
            samples[i] = 0.001f;
        }

        var time = AudioEnergy.FindLocalMinimum(samples, 38.6);
        Assert.InRange(time ?? 0, 38.99, 39.02);
    }

    [Fact]
    public void ParsedSilenceLogSnapsNearbyCutsAndLeavesSpeechBoundariesFlagged()
    {
        var silences = SilenceSnapper.ParseFfmpegLog("""
            [silencedetect] silence_start: 117.725
            [silencedetect] silence_end: 118.017 | silence_duration: 0.292
            [silencedetect] silence_start: 169.945
            [silencedetect] silence_end: 171.000 | silence_duration: 1.055
            """);
        Assert.Equal(2, silences.Count);
        var flubStart = SilenceSnapper.SnapStart(38.86, silences);
        var instructionStart = SilenceSnapper.SnapStart(169.9, silences);
        var costsEnd = SilenceSnapper.SnapEnd(118.1, EditKinds.FalseStart, silences);
        var whyEnd = SilenceSnapper.SnapEnd(64.98, EditKinds.FalseStart, silences);

        Assert.True(flubStart.NoCleanBoundary);
        Assert.Equal(38.86, flubStart.Time);
        Assert.False(instructionStart.NoCleanBoundary);
        Assert.InRange(instructionStart.Time, 170.01, 170.02);
        Assert.False(costsEnd.NoCleanBoundary);
        Assert.InRange(costsEnd.Time, 117.98, 118.00);
        Assert.Equal(64.98, whyEnd.Time);
    }

    [Fact]
    public void StopConsonantClosuresAreNotSnapTargets()
    {
        var silences = new[]
        {
            new SilenceInterval(23.916854, 23.992333),
            new SilenceInterval(24.114896, 25.157667),
            new SilenceInterval(42.661354, 42.729479),
            new SilenceInterval(43.056062, 44.586917),
        };

        var pause24 = SilenceSnapper.SnapEdit(new TimeRange(23.986854, 24.557667), EditKinds.Pause, silences);
        Assert.False(pause24.NoCleanBoundary);
        Assert.InRange(pause24.Range.Start, 24.18, 24.19);
        Assert.InRange(pause24.Range.End, 24.55, 24.56);

        var pause43 = SilenceSnapper.SnapEdit(new TimeRange(42.731354, 43.986917), EditKinds.Pause, silences);
        Assert.False(pause43.NoCleanBoundary);
        Assert.InRange(pause43.Range.Start, 43.12, 43.13);
        Assert.InRange(pause43.Range.End, 43.98, 43.99);
    }

    [Fact]
    public void SubOneFiftyMsSilenceIsKeptOnlyForNoCleanDiagnosis()
    {
        var shortOnly = new[] { new SilenceInterval(14.95, 15.03) };
        var inside = SilenceSnapper.SnapStart(15.0, shortOnly);
        Assert.False(inside.NoCleanBoundary);
        Assert.Equal(15.0, inside.Time);

        var missing = SilenceSnapper.SnapStart(15.0, Array.Empty<SilenceInterval>());
        Assert.True(missing.NoCleanBoundary);
        Assert.Equal(15.0, missing.Time);
    }

    [Fact]
    public void NoVadGapCutsOnsetToOnset()
    {
        var silences = new[] { new SilenceInterval(0, 1) };
        var onsets = new[] { 37.15, 40.29, 88.47 };
        var start = SilenceSnapper.SnapStart(39.31, EditKinds.Flub, silences, null, null, onsets);
        Assert.True(start.NoCleanBoundary);
        Assert.Equal(39.31, start.Time);

        var end = SilenceSnapper.SnapEnd(40.20, EditKinds.Flub, silences, null, null, onsets);
        Assert.True(end.NoCleanBoundary);
        Assert.Equal(40.29, end.Time);
    }

    [Fact]
    public void PolicyOverridesSnapConstants()
    {
        var policy = new EditingPolicy
        {
            SpeechPadMs = new SpeechPadMs { Start = 100, End = 30 },
            PauseTargetS = 0.4,
        };
        var snap = SilenceSnapper.SnapStart(10.05, EditKinds.Pause, Silences, policy);
        Assert.Equal(10.1, snap.Time, 3);

        var end = SilenceSnapper.SnapEnd(11.9, EditKinds.Pause, Silences, policy);
        Assert.Equal(11.6, end.Time, 3);
    }

    [Fact]
    public void LoadOrDefaultMigratesLegacyPolicyKeysAndReadsVadVocabulary()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "edit", EditingPolicy.FileName);
        Assert.Equal(150, EditingPolicy.LoadOrDefault(path).MinSilenceMs);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "silenceThresholdDb": -40,
              "minSnapSilenceSeconds": 0.15,
              "startInsetSeconds": 0.07,
              "pauseKeepSeconds": 0.6,
              "keptWordLeadSeconds": 0.35,
              "minResidualGapSeconds": 0.5
            }
            """);
        var migrated = EditingPolicy.LoadOrDefault(path);
        Assert.Equal(0.5, migrated.VadThreshold);
        Assert.Equal(150, migrated.MinSilenceMs);
        Assert.Equal(70, migrated.SpeechPadMs.Start);
        Assert.Equal(0.35, migrated.MarginAfterS);
        Assert.Equal(0.6, migrated.PauseTargetS);
        Assert.False(migrated.PunchInAlternation);
        Assert.Contains("vad_threshold", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("scene_boundary_gap_s", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("room_tone_fill_ms", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(0.25, migrated.SceneBoundaryGapS);
        Assert.Equal(40, migrated.RoomToneFillMs);

        EditingPolicy.Write(path, new EditingPolicy { MinSilenceMs = 200, PauseTargetS = 0.55 });
        var loaded = EditingPolicy.LoadOrDefault(path);
        Assert.Equal(200, loaded.MinSilenceMs);
        Assert.Equal(0.55, loaded.PauseTargetS);
        Assert.Equal(0.25, loaded.SceneBoundaryGapS);
        Assert.Equal(40, loaded.RoomToneFillMs);

        File.WriteAllText(path, """
            {
              "vad_threshold": 0.5,
              "min_silence_ms": 150,
              "speech_pad_ms": { "start": 70, "end": 30 },
              "margin_after_s": 0.35,
              "pause_target_s": 0.6,
              "min_residual_gap_s": 0.5,
              "punch_in_alternation": false
            }
            """);
        var filled = EditingPolicy.LoadOrDefault(path);
        Assert.Equal(0.25, filled.SceneBoundaryGapS);
        Assert.Equal(40, filled.RoomToneFillMs);
        Assert.Contains("scene_boundary_gap_s", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("room_tone_fill_ms", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void NoCleanFlubUsesRoomToneFillFromPolicy()
    {
        var edit = new SceneEdit { NoCleanBoundary = true };
        Assert.Equal(0.04, CompositionResolver.FadeSeconds(edit), 3);
        Assert.Equal(
            0.08,
            CompositionResolver.FadeSeconds(edit, new EditingPolicy { RoomToneFillMs = 80 }),
            3);
        Assert.Equal(AudioTreatment.DefaultFadeSeconds, CompositionResolver.FadeSeconds(new SceneEdit()), 3);
    }

    [Fact]
    public void VadCliOutputBecomesGapsBetweenSpeechSegments()
    {
        var map = VadSpeechMap.ParseCli(
            """
            Detected 2 speech segments:
            Speech segment 0: start = 35.00, end = 224.00
            Speech segment 1: start = 330.00, end = 438.00
            """,
            "ggml-silero-v6.2.0.bin",
            EditingPolicy.Default);
        Assert.Equal(0.35, map.Segments[0].Start, 3);
        Assert.Equal(2.24, map.Segments[0].End, 3);
        var silences = map.ToSilences(10);
        Assert.Equal(3, silences.Count);
        Assert.Equal(0, silences[0].Start);
        Assert.Equal(0.35, silences[0].End, 3);
        Assert.Equal(2.24, silences[1].Start, 3);
        Assert.Equal(3.30, silences[1].End, 3);
        Assert.Equal(4.38, silences[2].Start, 3);
        Assert.Equal(10, silences[2].End);
    }
}

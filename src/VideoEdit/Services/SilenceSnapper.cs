using System.Globalization;
using System.Text.RegularExpressions;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record SilenceInterval(double Start, double End)
{
    public double Duration => End - Start;

    public bool Contains(double time) => Start <= time && time <= End;

    public double DistanceTo(double time)
    {
        if (Contains(time))
        {
            return 0;
        }

        return time < Start ? Start - time : time - End;
    }
}

public sealed record SnapResult(
    double Time,
    bool NoCleanBoundary,
    string? SilenceNote,
    SilenceInterval? Silence = null);

public static class SilenceSnapper
{
    public static IReadOnlyList<SilenceInterval> ParseFfmpegLog(string text, EditingPolicy? policy = null)
    {
        policy ??= EditingPolicy.Default;
        var intervals = new List<SilenceInterval>();
        double? start = null;
        foreach (var line in text.Split('\n'))
        {
            var startMatch = Regex.Match(line, @"silence_start:\s*([0-9.]+)");
            if (startMatch.Success)
            {
                start = double.Parse(startMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var endMatch = Regex.Match(line, @"silence_end:\s*([0-9.]+)");
            if (endMatch.Success && start is double silenceStart)
            {
                var end = double.Parse(endMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (end > silenceStart)
                {
                    intervals.Add(new SilenceInterval(silenceStart, end));
                }

                start = null;
            }
        }

        return MergeAdjacent(intervals, policy.MergeGapSeconds)
            .Where(item => item.Duration >= EditingPolicy.DiagnosisMinSilenceSeconds)
            .ToList();
    }

    public static IReadOnlyList<SilenceInterval> MergeAdjacent(
        IReadOnlyList<SilenceInterval> silences,
        double? maxGapSeconds = null)
    {
        var gap = maxGapSeconds ?? EditingPolicy.Default.MergeGapSeconds;
        var ordered = silences
            .Where(item => item.End > item.Start)
            .OrderBy(item => item.Start)
            .ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        var merged = new List<SilenceInterval> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = merged[^1];
            var next = ordered[i];
            if (next.Start - current.End <= gap)
            {
                merged[^1] = new SilenceInterval(current.Start, Math.Max(current.End, next.End));
            }
            else
            {
                merged.Add(next);
            }
        }

        return merged;
    }

    public static SnapResult SnapStart(double proposed, IReadOnlyList<SilenceInterval> silences, EditingPolicy? policy = null) =>
        SnapStart(proposed, kind: null, silences, policy);

    public static SnapResult SnapStart(
        double proposed,
        string? kind,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy? policy = null,
        IReadOnlyList<SilenceInterval>? diagnosis = null,
        IReadOnlyList<double>? onsets = null)
    {
        policy ??= EditingPolicy.Default;
        if (kind == EditKinds.Preroll && proposed <= 0)
        {
            return new SnapResult(Round(proposed), false, null);
        }

        if (kind == EditKinds.Retake && TryRetakeStart(proposed, diagnosis, policy, out var retakeStart))
        {
            return retakeStart;
        }

        var silence = FindForStart(proposed, kind, silences, policy);
        if (silence is null)
        {
            return NoGapBoundary(proposed, silences, diagnosis, onsets, policy, forEnd: false);
        }

        return new SnapResult(Round(silence.Start + policy.SpeechPadStartSeconds), false, null, silence);
    }

    public static SnapResult SnapEnd(
        double proposed,
        string kind,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy? policy = null,
        IReadOnlyList<SilenceInterval>? diagnosis = null,
        IReadOnlyList<double>? onsets = null)
    {
        policy ??= EditingPolicy.Default;
        if (kind == EditKinds.Postroll)
        {
            return new SnapResult(proposed, false, null);
        }

        if (kind == EditKinds.Retake && TryRetakeEnd(proposed, silences, diagnosis, onsets, policy, out var retakeEnd))
        {
            return retakeEnd;
        }

        var silence = FindForEnd(proposed, kind, silences, policy);
        if (silence is null)
        {
            var fallback = NoGapBoundary(proposed, silences, diagnosis, onsets, policy, forEnd: true);
            return ClampRetakeEnd(kind, proposed, fallback);
        }

        var lead = kind == EditKinds.Pause ? policy.PauseTargetS : policy.MarginAfterS;
        lead = Math.Max(lead, Math.Max(policy.SpeechPadEndSeconds, policy.EndGuardSeconds));
        var snapped = silence.End - lead;
        if (snapped <= silence.Start + policy.SpeechPadStartSeconds)
        {
            snapped = silence.End - policy.EndGuardSeconds;
        }

        return ClampRetakeEnd(kind, proposed, new SnapResult(Round(snapped), false, null, silence));
    }

    public static (TimeRange Range, bool NoCleanBoundary) SnapEdit(
        TimeRange proposed,
        string kind,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy? policy = null,
        IReadOnlyList<SilenceInterval>? diagnosis = null,
        IReadOnlyList<double>? onsets = null)
    {
        policy ??= EditingPolicy.Default;
        var start = SnapStart(proposed.Start, kind, silences, policy, diagnosis, onsets);
        var end = SnapEnd(proposed.End, kind, silences, policy, diagnosis, onsets);
        if (end.Time <= start.Time)
        {
            return (proposed, true);
        }

        var startTime = WidenResidualGap(start, end, policy);
        if (end.Time <= startTime)
        {
            return (proposed, true);
        }

        return (new TimeRange(startTime, end.Time), start.NoCleanBoundary || end.NoCleanBoundary);
    }

    public static double WidenResidualGap(SnapResult start, SnapResult end, EditingPolicy? policy = null)
    {
        policy ??= EditingPolicy.Default;
        if (start.Silence is null || end.Silence is null || start.NoCleanBoundary || end.NoCleanBoundary)
        {
            return start.Time;
        }

        var tail = start.Time - start.Silence.Start;
        var lead = end.Silence.End - end.Time;
        if (tail + lead >= policy.MinResidualGapS)
        {
            return start.Time;
        }

        var neededTail = policy.MinResidualGapS - lead;
        var widened = start.Silence.Start + neededTail;
        var latest = Math.Min(start.Silence.End - policy.SpeechPadStartSeconds, end.Time - 0.05);
        return Round(Math.Clamp(widened, start.Time, latest));
    }

    private static bool TryRetakeStart(
        double proposed,
        IReadOnlyList<SilenceInterval>? diagnosis,
        EditingPolicy policy,
        out SnapResult result)
    {
        result = default!;
        var previous = diagnosis?
            .Where(item =>
                item.Duration >= EditingPolicy.DiagnosisMinSilenceSeconds
                && item.End <= proposed + 0.02
                && proposed - item.End <= 0.4)
            .OrderByDescending(item => item.End)
            .FirstOrDefault();
        if (previous is null)
        {
            return false;
        }

        result = new SnapResult(Round(previous.End), false, null);
        return true;
    }

    private static bool TryRetakeEnd(
        double proposed,
        IReadOnlyList<SilenceInterval> silences,
        IReadOnlyList<SilenceInterval>? diagnosis,
        IReadOnlyList<double>? onsets,
        EditingPolicy policy,
        out SnapResult result)
    {
        result = default!;
        if (!IsOnsetAfterSilence(proposed, silences, diagnosis, onsets))
        {
            return false;
        }

        result = new SnapResult(Round(proposed), false, null);
        return true;
    }

    private static SnapResult ClampRetakeEnd(string kind, double proposed, SnapResult snapped)
    {
        if (kind != EditKinds.Retake || snapped.Time <= proposed + 0.000001)
        {
            return snapped;
        }

        return new SnapResult(Round(proposed), snapped.NoCleanBoundary, snapped.SilenceNote);
    }

    private static bool IsOnsetAfterSilence(
        double proposed,
        IReadOnlyList<SilenceInterval> silences,
        IReadOnlyList<SilenceInterval>? diagnosis,
        IReadOnlyList<double>? onsets)
    {
        var seams = diagnosis is { Count: > 0 } ? diagnosis : silences;
        var afterSilence = seams.Any(item =>
            item.Duration >= EditingPolicy.DiagnosisMinSilenceSeconds
            && Math.Abs(item.End - proposed) <= 0.02);
        if (afterSilence)
        {
            return true;
        }

        return onsets?.Any(item => Math.Abs(item - proposed) <= 0.02) == true
            && seams.Any(item =>
                item.Duration >= EditingPolicy.DiagnosisMinSilenceSeconds
                && item.End <= proposed + 0.02
                && proposed - item.End <= 0.05);
    }

    private static SnapResult NoGapBoundary(
        double proposed,
        IReadOnlyList<SilenceInterval> silences,
        IReadOnlyList<SilenceInterval>? diagnosis,
        IReadOnlyList<double>? onsets,
        EditingPolicy policy,
        bool forEnd)
    {
        if (HasDiagnosisSilence(proposed, diagnosis ?? silences))
        {
            return new SnapResult(Round(proposed), false, null);
        }

        var onset = FindOnset(proposed, onsets, policy, forEnd);
        return new SnapResult(Round(onset ?? proposed), true, "no VAD gap; onset-to-onset");
    }

    private static bool HasDiagnosisSilence(
        double proposed,
        IReadOnlyList<SilenceInterval> silences) =>
        silences.Any(item =>
            item.Duration >= EditingPolicy.DiagnosisMinSilenceSeconds && item.Contains(proposed));

    private static double? FindOnset(
        double proposed,
        IReadOnlyList<double>? onsets,
        EditingPolicy policy,
        bool forEnd)
    {
        if (onsets is null || onsets.Count == 0)
        {
            return null;
        }

        if (forEnd)
        {
            var next = onsets
                .Where(onset => onset >= proposed - 0.05 && onset - proposed <= policy.SearchWindowSeconds)
                .OrderBy(onset => onset)
                .Cast<double?>()
                .FirstOrDefault();
            return next;
        }

        var nearest = onsets
            .OrderBy(onset => Math.Abs(onset - proposed))
            .First();
        return Math.Abs(nearest - proposed) <= policy.CleanProximitySeconds ? nearest : null;
    }

    private static SilenceInterval? FindForStart(
        double proposed,
        string? kind,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy policy) =>
        FindSnapSilence(proposed, kind, forStart: true, silences, policy);

    private static SilenceInterval? FindForEnd(
        double proposed,
        string kind,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy policy) =>
        FindSnapSilence(proposed, kind, forStart: false, silences, policy);

    private static SilenceInterval? FindSnapSilence(
        double proposed,
        string? kind,
        bool forStart,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy policy)
    {
        var pause = kind == EditKinds.Pause;
        SilenceInterval? best = null;
        var bestScore = double.MaxValue;
        foreach (var silence in silences)
        {
            if (silence.End <= silence.Start
                || silence.Duration < policy.MinSnapSilenceSeconds
                || silence.DistanceTo(proposed) > policy.SearchWindowSeconds)
            {
                continue;
            }

            double? score = null;
            if (forStart)
            {
                var fromStart = Math.Abs(silence.Start - proposed);
                var afterEnd = proposed - silence.End;
                if (fromStart <= policy.CleanProximitySeconds)
                {
                    score = fromStart;
                }
                else if (afterEnd >= 0 && afterEnd <= policy.CleanProximitySeconds)
                {
                    score = 1 + afterEnd;
                }
                else if (pause && silence.Contains(proposed))
                {
                    score = 2 + (proposed - silence.Start);
                }
                else if (pause && proposed < silence.Start)
                {
                    score = 3 + (silence.Start - proposed);
                }
            }
            else
            {
                var fromEnd = Math.Abs(silence.End - proposed);
                var beforeStart = silence.Start - proposed;
                if (fromEnd <= policy.CleanProximitySeconds)
                {
                    score = fromEnd;
                }
                else if (beforeStart >= 0 && beforeStart <= policy.CleanProximitySeconds)
                {
                    score = 1 + beforeStart;
                }
                else if (pause && silence.Contains(proposed))
                {
                    score = 2 + (silence.End - proposed);
                }
            }

            if (score is double value && value < bestScore)
            {
                best = silence;
                bestScore = value;
            }
        }

        return best;
    }

    private static double Round(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

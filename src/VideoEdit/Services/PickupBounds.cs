using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record PickupSnap(
    TimeRange Range,
    IReadOnlyList<TimeRange> DroppedTakes,
    IReadOnlyList<TimeRange> PauseCuts,
    bool TimingMismatch = false,
    IReadOnlyList<TimeRange>? FlaggedTakes = null);

public static class PickupBounds
{
    public static PickupSnap Snap(
        TimeRange words,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy? policy = null,
        double? lastWordEnd = null,
        double? maxDuration = null,
        IReadOnlyList<SpokenWord>? spokenWords = null)
    {
        policy ??= EditingPolicy.Default;
        var speech = SpeechIn(new TimeRange(words.Start, words.End + policy.MarginAfterS), silences);
        if (speech.Count == 0)
        {
            var emptyPauses = PauseCutsInside(words, silences, policy);
            return new PickupSnap(words, [], emptyPauses, TimingMismatch(SpokenDuration(words, emptyPauses), maxDuration));
        }

        var dropped = new List<TimeRange>();
        var flagged = new List<TimeRange>();
        while (speech.Count >= 2)
        {
            var gap = speech[1].Start - speech[0].End;
            if (gap <= policy.PauseTargetS)
            {
                break;
            }

            var later = speech.Skip(1).ToList();
            if (IsRetake(speech[0], later, spokenWords))
            {
                dropped.Add(new TimeRange(speech[0].Start, speech[1].Start));
                speech.RemoveAt(0);
                continue;
            }

            if (IsShortRepeatedPhrase(speech[0], later, spokenWords))
            {
                flagged.Add(new TimeRange(speech[0].Start, speech[1].Start));
            }

            break;
        }

        var start = dropped.Count > 0 ? speech[0].Start : Math.Min(words.Start, speech[0].Start);
        var end = EndInLastWordGap(speech[^1].End, lastWordEnd ?? words.End, silences, policy);
        var range = new TimeRange(Round(start), Round(end));
        var pauses = PauseCutsInside(range, silences, policy);
        return new PickupSnap(range, dropped, pauses, TimingMismatch(SpokenDuration(range, pauses), maxDuration), flagged);
    }

    public static IReadOnlyList<TimeRange> PauseCutsInside(
        TimeRange window,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy? policy = null)
    {
        policy ??= EditingPolicy.Default;
        var cuts = new List<TimeRange>();
        foreach (var silence in silences.Where(item =>
                     item.Duration > policy.PauseTargetS
                     && window.Overlaps(new TimeRange(item.Start, item.End))))
        {
            var gap = new TimeRange(Math.Max(window.Start, silence.Start), Math.Min(window.End, silence.End));
            if (gap.Duration <= policy.PauseTargetS)
            {
                continue;
            }

            var cut = new TimeRange(Round(gap.Start), Round(gap.End - policy.PauseTargetS));
            if (cut.Duration > 0.05 && window.Contains(cut))
            {
                cuts.Add(cut);
            }
        }

        return cuts;
    }

    public static TimeRange FoldLeadingPauses(TimeRange audio, IReadOnlyList<TimeRange> pauseCuts)
    {
        var start = audio.Start;
        foreach (var cut in pauseCuts.Where(item => item.Start <= audio.Start + 0.02))
        {
            start = Math.Max(start, cut.End);
        }

        return start > audio.Start && start < audio.End
            ? new TimeRange(Round(start), audio.End)
            : audio;
    }

    public static double SpokenDuration(TimeRange audio, IReadOnlyList<TimeRange> pauseCuts) =>
        RangeMath.TotalDuration(RangeMath.Subtract(
            audio,
            pauseCuts.Where(item => audio.Contains(item))));

    public static TimeRange FitHost(TimeRange host, double pickupDuration, EditingPolicy? policy = null)
    {
        policy ??= EditingPolicy.Default;
        if (pickupDuration <= 0 || pickupDuration >= host.Duration)
        {
            return host;
        }

        var fitted = Round(host.Start + pickupDuration + policy.PauseTargetS);
        return fitted < host.End ? new TimeRange(host.Start, fitted) : host;
    }

    public static double TrailingPad(TimeRange host, TimeRange audio, IReadOnlyList<TimeRange>? pauseCuts = null) =>
        Math.Max(0, host.Duration - SpokenDuration(audio, pauseCuts ?? []));

    public static double LeadingSilence(TimeRange audio, IReadOnlyList<SilenceInterval> silences)
    {
        var speech = SpeechIn(audio, silences);
        return speech.Count == 0 ? audio.Duration : Math.Max(0, speech[0].Start - audio.Start);
    }

    public static double TrimLeading(double lead, double trail, double pauseTarget) =>
        Math.Max(0, Math.Min(lead, trail + lead - pauseTarget));

    public static double SkipLeadingSilence(
        double from,
        double to,
        IReadOnlyList<SilenceInterval> silences,
        IReadOnlyList<TimeRange> removals,
        double keepSilence)
    {
        if (to <= from)
        {
            return from;
        }

        foreach (var keep in RangeMath.Subtract(new TimeRange(from, to), removals))
        {
            var speech = SpeechIn(keep, silences)
                .Where(item => item.Duration > 0.15)
                .ToList();
            if (speech.Count == 0)
            {
                continue;
            }

            var start = speech[0].Start - Math.Max(0, keepSilence);
            return Round(Math.Clamp(start, keep.Start, speech[0].Start));
        }

        return from;
    }

    public static double HostTrailingSilence(
        double from,
        double to,
        IReadOnlyList<SilenceInterval> silences,
        IReadOnlyList<TimeRange> removals)
    {
        if (to <= from)
        {
            return 0;
        }

        var keeps = RangeMath.Subtract(new TimeRange(from, to), removals);
        for (var i = keeps.Count - 1; i >= 0; i--)
        {
            var speech = SpeechIn(keeps[i], silences).Where(item => item.Duration > 0.15).ToList();
            if (speech.Count == 0)
            {
                continue;
            }

            return Math.Max(0, to - speech[^1].End);
        }

        return Math.Max(0, to - from);
    }

    private static double EndInLastWordGap(
        double lastSpeechEnd,
        double lastWordEnd,
        IReadOnlyList<SilenceInterval> silences,
        EditingPolicy policy)
    {
        var gap = silences
            .Where(item => item.Start >= lastSpeechEnd - 0.02 && item.End > lastSpeechEnd)
            .OrderBy(item => item.Start)
            .FirstOrDefault();
        if (gap is not null && lastWordEnd > gap.Start + 0.02)
        {
            return gap.Start;
        }

        return Math.Min(lastWordEnd, lastSpeechEnd);
    }

    private static bool TimingMismatch(double retainedDuration, double? maxDuration) =>
        maxDuration is double cap && cap > 0 && retainedDuration > cap + 0.02;

    private static bool IsRetake(
        TimeRange first,
        IReadOnlyList<TimeRange> later,
        IReadOnlyList<SpokenWord>? spokenWords)
    {
        if (spokenWords is null || spokenWords.Count == 0 || later.Count == 0)
        {
            return false;
        }

        var firstTokens = TokensIn(spokenWords, first);
        if (firstTokens.Count == 0)
        {
            return false;
        }

        var laterTokens = later.SelectMany(span => TokensIn(spokenWords, span)).ToList();
        if (laterTokens.Count == 0)
        {
            return false;
        }

        return firstTokens.Count >= 3
            && HasConsecutiveRun(firstTokens, laterTokens, 3)
            && OrderedOverlap(firstTokens, laterTokens) >= 0.6 * firstTokens.Count;
    }

    private static bool IsShortRepeatedPhrase(
        TimeRange first,
        IReadOnlyList<TimeRange> later,
        IReadOnlyList<SpokenWord>? spokenWords)
    {
        if (spokenWords is null || spokenWords.Count == 0 || later.Count == 0)
        {
            return false;
        }

        var firstTokens = TokensIn(spokenWords, first);
        if (firstTokens.Count == 0 || firstTokens.Count >= 3)
        {
            return false;
        }

        var laterTokens = later.SelectMany(span => TokensIn(spokenWords, span)).ToList();
        return OrderedOverlap(firstTokens, laterTokens) > 0;
    }

    private static List<string> TokensIn(IReadOnlyList<SpokenWord> spokenWords, TimeRange span) =>
        spokenWords
            .Where(word => word.Range.Overlaps(span) && word.Normalized.Length > 0)
            .OrderBy(word => word.Start)
            .Select(word => word.Normalized)
            .ToList();

    private static bool HasConsecutiveRun(IReadOnlyList<string> first, IReadOnlyList<string> later, int length)
    {
        if (first.Count < length || later.Count < length)
        {
            return false;
        }

        for (var i = 0; i <= first.Count - length; i++)
        {
            for (var j = 0; j <= later.Count - length; j++)
            {
                var match = true;
                for (var k = 0; k < length; k++)
                {
                    if (!string.Equals(first[i + k], later[j + k], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int OrderedOverlap(IReadOnlyList<string> first, IReadOnlyList<string> later)
    {
        var index = 0;
        var matched = 0;
        foreach (var token in first)
        {
            while (index < later.Count && !string.Equals(later[index], token, StringComparison.Ordinal))
            {
                index++;
            }

            if (index >= later.Count)
            {
                break;
            }

            matched++;
            index++;
        }

        return matched;
    }

    private static List<TimeRange> SpeechIn(TimeRange words, IReadOnlyList<SilenceInterval> silences)
    {
        var speech = new List<TimeRange>();
        var cursor = words.Start;
        foreach (var silence in silences.OrderBy(item => item.Start))
        {
            if (silence.Start > cursor)
            {
                var piece = new TimeRange(cursor, Math.Min(silence.Start, words.End));
                if (piece.Duration > 0.02 && words.Overlaps(piece))
                {
                    speech.Add(piece);
                }
            }

            cursor = Math.Max(cursor, silence.End);
            if (cursor >= words.End)
            {
                break;
            }
        }

        if (words.End > cursor)
        {
            speech.Add(new TimeRange(cursor, words.End));
        }

        return speech;
    }

    private static double Round(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

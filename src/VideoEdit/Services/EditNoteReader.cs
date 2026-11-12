using System.Globalization;
using System.Text.RegularExpressions;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record NoteProposal(
    TimeRange Range,
    string? Kind,
    string Reason,
    bool Crossfade,
    double? FadeSeconds,
    bool Understood);

public static class EditNoteReader
{
    public const string NotUnderstood = "Note not understood; re-propose manually.";
    private static readonly Regex RangePattern = new(
        @"(\d+(?:\.\d+)?)\s*(?:-|–|to)\s*(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex StartPattern = new(
        @"\bstart(?:s|ing)?(?:\s+at)?\s+(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EndPattern = new(
        @"\bend(?:s|ing)?(?:\s+at)?\s+(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WordHalfPattern = new(
        @"\b(last|first)\s+half\s+of(?:\s+the)?\s+word\s+([A-Za-z0-9']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MillisecondsPattern = new(
        @"(\d+(?:\.\d+)?)\s*ms\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedPhrase = new(
        @"[“""']([^“""']{1,80})[”""']",
        RegexOptions.CultureInvariant);

    public static NoteProposal ReadFinding(
        SourceFinding finding,
        string note,
        IReadOnlyList<SpokenWord>? words = null) =>
        Read(
            new SceneEdit
            {
                Start = finding.Start,
                End = finding.End,
                Kind = finding.Kind,
                Reason = finding.Reason,
            },
            note,
            words);

    public static NoteProposal Read(
        SceneEdit edit,
        string note,
        IReadOnlyList<SpokenWord>? words = null)
    {
        var text = note.Trim();
        var start = edit.Start;
        var end = edit.End;
        string? kind = null;
        var crossfade = edit.Crossfade;
        var fade = edit.FadeSeconds;
        var understood = false;

        if (TryCutKeepPhrases(text, edit.Range, words, out var cutKeep))
        {
            start = cutKeep.Start;
            end = cutKeep.End;
            kind = EditKinds.Retake;
            understood = true;
        }
        else if (HasPhrase(text, "retake"))
        {
            kind = EditKinds.Retake;
            understood = true;
        }
        else if (TryRange(text, out var range))
        {
            start = range.Start;
            end = range.End;
            understood = true;
        }
        else if (TrySingle(StartPattern, text, out var newStart))
        {
            start = newStart;
            understood = true;
        }
        else if (TrySingle(EndPattern, text, out var newEnd))
        {
            end = newEnd;
            understood = true;
        }
        else if (TryWordHalf(text, edit, words, out var half))
        {
            start = half.Start;
            end = half.End;
            understood = true;
        }
        else if (HasPhrase(text, "later", "too early"))
        {
            start += 0.15;
            understood = true;
        }
        else if (HasPhrase(text, "earlier", "too late"))
        {
            start = Math.Max(0, start - 0.15);
            understood = true;
        }
        else if (HasPhrase(text, "shorter", "too much", "too long"))
        {
            end = start + (edit.Range.Duration * 0.75);
            understood = true;
        }
        else if (HasPhrase(text, "longer", "too little", "too short"))
        {
            end += edit.Range.Duration * 0.25;
            understood = true;
        }

        if (HasPhrase(text, "not a pause", "isn't a pause", "language"))
        {
            understood = true;
            if (edit.Kind == EditKinds.Pause)
            {
                kind = EditKinds.Flub;
            }
        }
        else if (HasPhrase(text, "false start"))
        {
            kind = EditKinds.FalseStart;
            understood = true;
        }
        else if (Regex.IsMatch(text, @"\bflub\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            kind = EditKinds.Flub;
            understood = true;
        }

        if (HasPhrase(text, "crossfade"))
        {
            crossfade = true;
            fade = TrySingle(MillisecondsPattern, text, out var ms)
                ? ms / 1000.0
                : AudioTreatment.CrossfadeSeconds;
            understood = true;
        }

        if (end <= start)
        {
            start = edit.Start;
            end = edit.End;
        }

        var baseReason = BaseReason(edit.Reason);
        if (!understood)
        {
            var marked = string.IsNullOrWhiteSpace(baseReason)
                ? NotUnderstood
                : $"{baseReason} {NotUnderstood}";
            return new NoteProposal(edit.Range, null, marked, edit.Crossfade, edit.FadeSeconds, false);
        }

        var reason = string.IsNullOrWhiteSpace(baseReason)
            ? $"Re-proposed from note: {text}"
            : $"{baseReason} Re-proposed from note: {text}";
        return new NoteProposal(
            new TimeRange(Round(start), Round(end)),
            kind,
            reason,
            crossfade,
            fade,
            true);
    }

    private static bool TryCutKeepPhrases(
        string text,
        TimeRange current,
        IReadOnlyList<SpokenWord>? words,
        out TimeRange range)
    {
        range = default;
        string? cutPhrase = null;
        string? keepPhrase = null;
        foreach (Match match in QuotedPhrase.Matches(text))
        {
            var phrase = match.Groups[1].Value.Trim();
            if (phrase.Length == 0)
            {
                continue;
            }

            var before = text[Math.Max(0, match.Index - 16)..match.Index];
            var after = text[(match.Index + match.Length)..Math.Min(text.Length, match.Index + match.Length + 28)];
            if (IsCutPhrase(before, after))
            {
                cutPhrase ??= phrase;
            }
            else if (IsKeepPhrase(before, after))
            {
                keepPhrase ??= phrase;
            }
        }

        if (cutPhrase is null && keepPhrase is null)
        {
            return false;
        }

        var cut = cutPhrase is null ? null : FindPhrase(words, cutPhrase, current.Start - 2);
        var keep = keepPhrase is null ? null : FindPhrase(words, keepPhrase, cut?.Start ?? current.Start);
        if (cut is TimeRange cutRange && keep is TimeRange keepRange && keepRange.Start > cutRange.Start)
        {
            range = new TimeRange(cutRange.Start, keepRange.Start);
            return true;
        }

        if (cut is TimeRange onlyCut)
        {
            range = onlyCut;
            return true;
        }

        if (keep is TimeRange onlyKeep && current.Start < onlyKeep.Start)
        {
            range = new TimeRange(current.Start, onlyKeep.Start);
            return true;
        }

        return false;
    }

    private static bool IsCutPhrase(string before, string after) =>
        HasPhrase(after, "cut", "edited out", "edit out", "removed")
        || HasPhrase(before, "cut", "the first", "first");

    private static bool IsKeepPhrase(string before, string after) =>
        HasPhrase(after, "replacement")
        || HasPhrase(before, "keep", "kept", "retake of");

    private static TimeRange? FindPhrase(IReadOnlyList<SpokenWord>? words, string phrase, double hint)
    {
        if (words is null || words.Count == 0)
        {
            return null;
        }

        var needle = WhisperWords.TokenizeScript(phrase)
            .Select(item => item.Normalized)
            .Where(item => item.Length > 0)
            .ToList();
        if (needle.Count == 0)
        {
            return null;
        }

        TimeRange? best = null;
        var bestDistance = double.MaxValue;
        for (var i = 0; i + needle.Count <= words.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (!WhisperWords.SameToken(words[i + j].Normalized, needle[j]))
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            var candidate = new TimeRange(words[i].Start, words[i + needle.Count - 1].End);
            var distance = Math.Abs(candidate.Start - hint);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool TryRange(string text, out TimeRange range)
    {
        var match = RangePattern.Match(text);
        if (!match.Success)
        {
            range = default;
            return false;
        }

        var start = Number(match.Groups[1].Value);
        var end = Number(match.Groups[2].Value);
        if (end <= start)
        {
            range = default;
            return false;
        }

        range = new TimeRange(start, end);
        return true;
    }

    private static bool TrySingle(Regex pattern, string text, out double value)
    {
        var match = pattern.Match(text);
        if (!match.Success)
        {
            value = 0;
            return false;
        }

        value = Number(match.Groups[1].Value);
        return true;
    }

    private static bool TryWordHalf(
        string text,
        SceneEdit edit,
        IReadOnlyList<SpokenWord>? words,
        out TimeRange range)
    {
        var match = WordHalfPattern.Match(text);
        if (!match.Success)
        {
            range = default;
            return false;
        }

        var last = match.Groups[1].Value.Equals("last", StringComparison.OrdinalIgnoreCase);
        var needle = WhisperWords.Normalize(match.Groups[2].Value);
        var word = words?
            .Where(item => item.Normalized == needle && item.Range.Overlaps(edit.Range))
            .OrderBy(item => Math.Abs(item.Start - edit.Start))
            .FirstOrDefault();
        if (word is { } found && !string.IsNullOrEmpty(found.Normalized) && found.Range.Duration > 0)
        {
            var mid = found.Start + (found.Range.Duration / 2);
            range = last
                ? new TimeRange(mid, edit.End)
                : new TimeRange(edit.Start, mid);
            return range.Duration > 0.02;
        }

        range = default;
        return false;
    }

    private static bool HasPhrase(string text, params string[] phrases) =>
        phrases.Any(phrase => Regex.IsMatch(
            text,
            $@"\b{Regex.Escape(phrase)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static string BaseReason(string reason)
    {
        var text = reason.Trim();
        var marker = " Re-proposed from note:";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            text = text[..at].Trim();
        }

        if (text.EndsWith(NotUnderstood, StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^NotUnderstood.Length].Trim();
        }

        return text;
    }

    private static double Number(string text) =>
        double.Parse(text, CultureInfo.InvariantCulture);

    private static double Round(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

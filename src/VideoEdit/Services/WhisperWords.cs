using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoEdit.Models;

namespace VideoEdit.Services;

public readonly record struct SpokenWord(
    string Text,
    string Normalized,
    double Start,
    double End,
    bool EndsSentence = false)
{
    public TimeRange Range => new(Start, End);
}

public static class WhisperWords
{
    private static readonly Regex Token = new(
        @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<SpokenWord> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("transcription", out var rows))
        {
            throw VideoEditException.Validation("The word transcript has no transcription array.");
        }

        var words = new List<SpokenWord>();
        foreach (var row in rows.EnumerateArray())
        {
            var text = row.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!row.TryGetProperty("offsets", out var offsets)
                || !offsets.TryGetProperty("from", out var from)
                || !offsets.TryGetProperty("to", out var to))
            {
                throw VideoEditException.Validation("A transcript word is missing millisecond offsets.");
            }

            var start = from.GetInt32() / 1000.0;
            var end = to.GetInt32() / 1000.0;
            var safeEnd = end < start ? start + 0.05 : end;
            var tokens = Tokenize(text);
            var endsSentence = EndsUtterance(text);
            if (tokens.Count == 1)
            {
                words.Add(new SpokenWord(tokens[0].Text, tokens[0].Normalized, start, safeEnd, endsSentence));
                continue;
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                words.Add(new SpokenWord(
                    tokens[i].Text,
                    tokens[i].Normalized,
                    start,
                    safeEnd,
                    endsSentence && i == tokens.Count - 1));
            }
        }

        if (words.Count == 0)
        {
            throw VideoEditException.Validation("The word transcript has no spoken words.");
        }

        return words;
    }

    public static IReadOnlyList<(string Text, string Normalized)> TokenizeScript(string text) =>
        Tokenize(text);

    private static readonly Dictionary<string, int> CardinalWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90,
        ["hundred"] = 100,
        ["thousand"] = 1000,
    };

    private static readonly HashSet<string> OrdinalWords =
    [
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
        "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth",
        "eighteenth", "nineteenth", "twentieth",
    ];

    private static readonly Regex OrdinalDigits = new(
        @"^\d+(st|nd|rd|th)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        var trimmed = text.Trim().Trim('"', '\'', '`', ',', '.', ';', ':', '!', '?', '“', '”', '‘', '’');
        return trimmed.Replace('’', '\'').ToLowerInvariant();
    }

    public static bool SameToken(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal) || SameCardinal(left, right);

    public static bool SameCardinal(string left, string right)
    {
        if (IsOrdinal(left) || IsOrdinal(right))
        {
            return false;
        }

        var a = CardinalValue(left);
        var b = CardinalValue(right);
        return a is int leftValue && b is int rightValue && leftValue == rightValue;
    }

    private static bool IsOrdinal(string word) =>
        OrdinalWords.Contains(word) || OrdinalDigits.IsMatch(word);

    private static int? CardinalValue(string word)
    {
        if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return CardinalWords.TryGetValue(word, out var value) ? value : null;
    }

    public static string Join(IEnumerable<string> words) =>
        string.Join(' ', words.Where(word => !string.IsNullOrWhiteSpace(word)));

    public static TimeRange ExpandToUtterance(IReadOnlyList<SpokenWord> words, TimeRange seed, double maxGapSeconds = 0.8)
    {
        if (words.Count == 0)
        {
            return seed;
        }

        var first = 0;
        while (first < words.Count && words[first].End <= seed.Start)
        {
            first++;
        }

        var last = words.Count - 1;
        while (last >= 0 && words[last].Start >= seed.End)
        {
            last--;
        }

        if (first > last)
        {
            return seed;
        }

        while (first > 0)
        {
            var previous = words[first - 1];
            if (EndsUtterance(previous) || first == 0 || words[first].Start - previous.End > maxGapSeconds)
            {
                break;
            }

            first--;
        }

        while (last < words.Count - 1 && !EndsUtterance(words[last]))
        {
            var next = words[last + 1];
            if (next.Start - words[last].End > maxGapSeconds)
            {
                break;
            }

            last++;
        }

        return new TimeRange(Math.Min(seed.Start, words[first].Start), Math.Max(seed.End, words[last].End));
    }

    public static TimeRange ExpandToSentence(IReadOnlyList<SpokenWord> words, TimeRange seed)
    {
        if (!TryBounds(words, seed, out var first, out var last))
        {
            return seed;
        }

        while (first > 0 && !EndsUtterance(words[first - 1]))
        {
            first--;
        }

        while (last < words.Count - 1 && !EndsUtterance(words[last]))
        {
            last++;
        }

        return new TimeRange(words[first].Start, words[last].End);
    }

    public static TimeRange AlignSentence(
        IReadOnlyList<SpokenWord> host,
        IReadOnlyList<SpokenWord> pickupSentence,
        TimeRange hint)
    {
        if (pickupSentence.Count == 0 || host.Count == 0)
        {
            return ExpandToSentence(host, hint);
        }

        var needles = pickupSentence.Select(word => word.Normalized).ToList();
        var bestStart = -1;
        var bestEnd = -1;
        var bestMatched = 0;
        var bestScore = int.MinValue;
        for (var i = 0; i < host.Count; i++)
        {
            if (host[i].Normalized != needles[0])
            {
                continue;
            }

            var matched = 1;
            var cursor = i + 1;
            var end = i;
            for (var n = 1; n < needles.Count && cursor < host.Count; n++)
            {
                var look = cursor;
                var limit = Math.Min(host.Count, cursor + 4);
                while (look < limit && host[look].Normalized != needles[n])
                {
                    if (EndsUtterance(host[look]))
                    {
                        look = limit;
                        break;
                    }

                    look++;
                }

                if (look >= limit)
                {
                    continue;
                }

                matched++;
                end = look;
                cursor = look + 1;
            }

            var span = new TimeRange(host[i].Start, host[end].End);
            var sentenceStart = i == 0 || EndsUtterance(host[i - 1]);
            var score = matched * 20
                + (span.Overlaps(hint) ? 80 : 0)
                + (sentenceStart ? 120 : 0);
            if (hint.Duration > 0)
            {
                score -= (int)(Math.Abs(span.Start - hint.Start) * 2);
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestMatched = matched;
                bestStart = i;
                bestEnd = end;
            }
        }

        if (bestStart < 0 || bestMatched < Math.Max(3, needles.Count / 2))
        {
            return ExpandToSentence(host, hint);
        }

        return new TimeRange(host[bestStart].Start, host[bestEnd].End);
    }

    public static TimeRange CompleteSentence(IReadOnlyList<SpokenWord> words, TimeRange aligned)
    {
        if (!TryBounds(words, aligned, out var first, out var last))
        {
            return aligned;
        }

        while (last < words.Count - 1 && !EndsUtterance(words[last]))
        {
            last++;
        }

        return SnapIntoPauses(words, new TimeRange(words[first].Start, words[last].End));
    }

    public static TimeRange SnapIntoPauses(IReadOnlyList<SpokenWord> words, TimeRange sentence)
    {
        if (!TryBounds(words, sentence, out var first, out var last))
        {
            return sentence;
        }

        var start = words[first].Start;
        var end = words[last].End;
        if (first > 0)
        {
            var gap = start - words[first - 1].End;
            if (gap is >= 0.04 and <= 1.0)
            {
                start = words[first - 1].End + (gap / 2);
            }
        }

        if (last + 1 < words.Count)
        {
            var gap = words[last + 1].Start - end;
            if (gap is >= 0.04 and <= 1.0)
            {
                end += gap / 2;
            }
        }

        return new TimeRange(start, end);
    }

    private static bool TryBounds(IReadOnlyList<SpokenWord> words, TimeRange seed, out int first, out int last)
    {
        first = 0;
        while (first < words.Count && words[first].End <= seed.Start)
        {
            first++;
        }

        last = words.Count - 1;
        while (last >= 0 && words[last].Start >= seed.End)
        {
            last--;
        }

        return first <= last;
    }

    private static bool EndsUtterance(SpokenWord word) =>
        word.EndsSentence || EndsUtterance(word.Text);

    private static bool EndsUtterance(string text) =>
        text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('!') || text.TrimEnd().EndsWith('?');

    public static string FileName(string analysisDirectory) =>
        TryFileName(analysisDirectory)
        ?? throw VideoEditException.NotFound("No word transcript (words2.json or words.json) is in edit/analysis.");

    public static string? TryFileName(string analysisDirectory)
    {
        var preferred = Path.Combine(analysisDirectory, "words2.json");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var fallback = Path.Combine(analysisDirectory, "words.json");
        return File.Exists(fallback) ? fallback : null;
    }

    private static List<(string Text, string Normalized)> Tokenize(string text)
    {
        var tokens = new List<(string, string)>();
        foreach (Match match in Token.Matches(text))
        {
            var normalized = Normalize(match.Value);
            if (normalized.Length > 0)
            {
                tokens.Add((match.Value, normalized));
            }
        }

        return tokens;
    }
}

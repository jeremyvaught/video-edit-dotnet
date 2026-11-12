using System.Globalization;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record ScriptDrift(
    string Kind,
    string ScriptText,
    string SpokenText,
    double Start,
    double End,
    string Severity,
    int? ScriptTokenIndex = null);

public static class ScriptAligner
{
    private static readonly HashSet<string> Articles = ["a", "an", "the"];

    private static readonly (string Expanded, string Contracted)[] Contractions =
    [
        ("i am", "i'm"),
        ("do not", "don't"),
        ("does not", "doesn't"),
        ("cannot", "can't"),
        ("will not", "won't"),
        ("it is", "it's"),
        ("that is", "that's"),
        ("we are", "we're"),
        ("you are", "you're"),
        ("they are", "they're"),
        ("is not", "isn't"),
        ("are not", "aren't"),
        ("was not", "wasn't"),
        ("were not", "weren't"),
        ("let us", "let's"),
    ];

    public static IReadOnlyList<SourceFinding> Align(
        string scriptMarkdown,
        IReadOnlyList<SpokenWord> words,
        ProjectDocument project,
        string sourceId,
        string fingerprint)
    {
        var duration = project.Sources.FirstOrDefault(source => source.Id == sourceId)?.DurationSeconds
            ?? (words.Count > 0 ? words[^1].End : 0);
        var heading = WhisperWords.TokenizeScript(ScriptPassage.ReadHeading(scriptMarkdown));
        var body = WhisperWords.TokenizeScript(ScriptPassage.ReadAuthoritative(scriptMarkdown));
        var spoken = words.ToList();
        var headingStart = BodyStartsWith(body, heading) ? null : IndexOfHeading(spoken, heading);
        IReadOnlyList<SpokenWord> bodySpoken = spoken;
        var drifts = new List<ScriptDrift>();
        if (headingStart is int start)
        {
            if (start > 0)
            {
                drifts.AddRange(Diff([], spoken.Take(start).ToList(), duration));
            }

            bodySpoken = spoken.Skip(start + heading.Count).ToList();
        }

        drifts.AddRange(Diff(body, bodySpoken.ToList(), duration));
        var findings = new List<SourceFinding>();
        for (var index = 0; index < drifts.Count; index++)
        {
            var drift = drifts[index];
            findings.Add(new SourceFinding
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"drift-{index + 1:00}"),
                SourceAssetId = sourceId,
                SourceFingerprint = fingerprint,
                Start = drift.Start,
                End = drift.End,
                Kind = drift.Kind,
                TranscriptEvidence = drift.SpokenText,
                ScriptText = string.IsNullOrWhiteSpace(drift.ScriptText) ? null : drift.ScriptText,
                SpokenText = string.IsNullOrWhiteSpace(drift.SpokenText) ? null : drift.SpokenText,
                ScriptTokenIndex = drift.ScriptTokenIndex,
                Severity = drift.Severity,
                Reason = Reason(drift),
                Confidence = drift.Severity == DriftSeverity.Minor
                    ? 0.5
                    : drift.Kind == ScriptDriftKinds.Substitution ? 0.8 : 0.65,
            });
        }

        return findings;
    }

    private static bool BodyStartsWith(
        IReadOnlyList<(string Text, string Normalized)> body,
        IReadOnlyList<(string Text, string Normalized)> heading)
    {
        if (heading.Count == 0 || body.Count < heading.Count)
        {
            return false;
        }

        for (var i = 0; i < heading.Count; i++)
        {
            if (!WhisperWords.SameToken(body[i].Normalized, heading[i].Normalized))
            {
                return false;
            }
        }

        return true;
    }

    private static int? IndexOfHeading(
        IReadOnlyList<SpokenWord> spoken,
        IReadOnlyList<(string Text, string Normalized)> heading)
    {
        if (heading.Count == 0 || spoken.Count < heading.Count)
        {
            return null;
        }

        for (var i = 0; i < heading.Count; i++)
        {
            if (!WhisperWords.SameToken(spoken[i].Normalized, heading[i].Normalized))
            {
                return null;
            }
        }

        return 0;
    }

    private static List<ScriptDrift> Diff(
        IReadOnlyList<(string Text, string Normalized)> script,
        IReadOnlyList<SpokenWord> spoken,
        double sourceDuration)
    {
        var n = script.Count;
        var m = spoken.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = WhisperWords.SameToken(script[i].Normalized, spoken[j].Normalized)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var iPos = 0;
        var jPos = 0;
        var pendingScript = new List<string>();
        var pendingSpoken = new List<SpokenWord>();
        var drifts = new List<ScriptDrift>();
        double lastSpokenEnd = spoken.Count > 0 ? spoken[0].Start : 0;
        string? lastMatched = null;
        void Flush()
        {
            if (pendingScript.Count == 0 && pendingSpoken.Count == 0)
            {
                return;
            }

            var kind = pendingScript.Count > 0 && pendingSpoken.Count > 0
                ? ScriptDriftKinds.Substitution
                : pendingSpoken.Count > 0 ? ScriptDriftKinds.Insertion : ScriptDriftKinds.Omission;
            if (kind == ScriptDriftKinds.Insertion && IsRepeatTake(pendingSpoken, script))
            {
                kind = ScriptDriftKinds.Repeat;
            }

            var start = pendingSpoken.Count > 0 ? pendingSpoken[0].Start : lastSpokenEnd;
            var end = pendingSpoken.Count > 0 ? pendingSpoken[^1].End : start + 0.05;
            (start, end) = ClampRange(start, end, sourceDuration);

            var scriptText = WhisperWords.Join(pendingScript);
            var spokenText = WhisperWords.Join(pendingSpoken.Select(word => word.Text));
            var scriptTokenIndex = pendingScript.Count > 0 ? iPos - pendingScript.Count : iPos;
            drifts.Add(new ScriptDrift(
                kind,
                scriptText,
                spokenText,
                start,
                end,
                Severity(kind, scriptText, spokenText, pendingSpoken, lastMatched),
                scriptTokenIndex));
            pendingScript.Clear();
            pendingSpoken.Clear();
        }

        while (iPos < n && jPos < m)
        {
            if (WhisperWords.SameToken(script[iPos].Normalized, spoken[jPos].Normalized))
            {
                Flush();
                lastSpokenEnd = spoken[jPos].End;
                lastMatched = spoken[jPos].Normalized;
                iPos++;
                jPos++;
                continue;
            }

            if (iPos + 1 < n && jPos + 1 < m
                && WhisperWords.SameToken(script[iPos + 1].Normalized, spoken[jPos + 1].Normalized))
            {
                pendingScript.Add(script[iPos].Text);
                pendingSpoken.Add(spoken[jPos]);
                lastSpokenEnd = spoken[jPos].End;
                iPos++;
                jPos++;
                continue;
            }

            if (dp[iPos + 1, jPos] >= dp[iPos, jPos + 1])
            {
                pendingScript.Add(script[iPos].Text);
                iPos++;
            }
            else
            {
                pendingSpoken.Add(spoken[jPos]);
                lastSpokenEnd = spoken[jPos].End;
                jPos++;
            }
        }

        while (iPos < n)
        {
            pendingScript.Add(script[iPos].Text);
            iPos++;
        }

        while (jPos < m)
        {
            pendingSpoken.Add(spoken[jPos]);
            lastSpokenEnd = spoken[jPos].End;
            jPos++;
        }

        Flush();
        return drifts;
    }

    private static (double Start, double End) ClampRange(double start, double end, double duration)
    {
        if (duration <= 0)
        {
            return end > start ? (start, end) : (0, 0.05);
        }

        start = Math.Clamp(start, 0, duration);
        end = Math.Clamp(end, 0, duration);
        if (end > start)
        {
            return (start, end);
        }

        start = Math.Max(0, duration - 0.05);
        end = duration;
        if (end > start)
        {
            return (start, end);
        }

        return (0, duration);
    }

    private static string Severity(
        string kind,
        string scriptText,
        string spokenText,
        IReadOnlyList<SpokenWord> spokenWords,
        string? lastMatched)
    {
        if (IsContraction(scriptText, spokenText)
            || IsNumberAgreement(scriptText, spokenText)
            || IsRepeatedWord(kind, spokenWords, lastMatched))
        {
            return DriftSeverity.Minor;
        }

        return DriftSeverity.Rerecord;
    }

    private static bool IsContraction(string scriptText, string spokenText)
    {
        var left = JoinNormalized(scriptText);
        var right = JoinNormalized(spokenText);
        return Contractions.Any(pair =>
            (pair.Expanded == left && pair.Contracted == right)
            || (pair.Expanded == right && pair.Contracted == left));
    }

    private static bool IsRepeatTake(
        IReadOnlyList<SpokenWord> spokenWords,
        IReadOnlyList<(string Text, string Normalized)> script)
    {
        var spoken = spokenWords.Select(word => word.Normalized).Where(word => word.Length > 0).ToList();
        if (spoken.Count < 2)
        {
            return false;
        }

        var tokens = script.Select(item => item.Normalized).ToList();
        for (var i = 0; i + spoken.Count <= tokens.Count; i++)
        {
            var match = true;
            for (var j = 0; j < spoken.Count; j++)
            {
                if (!WhisperWords.SameToken(tokens[i + j], spoken[j]))
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

        return false;
    }

    private static bool IsRepeatedWord(
        string kind,
        IReadOnlyList<SpokenWord> spokenWords,
        string? lastMatched)
    {
        if (kind != ScriptDriftKinds.Insertion || spokenWords.Count != 1)
        {
            return false;
        }

        var word = spokenWords[0].Normalized;
        return string.Equals(word, lastMatched, StringComparison.Ordinal)
            || spokenWords.Count(item => item.Normalized == word) > 1;
    }

    private static bool IsNumberAgreement(string scriptText, string spokenText)
    {
        var script = ContentWords(scriptText);
        var spoken = ContentWords(spokenText);
        return script.Count == 1 && spoken.Count == 1 && SameLemma(script[0], spoken[0]);
    }

    private static List<string> ContentWords(string text) =>
        WhisperWords.TokenizeScript(text)
            .Select(item => item.Normalized)
            .Where(word => !Articles.Contains(word))
            .ToList();

    private static bool SameLemma(string left, string right)
    {
        if (left == right)
        {
            return true;
        }

        return left + "s" == right
            || right + "s" == left
            || left + "es" == right
            || right + "es" == left
            || (left.EndsWith('y') && left[..^1] + "ies" == right)
            || (right.EndsWith('y') && right[..^1] + "ies" == left);
    }

    private static string JoinNormalized(string text) =>
        string.Join(' ', WhisperWords.TokenizeScript(text).Select(item => item.Normalized));

    private static string Reason(ScriptDrift drift) =>
        drift.Kind switch
        {
            ScriptDriftKinds.Substitution => $"Said “{drift.SpokenText}”; script says “{drift.ScriptText}”.",
            ScriptDriftKinds.Insertion => $"Ad-lib not in the script: “{drift.SpokenText}”.",
            ScriptDriftKinds.Repeat => $"Repeated take: “{drift.SpokenText}”.",
            _ => $"Script has “{drift.ScriptText}” which was not said.",
        };
}

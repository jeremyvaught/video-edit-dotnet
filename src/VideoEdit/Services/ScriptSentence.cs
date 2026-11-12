using System.Text.RegularExpressions;
using VideoEdit.Models;

namespace VideoEdit.Services;

public static class ScriptSentence
{
    public static string ForFinding(string? scriptMarkdown, SourceFinding finding)
    {
        var fallback = FirstNonEmpty(finding.ScriptText, finding.SpokenText, finding.TranscriptEvidence);
        if (string.IsNullOrWhiteSpace(scriptMarkdown))
        {
            return fallback;
        }

        string body;
        try
        {
            body = ScriptPassage.ReadAuthoritative(scriptMarkdown);
        }
        catch (VideoEditException)
        {
            return fallback;
        }

        var sentences = SplitSentences(body);
        if (sentences.Count == 0)
        {
            return fallback;
        }

        if (finding.ScriptTokenIndex is int token
            && SentenceAtToken(body, token) is string located)
        {
            return located;
        }

        var needle = finding.ScriptText?.Trim();
        if (!string.IsNullOrEmpty(needle))
        {
            var hits = sentences
                .Where(sentence => sentence.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 1)
            {
                return hits[0];
            }

            if (hits.Count > 1)
            {
                throw VideoEditException.Validation(
                    "This script phrase appears more than once. Choose the sentence to record.");
            }
        }

        if (!string.IsNullOrWhiteSpace(finding.SpokenText))
        {
            var spoken = sentences.Where(sentence =>
                    sentence.Contains(finding.SpokenText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (spoken.Count == 1)
            {
                return spoken[0];
            }

            if (spoken.Count > 1)
            {
                throw VideoEditException.Validation(
                    "This script phrase appears more than once. Choose the sentence to record.");
            }
        }

        return fallback.Length > 0 ? fallback : sentences[0];
    }

    public static IReadOnlyList<string> SplitSentences(string body)
    {
        var text = Regex.Replace((body ?? "").Trim(), @"\s+", " ");
        if (text.Length == 0)
        {
            return [];
        }

        return Regex.Split(text, @"(?<=[.!?])\s+")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    public static string? SentenceAtToken(string body, int tokenIndex)
    {
        var sentences = SplitSentences(body);
        var cursor = 0;
        foreach (var sentence in sentences)
        {
            var count = WhisperWords.TokenizeScript(sentence).Count;
            if (tokenIndex >= cursor && tokenIndex < cursor + count)
            {
                return sentence;
            }

            cursor += count;
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

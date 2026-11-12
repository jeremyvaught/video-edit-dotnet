using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record RetakeProposal(
    TimeRange Range,
    string Phrase,
    string Reason,
    string RemovedText);

public static class RetakeDetector
{
    public const double MaxGapSeconds = 1.0;

    public static bool TryPropose(
        SourceFinding substitution,
        SourceFinding? insertion,
        IReadOnlyList<SpokenWord> words,
        out RetakeProposal proposal)
    {
        proposal = default!;
        if (substitution.Kind != ScriptDriftKinds.Substitution
            || insertion is null
            || insertion.Kind != ScriptDriftKinds.Insertion
            || insertion.Start < substitution.Start
            || insertion.Start - substitution.End > MaxGapSeconds + 0.000001)
        {
            return false;
        }

        var script = WhisperWords.TokenizeScript(substitution.ScriptText ?? "")
            .Select(item => item.Normalized)
            .Where(item => item.Length > 0)
            .ToList();
        script.AddRange(words
            .Where(word => word.Start >= substitution.End - 0.02 && word.End <= insertion.Start + 0.02)
            .Select(word => word.Normalized)
            .Where(item => item.Length > 0));
        if (script.Count == 0)
        {
            return false;
        }

        var spoken = words
            .Where(word => word.Start >= insertion.Start - 0.02 && word.End <= insertion.End + 0.02)
            .ToList();
        if (spoken.Count == 0 || LastIndexOf(spoken, script) is not int matchStart)
        {
            return false;
        }

        var wrong = new HashSet<string>(
            WhisperWords.TokenizeScript(substitution.SpokenText ?? "")
                .Select(item => item.Normalized)
                .Where(item => item.Length > 0),
            StringComparer.Ordinal);
        var phraseStart = matchStart;
        while (phraseStart > 0 && wrong.Contains(spoken[phraseStart - 1].Normalized))
        {
            phraseStart--;
        }

        var keepStart = spoken[phraseStart].Start;
        if (keepStart <= substitution.Start + 0.02)
        {
            return false;
        }

        var phraseWords = spoken.Skip(phraseStart).Take(matchStart + script.Count - phraseStart).ToList();
        var phrase = WhisperWords.Join(phraseWords.Select(word => word.Text)).Trim();
        if (phrase.Length == 0)
        {
            return false;
        }

        var range = new TimeRange(substitution.Start, keepStart);
        var removed = WhisperWords.Join(words
            .Where(word => word.Start >= range.Start - 0.02 && word.End <= range.End + 0.02)
            .Select(word => word.Text));
        proposal = new RetakeProposal(
            range,
            phrase,
            $"Retake of “{phrase}”.",
            string.IsNullOrWhiteSpace(removed) ? substitution.SpokenText ?? "" : removed);
        return true;
    }

    public static SourceFinding? NextInsertion(IReadOnlyList<SourceFinding> findings, SourceFinding substitution)
    {
        return findings
            .Where(item =>
                item.Id != substitution.Id
                && item.Kind == ScriptDriftKinds.Insertion
                && item.SourceAssetId == substitution.SourceAssetId
                && item.Start >= substitution.Start)
            .OrderBy(item => item.Start)
            .FirstOrDefault();
    }

    private static int? LastIndexOf(IReadOnlyList<SpokenWord> spoken, IReadOnlyList<string> needle)
    {
        for (var i = spoken.Count - needle.Count; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (!WhisperWords.SameToken(spoken[i + j].Normalized, needle[j]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return null;
    }
}

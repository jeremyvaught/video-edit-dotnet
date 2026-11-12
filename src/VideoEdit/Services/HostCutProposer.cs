using System.Globalization;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed record HostCutProposal(
    string FindingId,
    string EditId,
    TimeRange Range,
    string Kind,
    string Reason,
    double Confidence,
    string TranscriptEvidence);

public static class HostCutProposer
{
    public const double MinPauseSeconds = 1.0;
    public const double MinRollSeconds = 0.3;

    public static IReadOnlyList<HostCutProposal> Propose(
        VadSpeechMap map,
        double durationSeconds,
        EditingPolicy? policy = null,
        IReadOnlyList<SpokenWord>? words = null)
    {
        policy ??= EditingPolicy.Default;
        var segments = map.Segments
            .Where(item => item.End > item.Start)
            .OrderBy(item => item.Start)
            .ToList();
        if (segments.Count == 0 || durationSeconds <= 0)
        {
            return [];
        }

        var proposals = new List<HostCutProposal>();
        var firstSpeech = segments[0].Start;
        var lastSpeech = segments[^1].End;
        var prerollEnd = Round(Math.Max(0, firstSpeech - policy.MarginAfterS));
        if (prerollEnd >= MinRollSeconds)
        {
            proposals.Add(Proposal(
                "preroll",
                new TimeRange(0, prerollEnd),
                EditKinds.Preroll,
                "Pre-roll dead air before first speech. Ends before the opening line so the first word is not clipped.",
                0.9,
                Evidence(words, 0, prerollEnd)));
        }

        for (var i = 0; i < segments.Count - 1; i++)
        {
            var gapStart = segments[i].End;
            var gapEnd = segments[i + 1].Start;
            var gap = gapEnd - gapStart;
            if (gap < MinPauseSeconds)
            {
                continue;
            }

            var cut = new TimeRange(Round(gapStart), Round(gapEnd - policy.PauseTargetS));
            if (cut.Duration <= 0.05)
            {
                continue;
            }

            var id = string.Create(CultureInfo.InvariantCulture, $"pause-{cut.Start:0}");
            proposals.Add(Proposal(
                id,
                cut,
                EditKinds.Pause,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pause of {gap:0.##}s shortened to {policy.PauseTargetS:0.##}s. Residual pause is kept."),
                0.85,
                Evidence(words, cut.Start, cut.End)));
        }

        var postrollStart = Math.Min(Round(lastSpeech), durationSeconds);
        if (durationSeconds - lastSpeech >= MinRollSeconds && durationSeconds > postrollStart)
        {
            proposals.Add(Proposal(
                "postroll",
                new TimeRange(postrollStart, durationSeconds),
                EditKinds.Postroll,
                "Post-roll after the last spoken line.",
                0.9,
                Evidence(words, lastSpeech, durationSeconds)));
        }

        return proposals;
    }

    private static HostCutProposal Proposal(
        string findingId,
        TimeRange range,
        string kind,
        string reason,
        double confidence,
        string evidence) =>
        new(findingId, "e-" + findingId, range, kind, reason, confidence, evidence);

    private static string Evidence(IReadOnlyList<SpokenWord>? words, double start, double end)
    {
        if (words is null || words.Count == 0)
        {
            return "";
        }

        return WhisperWords.Join(
            words.Where(word => word.End > start && word.Start < end).Select(word => word.Text));
    }

    private static double Round(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

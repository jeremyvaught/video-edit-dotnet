namespace VideoEdit.Services;

public static class RoomTone
{
    public static SilenceInterval Select(IReadOnlyList<SilenceInterval> silences, double maxDurationSeconds)
    {
        var candidate = silences
            .Where(item => item.Duration > 0 && item.Duration <= maxDurationSeconds)
            .OrderByDescending(item => item.Duration)
            .ThenBy(item => item.Start)
            .FirstOrDefault();
        if (candidate is null)
        {
            throw VideoEditException.Validation(
                "No room-tone silence is shorter than the policy maximum. Check the diagnosis map.");
        }

        return candidate;
    }
}

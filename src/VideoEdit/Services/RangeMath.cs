using VideoEdit.Models;

namespace VideoEdit.Services;

public static class RangeMath
{
    public static bool RangesOverlap(IReadOnlyList<TimeRange> ranges)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = i + 1; j < ranges.Count; j++)
            {
                if (ranges[i].Overlaps(ranges[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static IReadOnlyList<TimeRange> Subtract(TimeRange source, IEnumerable<TimeRange> removals)
    {
        var remaining = new List<TimeRange> { source };
        foreach (var removal in removals.OrderBy(range => range.Start))
        {
            var next = new List<TimeRange>();
            foreach (var piece in remaining)
            {
                var overlap = piece.Intersect(removal);
                if (overlap is null)
                {
                    next.Add(piece);
                    continue;
                }

                if (piece.Start < overlap.Value.Start)
                {
                    next.Add(new TimeRange(piece.Start, overlap.Value.Start));
                }

                if (overlap.Value.End < piece.End)
                {
                    next.Add(new TimeRange(overlap.Value.End, piece.End));
                }
            }

            remaining = next;
        }

        return remaining;
    }

    public static IReadOnlyList<TimeRange> KeepRanges(
        IEnumerable<TimeRange> selections,
        IEnumerable<TimeRange> removals)
    {
        var removalList = removals.ToList();
        return selections
            .OrderBy(range => range.Start)
            .SelectMany(selection => Subtract(selection, removalList))
            .ToList();
    }

    public static double TotalDuration(IEnumerable<TimeRange> ranges) =>
        ranges.Sum(range => range.Duration);

    public static IReadOnlyList<TimeRange> SliceOutput(
        IReadOnlyList<TimeRange> donorKeeps,
        double outputStart,
        double outputDuration)
    {
        if (outputDuration <= 0 || donorKeeps.Count == 0)
        {
            return [];
        }

        var window = new TimeRange(Math.Max(0, outputStart), Math.Max(0, outputStart) + outputDuration);
        var sliced = new List<TimeRange>();
        var cursor = 0.0;
        foreach (var keep in donorKeeps)
        {
            var piece = new TimeRange(cursor, cursor + keep.Duration);
            if (piece.Intersect(window) is TimeRange taken)
            {
                sliced.Add(new TimeRange(
                    keep.Start + (taken.Start - cursor),
                    keep.Start + (taken.End - cursor)));
            }

            cursor += keep.Duration;
            if (cursor >= window.End - 0.000001)
            {
                break;
            }
        }

        return sliced;
    }

    public static IReadOnlyList<TimeRange> CapDuration(IReadOnlyList<TimeRange> ranges, double maxDuration)
    {
        if (maxDuration <= 0)
        {
            return [];
        }

        var kept = new List<TimeRange>();
        var used = 0.0;
        foreach (var range in ranges)
        {
            var remaining = maxDuration - used;
            if (remaining <= 0.000001)
            {
                break;
            }

            if (range.Duration <= remaining + 0.000001)
            {
                kept.Add(range);
                used += range.Duration;
                continue;
            }

            kept.Add(new TimeRange(range.Start, range.Start + remaining));
            break;
        }

        return kept;
    }
}

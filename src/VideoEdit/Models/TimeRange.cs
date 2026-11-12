using System.Globalization;

namespace VideoEdit.Models;

public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;

    public bool IsValid => End > Start && !double.IsNaN(Start) && !double.IsNaN(End);

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public bool Contains(TimeRange other) => Start <= other.Start && other.End <= End;

    public TimeRange? Intersect(TimeRange other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return end > start ? new TimeRange(start, end) : null;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Start:0.######}, {End:0.######})");
}

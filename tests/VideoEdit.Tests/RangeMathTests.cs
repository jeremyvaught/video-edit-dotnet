using VideoEdit.Models;
using VideoEdit.Services;

namespace VideoEdit.Tests;

public class RangeMathTests
{
    [Fact]
    public void AdjacentHalfOpenRangesDoNotOverlap()
    {
        var ranges = new[] { new TimeRange(0, 5), new TimeRange(5, 10) };
        Assert.False(RangeMath.RangesOverlap(ranges));
    }

    [Fact]
    public void OverlappingRangesAreDetected()
    {
        var ranges = new[] { new TimeRange(0, 6), new TimeRange(5, 10) };
        Assert.True(RangeMath.RangesOverlap(ranges));
    }

    [Fact]
    public void SubtractRemovesTheInteriorAndKeepsEdges()
    {
        var keep = RangeMath.Subtract(new TimeRange(0, 20), [new TimeRange(5, 8)]);
        Assert.Equal([new TimeRange(0, 5), new TimeRange(8, 20)], keep);
        Assert.Equal(17, RangeMath.TotalDuration(keep));
    }

    [Fact]
    public void SubtractIgnoresRemovalsOutsideTheSource()
    {
        var keep = RangeMath.Subtract(new TimeRange(10, 15), [new TimeRange(0, 3), new TimeRange(18, 20)]);
        Assert.Equal([new TimeRange(10, 15)], keep);
    }

    [Fact]
    public void CapDurationTrimsTheLastKeepToTheBudget()
    {
        var kept = RangeMath.CapDuration(
            [new TimeRange(51.82, 54.0), new TimeRange(54.6, 56.5)],
            3.36);
        Assert.Equal(51.82, kept[0].Start, 5);
        Assert.Equal(54.0, kept[0].End, 5);
        Assert.Equal(54.6, kept[1].Start, 5);
        Assert.Equal(55.78, kept[1].End, 5);
        Assert.Equal(3.36, RangeMath.TotalDuration(kept), 5);
    }
}

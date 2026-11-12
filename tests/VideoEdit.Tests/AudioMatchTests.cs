using VideoEdit.Services;

namespace VideoEdit.Tests;

public class AudioMatchTests
{
    [Fact]
    public void GainClosesTheMeasuredLoudnessGap()
    {
        Assert.Equal(-4, AudioLoudness.GainDb(-24.5, -28.5), 6);
        Assert.Equal(-28.5, AudioLoudness.Average([-28.4, -28.6]), 6);
        Assert.True(AudioLoudness.WithinTolerance(-28.2, -28.5, 1));
        Assert.False(AudioLoudness.WithinTolerance(-26.0, -28.5, 1));
    }

    [Fact]
    public void ParseIntegratedReadsEbur128Summary()
    {
        var log = """
            [Parsed_ebur128_0 @ 0x1] t: 0.1 I: -70.0 LUFS
            [Parsed_ebur128_0 @ 0x1] Summary:
              Integrated loudness:
                I:         -28.5 LUFS
                Threshold: -39.8 LUFS
            """;
        Assert.Equal(-28.5, AudioLoudness.ParseIntegrated(log));
    }

    [Fact]
    public void RoomBedGainMatchesTheMeasuredHostFloor()
    {
        var log = """
            [Parsed_astats_0 @ 0x1] RMS level dB: -75.612
            [Parsed_astats_0 @ 0x1] RMS level dB: -75.612
            """;
        Assert.Equal(-75.612, AudioLoudness.ParseRmsDb(log), 3);
        Assert.Equal(0.61, AudioLoudness.FloorGainDb(-75.61, -75));
        Assert.Equal(0, AudioLoudness.FloorGainDb(-75, -75));
        Assert.Equal(-25, AudioLoudness.FloorGainDb(-50, -75));
    }

    [Fact]
    public void HostNoiseFloorIsTheQuietFifthOfVadGaps()
    {
        var sampleRate = 16000;
        var window = (int)(RoomFloor.WindowSeconds * sampleRate);
        var samples = new short[window * 20];
        var quiet = (short)Math.Round(32768 * Math.Pow(10, -76 / 20.0));
        var loud = (short)Math.Round(32768 * Math.Pow(10, -40 / 20.0));
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i < window * 19 ? quiet : loud;
        }

        var floor = RoomFloor.NoiseFloorDb(samples, sampleRate, [new SilenceInterval(0, 4)]);
        Assert.InRange(floor, -77, -75);
        Assert.Equal(-79.2, AudioLoudness.Percentile([-80, -76, -75, -74, -40], 5), 6);
    }

    [Fact]
    public void RoomToneSkipsAbandonedDeadAirOverThePolicyMaximum()
    {
        var room = RoomTone.Select(
            [
                new SilenceInterval(187.390, 207.936),
                new SilenceInterval(177.093, 184.969),
                new SilenceInterval(15.451, 21.714),
            ],
            maxDurationSeconds: 10);

        Assert.Equal(177.093, room.Start, 3);
        Assert.Equal(184.969, room.End, 3);
    }

    [Fact]
    public void PolicyDefaultsKeepAOneLuTolerance()
    {
        Assert.Equal(1, EditingPolicy.Default.LoudnessToleranceLu);
        Assert.Equal(10, EditingPolicy.Default.RoomToneMaxS);
    }
}

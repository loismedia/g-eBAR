using GeBar.App;

namespace GeBar.Tests;

public class BatteryHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 16, 0, 0, TimeSpan.FromHours(3));

    private static BatteryPoint[] Discharge(int from, int to, TimeSpan step, bool? charging = false) =>
        [.. Enumerable.Range(0, from - to + 1).Select(index => new BatteryPoint(Start + step * index, from - index, charging))];

    [Theory]
    [InlineData(60, 7 * 24 * 60)]
    [InlineData(15, 24 * 60 * 4)]
    public void ReadingHistoryKeepsTheWholeRequestedPeriod(int seconds, int samples)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            DateTimeOffset first = DateTimeOffset.Now.AddSeconds(-(long)seconds * (samples - 1));
            File.WriteAllLines(Path.Combine(directory, "test.log"), Enumerable.Range(0, samples).Select(index =>
                $"{first.AddSeconds((long)seconds * index):O} [INF] Device state: name=Mouse, id=mouse, status=DeviceConnected, battery=50, charging=no"));

            IReadOnlyList<BatteryPoint> points = BatteryHistory.Read("mouse", "Mouse", TimeSpan.FromDays(7), directory);

            Assert.Equal(samples, points.Count);
            Assert.Equal(first, points[0].Time);
            Assert.Equal(first.AddSeconds((long)seconds * (samples - 1)), points[^1].Time);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EstimatesFromSteadyDischarge()
    {
        BatteryPoint[] points = Discharge(90, 80, TimeSpan.FromMinutes(30));

        TimeSpan? remaining = BatteryHistory.EstimateRemaining(points);

        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value.TotalHours, 35, 40);
    }

    [Fact]
    public void NoEstimateWhileCharging()
    {
        BatteryPoint[] points = [.. Discharge(90, 80, TimeSpan.FromMinutes(30)), new(Start + TimeSpan.FromHours(6), 81, true)];

        Assert.Null(BatteryHistory.EstimateRemaining(points));
    }

    [Fact]
    public void NoEstimateFromTooShortStretch()
    {
        Assert.Null(BatteryHistory.EstimateRemaining(Discharge(90, 85, TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void PreviousDischargeRateIsUsedImmediatelyAfterCharging()
    {
        BatteryPoint[] points = [.. Discharge(90, 80, TimeSpan.FromMinutes(30)),
            new(Start.AddHours(6), 90, true), new(Start.AddHours(7), 100, true),
            new(Start.AddHours(8), 100, false), new(Start.AddHours(8).AddMinutes(5), 99, false)];

        Assert.Equal(TimeSpan.FromHours(48), BatteryHistory.EstimateRemaining(points));
    }

    [Fact]
    public void ReliableNewDischargeReplacesThePreviousRate()
    {
        BatteryPoint[] points = [.. Discharge(90, 80, TimeSpan.FromMinutes(30)),
            new(Start.AddHours(6), 90, true), new(Start.AddHours(7), 100, true),
            .. Discharge(100, 94, TimeSpan.FromMinutes(5)).Select(point => point with { Time = point.Time.AddHours(8) })];

        Assert.Equal(TimeSpan.FromHours(91d / 12), BatteryHistory.EstimateRemaining(points));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public void BatteryRiseDoesNotEraseThePreviousDischargeRate(int gapMinutes)
    {
        DateTimeOffset resumed = Start.AddHours(5).AddMinutes(gapMinutes);
        BatteryPoint[] points = [.. Discharge(90, 80, TimeSpan.FromMinutes(30)),
            new(resumed, 99, false), new(resumed.AddMinutes(5), 98, false)];

        Assert.Equal(TimeSpan.FromHours(47.5), BatteryHistory.EstimateRemaining(points));
    }

    [Fact]
    public void ShortCyclesSeparatedByChargingAreNotCombined()
    {
        BatteryPoint[] points = [new(Start, 80, false), new(Start.AddMinutes(20), 77, false),
            new(Start.AddMinutes(40), 90, true), new(Start.AddMinutes(60), 100, true),
            new(Start.AddMinutes(80), 99, false), new(Start.AddMinutes(100), 96, false)];

        Assert.Null(BatteryHistory.EstimateRemaining(points));
    }

    [Fact]
    public void SleepGapIsNotCountedAsDischarge()
    {
        BatteryPoint[] evening = Discharge(90, 80, TimeSpan.FromMinutes(30));
        BatteryPoint[] withNight = [.. evening[..9], .. evening[9..].Select(point => point with { Time = point.Time + TimeSpan.FromHours(8) })];

        Assert.Equal(BatteryHistory.EstimateRemaining(evening), BatteryHistory.EstimateRemaining(withNight));
    }

    [Fact]
    public void ChargingFlagWithoutRiseIsDropped()
    {
        BatteryPoint[] points = [new(Start, 50, false), new(Start.AddMinutes(5), 50, true), new(Start.AddMinutes(10), 49, true), new(Start.AddMinutes(15), 49, false)];

        Assert.All(BatteryHistory.Analysed(points), point => Assert.NotEqual(true, point.Charging));
    }

    [Fact]
    public void ChargingFlagWithRiseIsKept()
    {
        BatteryPoint[] points = [new(Start, 50, false), new(Start.AddMinutes(5), 51, true), new(Start.AddMinutes(10), 53, true)];

        Assert.Equal([false, true, true], BatteryHistory.Analysed(points).Select(point => point.Charging));
    }
}

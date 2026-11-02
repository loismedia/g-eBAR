using GeBar.App;

namespace GeBar.Tests;

public class BatteryHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 16, 0, 0, TimeSpan.FromHours(3));

    private static BatteryPoint[] Discharge(int from, int to, TimeSpan step, bool? charging = false) =>
        [.. Enumerable.Range(0, from - to + 1).Select(index => new BatteryPoint(Start + step * index, from - index, charging))];

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

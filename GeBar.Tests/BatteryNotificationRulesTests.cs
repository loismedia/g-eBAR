using GeBar.Core;

namespace GeBar.Tests;

public class BatteryNotificationRulesTests
{
    private const int Warning = 20;
    private const int Critical = 10;

    private static IReadOnlyList<BatteryNotificationKind> Feed(BatteryNotificationRules rules, int percentage, bool charging = false) =>
        rules.Evaluate(percentage, charging, Warning, Critical);

    [Fact]
    public void WarningFiresOnceUntilBatteryRecovers()
    {
        var rules = new BatteryNotificationRules();

        Assert.Empty(Feed(rules, 30));
        Assert.Equal([BatteryNotificationKind.Warning], Feed(rules, 20));
        Assert.Empty(Feed(rules, 19));
        Assert.Empty(Feed(rules, 22));
        Assert.Empty(Feed(rules, 26));
        Assert.Equal([BatteryNotificationKind.Warning], Feed(rules, 20));
    }

    [Fact]
    public void CriticalSkipsWarningWhenBatteryFallsFast()
    {
        var rules = new BatteryNotificationRules();

        Assert.Equal([BatteryNotificationKind.Critical], Feed(rules, 9));
        Assert.Empty(Feed(rules, 15));
    }

    [Fact]
    public void ChargingStartedNeedsRealRise()
    {
        var rules = new BatteryNotificationRules();

        Assert.Empty(Feed(rules, 40, charging: true));
        Assert.Empty(Feed(rules, 40, charging: true));
        Assert.Equal([BatteryNotificationKind.ChargingStarted], Feed(rules, 41, charging: true));
        Assert.Empty(Feed(rules, 42, charging: true));
    }

    [Fact]
    public void FullyChargedOnlyAfterCharging()
    {
        var rules = new BatteryNotificationRules();

        Assert.Empty(Feed(rules, 100));
        Feed(rules, 99, charging: true);
        Assert.Contains(BatteryNotificationKind.FullyCharged, Feed(rules, 100, charging: true));
        Assert.Empty(Feed(rules, 100));
    }

    [Fact]
    public void NoLowBatteryNoticeWhileCharging()
    {
        var rules = new BatteryNotificationRules();

        Assert.DoesNotContain(BatteryNotificationKind.Critical, Feed(rules, 5, charging: true));
        Assert.DoesNotContain(BatteryNotificationKind.Warning, Feed(rules, 15, charging: true));
    }
}

namespace GeBar.Core;

public enum BatteryNotificationKind { Warning, Critical, ChargingStarted, FullyCharged }
public sealed class BatteryNotificationRules
{
    private bool _warningShown;
    private bool _criticalShown;
    private bool _fullShown;
    private bool _sawCharging;
    private int? _chargingStart;
    private bool _chargingNotified;
    private const int Hysteresis = 5;
    public IReadOnlyList<BatteryNotificationKind> Evaluate(int percentage, bool charging, int warning, int critical)
    {
        var result = new List<BatteryNotificationKind>(3);
        if (!charging) { _chargingStart = null; _chargingNotified = false; }
        else { _chargingStart ??= percentage; _sawCharging = true; }
        bool chargingStarted = charging && !_chargingNotified && _chargingStart is int start && percentage > start;
        if (chargingStarted) _chargingNotified = true;
        if (charging || percentage > warning + Hysteresis) _warningShown = false;
        if (charging || percentage > critical + Hysteresis) _criticalShown = false;
        if (percentage < 100) _fullShown = false;
        if (!charging)
        {
            if (percentage <= critical && !_criticalShown) { _criticalShown = true; _warningShown = true; result.Add(BatteryNotificationKind.Critical); }
            else if (percentage <= warning && !_warningShown) { _warningShown = true; result.Add(BatteryNotificationKind.Warning); }
        }
        if (chargingStarted && percentage < 100) result.Add(BatteryNotificationKind.ChargingStarted);
        if (percentage >= 100 && !_fullShown && _sawCharging) { _fullShown = true; _sawCharging = false; result.Add(BatteryNotificationKind.FullyCharged); }
        return result;
    }
}

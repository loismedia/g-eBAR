namespace GeBar.Core;

public static class HidBatteryUsages
{
    public const uint BatteryStrength = 0x0006_0020;
    public const uint RelativeStateOfCharge = 0x0085_0064;
    public const uint AbsoluteStateOfCharge = 0x0085_0065;
    public const uint RemainingCapacity = 0x0085_0066;
    public const uint FullChargeCapacity = 0x0085_0067;
    public const uint Charging = 0x0085_0044;
    public const uint Discharging = 0x0085_0045;
    public const uint AcPresent = 0x0085_00D0;
    public const uint BatteryPresent = 0x0085_00D1;

    public static bool IsPercentageUsage(uint usage) =>
        usage is BatteryStrength or RelativeStateOfCharge or AbsoluteStateOfCharge;

    public static bool IsChargingUsage(uint usage) => usage is Charging or AcPresent;

    public static ushort Page(uint usage) => (ushort)(usage >> 16);

    public static ushort Id(uint usage) => (ushort)(usage & 0xFFFF);

    public static string Describe(uint usage) => usage switch
    {
        BatteryStrength => "Generic Device Controls / Battery Strength",
        RelativeStateOfCharge => "Battery System / RelativeStateOfCharge",
        AbsoluteStateOfCharge => "Battery System / AbsoluteStateOfCharge",
        RemainingCapacity => "Battery System / RemainingCapacity",
        FullChargeCapacity => "Battery System / FullChargeCapacity",
        Charging => "Battery System / Charging",
        Discharging => "Battery System / Discharging",
        AcPresent => "Battery System / ACPresent",
        BatteryPresent => "Battery System / BatteryPresent",
        _ => $"Usage {Page(usage):X4}:{Id(usage):X4}"
    };
}

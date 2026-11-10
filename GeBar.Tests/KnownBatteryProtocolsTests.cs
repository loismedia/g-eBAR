using GeBar.Core;
using GeBar.Hid;

namespace GeBar.Tests;

public class KnownBatteryProtocolsTests
{
    private const string RyDongle = "FEA status 0xF7 (RY5088 dongle)";
    private const string WaizowlDongle = "Waizowl status 0x41 (OGM family)";

    [Fact]
    public void AllZeroPacketIsNeverAccepted()
    {
        foreach (BatteryCommand command in KnownBatteryProtocols.All)
            Assert.False(KnownBatteryProtocols.Accepts(new byte[65], command), command.Name);
    }

    [Fact]
    public void ProtocolNamesAreUnique()
    {
        Assert.Equal(KnownBatteryProtocols.All.Count, KnownBatteryProtocols.All.Select(command => command.Name).Distinct().Count());
    }

    [Fact]
    public void NormalizeDropsSleepByteStoredAsCharging()
    {
        HidDeviceProfile stored = Device(new HidBatteryProtocolProfile(ChargingOffset: 4, ChargingValue: 1, ProtocolName: RyDongle));

        HidBatteryProtocolProfile battery = KnownBatteryProtocols.Normalize(stored).Battery;

        Assert.Null(battery.ChargingOffset);
        Assert.Null(battery.ChargingValue);
    }

    [Fact]
    public void NormalizeKeepsExplicitChargingByte()
    {
        HidDeviceProfile stored = Device(new HidBatteryProtocolProfile(ChargingOffset: 9, ChargingValue: 3, ProtocolName: WaizowlDongle));

        HidBatteryProtocolProfile battery = KnownBatteryProtocols.Normalize(stored).Battery;

        Assert.Equal(9, battery.ChargingOffset);
        Assert.Equal(3, battery.ChargingValue);
    }

    [Fact]
    public void NormalizeLeavesUnknownProtocolAlone()
    {
        var original = new HidBatteryProtocolProfile(ChargingOffset: 4, ProtocolName: "learned by hand");

        Assert.Same(original, KnownBatteryProtocols.Normalize(Device(original)).Battery);
    }

    [Fact]
    public void NormalizeCoversEveryConnection()
    {
        var extra = new HidDeviceConnectionProfile(0x1915, 0x07A2, null, null, [], new HidBatteryProtocolProfile(ProtocolName: WaizowlDongle));
        HidDeviceProfile stored = Device(new HidBatteryProtocolProfile(ProtocolName: WaizowlDongle)) with { Connections = [extra] };

        HidDeviceProfile normalized = KnownBatteryProtocols.Normalize(stored);

        Assert.All(normalized.AllConnections(), connection =>
        {
            Assert.Equal(6, connection.Battery.ChargingOffset);
            Assert.Equal(1, connection.Battery.ChargingValue);
        });
    }

    private static HidDeviceProfile Device(HidBatteryProtocolProfile battery) =>
        new("device", "Device", 0x1915, 0x0728, null, null, [], battery);
}

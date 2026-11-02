using GeBar.Core;
using GeBar.Hid;

namespace GeBar.Tests;

public class KnownBatteryProtocolsTests
{
    private const string RyDongle = "FEA status 0xF7 (RY5088 dongle)";
    private const string WaizowlDongle = "Waizowl status 0x41 (OGM family)";
    private const string WaizowlWired = "Waizowl status 0x01 (OGM wired)";
    private const string LogitechGaming = "Logitech Gaming 0x51";

    private static BatteryCommand Command(string name) => KnownBatteryProtocols.Find(name) ?? throw new InvalidOperationException(name);

    private static byte[] Packet(string hex, int length = 65) => Convert.FromHexString(hex.PadRight(length * 2, '0'));

    [Theory]
    [InlineData("00002C0000010101FA", 44, false)]
    [InlineData("00002C0001010101FA", 44, true)]
    public void MonsgeekDongleReportsSleepNotCharging(string hex, int percentage, bool asleep)
    {
        BatteryCommand command = Command(RyDongle);
        byte[] response = Packet(hex);

        Assert.True(KnownBatteryProtocols.Accepts(response, command));
        Assert.Equal(percentage, response[command.PercentageOffset]);
        Assert.Equal(asleep, KnownBatteryProtocols.IsAsleep(response, command));
        Assert.Null(command.ChargingOffset);
    }

    [Theory]
    [InlineData(WaizowlDongle, "0041008C01000033000200060", 51, false)]
    [InlineData(WaizowlDongle, "0041008C0100013C000200060", 60, true)]
    [InlineData(WaizowlWired, "0001008C0100013E00020303", 62, true)]
    public void WaizowlBatteryPacketsAreRead(string protocol, string hex, int percentage, bool charging)
    {
        BatteryCommand command = Command(protocol);
        byte[] response = Packet(hex);

        Assert.True(KnownBatteryProtocols.Accepts(response, command));
        Assert.Equal(percentage, response[command.PercentageOffset]);
        Assert.Equal(charging, response[command.ChargingOffset!.Value] == command.ChargingValue);
    }

    [Fact]
    public void WaizowlDongleVersionPacketIsNotBattery()
    {
        Assert.False(KnownBatteryProtocols.Accepts(Packet("0001008C0102151928070601F507"), Command(WaizowlWired)));
    }

    [Fact]
    public void WaizowlSpontaneousStatusIsNotBattery()
    {
        Assert.False(KnownBatteryProtocols.Accepts(Packet("001F008101"), Command(WaizowlDongle)));
        Assert.False(KnownBatteryProtocols.Accepts(Packet("001F008101"), Command(WaizowlWired)));
    }

    [Fact]
    public void LogitechErrorMeansHeadsetIsOff()
    {
        BatteryCommand command = Command(LogitechGaming);

        Assert.True(command.Refusal!(Packet("510500FF031A0B", 64)));
        Assert.False(command.Refusal!(Packet("510B000310000600040A27270200", 64)));
    }

    [Fact]
    public void LogitechAnswerIsRead()
    {
        BatteryCommand command = Command(LogitechGaming);
        byte[] response = Packet("510B000310000600040A27270200", 64);

        Assert.True(KnownBatteryProtocols.Accepts(response, command));
        Assert.Equal(39, response[command.PercentageOffset]);
        Assert.Equal(command.ChargingValue, response[command.ChargingOffset!.Value]);
    }

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
    public void NormalizeFillsChargingByteMissingFromOldProfile()
    {
        HidDeviceProfile stored = Device(new HidBatteryProtocolProfile(PercentageOffset: 7, ProtocolName: WaizowlDongle));

        HidBatteryProtocolProfile battery = KnownBatteryProtocols.Normalize(stored).Battery;

        Assert.Equal(6, battery.ChargingOffset);
        Assert.Equal(1, battery.ChargingValue);
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

        Assert.All(normalized.AllConnections(), connection => Assert.Equal(6, connection.Battery.ChargingOffset));
    }

    private static HidDeviceProfile Device(HidBatteryProtocolProfile battery) =>
        new("device", "Device", 0x1915, 0x0728, null, null, [], battery);
}

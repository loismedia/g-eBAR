using GeBar.Core;
using GeBar.Hid;

namespace GeBar.Tests;

public class BatteryReplyTests
{
    private static byte[] Packet(string hex, int length = 65) => Convert.FromHexString(hex.PadRight(length * 2, '0'));

    private static HidBatteryProtocolProfile Profile(string protocol)
    {
        BatteryCommand command = KnownBatteryProtocols.Find(protocol)!;
        return new(Source: HidBatterySource.VendorProtocol, PercentageOffset: command.PercentageOffset, ProtocolName: protocol);
    }

    [Theory]
    [InlineData("Waizowl status 0x41 (OGM family)", "0041008C01000033000200060", 51, false)]
    [InlineData("Waizowl status 0x41 (OGM family)", "0041008C0100013C000200060", 60, true)]
    [InlineData("Waizowl status 0x01 (OGM wired)", "0001008C0100013E00020303", 62, true)]
    [InlineData("Logitech Gaming 0x51", "510B000310000600040A27270200", 39, true)]
    [InlineData("FEA status 0xF7 (RY5088 dongle)", "00002C0000010101FA", 44, null)]
    public void BatteryRepliesDecodePercentageAndCharging(string protocol, string hex, int percentage, bool? charging)
    {
        HidReplay replay = HidBatteryReader.ParseReply(Packet(hex), Profile(protocol));

        Assert.True(replay.Answered);
        Assert.NotNull(replay.Reading);
        Assert.Equal(percentage, replay.Reading.Percentage);
        Assert.Equal(charging, replay.Reading.IsCharging);
    }

    [Theory]
    [InlineData("Waizowl status 0x41 (OGM family)", "001F008101")]
    [InlineData("Waizowl status 0x01 (OGM wired)", "001F008101")]
    [InlineData("Waizowl status 0x01 (OGM wired)", "0001008C0102151928070601F507")]
    [InlineData("Waizowl status 0x41 (OGM family)", "0041")]
    [InlineData("Waizowl status 0x41 (OGM family)", "0041008C01000000000200060")]
    public void InvalidWaizowlRepliesDoNotMeanSleep(string protocol, string hex)
    {
        HidReplay replay = HidBatteryReader.ParseReply(Packet(hex), Profile(protocol));

        Assert.False(replay.Answered);
        Assert.Null(replay.Reading);
    }

    [Fact]
    public void MonsgeekSleepKeepsLastKnownBattery()
    {
        var profile = new HidBatteryProtocolProfile(Source: HidBatterySource.VendorProtocol, PercentageOffset: 2, ProtocolName: "FEA status 0xF7 (RY5088 dongle)");

        HidReplay replay = HidBatteryReader.ParseReply(Packet("00002C0001010101FA"), profile);

        Assert.True(replay.Answered);
        Assert.Null(replay.Reading);
        Assert.Equal(44, replay.LastKnown);
    }

    [Fact]
    public void TruncatedFeatureReplyDoesNotMeanSleep()
    {
        var profile = new HidBatteryProtocolProfile(Source: HidBatterySource.VendorProtocol, PercentageOffset: 2, ProtocolName: "FEA status 0xF7 (RY5088 dongle)");

        HidReplay replay = HidBatteryReader.ParseReply([0, 0, 44], profile);

        Assert.False(replay.Answered);
        Assert.Null(replay.Reading);
    }

    [Fact]
    public void LogitechExplicitRefusalStillMeansSleep()
    {
        var profile = new HidBatteryProtocolProfile(Source: HidBatterySource.VendorProtocol, PercentageOffset: 10, ProtocolName: "Logitech Gaming 0x51");

        HidReplay replay = HidBatteryReader.ParseReply(Packet("510500FF031A0B", 64), profile);

        Assert.True(replay.Answered);
        Assert.Null(replay.Reading);
    }
}

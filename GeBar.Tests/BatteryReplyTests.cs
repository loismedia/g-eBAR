using GeBar.Core;
using GeBar.Hid;

namespace GeBar.Tests;

public class BatteryReplyTests
{
    private static byte[] Packet(string hex, int length = 65) => Convert.FromHexString(hex.PadRight(length * 2, '0'));

    private static HidBatteryProtocolProfile Profile(string protocol) =>
        new(Source: HidBatterySource.VendorProtocol, PercentageOffset: 7, ProtocolName: protocol);

    [Fact]
    public void WaizowlVersionReplyDoesNotMeanSleep()
    {
        HidReplay replay = HidBatteryReader.ParseReply(Packet("0001008C0102151928070601F507"), Profile("Waizowl status 0x01 (OGM wired)"));

        Assert.False(replay.Answered);
        Assert.Null(replay.Reading);
        Assert.False(KnownBatteryProtocols.IsSleepResponse(Packet("0001008C0102151928070601F507"), KnownBatteryProtocols.Find("Waizowl status 0x01 (OGM wired)")!));
    }

    [Theory]
    [InlineData("0041008C0100004A000200060", 74)]
    [InlineData("0041008C0100004C000200060", 76)]
    public void WaizowlBatteryReplyFromLogIsConnected(string hex, int percentage)
    {
        HidReplay replay = HidBatteryReader.ParseReply(Packet(hex), Profile("Waizowl status 0x41 (OGM family)"));

        Assert.True(replay.Answered);
        Assert.NotNull(replay.Reading);
        Assert.Equal(percentage, replay.Reading.Percentage);
    }

    [Theory]
    [InlineData("001F008101")]
    [InlineData("0041")]
    [InlineData("0041008C01000000000200060")]
    public void InvalidWaizowlRepliesDoNotMeanSleep(string hex)
    {
        HidReplay replay = HidBatteryReader.ParseReply(Packet(hex), Profile("Waizowl status 0x41 (OGM family)"));

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

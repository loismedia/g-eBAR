using GeBar.Core;

namespace GeBar.Tests;

public class HidProfileValidationTests
{
    private static readonly HidInterfaceProfile Interface = new(@"\\?\hid#vid_1915&pid_0728&mi_03", 65, 65, 0);

    private static HidDeviceProfile Device(HidBatteryProtocolProfile? battery = null) =>
        new("ogm", "OGM", 0x1915, 0x0728, null, null, [Interface], battery ?? new HidBatteryProtocolProfile(RequestHex: "0041", PercentageOffset: 7));

    private static HidBatteryFieldSpec Field(int reportLength = 8, int bitOffset = 8, int elementBits = 8) =>
        new(HidReportKind.Input, 1, reportLength, bitOffset, 0, elementBits, 0, 100, HidBatteryUsages.BatteryStrength);

    [Fact]
    public void ValidProfilePasses() => Assert.Null(Device().Problem());

    [Fact]
    public void ExportFromNewerFormatIsRejected()
    {
        var export = new HidProfileExport(HidProfileValidation.SupportedFormatVersion + 1, DateTimeOffset.Now, []);

        Assert.NotNull(export.Problem());
    }

    [Fact]
    public void MissingNameIsRejected() => Assert.NotNull((Device() with { Name = " " }).Problem());

    [Fact]
    public void VendorOutOfRangeIsRejected() => Assert.NotNull((Device() with { VendorId = 0x1_0000 }).Problem());

    [Fact]
    public void OddHexIsRejected() => Assert.NotNull(Device(new HidBatteryProtocolProfile(RequestHex: "004")).Problem());

    [Fact]
    public void NonHexIsRejected() => Assert.NotNull(Device(new HidBatteryProtocolProfile(MatchPrefixHex: "ZZ")).Problem());

    [Theory]
    [InlineData(2_000_000_000, 8, 8)]
    [InlineData(8, -1, 8)]
    [InlineData(8, 8, 0)]
    [InlineData(8, 8, 64)]
    public void BrokenBatteryFieldIsRejected(int reportLength, int bitOffset, int elementBits) =>
        Assert.NotNull(Device(new HidBatteryProtocolProfile(Percentage: Field(reportLength, bitOffset, elementBits))).Problem());

    [Fact]
    public void SaneBatteryFieldPasses() =>
        Assert.Null(Device(new HidBatteryProtocolProfile(Percentage: Field(), Charging: Field(bitOffset: 16, elementBits: 1))).Problem());

    [Fact]
    public void TooManyConnectionsAreRejected()
    {
        var connection = new HidDeviceConnectionProfile(0x1915, 0x07A2, null, null, [Interface], new HidBatteryProtocolProfile());

        Assert.NotNull((Device() with { Connections = [.. Enumerable.Repeat(connection, 17)] }).Problem());
    }
}

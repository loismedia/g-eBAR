using GeBar.App;
using GeBar.Core;

namespace GeBar.Tests;

public class DeviceAutoSwitchTests
{
    private static DeviceBatteryState State(DeviceConnectionState connection, int? percentage = null) =>
        new(connection, percentage, false, null, null);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WaitsForStartupCheckAndRespectsDisabledSetting(bool startupGraceElapsed, bool enabled)
    {
        var states = new Dictionary<string, DeviceBatteryState>
        {
            ["selected"] = DeviceBatteryState.Missing,
            ["available"] = State(DeviceConnectionState.DeviceConnected, 75)
        };
        var settings = new AppSettings(SelectedDeviceId: "selected", AutoSwitchDevice: enabled);

        Assert.Null(BatteryTrayApplicationContext.FindAutoSwitchTarget(["selected", "available"], states, settings, startupGraceElapsed));
    }

    [Fact]
    public void StartupCheckFindsReplacementWithoutNewDeviceEvents()
    {
        var states = new Dictionary<string, DeviceBatteryState>
        {
            ["selected"] = DeviceBatteryState.Missing,
            ["available"] = State(DeviceConnectionState.DeviceConnected, 75)
        };
        var settings = new AppSettings(SelectedDeviceId: "selected");

        Assert.Null(BatteryTrayApplicationContext.FindAutoSwitchTarget(["selected", "available"], states, settings, false));
        Assert.Equal("available", BatteryTrayApplicationContext.FindAutoSwitchTarget(["selected", "available"], states, settings, true));
    }

    [Theory]
    [InlineData(DeviceConnectionState.DeviceConnected, 75, false)]
    [InlineData(DeviceConnectionState.DeviceOff, 75, false)]
    [InlineData(DeviceConnectionState.Reading, 75, false)]
    [InlineData(DeviceConnectionState.ReadError, null, false)]
    [InlineData(DeviceConnectionState.DeviceNotFound, null, true)]
    [InlineData(DeviceConnectionState.DeviceOff, null, true)]
    public void ReplacesMissingDeviceButKeepsSleepingDeviceWithKnownCharge(DeviceConnectionState connection, int? percentage, bool switches)
    {
        var states = new Dictionary<string, DeviceBatteryState>
        {
            ["selected"] = State(connection, percentage),
            ["available"] = State(DeviceConnectionState.DeviceConnected, 75)
        };

        Assert.Equal(switches ? "available" : null, BatteryTrayApplicationContext.FindAutoSwitchTarget(
            ["selected", "available"], states, new AppSettings(SelectedDeviceId: "selected"), true));
    }

    [Fact]
    public void UsesProfileOrderAndSkipsUnavailableOrUnknownCharge()
    {
        var states = new Dictionary<string, DeviceBatteryState>
        {
            ["selected"] = DeviceBatteryState.Missing,
            ["removed"] = State(DeviceConnectionState.DeviceConnected, 90),
            ["missing"] = State(DeviceConnectionState.DeviceNotFound, 80),
            ["unknown"] = State(DeviceConnectionState.DeviceConnected),
            ["second"] = State(DeviceConnectionState.DeviceConnected, 60),
            ["first"] = State(DeviceConnectionState.DeviceConnected, 50)
        };

        Assert.Equal("first", BatteryTrayApplicationContext.FindAutoSwitchTarget(
            ["selected", "missing", "unknown", "first", "second"], states, new AppSettings(SelectedDeviceId: "selected"), true));
    }

    [Fact]
    public void KeepsSelectionWhenNoReplacementIsAvailable()
    {
        var states = new Dictionary<string, DeviceBatteryState>
        {
            ["selected"] = DeviceBatteryState.Missing,
            ["other"] = State(DeviceConnectionState.DeviceOff)
        };

        Assert.Null(BatteryTrayApplicationContext.FindAutoSwitchTarget(
            ["selected", "other"], states, new AppSettings(SelectedDeviceId: "selected"), true));
    }

    [Fact]
    public void PreviousDeviceReturningDoesNotReplaceNewSelection()
    {
        var states = new Dictionary<string, DeviceBatteryState>
        {
            ["original"] = DeviceBatteryState.Missing,
            ["replacement"] = State(DeviceConnectionState.DeviceConnected, 75)
        };
        var settings = new AppSettings(SelectedDeviceId: "original");
        string? replacement = BatteryTrayApplicationContext.FindAutoSwitchTarget(["original", "replacement"], states, settings, true);
        Assert.Equal("replacement", replacement);
        settings = settings with { SelectedDeviceId = replacement! };
        states["original"] = State(DeviceConnectionState.DeviceConnected, 90);

        Assert.Null(BatteryTrayApplicationContext.FindAutoSwitchTarget(["original", "replacement"], states, settings, true));
        states["replacement"] = DeviceBatteryState.Missing;
        Assert.Equal("original", BatteryTrayApplicationContext.FindAutoSwitchTarget(["original", "replacement"], states, settings, true));
    }
}

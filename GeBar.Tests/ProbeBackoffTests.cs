using System.Reflection;
using GeBar.Core;
using GeBar.Hid;

namespace GeBar.Tests;

public class ProbeBackoffTests
{
    private static readonly HidDeviceConnectionProfile Connection = new(0xFFFF, 0xFFFF, null, null, [], new());

    private static ProfileBatteryMonitor Monitor() => new(
        new HidDeviceProfile("absent", "Absent device", Connection.VendorId, Connection.ProductId, null, null, [], new()),
        new Logger(), TimeSpan.FromMinutes(5));

    [Fact]
    public async Task AutomaticRefreshKeepsProbePause()
    {
        await using var monitor = Monitor();
        Pause(monitor);
        string? paused = monitor.Diagnostics.ProbingPaused;
        Assert.NotNull(paused);

        await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal(paused, monitor.Diagnostics.ProbingPaused);
    }

    [Fact]
    public async Task ManualRefreshClearsProbePause()
    {
        await using var monitor = Monitor();
        Pause(monitor);
        Assert.NotNull(monitor.Diagnostics.ProbingPaused);

        await ((IDeviceBatteryMonitor)monitor).RefreshNowAsync(CancellationToken.None);

        Assert.Null(monitor.Diagnostics.ProbingPaused);
        Assert.Empty(Probes(monitor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupFailuresRetrySoonerThanEstablishedFailures(bool startup)
    {
        await using var monitor = Monitor();
        if (startup) Field("_startedAt").SetValue(monitor, DateTimeOffset.Now);
        Pause(monitor);

        TimeSpan delay = Probes(monitor).Single().Value.Resume - DateTimeOffset.Now;

        Assert.True(delay > TimeSpan.Zero);
        if (startup) Assert.True(delay <= TimeSpan.FromMinutes(1));
        else Assert.True(delay > TimeSpan.FromMinutes(29));
    }

    private static void Pause(ProfileBatteryMonitor monitor)
    {
        MethodInfo quiet = typeof(ProfileBatteryMonitor).GetMethod("Quiet", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int attempt = 0; attempt < 3; attempt++) quiet.Invoke(monitor, [(Connection.VendorId, Connection.ProductId), Connection]);
    }

    private static Dictionary<(int Vendor, int Product), (int Quiet, DateTimeOffset Resume)> Probes(ProfileBatteryMonitor monitor) =>
        (Dictionary<(int Vendor, int Product), (int Quiet, DateTimeOffset Resume)>)Field("_probes").GetValue(monitor)!;

    private static FieldInfo Field(string name) => typeof(ProfileBatteryMonitor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Logger : IAppLogger
    {
        public string LogDirectory => Path.GetTempPath();
        public bool IsRawPacketLoggingEnabled => false;
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}

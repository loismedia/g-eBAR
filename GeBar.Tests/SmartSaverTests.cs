using GeBar.Core;
using GeBar.Dpi;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GeBar.Tests;

public class SmartSaverTests
{
    private static readonly HidDeviceProfile Mouse = new("mouse", "Mouse", 0xFFFF, 1, null, null, [], new());

    [Fact]
    public void GameStartsImmediatelyAndDesktopRequiresThreeStableSeconds()
    {
        using var fixture = new Fixture();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(fixture.Saver.UpdateActivity(true, now));
        Assert.True(fixture.Saver.UpdateActivity(false, now.AddSeconds(1)));
        Assert.True(fixture.Saver.UpdateActivity(false, now.AddSeconds(3)));
        Assert.True(fixture.Saver.UpdateActivity(true, now.AddSeconds(3.5)));
        Assert.True(fixture.Saver.UpdateActivity(false, now.AddSeconds(4)));
        Assert.True(fixture.Saver.UpdateActivity(false, now.AddSeconds(6)));
        Assert.False(fixture.Saver.UpdateActivity(false, now.AddSeconds(7)));
    }

    [Theory]
    [InlineData("chrome", false)]
    [InlineData("msedge", false)]
    [InlineData("firefox", false)]
    [InlineData("VLC", false)]
    [InlineData("mpv", false)]
    [InlineData("PotPlayerMini64", false)]
    [InlineData("ApplicationFrameHost", false)]
    [InlineData("explorer", false)]
    [InlineData("cs2", true)]
    [InlineData("Cyberpunk2077", true)]
    public void BrowsersVideoPlayersAndDesktopAreExcluded(string process, bool expected) => Assert.Equal(expected, FullscreenApplication.IsEligible(process));

    [Fact]
    public void FullscreenMustCoverWholeMonitorIncludingSecondaryMonitors()
    {
        var monitor = new FullscreenApplication.Bounds { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        Assert.True(FullscreenApplication.CoversMonitor(monitor, monitor));
        Assert.False(FullscreenApplication.CoversMonitor(new() { Left = -1920, Top = 0, Right = 0, Bottom = 1040 }, monitor));
        Assert.False(FullscreenApplication.CoversMonitor(new() { Left = -3840, Top = 0, Right = 0, Bottom = 1080 }, monitor));
    }

    [Fact]
    public async Task BackgroundSwitchPreservesNormalRateNotifiesOnceAndRestoresOnStop()
    {
        using var fixture = new Fixture();
        int notifications = 0;
        fixture.Saver.Notification += (_, message) => { Assert.Contains("2000", message); Interlocked.Increment(ref notifications); };
        await fixture.Configure(new(true, true, 2000));
        Assert.Equal(125, fixture.Driver.Settings.PollingRate);
        Assert.Equal(2000, fixture.Saver.Editor(Mouse.Id, fixture.Driver.Settings, fixture.Driver.Capabilities).PollingRate);
        Assert.Equal(0, notifications);
        await fixture.Saver.StartAsync([Mouse], CancellationToken.None);
        fixture.Game = true;
        await Until(() => fixture.Driver.Settings.PollingRate == 2000 && notifications == 1);
        await Task.Delay(1100);
        Assert.Equal(1, notifications);
        Assert.Equal(new[] { 125, 2000 }, fixture.Driver.Rates);
        await fixture.Saver.StopAsync(CancellationToken.None);
        Assert.Equal(2000, fixture.Driver.Settings.PollingRate);
        Assert.Equal(0, fixture.Driver.Saves);
    }

    [Fact]
    public async Task StopRestoresNormalRateWhileKeepingOtherMouseSettings()
    {
        using var fixture = new Fixture();
        await fixture.Configure(new(true, false, 1000));
        fixture.Driver.Settings = fixture.Driver.Settings with { Dpi = 1200, Lod = 2 };
        await fixture.Saver.StopAsync(CancellationToken.None);
        Assert.Equal(new(1200, 1000, 2), fixture.Driver.Settings);
    }

    [Fact]
    public async Task DisablingAndRemovingProfileRestoreNormalPolling()
    {
        using var fixture = new Fixture();
        await fixture.Configure(new(true, false, 1000));
        await fixture.Configure(new(false, false, 1000));
        Assert.Equal(1000, fixture.Driver.Settings.PollingRate);
        await fixture.Configure(new(true, false, 1000));
        await fixture.Saver.StartAsync([Mouse], CancellationToken.None);
        await fixture.Saver.StartAsync([], CancellationToken.None);
        Assert.Equal(1000, fixture.Driver.Settings.PollingRate);
        await Task.Delay(1100);
        Assert.Equal(1000, fixture.Driver.Settings.PollingRate);
        await fixture.Saver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WiredPollingLimitDoesNotReplaceSavedWirelessRate()
    {
        using var fixture = new Fixture();
        await fixture.Configure(new(true, false, 2000));
        fixture.Driver.Capabilities = fixture.Driver.Capabilities with { PollingRates = [125, 500, 1000] };
        fixture.Game = true;
        await fixture.Saver.StartAsync([Mouse], CancellationToken.None);
        await Until(() => fixture.Saver.Rate(Mouse.Id) == 1000);
        Assert.Equal(2000, fixture.Saver.Options(Mouse.Id).NormalPollingRate);
        Assert.Equal(1000, fixture.Saver.Editor(Mouse.Id, fixture.Driver.Settings, fixture.Driver.Capabilities).PollingRate);
        await fixture.Saver.StopAsync(CancellationToken.None);
        Assert.Equal(1000, fixture.Driver.Settings.PollingRate);
    }

    [Fact]
    public async Task ShutdownRestoresRateEvenWhenWriteSucceededButAcknowledgementFailed()
    {
        using var fixture = new Fixture();
        fixture.Driver.FailAfterWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Configure(new(true, false, 1000)));
        Assert.Equal(125, fixture.Driver.Settings.PollingRate);
        fixture.Driver.FailAfterWrite = false;
        await fixture.Saver.StopAsync(CancellationToken.None);
        Assert.Equal(1000, fixture.Driver.Settings.PollingRate);
    }

    [Fact]
    public async Task ReopeningSettingsKeepsBaselineAndConfigurationIsPerMouse()
    {
        using var fixture = new Fixture();
        await fixture.Configure(new(true, true, 2000));
        var restored = fixture.NewSaver();
        Assert.Equal(new(true, true, 2000), restored.Options(Mouse.Id));
        Assert.Equal(new(), restored.Options("another-mouse"));
        await restored.StartAsync([Mouse], CancellationToken.None);
        await Until(() => restored.Physical(Mouse.Id, new(800, 2000, 1)).PollingRate == 125);
        await restored.StopAsync(CancellationToken.None);
        Assert.Equal(2000, fixture.Driver.Settings.PollingRate);
    }

    [Fact]
    public async Task PollingFailureDoesNotSendSuccessfulGameNotification()
    {
        using var fixture = new Fixture { Game = true };
        fixture.Driver.FailPolling = true;
        int notifications = 0;
        fixture.Saver.Notification += (_, _) => notifications++;
        await Assert.ThrowsAsync<IOException>(() => fixture.Configure(new(true, true, 2000)));
        Assert.Equal(0, notifications);
        Assert.Equal(1000, fixture.Driver.Settings.PollingRate);
    }

    [Fact]
    public async Task ExternalPollingChangeIsPreservedDuringShutdown()
    {
        using var fixture = new Fixture();
        await fixture.Configure(new(true, false, 1000));
        fixture.Driver.Settings = fixture.Driver.Settings with { PollingRate = 500 };
        await fixture.Saver.StopAsync(CancellationToken.None);
        Assert.Equal(500, fixture.Driver.Settings.PollingRate);
    }

    [Fact]
    public async Task UnknownSettingsArePreservedAndInvalidRateDoesNotEnableAutomation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, """{"future":7,"Devices":{"mouse":{"Enabled":false,"NormalPollingRate":1000,"futureDevice":9}}}""");
        SmartSaver saver = fixture.NewSaver();
        await saver.ConfigureAsync(Mouse, fixture.Driver, saver.Options(Mouse.Id) with { Notify = true }, CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal(7, document.RootElement.GetProperty("future").GetInt32());
        Assert.Equal(9, document.RootElement.GetProperty("Devices").GetProperty(Mouse.Id).GetProperty("futureDevice").GetInt32());
        File.WriteAllText(fixture.Path, """{"Devices":{"mouse":{"Enabled":true,"NormalPollingRate":123}}}""");
        Assert.False(fixture.NewSaver().Options(Mouse.Id).Enabled);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gebar-smart-{Guid.NewGuid():N}", "settings.json");
        public Driver Driver { get; } = new();
        public bool Game;
        private readonly SemaphoreSlim _gate = new(1, 1);
        public SmartSaver Saver { get; }
        public Fixture()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            Saver = NewSaver();
        }
        public SmartSaver NewSaver() => new(new Logger(), _gate, _ => Driver, Path, () => Game);
        public async Task Configure(SmartSaverOptions options)
        {
            await _gate.WaitAsync();
            try { await Saver.ConfigureAsync(Mouse, Driver, options, CancellationToken.None); }
            finally { _gate.Release(); }
        }
        public void Dispose()
        {
            Saver.Cancel();
            string directory = System.IO.Path.GetDirectoryName(Path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class Driver : IMouseSettingsDriver, IMousePollingDriver
    {
        public MouseSettings Settings = new(800, 1000, 1);
        public ConcurrentQueue<int> Rates { get; } = new();
        public int Saves;
        public bool FailPolling;
        public bool FailAfterWrite;
        public MouseSettingsCapabilities Capabilities { get; set; } = new(50, 30000, 50, [125, 500, 1000, 2000], [1, 2]);
        public bool Supports(HidDeviceProfile device) => true;
        public Task<MouseSettings> ReadAsync(HidDeviceProfile device, CancellationToken token) => Task.FromResult(Settings);
        public Task<MouseSettings> SaveAsync(HidDeviceProfile device, MouseSettings expected, MouseSettings requested, CancellationToken token)
        {
            Interlocked.Increment(ref Saves);
            if (Settings != expected) throw new InvalidOperationException();
            return Task.FromResult(Settings = requested);
        }
        public Task<MouseSettings> SetPollingAsync(HidDeviceProfile device, int pollingRate, CancellationToken token)
        {
            if (FailPolling) throw new IOException("Mouse unavailable");
            Rates.Enqueue(pollingRate);
            Settings = Settings with { PollingRate = pollingRate };
            if (FailAfterWrite) throw new IOException("Acknowledgement unavailable");
            return Task.FromResult(Settings);
        }
    }

    private sealed class Logger : IAppLogger
    {
        public string LogDirectory => System.IO.Path.GetTempPath();
        public bool IsRawPacketLoggingEnabled => false;
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}

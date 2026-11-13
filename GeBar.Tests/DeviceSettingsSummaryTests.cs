using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GeBar.App;
using GeBar.Core;
using GeBar.Dpi;
using GeBar.UI;

namespace GeBar.Tests;

public class DeviceSettingsSummaryTests
{
    private static readonly HidDeviceProfile Device = new("mouse-a", "Mouse A", 0xFFFF, 0xFFFF, null, null, [], new());

    [Fact]
    public void SmartSaverDoesNotReplaceSelectedRateAndDisablingRestoresIt() => Sta(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-smart-ui-{Guid.NewGuid():N}");
        var driver = new Driver();
        using var module = new DpiModule(new TestLogger(), [driver], Path.Combine(directory, "smart.json"), () => false);
        try
        {
            await module.SetDeviceAsync(Device, CancellationToken.None);
            DependencyObject[] elements = [.. Descendants(module.View)];
            CheckBox enabled = elements.OfType<CheckBox>().First();
            CheckBox notify = elements.OfType<CheckBox>().Last();
            ComboBox polling = elements.OfType<ComboBox>().First();
            Assert.False(module.CanSave);
            Assert.False(notify.IsChecked);
            enabled.IsChecked = true;
            Assert.True(module.CanSave);
            Assert.True(await module.SaveAsync(CancellationToken.None));
            Assert.Equal(125, driver.Settings.PollingRate);
            Assert.Equal(1000, polling.SelectedValue);
            Assert.Equal(125, module.CurrentSettings!.PollingRate);
            Assert.True(module.SmartSaverActive);
            Assert.False(module.CanSave);
            int saves = driver.Saves;
            notify.IsChecked = true;
            Assert.True(await module.SaveAsync(CancellationToken.None));
            Assert.Equal(saves, driver.Saves);
            Assert.Equal(125, driver.Settings.PollingRate);
            elements.OfType<TextBox>().Single().Text = "1200";
            Assert.True(await module.SaveAsync(CancellationToken.None));
            Assert.Equal(new(1200, 125, 1), driver.Settings);
            await module.RefreshSettingsAsync(CancellationToken.None);
            Assert.Equal(1000, polling.SelectedValue);
            enabled.IsChecked = false;
            Assert.True(await module.SaveAsync(CancellationToken.None));
            Assert.Equal(new(1200, 1000, 1), driver.Settings);
            Assert.False(module.SmartSaverActive);
            Assert.False(module.CanSave);
        }
        finally
        {
            await module.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    });

    [Fact]
    public void ClosingAndReopeningWindowKeepsSharedModuleAliveAndDetachesItsView() => Sta(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-shared-module-{Guid.NewGuid():N}");
        using var loader = new SettingsModuleLoader();
        var driver = new Driver();
        using var module = new DpiModule(new TestLogger(), [driver], Path.Combine(directory, "smart.json"), () => false);
        typeof(SettingsModuleLoader).GetProperty(nameof(SettingsModuleLoader.Module))!.SetValue(loader, module);
        ControlCenterWindow? first = null, second = null;
        try
        {
            first = new(new(), false, string.Empty, new FileLogger(directory, "test"), moduleLoader: loader);
            Assert.Same(module.View, ((ContentControl)first.FindName("DpiPage")).Content);
            first.Close();
            Assert.Null(((ContentControl)first.FindName("DpiPage")).Content);
            second = new(new(), false, string.Empty, new FileLogger(directory, "test"), moduleLoader: loader);
            Assert.Same(module.View, ((ContentControl)second.FindName("DpiPage")).Content);
            await module.SetDeviceAsync(Device, CancellationToken.None);
            Assert.Equal(800, module.CurrentSettings!.Dpi);
        }
        finally
        {
            first?.Close(); second?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void SummaryKeepsRequestedUnitsAndDecimalPoint(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var summary = new DeviceSettingsSummary("mouse", 400, 1000, 1);

            Assert.Equal("Mouse (400dpi, 1000hz, 1.0mm)", ControlCenterWindow.FormatDeviceName("Mouse", summary.Display));
            Assert.Equal("(400dpi, 1000hz (SS), 1.0mm)", summary.Format(true));
            Assert.Equal("Mouse", ControlCenterWindow.FormatDeviceName("Mouse", null));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void EditedValuesAreNotPublishedBeforeSave() => Sta(async () =>
    {
        var driver = new Driver();
        using var module = new DpiModule(new TestLogger(), [driver]);
        await module.SetDeviceAsync(Device, CancellationToken.None);
        TextBox input = Assert.Single(Descendants(module.View).OfType<TextBox>());

        input.Text = "1200";

        Assert.True(module.CanSave);
        Assert.Equal(800, module.CurrentSettings?.Dpi);
        Assert.Equal(0, driver.Saves);
        Assert.True(await module.SaveAsync(CancellationToken.None));
        Assert.Equal(1, driver.Saves);
        Assert.Equal(1200, module.CurrentSettings?.Dpi);
        Assert.False(module.CanSave);
    });

    [Fact]
    public void BackgroundReadRefreshesActualSummaryWithoutOverwritingEdits() => Sta(async () =>
    {
        var driver = new Driver();
        using var module = new DpiModule(new TestLogger(), [driver]);
        await module.SetDeviceAsync(Device, CancellationToken.None);
        TextBox input = Assert.Single(Descendants(module.View).OfType<TextBox>());
        input.Text = "1200";
        driver.Settings = new(400, 1000, 2);

        await module.RefreshSettingsAsync(CancellationToken.None);

        Assert.Equal("1200", input.Text);
        Assert.Equal(new DeviceSettingsSummary(Device.Id, 400, 1000, 2), module.CurrentSettings);
        Assert.Equal(0, driver.Saves);
    });

    [Fact]
    public void UnsupportedSelectionClearsPreviousSummary() => Sta(async () =>
    {
        using var module = new DpiModule(new TestLogger(), [new Driver()]);
        await module.SetDeviceAsync(Device, CancellationToken.None);
        Assert.NotNull(module.CurrentSettings);

        await module.SetDeviceAsync(Device with { Id = "unsupported" }, CancellationToken.None);

        Assert.Null(module.CurrentSettings);
        Assert.False(module.CanSave);
    });

    [Fact]
    public void PreloadedSettingsAppearBeforeDeviceReadCompletes() => Sta(async () =>
    {
        var driver = new Driver();
        using var module = new DpiModule(new TestLogger(), [driver]);
        await module.SetDeviceAsync(Device, CancellationToken.None);
        HidDeviceProfile other = Device with { Id = "mouse-b", Name = "Mouse B" };
        driver.Settings = new(400, 1000, 2);
        await module.PreloadSettingsAsync([other], CancellationToken.None);
        driver.ReadReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task change = module.SetDeviceAsync(other, CancellationToken.None);

        Assert.False(change.IsCompleted);
        Assert.Equal(new DeviceSettingsSummary(other.Id, 400, 1000, 2), module.CurrentSettings);
        Assert.Equal("400", Assert.Single(Descendants(module.View).OfType<TextBox>()).Text);
        Assert.False(module.CanSave);
        driver.Settings = new(1600, 1000, 1);
        driver.ReadReady.SetResult();
        await change;

        Assert.Equal(new DeviceSettingsSummary(other.Id, 1600, 1000, 1), module.CurrentSettings);
    });

    [Theory]
    [InlineData("800", 120, "850")]
    [InlineData("800", -120, "750")]
    [InlineData("801", 120, "850")]
    [InlineData("801", -120, "800")]
    [InlineData("30000", 120, "30000")]
    [InlineData("50", -120, "50")]
    public void DpiWheelUsesDeviceStepWithoutWriting(string initial, int delta, string expected) => Sta(async () =>
    {
        var driver = new Driver();
        using var module = new DpiModule(new TestLogger(), [driver]);
        await module.SetDeviceAsync(Device, CancellationToken.None);
        TextBox input = Assert.Single(Descendants(module.View).OfType<TextBox>());
        input.Text = initial;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };

        input.RaiseEvent(wheel);

        Assert.True(wheel.Handled);
        Assert.Equal(expected, input.Text);
        Assert.Equal(0, driver.Saves);
        Assert.Equal(800, module.CurrentSettings?.Dpi);
    });

    [Fact]
    public void SelectedDeviceDoesNotInheritAnotherMousesParameters() => Sta(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-summary-{Guid.NewGuid():N}");
        ControlCenterWindow? window = null;
        try
        {
            window = new ControlCenterWindow(new(), false, string.Empty, new FileLogger(directory, "test"));
            var loader = (SettingsModuleLoader)typeof(ControlCenterWindow).GetField("_moduleLoader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            loader.Dispose();
            typeof(SettingsModuleLoader).GetProperty(nameof(SettingsModuleLoader.Module))!.SetValue(loader, new SummaryModule());
            window.SetDevices([("mouse-a", "Mouse A"), ("mouse-b", "Mouse B")], "mouse-a");
            var combo = (ComboBox)window.FindName("DeviceCombo");

            Assert.Equal(new[] { "Mouse A (400dpi, 1000hz, 1.0mm)", "Mouse B" }, (string[])combo.ItemsSource);
            Assert.Equal("mouse-a", window.Value.SelectedDeviceId);

            window.SetDevices([("mouse-a", "Mouse A"), ("mouse-b", "Mouse B")], "mouse-b");

            Assert.Equal(new[] { "Mouse A", "Mouse B" }, (string[])combo.ItemsSource);
            Assert.Equal(1, combo.SelectedIndex);
            Assert.Equal("Mouse B", ((TextBlock)window.FindName("DeviceNameText")).Text);
            return Task.CompletedTask;
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object value in LogicalTreeHelper.GetChildren(root))
        {
            if (value is not DependencyObject child) continue;
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Sta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                Task work = action();
                var frame = new DispatcherFrame();
                _ = work.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                work.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Settings summary test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Driver : IMouseSettingsDriver, IMousePollingDriver
    {
        public TaskCompletionSource? ReadReady { get; set; }
        public MouseSettings Settings { get; set; } = new(800, 1000, 1);
        public int Saves { get; private set; }
        public MouseSettingsCapabilities Capabilities { get; } = new(50, 30000, 50, [125, 1000], [1, 2]);
        public bool Supports(HidDeviceProfile device) => device.Id != "unsupported";
        public async Task<MouseSettings> ReadAsync(HidDeviceProfile device, CancellationToken token)
        {
            if (ReadReady is not null) await ReadReady.Task.WaitAsync(token);
            return Settings;
        }
        public Task<MouseSettings> SaveAsync(HidDeviceProfile device, MouseSettings expected, MouseSettings requested, CancellationToken token)
        {
            if (Settings != expected) throw new InvalidOperationException();
            Saves++;
            Settings = requested;
            return Task.FromResult(Settings);
        }
        public Task<MouseSettings> SetPollingAsync(HidDeviceProfile device, int pollingRate, CancellationToken token) =>
            Task.FromResult(Settings = Settings with { PollingRate = pollingRate });
    }

    private sealed class SummaryModule : ISettingsModule, IDeviceSettingsSource
    {
        public string Name => "g-eDPI";
        public string Version => "03.11-dpi-LOCAL";
        public FrameworkElement View { get; } = new Border();
        public bool CanSave => false;
        public DeviceSettingsSummary? CurrentSettings => new("mouse-a", 400, 1000, 1);
        public event EventHandler? SaveAvailabilityChanged { add { } remove { } }
        public event EventHandler? SettingsChanged { add { } remove { } }
        public Task SetDeviceAsync(HidDeviceProfile? device, CancellationToken token) => Task.CompletedTask;
        public Task<bool> SaveAsync(CancellationToken token) => Task.FromResult(false);
        public Task RefreshSettingsAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }

}

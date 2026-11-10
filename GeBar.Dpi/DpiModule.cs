using GeBar.Core;
using GeBar.UI;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;

namespace GeBar.Dpi;

public sealed class DpiModule : ISettingsModule, IDeviceSettingsSource, IBackgroundSettingsModule
{
    private readonly IAppLogger _logger;
    private readonly Func<IMouseSettingsDriver>[] _drivers;
    private readonly TextBlock _deviceName = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock _range = new() { FontSize = 12, Margin = new(0, 4, 0, 0) };
    private readonly TextBox _dpi = new();
    private readonly ComboBox _polling = new() { Width = 112, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly ComboBox _lod = new() { Width = 112, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly Button _refresh = new() { Width = 36, Height = 36, Padding = new(0), ToolTip = Strings.Read,
        Content = new TextBlock { Text = "\uE72C", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 } };
    private readonly StackPanel _fields = new();
    private readonly StackPanel _powerFields = new();
    private readonly ComboBox _sleep = new() { Width = 112, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly ComboBox _performance = new() { Width = 112, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly CheckBox _smartEnabled = new() { Content = Strings.EnableSmartSaver };
    private readonly CheckBox _notifyGame = new() { Content = Strings.NotifyGame };
    private readonly TextBlock _smartStatus = new() { Margin = new(16, 0, 16, 12) };
    private readonly TextBlock _powerHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly SmartSaver _smartSaver;
    private MousePowerSettings? _power;
    private CancellationTokenSource? _readLifetime;
    private HidDeviceProfile? _device;
    private IMouseSettingsDriver? _driver;
    private MouseSettings? _current;
    private bool _busy;
    private bool _disposed;
    private bool _refreshing;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private int _saveGeneration;
    private bool _preloading;
    private readonly Dictionary<string, (MouseSettings Values, MouseSettingsCapabilities Limits)> _cache = new(StringComparer.Ordinal);

    public DpiModule(IAppLogger logger) : this(logger, [() => new WlMouseSettingsDriver(logger), () => new WaizowlSettingsDriver(logger)]) { }

    internal DpiModule(IAppLogger logger, IMouseSettingsDriver[] drivers, string? smartSaverPath = null, Func<bool>? fullscreen = null)
        : this(logger, drivers.Select(driver => new Func<IMouseSettingsDriver>(() => driver)).ToArray(), smartSaverPath, fullscreen) { }

    private DpiModule(IAppLogger logger, Func<IMouseSettingsDriver>[] drivers, string? smartSaverPath = null, Func<bool>? fullscreen = null)
    {
        _logger = logger;
        _drivers = drivers;
        _smartSaver = new(logger, _requests, DriverFor,
            smartSaverPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "g-eBAR", "smartsaver.json"), fullscreen ?? new FullscreenApplication().IsActive);
        _smartSaver.Applied += ApplySettings;
        _smartSaver.StateChanged += UpdateSmartStatus;
        var panel = new StackPanel();
        panel.Children.Add(_deviceName);
        _deviceName.Margin = new(4, 0, 0, 12);
        AutomationProperties.SetName(_refresh, Strings.Read);
        _dpi.SetResourceReference(FrameworkElement.StyleProperty, "NumberBox");
        _range.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        var dpiLabel = new StackPanel();
        dpiLabel.Children.Add(new TextBlock { Text = "DPI" });
        dpiLabel.Children.Add(_range);
        _fields.Children.Add(Row(dpiLabel, _dpi, false));
        _fields.Children.Add(Row(new TextBlock { Text = Strings.Polling }, _polling, false));
        _fields.Children.Add(Row(new TextBlock { Text = Strings.Lod }, _lod, true));
        var columns = new Grid { Margin = new(0, 0, 0, 16) };
        Grid.SetIsSharedSizeScope(columns, true);
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.Children.Add(Section(Strings.Title, _fields));
        ShareRowHeight((Border)_fields.Children[0], "DpiSleep");
        var sleepRow = Row(new TextBlock { Text = Strings.Sleep }, _sleep, false);
        ShareRowHeight(sleepRow, "DpiSleep");
        _powerFields.Children.Add(sleepRow);
        _powerFields.Children.Add(Row(new TextBlock { Text = Strings.Performance }, _performance, false));
        _powerHint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        var powerPanel = new StackPanel();
        powerPanel.Children.Add(_powerFields);
        var hintRow = new Border { Child = _powerHint };
        hintRow.SetResourceReference(FrameworkElement.StyleProperty, "LastRow");
        powerPanel.Children.Add(hintRow);
        FrameworkElement powerCard = Section(Strings.Power, powerPanel);
        Grid.SetColumn(powerCard, 2);
        columns.Children.Add(powerCard);
        panel.Children.Add(columns);
        var smartPanel = new StackPanel();
        smartPanel.Children.Add(ToggleRow(_smartEnabled, false));
        smartPanel.Children.Add(ToggleRow(_notifyGame, true));
        smartPanel.Children.Add(new TextBlock { Text = Strings.SmartSaverHint, TextWrapping = TextWrapping.Wrap, Margin = new(16, 4, 16, 8) });
        var fullscreenHint = new TextBlock { Text = Strings.FullscreenHint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(16, 0, 16, 8) };
        fullscreenHint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        smartPanel.Children.Add(fullscreenHint);
        _smartStatus.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        smartPanel.Children.Add(_smartStatus);
        panel.Children.Add(Section("SmartSaver", smartPanel));
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        panel.Children.Add(_status);
        var scroll = new ScrollViewer { Content = panel };
        scroll.SetResourceReference(FrameworkElement.StyleProperty, "PageScroll");
        View = scroll;
        _dpi.TextChanged += (_, _) => Notify();
        _dpi.PreviewMouseWheel += DpiMouseWheel;
        _polling.SelectionChanged += (_, _) => Notify();
        _lod.SelectionChanged += (_, _) => Notify();
        _sleep.SelectionChanged += (_, _) => Notify();
        _performance.SelectionChanged += (_, _) => Notify();
        _smartEnabled.Checked += (_, _) => Notify();
        _smartEnabled.Unchecked += (_, _) => Notify();
        _notifyGame.Checked += (_, _) => Notify();
        _notifyGame.Unchecked += (_, _) => Notify();
        _refresh.Click += async (_, _) => await SetDeviceAsync(_device, CancellationToken.None);
        SetBusy(false);
    }

    public string Name => "g-eDPI";
    public string Version => typeof(DpiModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";
    public FrameworkElement View { get; }
    public FrameworkElement Actions => _refresh;
    public bool CanSave => !_disposed && !_busy && Requested() is MouseSettings requested
        && (requested != _current || RequestedPower() != _power || RequestedSmart() != _smartSaver.Options(_device!.Id));
    public event EventHandler? SaveAvailabilityChanged;
    public DeviceSettingsSummary? CurrentSettings { get; private set; }
    public bool SmartSaverActive { get; private set; }
    public event EventHandler? SettingsChanged;
    public event Action<string, string>? Notification
    {
        add => _smartSaver.Notification += value;
        remove => _smartSaver.Notification -= value;
    }

    public Task StartAsync(IEnumerable<HidDeviceProfile> devices, CancellationToken token) => _smartSaver.StartAsync(devices, token);
    public Task StopAsync(CancellationToken token)
    {
        Dispose();
        return _smartSaver.StopAsync(token);
    }
    private IMouseSettingsDriver? DriverFor(HidDeviceProfile device) => _drivers.Select(create => create()).FirstOrDefault(driver => driver.Supports(device));

    public async Task SetDeviceAsync(HidDeviceProfile? device, CancellationToken token)
    {
        if (_disposed) return;
        _readLifetime?.Cancel();
        _readLifetime?.Dispose();
        var lifetime = _readLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken cancellation = lifetime.Token;
        _device = device;
        _current = null;
        _power = null;
        _powerHint.Text = Strings.PowerUnavailable;
        _sleep.ItemsSource = _performance.ItemsSource = null;
        _driver = device is null ? null : DriverFor(device);
        SmartSaverOptions options = device is null ? new() : _smartSaver.Options(device.Id);
        _smartEnabled.IsChecked = options.Enabled;
        _notifyGame.IsChecked = options.Notify;
        if (device is not null && _driver is not null && _cache.TryGetValue(device.Id, out var cached))
        {
            _current = _smartSaver.Editor(device.Id, cached.Values, cached.Limits);
            ShowCapabilities(cached.Limits);
            Show(_current);
            PublishSettings(cached.Values);
        }
        else
        {
            PublishSettings(null);
            _dpi.Clear();
            _range.Text = string.Empty;
            _polling.ItemsSource = _lod.ItemsSource = null;
        }
        _deviceName.Text = device?.DisplayName ?? string.Empty;
        SetBusy(true);
        _status.Text = _driver is null ? Strings.Unsupported : Strings.Loading;
        try
        {
            if (_driver is null || device is null) return;
            IMouseSettingsDriver driver = _driver;
            MouseSettings settings = await Request(() => driver.ReadAsync(device, cancellation), cancellation);
            if (lifetime.IsCancellationRequested || _disposed) return;
            _current = _smartSaver.Editor(device.Id, settings, driver.Capabilities);
            _cache[device.Id] = (settings, driver.Capabilities);
            ShowCapabilities(driver.Capabilities);
            Show(_current);
            PublishSettings(settings);
            if (driver is IMousePowerDriver powerDriver)
            {
                try
                {
                    MousePowerSettings power = await Request(() => powerDriver.ReadPowerAsync(device, cancellation), cancellation);
                    if (lifetime.IsCancellationRequested || _disposed) return;
                    _power = power;
                    ShowPower(powerDriver, power);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
                {
                    _logger.Warning($"g-eDPI: power settings unavailable: {error.Message}");
                }
            }
            _powerHint.Text = _power is null ? Strings.PowerUnavailable : Strings.PowerHint(((IMousePowerDriver)driver).PerformanceModes.Contains(2));
            _status.Text = Strings.Hint;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            if (lifetime.IsCancellationRequested || _disposed) return;
            _logger.Error("g-eDPI: reading mouse settings failed", error);
            _current = null;
            _status.Text = error.Message;
        }
        finally { if (ReferenceEquals(lifetime, _readLifetime) && !_disposed) SetBusy(false); }
    }

    public async Task<bool> SaveAsync(CancellationToken token)
    {
        if (!CanSave || _device is null || _driver is null || _current is null || Requested() is not MouseSettings requested) return false;
        IMouseSettingsDriver driver = _driver;
        HidDeviceProfile device = _device;
        MouseSettings expected = _current;
        MousePowerSettings? requestedPower = RequestedPower();
        SmartSaverOptions requestedSmart = RequestedSmart();
        _saveGeneration++;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _readLifetime?.Token ?? CancellationToken.None);
        SetBusy(true);
        try
        {
            await _requests.WaitAsync(lifetime.Token);
            try
            {
                MouseSettings physical = _smartSaver.Physical(device.Id, expected);
                if (_smartSaver.Options(device.Id).Enabled && _smartSaver.Rate(device.Id) is null && _cache.TryGetValue(device.Id, out var cached))
                    physical = expected with { PollingRate = cached.Values.PollingRate };
                if (requested != expected)
                    await Task.Run(() => driver.SaveAsync(device, physical, requested, lifetime.Token), lifetime.Token);
                if (requestedPower is not null && driver is IMousePowerDriver powerDriver)
                {
                    if (requestedPower != _power)
                        await Task.Run(() => powerDriver.SavePowerAsync(device, _power!, requestedPower, lifetime.Token), lifetime.Token);
                    requestedPower = await Task.Run(() => powerDriver.ReadPowerAsync(device, lifetime.Token), lifetime.Token);
                }
                if (requestedSmart.Enabled || requestedSmart != _smartSaver.Options(device.Id))
                    await _smartSaver.ConfigureAsync(device, driver, requestedSmart, lifetime.Token);
            }
            finally { _requests.Release(); }
            if (lifetime.IsCancellationRequested || _disposed) return false;
            _current = requested;
            _power = requestedPower;
            if (_power is not null && driver is IMousePowerDriver power) ShowPower(power, _power);
            MouseSettings actual = _smartSaver.Physical(device.Id, requested);
            _cache[device.Id] = (actual, driver.Capabilities);
            Show(requested);
            PublishSettings(actual);
            _status.Text = Strings.Hint;
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.Error("g-eDPI: saving mouse settings failed", error);
            if (!lifetime.IsCancellationRequested && !_disposed)
            {
                _current = null;
                _cache.Remove(device.Id);
                PublishSettings(null);
                _status.Text = error is ArgumentException ? Strings.ChangedExternally : error.Message;
            }
            return false;
        }
        finally { if (!_disposed) SetBusy(false); }
    }

    private MouseSettings? Requested()
    {
        if (_current is null || _driver is null || !int.TryParse(_dpi.Text, out int dpi)
            || _polling.SelectedValue is not int rate || _lod.SelectedValue is not double lod) return null;
        var settings = new MouseSettings(dpi, rate, lod);
        return _driver.Capabilities.Accepts(settings) ? settings : null;
    }

    private MousePowerSettings? RequestedPower() => _power is not null && _sleep.SelectedValue is int seconds && _performance.SelectedValue is int mode
        ? new(seconds, mode) : _power;

    private SmartSaverOptions RequestedSmart() => _device is null ? new() : _smartSaver.Options(_device.Id) with
    {
        Enabled = _smartEnabled.IsChecked == true,
        Notify = _notifyGame.IsChecked == true,
        NormalPollingRate = (_smartEnabled.IsChecked == true || _smartSaver.Options(_device.Id).Enabled) && _polling.SelectedValue is int rate
            && (!_smartSaver.Options(_device.Id).Enabled || rate != _current?.PollingRate) ? rate : _smartSaver.Options(_device.Id).NormalPollingRate
    };

    public async Task RefreshSettingsAsync(CancellationToken token)
    {
        if (_disposed || _busy || _refreshing || _driver is null || _device is null || _readLifetime is null) return;
        IMouseSettingsDriver driver = _driver;
        HidDeviceProfile device = _device;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _readLifetime.Token);
        _refreshing = true;
        int generation = _saveGeneration;
        try
        {
            MouseSettings settings = await Request(() => driver.ReadAsync(device, lifetime.Token), lifetime.Token);
            if (_disposed || lifetime.IsCancellationRequested || generation != _saveGeneration) return;
            _cache[device.Id] = (settings, driver.Capabilities);
            if (!_busy && _current is not null && Requested() == _current)
            {
                _current = _smartSaver.Editor(device.Id, settings, driver.Capabilities);
                ShowCapabilities(driver.Capabilities);
                Show(_current);
                Notify();
            }
            PublishSettings(settings);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!_disposed && !lifetime.IsCancellationRequested) _logger.Warning($"g-eDPI: settings summary could not be refreshed: {error.Message}");
        }
        finally { _refreshing = false; }
    }

    public async Task PreloadSettingsAsync(IEnumerable<HidDeviceProfile> devices, CancellationToken token)
    {
        if (_disposed || _preloading || _readLifetime is null) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _readLifetime.Token);
        _preloading = true;
        try
        {
            foreach (HidDeviceProfile device in devices)
            {
                if (_disposed || lifetime.IsCancellationRequested) return;
                if (device.Id == _device?.Id || _cache.ContainsKey(device.Id)) continue;
                IMouseSettingsDriver? driver = _drivers.Select(create => create()).FirstOrDefault(driver => driver.Supports(device));
                if (driver is null) continue;
                try
                {
                    MouseSettings settings = await Request(() => driver.ReadAsync(device, lifetime.Token), lifetime.Token);
                    if (_disposed || lifetime.IsCancellationRequested) return;
                    _cache[device.Id] = (settings, driver.Capabilities);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
                {
                    _logger.Warning($"g-eDPI: settings preload failed for {device.Id}: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { _preloading = false; }
    }

    private void DpiMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_dpi.IsEnabled || _busy || _driver is null || e.Delta == 0 || !int.TryParse(_dpi.Text, out int value)) return;
        MouseSettingsCapabilities caps = _driver.Capabilities;
        value = Math.Clamp(value, caps.MinimumDpi, caps.MaximumDpi);
        int step = caps.DpiStep;
        int next = e.Delta > 0 ? value / step * step + step : (value + step - 1) / step * step - step;
        _dpi.Text = Math.Clamp(next, caps.MinimumDpi, caps.MaximumDpi).ToString(CultureInfo.CurrentCulture);
        e.Handled = true;
    }

    private async Task<T> Request<T>(Func<Task<T>> action, CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try { return await Task.Run(action, token); }
        finally { _requests.Release(); }
    }

    private void PublishSettings(MouseSettings? settings)
    {
        DeviceSettingsSummary? summary = settings is null || _device is null ? null : new(_device.Id, settings.Dpi, settings.PollingRate, settings.Lod);
        bool smartSaverActive = summary is not null && _smartSaver.Options(summary.DeviceId).Enabled
            && _smartSaver.Error(summary.DeviceId) is null && _smartSaver.Rate(summary.DeviceId) == summary.PollingRate;
        if (CurrentSettings == summary && SmartSaverActive == smartSaverActive) return;
        CurrentSettings = summary;
        SmartSaverActive = smartSaverActive;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySettings(string id, MouseSettings settings)
    {
        if (_disposed) return;
        if (!View.Dispatcher.CheckAccess()) { View.Dispatcher.BeginInvoke(() => ApplySettings(id, settings)); return; }
        if (id != _device?.Id || _driver is null) return;
        _cache[id] = (settings, _driver.Capabilities);
        PublishSettings(settings);
    }

    private void Show(MouseSettings settings)
    {
        _dpi.Text = settings.Dpi.ToString(CultureInfo.CurrentCulture);
        _polling.SelectedValue = settings.PollingRate;
        _lod.SelectedValue = settings.Lod;
    }

    private void ShowCapabilities(MouseSettingsCapabilities caps)
    {
        _range.Text = Strings.DpiRange(caps.MinimumDpi, caps.MaximumDpi, caps.DpiStep);
        _polling.ItemsSource = caps.PollingRates.Select(rate => new Choice<int>(rate, $"{rate} Hz")).ToArray();
        _lod.ItemsSource = caps.LodValues.Select(lod => new Choice<double>(lod, $"{lod.ToString("0.#", CultureInfo.CurrentCulture)} mm")).ToArray();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _fields.IsEnabled = !busy && _current is not null;
        _powerFields.IsEnabled = !busy && _power is not null;
        _smartEnabled.IsEnabled = !busy && _current is not null && _driver is IMousePollingDriver && _driver.Capabilities.PollingRates.Contains(125);
        _notifyGame.IsEnabled = _smartEnabled.IsEnabled;
        _refresh.IsEnabled = !busy && _driver is not null;
        Notify();
    }

    private void Notify()
    {
        UpdateSmartStatus();
        SaveAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSmartStatus()
    {
        if (_disposed) return;
        if (!View.Dispatcher.CheckAccess()) { View.Dispatcher.BeginInvoke(UpdateSmartStatus); return; }
        if (_device is not null && _cache.TryGetValue(_device.Id, out var cached)) PublishSettings(cached.Values);
        _smartStatus.Text = _driver is not IMousePollingDriver ? Strings.SmartSaverUnsupported
            : _device is not null && _smartSaver.Options(_device.Id).Enabled
                ? _smartSaver.Error(_device.Id) ?? (_smartSaver.Rate(_device.Id) is not int rate ? Strings.Loading
                    : rate == 125 ? Strings.Saving : Strings.GameRate(rate))
                : Strings.SmartSaverOff;
    }

    private void ShowPower(IMousePowerDriver driver, MousePowerSettings power)
    {
        _sleep.ItemsSource = new[] { 30, 60, 120, 300, 600, 900, power.SleepSeconds }.Distinct().Order()
            .Select(seconds => new Choice<int>(seconds, Strings.SleepDuration(seconds))).ToArray();
        _performance.ItemsSource = driver.PerformanceModes.Select(mode => new Choice<int>(mode, mode switch { 1 => "Speed", 2 => "Turbo", _ => Strings.Normal })).ToArray();
        _sleep.SelectedValue = power.SleepSeconds;
        _performance.SelectedValue = power.PerformanceMode;
    }

    private static FrameworkElement Section(string heading, FrameworkElement content)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        var title = new TextBlock { Text = heading };
        title.SetResourceReference(FrameworkElement.StyleProperty, "GroupHeader");
        panel.Children.Add(title);
        var card = new Border { Child = content };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        Grid.SetRow(card, 1);
        panel.Children.Add(card);
        return panel;
    }

    private static Border ToggleRow(CheckBox toggle, bool last)
    {
        var row = new Border { Child = toggle };
        row.SetResourceReference(FrameworkElement.StyleProperty, last ? "LastRow" : "Row");
        return row;
    }

    private static void ShareRowHeight(Border row, string group)
    {
        var grid = (Grid)row.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = group });
    }

    private static Border Row(FrameworkElement label, FrameworkElement input, bool last)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.VerticalAlignment = input.VerticalAlignment = VerticalAlignment.Center;
        input.HorizontalAlignment = HorizontalAlignment.Right;
        label.Margin = new(0, 0, 16, 0);
        grid.Children.Add(label);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        var row = new Border { Child = grid };
        row.SetResourceReference(FrameworkElement.StyleProperty, last ? "LastRow" : "Row");
        return row;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetBusy(true);
        _smartSaver.Cancel();
        _readLifetime?.Cancel();
        _readLifetime?.Dispose();
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}

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

public sealed class DpiModule : ISettingsModule, IDeviceSettingsSource
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

    internal DpiModule(IAppLogger logger, IMouseSettingsDriver[] drivers)
        : this(logger, drivers.Select(driver => new Func<IMouseSettingsDriver>(() => driver)).ToArray()) { }

    private DpiModule(IAppLogger logger, Func<IMouseSettingsDriver>[] drivers)
    {
        _logger = logger;
        _drivers = drivers;
        var panel = new StackPanel();
        var title = new TextBlock { Text = Strings.Title };
        title.SetResourceReference(FrameworkElement.StyleProperty, "GroupHeader");
        panel.Children.Add(title);
        panel.Children.Add(_deviceName);
        _deviceName.Margin = new(4, 0, 0, 12);
        Grid.SetIsSharedSizeScope(_fields, true);
        AutomationProperties.SetName(_refresh, Strings.Read);
        _dpi.SetResourceReference(FrameworkElement.StyleProperty, "NumberBox");
        _range.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        var dpiLabel = new StackPanel();
        dpiLabel.Children.Add(new TextBlock { Text = "DPI" });
        dpiLabel.Children.Add(_range);
        _fields.Children.Add(Row(dpiLabel, _dpi, false));
        _fields.Children.Add(Row(new TextBlock { Text = Strings.Polling }, _polling, false));
        _fields.Children.Add(Row(new TextBlock { Text = Strings.Lod }, _lod, true));
        var card = new Border { Child = _fields, HorizontalAlignment = HorizontalAlignment.Left };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        panel.Children.Add(card);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        panel.Children.Add(_status);
        var scroll = new ScrollViewer { Content = panel };
        scroll.SetResourceReference(FrameworkElement.StyleProperty, "PageScroll");
        View = scroll;
        _dpi.TextChanged += (_, _) => Notify();
        _dpi.PreviewMouseWheel += DpiMouseWheel;
        _polling.SelectionChanged += (_, _) => Notify();
        _lod.SelectionChanged += (_, _) => Notify();
        _refresh.Click += async (_, _) => await SetDeviceAsync(_device, CancellationToken.None);
        SetBusy(false);
    }

    public string Name => "g-eDPI";
    public string Version => typeof(DpiModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";
    public FrameworkElement View { get; }
    public FrameworkElement Actions => _refresh;
    public bool CanSave => !_disposed && !_busy && Requested() is MouseSettings requested && requested != _current;
    public event EventHandler? SaveAvailabilityChanged;
    public DeviceSettingsSummary? CurrentSettings { get; private set; }
    public event EventHandler? SettingsChanged;

    public async Task SetDeviceAsync(HidDeviceProfile? device, CancellationToken token)
    {
        if (_disposed) return;
        _readLifetime?.Cancel();
        _readLifetime?.Dispose();
        var lifetime = _readLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken cancellation = lifetime.Token;
        _device = device;
        _current = null;
        _driver = device is null ? null : _drivers.Select(create => create()).FirstOrDefault(driver => driver.Supports(device));
        if (device is not null && _driver is not null && _cache.TryGetValue(device.Id, out var cached))
        {
            _current = cached.Values;
            ShowCapabilities(cached.Limits);
            Show(cached.Values);
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
            _current = settings;
            _cache[device.Id] = (settings, driver.Capabilities);
            ShowCapabilities(driver.Capabilities);
            Show(settings);
            PublishSettings(settings);
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
        _saveGeneration++;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _readLifetime?.Token ?? CancellationToken.None);
        SetBusy(true);
        try
        {
            MouseSettings saved = await Request(() => driver.SaveAsync(device, expected, requested, lifetime.Token), lifetime.Token);
            if (lifetime.IsCancellationRequested || _disposed) return false;
            _current = saved;
            _cache[device.Id] = (saved, driver.Capabilities);
            Show(saved);
            PublishSettings(saved);
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
                _current = settings;
                ShowCapabilities(driver.Capabilities);
                Show(settings);
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

    private async Task<MouseSettings> Request(Func<Task<MouseSettings>> action, CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try { return await Task.Run(action, token); }
        finally { _requests.Release(); }
    }

    private void PublishSettings(MouseSettings? settings)
    {
        DeviceSettingsSummary? summary = settings is null || _device is null ? null : new(_device.Id, settings.Dpi, settings.PollingRate, settings.Lod);
        if (CurrentSettings == summary) return;
        CurrentSettings = summary;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
        _refresh.IsEnabled = !busy && _driver is not null;
        Notify();
    }

    private void Notify() => SaveAvailabilityChanged?.Invoke(this, EventArgs.Empty);

    private static Border Row(FrameworkElement label, FrameworkElement input, bool last)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "DpiLabels" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "DpiValues" });
        label.VerticalAlignment = input.VerticalAlignment = VerticalAlignment.Center;
        input.HorizontalAlignment = HorizontalAlignment.Left;
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
        _readLifetime?.Cancel();
        _readLifetime?.Dispose();
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }
}

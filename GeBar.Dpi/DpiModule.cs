using GeBar.Core;
using GeBar.UI;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace GeBar.Dpi;

public sealed class DpiModule : ISettingsModule, IDeviceSettingsSource
{
    private readonly IAppLogger _logger;
    private readonly IMouseSettingsDriver[] _drivers;
    private readonly TextBlock _deviceName = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock _range = new() { FontSize = 12, Margin = new(0, 4, 0, 0) };
    private readonly TextBox _dpi = new();
    private readonly ComboBox _polling = new() { MinWidth = 144, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly ComboBox _lod = new() { MinWidth = 144, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
    private readonly Button _refresh = new() { Content = Text.Read, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 12, 0, 0) };
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

    public DpiModule(IAppLogger logger) : this(logger, [new WlMouseSettingsDriver(logger), new WaizowlSettingsDriver(logger)]) { }

    internal DpiModule(IAppLogger logger, IMouseSettingsDriver[] drivers)
    {
        _logger = logger;
        _drivers = drivers;
        var panel = new StackPanel();
        var title = new TextBlock { Text = Text.Title };
        title.SetResourceReference(FrameworkElement.StyleProperty, "GroupHeader");
        panel.Children.Add(title);
        panel.Children.Add(_deviceName);
        _deviceName.Margin = new(0, 0, 0, 12);
        _dpi.SetResourceReference(FrameworkElement.StyleProperty, "NumberBox");
        _range.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        var dpiLabel = new StackPanel();
        dpiLabel.Children.Add(new TextBlock { Text = "DPI" });
        dpiLabel.Children.Add(_range);
        _fields.Children.Add(Row(dpiLabel, _dpi, false));
        _fields.Children.Add(Row(new TextBlock { Text = Text.Polling }, _polling, false));
        _fields.Children.Add(Row(new TextBlock { Text = Text.Lod }, _lod, true));
        var card = new Border { Child = _fields };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        panel.Children.Add(card);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        panel.Children.Add(_status);
        panel.Children.Add(_refresh);
        var scroll = new ScrollViewer { Content = panel };
        scroll.SetResourceReference(FrameworkElement.StyleProperty, "PageScroll");
        View = scroll;
        _dpi.TextChanged += (_, _) => Notify();
        _polling.SelectionChanged += (_, _) => Notify();
        _lod.SelectionChanged += (_, _) => Notify();
        _refresh.Click += async (_, _) => await SetDeviceAsync(_device, CancellationToken.None);
        SetBusy(false);
    }

    public string Name => "g-eDPI";
    public string Version => typeof(DpiModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";
    public FrameworkElement View { get; }
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
        PublishSettings(null);
        _dpi.Clear();
        _range.Text = string.Empty;
        _polling.ItemsSource = _lod.ItemsSource = null;
        _driver = device is null ? null : _drivers.FirstOrDefault(driver => driver.Supports(device));
        _deviceName.Text = device?.DisplayName ?? string.Empty;
        SetBusy(true);
        _status.Text = _driver is null ? Text.Unsupported : Text.Loading;
        try
        {
            if (_driver is null || device is null) return;
            IMouseSettingsDriver driver = _driver;
            MouseSettings settings = await Request(() => driver.ReadAsync(device, cancellation), cancellation);
            if (lifetime.IsCancellationRequested || _disposed) return;
            _current = settings;
            ShowCapabilities(driver.Capabilities);
            Show(settings);
            PublishSettings(settings);
            _status.Text = Text.Hint;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            if (lifetime.IsCancellationRequested || _disposed) return;
            _logger.Error("g-eDPI: reading mouse settings failed", error);
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
            Show(saved);
            PublishSettings(saved);
            _status.Text = Text.Hint;
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.Error("g-eDPI: saving mouse settings failed", error);
            if (!lifetime.IsCancellationRequested && !_disposed)
            {
                _current = null;
                PublishSettings(null);
                _status.Text = error is ArgumentException ? Text.ChangedExternally : error.Message;
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
        _range.Text = Text.DpiRange(caps.MinimumDpi, caps.MaximumDpi, caps.DpiStep);
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
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.VerticalAlignment = input.VerticalAlignment = VerticalAlignment.Center;
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

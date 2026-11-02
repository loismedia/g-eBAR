using GeBar.UI;
using System.Globalization;
using GeBar.Core;
using Microsoft.Win32;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;

namespace GeBar.App;

internal partial class ControlCenterWindow : Window
{
    private const string SelectedTab = "selected";
    private MediaColor _normalColor;
    private MediaColor _warningColor;
    private MediaColor _criticalColor;
    private int _percentage;
    private WpfButton? _activeTab;
    private bool _suppressDeviceChange;
    private (string Id, string Name)[] _deviceOptions = [];
    private const int AccurateIntervalSeconds = 300;
    private const int OfflineChargeRise = 15;
    private static readonly TimeSpan OfflineChargeGap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HistoryReload = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AnalysisWindow = TimeSpan.FromDays(7);
    private List<BatteryPoint> _history = [];
    private string? _historyDevice;
    private string? _historyDeviceId;
    private DateTimeOffset _historyLoaded;
    private readonly bool _diagnosticsMode;
    private readonly FileLogger _logger;
    private readonly System.Windows.Threading.DispatcherTimer _saveFeedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    public AppSettings Value { get; private set; }
    public event EventHandler? OpenLogsRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? HidLaunchRequested;
    public event EventHandler? HidRemoveRequested;
    public event Action<string>? BugReportRequested;
    public event EventHandler? ChangelogRequested;
    public event Func<string, bool>? GitHubTokenChanged;
    public event Action<ReleaseChannel, string>? ChannelSwitchRequested;
    public event Func<AppSettings, Task<bool>>? SettingsSaved;
    public event EventHandler? DevicesRefreshRequested;

    public void SetDevices(IReadOnlyList<(string Id, string Name)> devices, string selectedId)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetDevices(devices, selectedId)); return; }
        _suppressDeviceChange = true;
        Value = Value with { SelectedDeviceId = selectedId };
        _deviceOptions = devices.ToArray();
        DeviceCombo.ItemsSource = _deviceOptions.Select(option => option.Name).ToArray();
        int selectedIndex = Array.FindIndex(_deviceOptions, option => option.Id == selectedId);
        DeviceCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : _deviceOptions.Length > 0 ? 0 : -1;
        DeviceCombo.Visibility = _deviceOptions.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        DeviceNameText.Visibility = _deviceOptions.Length > 1 ? Visibility.Collapsed : Visibility.Visible;
        Visibility multiDevice = _deviceOptions.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        NotifyScopeRow.Visibility = multiDevice;
        AllDevicesTooltipRow.Visibility = multiDevice;
        AutoSwitchRow.Visibility = multiDevice;
        RawRow.Style = (Style)Resources[_deviceOptions.Length > 1 ? "Row" : "LastRow"];
        _suppressDeviceChange = false;
    }

    private async void DeviceComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceChange || DeviceCombo.SelectedIndex < 0 || DeviceCombo.SelectedIndex >= _deviceOptions.Length) return;
        Value = Value with { SelectedDeviceId = _deviceOptions[DeviceCombo.SelectedIndex].Id };
        if (SettingsSaved is { } save) await save(Value);
    }

    public ControlCenterWindow(AppSettings value, bool diagnostics, string githubToken, FileLogger logger)
    {
        InitializeComponent();
        Value = value;
        _diagnosticsMode = diagnostics;
        _logger = logger;
        DiagnosticsCard.Visibility = diagnostics ? Visibility.Visible : Visibility.Collapsed;
        HistoryCard.Visibility = diagnostics ? Visibility.Collapsed : Visibility.Visible;
        InitializeText();
        InitializeValues(value);
        _saveFeedbackTimer.Tick += (_, _) =>
        {
            _saveFeedbackTimer.Stop();
            SaveButton.Content = Strings.Save;
        };
        GitHubTokenBox.Password = githubToken;
        DeveloperAccessPanel.Visibility = BuildChannel.Current == ReleaseChannel.Dev ? Visibility.Visible : Visibility.Collapsed;
        SwitchDevButton.Visibility = BuildChannel.Current == ReleaseChannel.Public ? Visibility.Visible : Visibility.Collapsed;
        SwitchPublicButton.Visibility = BuildChannel.Current == ReleaseChannel.Dev ? Visibility.Visible : Visibility.Collapsed;
        VersionText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                DeveloperAccessPanel.Visibility = Visibility.Visible;
                ShowPage(SettingsPage, SettingsTab);
            }
        };
        ApplyTheme();
        ShowPage(HomePage, HomeTab);
        Loaded += (_, _) => Windows11WindowStyler.Apply(this);
        BatteryTrack.SizeChanged += (_, _) => UpdateBatteryFill();
        TabStrip.SizeChanged += (_, _) => { if (_activeTab is not null) MoveTabIndicator(_activeTab, false); };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Closed += (_, _) => _saveFeedbackTimer.Stop();
    }

    private void InitializeText()
    {
        Title = Strings.ControlCenter;
        HomeTab.Content = Strings.Home;
        SettingsTab.Content = Strings.Parameters;
        AppearanceTab.Content = Strings.Personalization;
        AboutTab.Content = Strings.About;
        DeviceStatusTitleText.Text = Strings.DeviceStatus;
        DeviceNameText.Text = Strings.DefaultDevice;
        RefreshButton.Content = Strings.Refresh;
        LogsButton.Content = Strings.OpenLog;
        UpdatesButton.Content = Strings.CheckUpdates;
        HidButton.Content = Strings.HidName;
        RemoveHidButton.Content = Strings.RemoveHid;
        ReportBugButton.ToolTip = Strings.ReportBug;
        ChangelogButton.ToolTip = Strings.VersionHistory;
        HistoryTitleText.Text = Strings.BatteryHistory;
        HistoryRangeButton.ToolTip = Strings.HistoryRangeHint;
        UpdateHistoryRangeButton();
        MonitoringTitleText.Text = Strings.Monitoring;
        NotificationsTitleText.Text = Strings.Notifications;
        TrayAppearanceTitleText.Text = Strings.TrayAppearance;
        StartupCheck.Content = Strings.Startup;
        RawCheck.Content = Strings.Raw;
        AllDevicesTooltipCheck.Content = Strings.AllDevicesTooltip;
        AutoSwitchCheck.Content = Strings.AutoSwitch;
        GitHubAccessTitleText.Text = Strings.GitHubAccess;
        GitHubTokenLabel.Text = Strings.GitHubToken;
        SwitchDevButton.Content = Strings.SwitchDev;
        SwitchPublicButton.Content = Strings.SwitchPublic;
        NotifyCheck.Content = Strings.NotifyLow;
        NotifyChargingCheck.Content = Strings.NotifyCharging;
        NotifyFullCheck.Content = Strings.NotifyFull;
        NotifyScopeLabel.Text = Strings.NotifyScope;
        IntervalLabel.Text = Strings.Interval;
        WarningLabel.Text = Strings.Warning;
        CriticalLabel.Text = Strings.Critical;
        IconStyleLabel.Text = Strings.IconStyle;
        IconScaleLabel.Text = Strings.IconScale;
        NormalColorLabel.Text = Strings.NormalColor;
        WarningColorLabel.Text = Strings.WarningColor;
        CriticalColorLabel.Text = Strings.CriticalColor;
        SaveButton.Content = Strings.Save;
        AboutProductText.Text = Strings.AppName;
        VersionText.Text = Strings.Version(GitHubUpdateService.CurrentVersionLabel);
        ModulesTitleText.Text = Strings.Modules;
        UpdateHidState();
        AboutDescriptionText.Text = Strings.AboutText;
        DeveloperText.Text = Strings.Developer;
        LogoImage.Source = LoadLogo();
    }

    private void InitializeValues(AppSettings value)
    {
        StartupCheck.IsChecked = value.StartWithWindows;
        NotifyCheck.IsChecked = value.NotifyLowBattery;
        NotifyChargingCheck.IsChecked = value.NotifyChargingConnected;
        NotifyFullCheck.IsChecked = value.NotifyFullyCharged;
        NotifyScopeCombo.Items.Add(Strings.NotifyScopeSelected);
        NotifyScopeCombo.Items.Add(Strings.NotifyScopeAll);
        NotifyScopeCombo.SelectedIndex = value.NotifyAllDevices ? 1 : 0;
        RawCheck.IsChecked = value.LogRawPackets;
        AllDevicesTooltipCheck.IsChecked = value.ShowAllDevicesInTrayTooltip;
        AutoSwitchCheck.IsChecked = value.AutoSwitchDevice;
        WarningBox.Text = value.WarningThreshold.ToString();
        CriticalBox.Text = value.CriticalThreshold.ToString();
        IntervalBox.Text = value.RefreshIntervalSeconds.ToString();
        IconStyleCombo.Items.Add(Strings.BatteryIcon);
        IconStyleCombo.Items.Add(Strings.PercentageIcon);
        IconStyleCombo.SelectedIndex = (int)value.IconStyle;
        IconScaleCombo.Items.Add(Strings.IconScaleSmall);
        IconScaleCombo.Items.Add(Strings.IconScaleDefault);
        IconScaleCombo.Items.Add(Strings.IconScaleLarge);
        IconScaleCombo.SelectedIndex = value.TrayIconScale - 1;
        _normalColor = FromArgb(value.NormalColorArgb);
        _warningColor = FromArgb(value.WarningColorArgb);
        _criticalColor = FromArgb(value.CriticalColorArgb);
        UpdateColorButtons();
    }

    public void UpdateState(DeviceBatteryState state, HidDiagnostics diagnostics, AppSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateState(state, diagnostics, settings));
            return;
        }
        _percentage = state.Percentage ?? 0;
        DeviceNameText.Text = state.DeviceName ?? Strings.DefaultDevice;
        PercentageText.Text = state.Percentage is int percentage ? $"{percentage}%" : Strings.EmptyValue;
        ChargingIconText.Visibility = state.IsCharging == true ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = Strings.Status(state);
        UpdatedText.Text = $"{Strings.Updated}: {(state.LastSuccessfulRead is DateTimeOffset time ? time.LocalDateTime.ToString("G") : Strings.Never)}";
        if (_diagnosticsMode) DiagnosticsText.Text = Strings.DiagnosticsText(diagnostics);
        else AppendHistory(settings.SelectedDeviceId, state);
        MediaColor color = state.Percentage <= settings.CriticalThreshold ? FromArgb(settings.CriticalColorArgb) : state.Percentage <= settings.WarningThreshold ? FromArgb(settings.WarningColorArgb) : FromArgb(settings.NormalColorArgb);
        if (BatteryFill.Background is not SolidColorBrush current || current.Color != color) BatteryFill.Background = AppTheme.Brush(color);
        UpdateBatteryFill();
    }

    public void ApplyTheme()
    {
        AppTheme.Apply(Resources);
        if (_activeTab is not null) StyleTabs(_activeTab);
        if (IsLoaded) Windows11WindowStyler.Apply(this);
    }

    private void UpdateBatteryFill()
    {
        BatteryFill.Width = Math.Max(0, BatteryTrack.ActualWidth * _percentage / 100d);
        BatteryFill.Height = BatteryTrack.ActualHeight;
    }

    private void ShowPage(FrameworkElement page, WpfButton tab)
    {
        HomePage.Visibility = page == HomePage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == AppearancePage ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == AboutPage ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = page == SettingsPage || page == AppearancePage ? Visibility.Visible : Visibility.Collapsed;
        StyleTabs(tab);
    }

    private void StyleTabs(WpfButton selected)
    {
        _activeTab = selected;
        StyleTab(HomeTab, selected);
        StyleTab(SettingsTab, selected);
        StyleTab(AppearanceTab, selected);
        StyleTab(AboutTab, selected);
        MoveTabIndicator(selected, IsLoaded);
    }

    private void StyleTab(WpfButton candidate, WpfButton selected)
    {
        bool active = candidate == selected;
        candidate.Tag = active ? SelectedTab : null;
        candidate.Foreground = (WpfBrush)Resources[active ? "PrimaryText" : "SecondaryText"];
        candidate.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void MoveTabIndicator(WpfButton tab, bool animate)
    {
        if (tab.ActualWidth <= 0 || !TabStrip.IsAncestorOf(tab)) return;
        double offset = tab.TransformToAncestor(TabStrip).Transform(default).X;
        double width = tab.ActualWidth;
        if (!animate)
        {
            TabIndicatorOffset.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicator.BeginAnimation(WidthProperty, null);
            TabIndicatorOffset.X = offset;
            TabIndicator.Width = width;
            return;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(240);
        TabIndicatorOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
        TabIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);
    }

    private void HomeTabClick(object sender, RoutedEventArgs e) => ShowPage(HomePage, HomeTab);
    private void SettingsTabClick(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsTab);
    private void AppearanceTabClick(object sender, RoutedEventArgs e) => ShowPage(AppearancePage, AppearanceTab);
    private void AboutTabClick(object sender, RoutedEventArgs e) => ShowPage(AboutPage, AboutTab);
    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
        DevicesRefreshRequested?.Invoke(this, EventArgs.Empty);
    }
    private void LogsClick(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
    private void UpdatesClick(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
    private void HidClick(object sender, RoutedEventArgs e) => HidLaunchRequested?.Invoke(this, EventArgs.Empty);
    private void RemoveHidClick(object sender, RoutedEventArgs e) => HidRemoveRequested?.Invoke(this, EventArgs.Empty);

    public void UpdateHidState()
    {
        string? version = GitHubUpdateService.GetInstalledHidVersion();
        ModulesList.ItemsSource = version is null ? Array.Empty<string>() : [Strings.HidVersion(version)];
        ModulesPanel.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
        RemoveHidButton.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void HistoryPlotSizeChanged(object sender, SizeChangedEventArgs e) => DrawHistory();

    private async void HistoryRangeClick(object sender, RoutedEventArgs e)
    {
        Value = Value with { HistoryHours = Value.HistoryHours == 24 ? 168 : 24 };
        UpdateHistoryRangeButton();
        if (SettingsSaved is { } save) await save(Value);
        DrawHistory();
    }

    private void UpdateHistoryRangeButton() => HistoryRangeButton.Content = Value.HistoryHours == 24 ? Strings.HistoryDay : Strings.HistoryWeek;

    private void AppendHistory(string? deviceId, DeviceBatteryState state)
    {
        bool reloaded = !string.Equals(_historyDeviceId, deviceId, StringComparison.Ordinal)
            || !string.Equals(_historyDevice, state.DeviceName, StringComparison.Ordinal)
            || DateTimeOffset.Now - _historyLoaded > HistoryReload;
        if (reloaded) { UpdateHistory(deviceId, state.DeviceName); return; }

        if (state.ConnectionState == DeviceConnectionState.DeviceConnected
            && state.Percentage is int percentage and >= 1 and <= 100
            && state.LastSuccessfulRead is DateTimeOffset time
            && (_history.Count == 0 || time > _history[^1].Time))
            _history.Add(new BatteryPoint(time, percentage, state.IsCharging));
        DrawHistory();
    }

    private void UpdateHistory(string? deviceId, string? deviceName)
    {
        if (!string.Equals(_historyDeviceId, deviceId, StringComparison.Ordinal) || !string.Equals(_historyDevice, deviceName, StringComparison.Ordinal) || DateTimeOffset.Now - _historyLoaded > HistoryReload)
        {
            _historyDevice = deviceName;
            _historyDeviceId = deviceId;
            _historyLoaded = DateTimeOffset.Now;
            _history = [.. BatteryHistory.Read(deviceId ?? string.Empty, deviceName ?? string.Empty, AnalysisWindow)];
        }
        DrawHistory();
    }

    public void ReloadHistory()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ReloadHistory);
            return;
        }
        _historyLoaded = DateTimeOffset.MinValue;
        UpdateHistory(Value.SelectedDeviceId, _historyDevice);
    }

    private void DrawHistory()
    {
        double width = HistoryPlot.ActualWidth, height = HistoryPlot.ActualHeight;
        DateTimeOffset since = DateTimeOffset.Now - TimeSpan.FromHours(Value.HistoryHours);
        IReadOnlyList<BatteryPoint> analysed = BatteryHistory.Analysed(_history);
        BatteryPoint[] view = [.. analysed.Where(point => point.Time >= since)];
        bool empty = view.Length < 2 || width < 8 || height < 8;
        HistoryEmptyText.Text = Strings.NoHistory;
        HistoryEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryLine.Data = HistoryFill.Data = HistoryGrid.Data = null;
        HistoryMarkers.Children.Clear();
        HistoryScale.Children.Clear();
        HistoryTimeline.Children.Clear();
        bool coarse = Value.RefreshIntervalSeconds > AccurateIntervalSeconds;
        HistoryWarningText.Visibility = coarse ? Visibility.Visible : Visibility.Collapsed;
        HistoryWarningText.ToolTip = coarse ? Strings.HistoryCoarse(Value.RefreshIntervalSeconds) : null;
        HistoryEstimateText.Text = BatteryHistory.EstimateRemaining(analysed) is TimeSpan remaining ? Strings.RemainingEstimate(remaining) : string.Empty;
        if (empty) return;

        var grid = new GeometryGroup();
        foreach (int level in new[] { 0, 25, 50, 75, 100 })
        {
            double y = height - (level / 100d * height);
            grid.Children.Add(new LineGeometry(new WpfPoint(0, y), new WpfPoint(width, y)));
        }
        HistoryGrid.Data = grid;
        DrawScales(view, width, height);

        long start = view[0].Time.ToUnixTimeSeconds(), end = view[^1].Time.ToUnixTimeSeconds();
        double span = Math.Max(1, end - start);
        var points = new PointCollection(view.Select(point => new WpfPoint(
            (point.Time.ToUnixTimeSeconds() - start) / span * width,
            height - (point.Percentage / 100d * height))));

        var line = new PathFigure { StartPoint = points[0], IsClosed = false };
        line.Segments.Add(new PolyLineSegment(points.Skip(1), true));
        HistoryLine.Data = new PathGeometry([line]);

        var area = new PathFigure { StartPoint = new WpfPoint(points[0].X, height), IsClosed = true };
        area.Segments.Add(new PolyLineSegment(points, false));
        area.Segments.Add(new LineSegment(new WpfPoint(points[^1].X, height), false));
        HistoryFill.Data = new PathGeometry([area]);

        for (int index = 1; index < view.Length; index++)
        {
            BatteryPoint before = view[index - 1], after = view[index];
            bool? previous = before.Charging, current = after.Charging;
            bool offline = after.Percentage - before.Percentage >= OfflineChargeRise && after.Time - before.Time >= OfflineChargeGap;
            if (!offline && (previous is null || current is null || previous == current)) continue;
            var marker = new TextBlock
            {
                Text = offline || current == true ? "⚡" : "🔋",
                FontFamily = new WpfFontFamily("Segoe UI Emoji"),
                FontSize = 11,
                ToolTip = offline
                    ? Strings.OfflineCharge(before.Percentage, after.Percentage, after.Time.LocalDateTime)
                    : $"{(current == true ? Strings.ChargingStarted : Strings.ChargingStopped)} — {after.Time.LocalDateTime:dd.MM HH:mm}"
            };
            Canvas.SetLeft(marker, Math.Clamp(points[index].X - 7, 0, Math.Max(0, width - 14)));
            Canvas.SetTop(marker, Math.Clamp(points[index].Y - 17, 0, Math.Max(0, height - 15)));
            HistoryMarkers.Children.Add(marker);
        }
    }

    private void DrawScales(BatteryPoint[] view, double width, double height)
    {
        foreach (int level in new[] { 100, 75, 50, 25, 0 })
        {
            TextBlock label = ScaleLabel($"{level}");
            Canvas.SetTop(label, height - (level / 100d * height) - label.DesiredSize.Height / 2);
            Canvas.SetRight(label, 0);
            HistoryScale.Children.Add(label);
        }

        DateTime from = view[0].Time.LocalDateTime, to = view[^1].Time.LocalDateTime;
        string format = Value.HistoryHours <= 24 ? "HH:mm" : "dd.MM";
        for (int index = 0; index <= 4; index++)
        {
            double fraction = index / 4d;
            TextBlock label = ScaleLabel((from + (to - from) * fraction).ToString(format, CultureInfo.CurrentCulture));
            Canvas.SetLeft(label, Math.Clamp(fraction * width - fraction * label.DesiredSize.Width, 0, Math.Max(0, width - label.DesiredSize.Width)));
            HistoryTimeline.Children.Add(label);
        }
    }

    private TextBlock ScaleLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Foreground = (WpfBrush)Resources["SecondaryText"] };
        label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return label;
    }

    private void ChangelogClick(object sender, RoutedEventArgs e) => ChangelogRequested?.Invoke(this, EventArgs.Empty);

    private void ReportBugClick(object sender, RoutedEventArgs e)
    {
        var form = new BugReportWindow(Value.LogRawPackets) { Owner = this };
        if (form.ShowDialog() == true) BugReportRequested?.Invoke(form.Description);
    }
    private void ColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button) return;
        MediaColor current = button == NormalColorButton ? _normalColor : button == WarningColorButton ? _warningColor : _criticalColor;
        var picker = new WpfColorPickerWindow(current) { Owner = this };
        if (picker.ShowDialog() != true) return;
        MediaColor selected = picker.SelectedColor;
        if (button == NormalColorButton) _normalColor = selected;
        else if (button == WarningColorButton) _warningColor = selected;
        else _criticalColor = selected;
        UpdateColorButtons();
    }

    private void UpdateColorButtons()
    {
        SetColor(NormalColorButton, _normalColor);
        SetColor(WarningColorButton, _warningColor);
        SetColor(CriticalColorButton, _criticalColor);
    }

    private static void SetColor(WpfButton button, MediaColor color)
    {
        button.Background = AppTheme.Brush(color);
        button.Foreground = color.R + color.G + color.B < 360 ? WpfBrushes.White : WpfBrushes.Black;
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        if (!int.TryParse(WarningBox.Text, out int warning) || warning is < 5 or > 50)
        {
            _logger.Warning($"Settings validation failed: WarningThreshold='{LogValue(WarningBox.Text)}'; expected 5..50");
            AppDialog.Show(this, Strings.WarningThresholdError, Strings.AppName);
            return;
        }
        if (!int.TryParse(CriticalBox.Text, out int critical) || critical is < 1 or > 49)
        {
            _logger.Warning($"Settings validation failed: CriticalThreshold='{LogValue(CriticalBox.Text)}'; expected 1..49");
            AppDialog.Show(this, Strings.CriticalThresholdError, Strings.AppName);
            return;
        }
        if (critical >= warning)
        {
            _logger.Warning($"Settings validation failed: CriticalThreshold={critical} must be lower than WarningThreshold={warning}");
            AppDialog.Show(this, Strings.ThresholdError, Strings.AppName);
            return;
        }
        if (!int.TryParse(IntervalBox.Text, out int interval) || interval is < 15 or > 86400)
        {
            _logger.Warning($"Settings validation failed: RefreshIntervalSeconds='{LogValue(IntervalBox.Text)}'; expected 15..86400");
            AppDialog.Show(this, Strings.IntervalError, Strings.AppName);
            return;
        }
        string token = GitHubTokenBox.Password.Trim();
        if (DeveloperAccessPanel.Visibility == Visibility.Visible && token.Length > 0 && !GitHubUpdateService.LooksLikeToken(token))
        {
            _logger.Warning("Settings validation failed: GitHub token does not match the expected format");
            AppDialog.Show(this, Strings.TokenInvalid, Strings.AppName);
            return;
        }
        if (DeveloperAccessPanel.Visibility == Visibility.Visible && GitHubTokenChanged?.Invoke(token) == false) return;
        Value = Value with
        {
            StartWithWindows = StartupCheck.IsChecked == true,
            NotifyLowBattery = NotifyCheck.IsChecked == true,
            NotifyChargingConnected = NotifyChargingCheck.IsChecked == true,
            NotifyFullyCharged = NotifyFullCheck.IsChecked == true,
            NotifyAllDevices = NotifyScopeCombo.SelectedIndex == 1,
            WarningThreshold = warning,
            CriticalThreshold = critical,
            RefreshIntervalSeconds = interval,
            LogRawPackets = RawCheck.IsChecked == true,
            ShowAllDevicesInTrayTooltip = AllDevicesTooltipCheck.IsChecked == true,
            AutoSwitchDevice = AutoSwitchCheck.IsChecked == true,
            NormalColorArgb = ToArgb(_normalColor),
            WarningColorArgb = ToArgb(_warningColor),
            CriticalColorArgb = ToArgb(_criticalColor),
            IconStyle = (TrayIconStyle)IconStyleCombo.SelectedIndex,
            TrayIconScale = IconScaleCombo.SelectedIndex + 1
        };
        _saveFeedbackTimer.Stop();
        SaveButton.Content = Strings.Saving;
        SaveButton.IsEnabled = false;
        bool saved = false;
        try
        {
            saved = SettingsSaved is { } save && await save(Value);
            if (saved && IsVisible) ShowSaveConfirmation();
            DrawHistory();
        }
        finally
        {
            SaveButton.IsEnabled = true;
            if (!saved) SaveButton.Content = Strings.Save;
        }
    }

    private void ShowSaveConfirmation()
    {
        SaveButton.Content = $"✓ {Strings.Saved}";
        if (SystemParameters.ClientAreaAnimation)
        {
            var pulse = new DoubleAnimation(1, 1.035, TimeSpan.FromMilliseconds(130))
            {
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            SaveButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            SaveButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        _saveFeedbackTimer.Start();
    }

    private void SwitchDevClick(object sender, RoutedEventArgs e)
    {
        string token = GitHubTokenBox.Password.Trim();
        if (!GitHubUpdateService.LooksLikeToken(token))
        {
            AppDialog.Show(this, Strings.TokenInvalid, Strings.AppName);
            return;
        }
        ChannelSwitchRequested?.Invoke(ReleaseChannel.Dev, token);
    }

    private void SwitchPublicClick(object sender, RoutedEventArgs e) => ChannelSwitchRequested?.Invoke(ReleaseChannel.Public, string.Empty);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ApplyTheme);
    }

    private static MediaColor FromArgb(int argb) => MediaColor.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    private static int ToArgb(MediaColor color) => unchecked((int)((uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B));
    private static string LogValue(string value)
    {
        string sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 128 ? sanitized : sanitized[..128];
    }

    private static BitmapImage LoadLogo() => LoadEmbeddedImage("GeBar.App.g-e.ico");
    private static BitmapImage LoadEmbeddedImage(string resourceName)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException(Strings.LogoMissing);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

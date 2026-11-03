using GeBar.UI;
using GeBar.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Controls;

namespace GeBar.App;

internal sealed class BatteryTrayApplicationContext
{
    private IDeviceBatteryMonitor _monitor;
    private readonly Dictionary<string, IDeviceBatteryMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceBatteryState> _deviceStates = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DeviceEntry> _devices;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private FileSystemWatcher? _profileWatcher;
    private DateTimeOffset _profilesChangedAt;
    private readonly TrayIcon _tray;
    private readonly ContextMenu _menu;
    private readonly MenuItem _chargeItem = new() { IsEnabled = false };
    private readonly MenuItem _stateItem = new() { IsEnabled = false };
    private readonly MenuItem _deviceItem = new() { IsEnabled = false };
    private readonly SynchronizationContext _uiContext;
    private readonly Dictionary<string, BatteryNotificationRules> _notifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _diagnosticsMode;
    private readonly GitHubUpdateService _updates;
    private Task? _updateLoop;
    private GitHubRelease? _availableRelease;
    private string? _notifiedRelease;
    private bool _updateBalloonActive;
    private AppSettings _settings;
    private TrayIconVisual? _iconVisual;
    private string? _tooltip;
    private ControlCenterWindow? _window;
    private DeviceBatteryState _lastState = DeviceBatteryState.Missing;
    private bool _closing;
    private bool _startupGraceElapsed;
    private static readonly TimeSpan SwitchGrace = TimeSpan.FromSeconds(30);

    public BatteryTrayApplicationContext(IReadOnlyList<DeviceEntry> devices, FileLogger logger, AppSettings settings, bool diagnostics)
    {
        _devices = devices; _logger = logger; _diagnosticsMode = diagnostics; _updates = new GitHubUpdateService(logger);
        DeviceEntry selectedDevice = Selected(devices, settings.SelectedDeviceId);
        _settings = settings with { SelectedDeviceId = selectedDevice.Id };
        _logger.LogRawPackets = _settings.LogRawPackets || diagnostics;
        foreach (DeviceEntry entry in MonitorsAll(settings) ? devices : [selectedDevice])
            RegisterMonitor(entry);
        _monitor = _monitors[selectedDevice.Id];
        _uiContext = SynchronizationContext.Current ?? throw new InvalidOperationException("UI synchronization context is unavailable.");
        _menu = new ContextMenu { FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"), FontSize = 13 };
        _menu.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = AppTheme.ThemeSource });
        AppTheme.Apply(_menu.Resources);
        _menu.Items.Add(_deviceItem);
        RebuildTrayDeviceMenu();
        _menu.Items.Add(new Separator()); _menu.Items.Add(_chargeItem); _menu.Items.Add(_stateItem); _menu.Items.Add(new Separator());
        _menu.Items.Add(MenuCommand(Strings.Refresh, async () => await RefreshAsync()));
        _menu.Items.Add(MenuCommand(Strings.ControlCenter, ShowMain));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(MenuCommand(Strings.Exit, async () => await ExitAsync()));
        _tray = new TrayIcon(Strings.AppName) { Menu = _menu };
        _tray.DoubleClicked += (_, _) => ShowMain();
        _tray.BalloonClicked += (_, _) => { if (_updateBalloonActive) { _updateBalloonActive = false; ShowAvailableUpdate(); } };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        UpdateUi(_monitor.CurrentState);
        if (diagnostics) ShowMain();
        _ = StartAsync(); _updateLoop = RunUpdateLoopAsync(_lifetime.Token);
        _ = CheckStartupSelectionAsync(_lifetime.Token);
        WatchProfiles();
    }

    private static MenuItem MenuCommand(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _uiContext.Post(_ => AppTheme.Apply(_menu.Resources), null);

    private void OnDisplayChanged(object? sender, EventArgs e) => RedrawTrayIcon("display settings changed");

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.RemoteConnect or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)) return;
        RedrawTrayIcon($"session switch {e.Reason}");
    }

    private void RedrawTrayIcon(string reason) => _uiContext.Post(_ =>
    {
        _logger.Info($"Tray icon redrawn: {reason}");
        _iconVisual = null;
        UpdateUi(_lastState);
    }, null);

    private void WatchProfiles()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.Profiles);
            _profileWatcher = new FileSystemWatcher(AppPaths.Profiles, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _profileWatcher.Created += ProfilesChanged;
            _profileWatcher.Changed += ProfilesChanged;
            _profileWatcher.Deleted += ProfilesChanged;
            _profileWatcher.Renamed += ProfilesChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Warning($"Profiles folder is not watched: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ProfilesChanged(object sender, FileSystemEventArgs e)
    {
        if (_closing) return;
        DateTimeOffset moment = _profilesChangedAt = DateTimeOffset.Now;
        _ = Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token).ContinueWith(_ =>
        {
            if (moment != _profilesChangedAt) return;
            _uiContext.Post(async _ => { if (!_closing) await ReloadDevicesAsync(); }, null);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public void ShowMain()
    {
        ControlCenterWindow form = EnsureWindow();
        form.ApplyTheme(); form.UpdateState(_lastState, _monitor.Diagnostics, _settings); form.Show(); form.Activate();
    }
    private async Task StartAsync()
    {
        foreach ((string id, IDeviceBatteryMonitor monitor) in _monitors)
        {
            try { await monitor.StartAsync(_lifetime.Token); }
            catch (Exception ex) { _logger.Error($"Monitor start failed: {id}", ex); }
        }
    }

    private async Task CheckStartupSelectionAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SwitchGrace, token);
            if (_closing) return;
            _startupGraceElapsed = true;
            ChooseAvailableDevice();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private IDeviceBatteryMonitor RegisterMonitor(DeviceEntry entry)
    {
        IDeviceBatteryMonitor monitor = entry.Create();
        _monitors[entry.Id] = monitor;
        _revisions[entry.Id] = entry.Revision;
        _deviceStates[entry.Id] = monitor.CurrentState;
        string deviceId = entry.Id;
        monitor.StateChanged += (_, state) => MonitorStateChanged(deviceId, monitor, state);
        return monitor;
    }

    private static DeviceEntry Selected(IReadOnlyList<DeviceEntry> devices, string id) =>
        devices.FirstOrDefault(entry => entry.Id == id) ?? devices[0];

    private void RebuildTrayDeviceMenu()
    {
        _deviceItem.Items.Clear();
        foreach (DeviceEntry entry in _devices)
        {
            var item = new MenuItem { Header = entry.Name, Tag = entry.Id };
            item.Click += async (_, _) => await SelectTrayDeviceAsync(entry.Id);
            _deviceItem.Items.Add(item);
        }
        _deviceItem.IsEnabled = _devices.Count > 1;
        UpdateTrayDeviceSelection();
    }

    private void UpdateTrayDeviceSelection()
    {
        foreach (MenuItem item in _deviceItem.Items.OfType<MenuItem>())
        {
            DeviceEntry? entry = _devices.FirstOrDefault(device => string.Equals(device.Id, item.Tag as string, StringComparison.Ordinal));
            if (entry is null) continue;
            item.Header = entry.Name;
            item.IsChecked = string.Equals(entry.Id, _settings.SelectedDeviceId, StringComparison.Ordinal);
        }
    }

    private async Task SelectTrayDeviceAsync(string id)
    {
        if (string.Equals(id, _settings.SelectedDeviceId, StringComparison.Ordinal)) return;
        await SaveSettingsAsync(_settings with { SelectedDeviceId = id });
        _window?.SetDevices(_devices.Select(entry => (entry.Id, entry.Name)).ToArray(), _settings.SelectedDeviceId);
    }

    private async Task SwitchDeviceAsync(string id)
    {
        DeviceEntry entry = Selected(_devices, id);
        if (_monitors.TryGetValue(entry.Id, out IDeviceBatteryMonitor? active) && ReferenceEquals(active, _monitor)) return;
        string previousName = _monitor.CurrentState.DeviceName ?? _settings.SelectedDeviceId;
        _logger.Info($"Active device changed: from={previousName}, to={entry.Name}, id={entry.Id}");
        if (!_monitors.TryGetValue(entry.Id, out IDeviceBatteryMonitor? selectedMonitor))
        {
            selectedMonitor = RegisterMonitor(entry);
            try { await selectedMonitor.StartAsync(_lifetime.Token); }
            catch (Exception ex) { _logger.Error($"Monitor start failed: {entry.Id}", ex); }
        }
        _monitor = selectedMonitor;
        _window?.SetDevices(_devices.Select(device => (device.Id, device.Name)).ToArray(), entry.Id);
        UpdateUi(_monitor.CurrentState);
        await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        try
        {
            await _monitor.RefreshNowAsync(_lifetime.Token);
            _window?.ReloadHistory();
        }
        catch (Exception ex) { _logger.Error("Refresh failed", ex); }
    }
    private void MonitorStateChanged(string id, IDeviceBatteryMonitor source, DeviceBatteryState state)
    {
        _uiContext.Post(_ =>
        {
            if (_closing) return;
            if (!_monitors.TryGetValue(id, out IDeviceBatteryMonitor? current) || !ReferenceEquals(current, source)) return;
            _deviceStates[id] = state;
            bool selected = string.Equals(id, _settings.SelectedDeviceId, StringComparison.OrdinalIgnoreCase);
            if (selected) _lastState = state;
            bool moved = ChooseAvailableDevice();
            if (!moved)
            {
                if (selected) UpdateUi(state);
                else UpdateTooltip(_lastState);
            }
            if (string.Equals(id, _settings.SelectedDeviceId, StringComparison.OrdinalIgnoreCase) || _settings.NotifyAllDevices) Notify(id, state);
        }, null);
    }
    private static bool MonitorsAll(AppSettings settings) => settings.ShowAllDevicesInTrayTooltip || settings.NotifyAllDevices || settings.AutoSwitchDevice;

    private bool ChooseAvailableDevice()
    {
        if (_closing) return false;
        string? id = FindAutoSwitchTarget(_devices.Select(entry => entry.Id), _deviceStates, _settings, _startupGraceElapsed);
        if (id is null || !_monitors.TryGetValue(id, out IDeviceBatteryMonitor? monitor)) return false;
        _logger.Info($"Selected device is gone, switching selection: from={_settings.SelectedDeviceId}, to={id}");
        _settings = _settings with { SelectedDeviceId = id };
        _monitor = monitor;
        _window?.SetDevices(_devices.Select(entry => (entry.Id, entry.Name)).ToArray(), id);
        UpdateUi(monitor.CurrentState);
        _ = SaveAutoSelectionAsync(_settings);
        return true;
    }

    internal static string? FindAutoSwitchTarget(IEnumerable<string> devices, IReadOnlyDictionary<string, DeviceBatteryState> states,
        AppSettings settings, bool startupGraceElapsed) =>
        settings.AutoSwitchDevice && startupGraceElapsed
        && states.TryGetValue(settings.SelectedDeviceId, out DeviceBatteryState? own) && Gone(own)
            ? devices.FirstOrDefault(id => !string.Equals(id, settings.SelectedDeviceId, StringComparison.OrdinalIgnoreCase)
                && states.TryGetValue(id, out DeviceBatteryState? other) && !Gone(other) && other.Percentage is not null)
            : null;

    private async Task SaveAutoSelectionAsync(AppSettings settings)
    {
        try { await settings.SaveAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.Error("Automatic device selection save failed", ex); }
    }

    private static bool Gone(DeviceBatteryState state) => state.ConnectionState == DeviceConnectionState.DeviceNotFound
        || state.ConnectionState == DeviceConnectionState.DeviceOff && state.Percentage is null;

    private void UpdateUi(DeviceBatteryState state)
    {
        _lastState = state;
        _deviceItem.Header = state.DeviceName ?? Strings.DefaultDevice;
        UpdateTrayDeviceSelection();
        _chargeItem.Header = $"{Strings.Charge}: {(state.Percentage is int p ? $"{p}%" : Strings.EmptyValue)}";
        _stateItem.Header = $"{Strings.State}: {Strings.Status(state)}";
        UpdateTooltip(state);
        var visual = new TrayIconVisual(state.ConnectionState, state.Percentage, state.IsCharging, _settings.IconStyle, _settings.TrayIconScale, _settings.NormalColorArgb, _settings.WarningColorArgb, _settings.CriticalColorArgb, _settings.WarningThreshold, _settings.CriticalThreshold, TrayIconFactory.IconSize());
        if (_iconVisual != visual)
        {
            _iconVisual = visual;
            _tray.SetIcon(TrayIconFactory.Create(state, _settings));
        }
        _window?.UpdateState(state, _monitor.Diagnostics, _settings);
    }

    private void Notify(string id, DeviceBatteryState state)
    {
        if (state.Percentage is not int percentage) return;
        if (!_notifications.TryGetValue(id, out BatteryNotificationRules? rules)) _notifications[id] = rules = new BatteryNotificationRules();
        foreach (BatteryNotificationKind notification in rules.Evaluate(percentage, state.IsCharging == true, _settings.WarningThreshold, _settings.CriticalThreshold))
            if (ShouldShowNotification(notification)) ShowNotification(notification, percentage, state.DeviceName);
    }

    private void UpdateTooltip(DeviceBatteryState selectedState)
    {
        string tooltip = Strings.TrayTooltip(selectedState);
        if (_settings.ShowAllDevicesInTrayTooltip)
        {
            string[] connected = _deviceStates.Values
                .Where(state => state.Percentage is not null
                    && state.ConnectionState is DeviceConnectionState.DeviceConnected or DeviceConnectionState.DeviceOff)
                .OrderBy(state => state.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .Select(state => $"{state.DeviceName}: {state.Percentage}%{(state.IsCharging == true ? " ⚡" : state.ConnectionState == DeviceConnectionState.DeviceOff ? $" ({Strings.Status(state)})" : string.Empty)}")
                .ToArray();
            if (connected.Length > 0) tooltip = Fit(connected);
        }
        tooltip = tooltip[..Math.Min(tooltip.Length, TooltipLimit)];
        if (!string.Equals(_tooltip, tooltip, StringComparison.Ordinal))
        {
            _tooltip = tooltip;
            _tray.Tooltip = tooltip;
        }
    }
    private const int TooltipLimit = TrayIcon.TooltipLimit;

    private static string Fit(IReadOnlyList<string> lines)
    {
        var kept = new List<string>();
        int length = 0;
        foreach (string line in lines)
        {
            int added = kept.Count == 0 ? line.Length : line.Length + Environment.NewLine.Length;
            if (length + added > TooltipLimit) break;
            kept.Add(line);
            length += added;
        }
        return kept.Count == 0 ? lines[0] : string.Join(Environment.NewLine, kept);
    }

    private bool ShouldShowNotification(BatteryNotificationKind kind) => kind switch
    {
        BatteryNotificationKind.ChargingStarted => _settings.NotifyChargingConnected,
        BatteryNotificationKind.FullyCharged => _settings.NotifyFullyCharged,
        _ => _settings.NotifyLowBattery
    };
    private void ShowNotification(BatteryNotificationKind kind, int percentage, string? deviceName)
    {
        _updateBalloonActive = false;
        string text = kind switch
        {
            BatteryNotificationKind.Critical => Strings.CriticalNotice(percentage),
            BatteryNotificationKind.Warning => Strings.WarningNotice(percentage),
            BatteryNotificationKind.ChargingStarted => Strings.ChargingNotice(percentage),
            _ => Strings.FullNotice
        };
        _logger.Info($"Notification: {kind}, device {deviceName ?? "unknown"}, battery {percentage}%");
        try { ShowAppBalloon(deviceName ?? Strings.DefaultDevice, text); }
        catch (InvalidOperationException ex) { _logger.Warning($"Battery notification could not be displayed: {ex.Message}"); }
    }

    private void ShowAppBalloon(string title, string text) => _tray.ShowBalloon(title, text);
    private async Task<bool> SaveSettingsAsync(AppSettings settings)
    {
        string previousDevice = _settings.SelectedDeviceId;
        int previousInterval = _settings.RefreshIntervalSeconds;
        _settings = settings.Validate();
        _logger.LogRawPackets = _settings.LogRawPackets;
        bool saved = true;
        try { await _settings.SaveAsync(); StartupManager.SetEnabled(_settings.StartWithWindows); UpdateUi(_monitor.CurrentState); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saved = false; _logger.Error("Settings save failed", ex); AppDialog.Show(null, Strings.SettingsSaveError, Strings.AppName); }
        if (_settings.RefreshIntervalSeconds != previousInterval) await ReloadDevicesAsync();
        if (_settings.SelectedDeviceId != previousDevice) await SwitchDeviceAsync(_settings.SelectedDeviceId);
        await SynchronizeBackgroundMonitorsAsync();
        return saved;
    }

    private async Task SynchronizeBackgroundMonitorsAsync()
    {
        HashSet<string> desired = MonitorsAll(_settings)
            ? _devices.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([_settings.SelectedDeviceId], StringComparer.OrdinalIgnoreCase);

        foreach (DeviceEntry entry in _devices.Where(entry => desired.Contains(entry.Id)
            && _monitors.ContainsKey(entry.Id)
            && _revisions.GetValueOrDefault(entry.Id) != entry.Revision).ToArray())
        {
            _logger.Info($"Device configuration changed, monitor rebuilt: id={entry.Id}");
            await StopMonitorAsync(entry.Id);
        }

        foreach (DeviceEntry entry in _devices.Where(entry => desired.Contains(entry.Id) && !_monitors.ContainsKey(entry.Id)))
        {
            IDeviceBatteryMonitor monitor = RegisterMonitor(entry);
            try { await monitor.StartAsync(_lifetime.Token); }
            catch (Exception ex) { _logger.Error($"Monitor start failed: {entry.Id}", ex); }
        }
        foreach (string id in _monitors.Keys.Where(id => !desired.Contains(id)).ToArray())
        {
            _deviceStates.Remove(id);
            _notifications.Remove(id);
            await StopMonitorAsync(id);
        }
        if (_monitors.TryGetValue(_settings.SelectedDeviceId, out IDeviceBatteryMonitor? selected) && !ReferenceEquals(selected, _monitor))
        {
            _monitor = selected;
            UpdateUi(_monitor.CurrentState);
        }
        ChooseAvailableDevice();
    }

    private async Task StopMonitorAsync(string id)
    {
        if (!_monitors.TryGetValue(id, out IDeviceBatteryMonitor? monitor)) return;
        _monitors.Remove(id);
        _revisions.Remove(id);
        try { await monitor.DisposeAsync(); }
        catch (Exception ex) { _logger.Error($"Monitor shutdown failed: {id}", ex); }
    }

    private async Task ReloadDevicesAsync()
    {
        IReadOnlyList<DeviceEntry> previousDevices = _devices;
        IReadOnlyList<DeviceEntry> rebuilt = DeviceCatalog.Build(_logger, TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds));
        if (rebuilt.Count == 0)
        {
            _logger.Warning("Device reload returned no profiles; keeping the previous list");
            return;
        }
        _devices = rebuilt;
        foreach (DeviceEntry entry in _devices.Where(entry => entry.Id != DeviceCatalog.BuiltInId && previousDevices.All(previous => previous.Id != entry.Id)))
            _logger.Info($"Profile added: name={entry.Name}, id={entry.Id}");
        foreach (DeviceEntry entry in previousDevices.Where(entry => entry.Id != DeviceCatalog.BuiltInId && _devices.All(current => current.Id != entry.Id)))
            _logger.Info($"Profile removed: name={entry.Name}, id={entry.Id}");

        if (_devices.All(entry => !entry.Id.Equals(_settings.SelectedDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            DeviceEntry fallback = _devices[0];
            _settings = _settings with { SelectedDeviceId = fallback.Id };
            await _settings.SaveAsync();
        }
        await SynchronizeBackgroundMonitorsAsync();
        _monitor = _monitors[_settings.SelectedDeviceId];
        UpdateUi(_monitor.CurrentState);
        RebuildTrayDeviceMenu();
        _window?.SetDevices(_devices.Select(entry => (entry.Id, entry.Name)).ToArray(), _settings.SelectedDeviceId);
    }
    private bool SaveGitHubToken(string token)
    {
        try { _updates.SetToken(token); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.Error("Cannot save GitHub token", ex);
            AppDialog.Show(null, Strings.TokenSaveError, Strings.AppName);
            return false;
        }
    }

    private async Task RunUpdateLoopAsync(CancellationToken token)
    {
        if (!_updates.IsConfigured) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                await CheckUpdatesAsync(false).ConfigureAwait(false);
                failures = _updates.LastCheckFailed ? failures + 1 : 0;
                await Task.Delay(TimeSpan.FromHours(failures == 0 ? 1 : Math.Min(8, 1 << Math.Min(failures, 3))), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckUpdatesAsync(bool interactive, ReleaseChannel? targetChannel = null)
    {
        GitHubRelease? release = await _updates.CheckAsync(_lifetime.Token, allowStableRollback: interactive, targetChannel: targetChannel).ConfigureAwait(false);
        _uiContext.Post(_ =>
        {
            if (release is null)
            {
                if (interactive) AppDialog.Show(null,
                    !_updates.IsConfigured ? Strings.UpdateUnavailable
                    : !_updates.LastCheckFailed ? Strings.NoUpdates
                    : _updates.LastAccess switch
                    {
                        UpdateAccess.NoToken => Strings.TokenMissing,
                        UpdateAccess.TokenRejected => Strings.TokenRejected,
                        UpdateAccess.TokenWithoutRepository => Strings.TokenWithoutAccess,
                        _ => Strings.UpdateUnavailable
                    }, Strings.AppName);
                return;
            }
            _availableRelease = release;
            if (interactive) ShowAvailableUpdate();
            else if (!release.AppUpdateAvailable) return;
            else if (!string.Equals(_notifiedRelease, release.Tag, StringComparison.Ordinal))
            {
                _notifiedRelease = release.Tag;
                _updateBalloonActive = true;
                ShowAppBalloon(Strings.NewVersion, Strings.AvailableVersion(release.Version.ToString()));
            }
        }, null);
    }

    private async Task SwitchChannelAsync(ReleaseChannel channel, string token)
    {
        if (channel == ReleaseChannel.Dev)
        {
            try { _updates.SetToken(token); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.Error("Cannot save GitHub token", ex);
                AppDialog.Show(null, Strings.TokenSaveError, Strings.AppName);
                return;
            }
        }
        await CheckUpdatesAsync(true, channel);
    }

    private void ShowAvailableUpdate()
    {
        if (_availableRelease is null) return;
        var form = new UpdateWindow(_availableRelease, _updates, _logger);
        form.ShowDialog();
        if (form.InstallStarted) { _ = ExitAsync(); return; }
        _window?.UpdateHidState();
    }
    private void LaunchHid()
    {
        try { GitHubUpdateService.LaunchHid(); }
        catch (FileNotFoundException)
        {
            AppDialog.Show(null, Strings.HidInstallHint, Strings.AppName);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.Error("Cannot launch g-eHID", ex);
            AppDialog.Show(null, Strings.HidLaunchError, Strings.AppName);
        }
    }
    private void RemoveHid()
    {
        try
        {
            GitHubUpdateService.RemoveHid();
            _window?.UpdateHidState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("Cannot remove g-eHID", ex);
            AppDialog.Show(null, Strings.HidRemoveError, Strings.AppName);
        }
    }
    private void OpenLogs()
    {
        try { System.IO.Directory.CreateDirectory(_logger.LogDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _logger.LogDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { _logger.Error("Cannot open logs", ex); }
    }
    private IReadOnlyList<string> BugReportDetails()
    {
        DeviceEntry active = Selected(_devices, _settings.SelectedDeviceId);
        HidDiagnostics diagnostics = _monitor.Diagnostics;
        var lines = new List<string>
        {
            $"**Device:** {_lastState.DeviceName ?? active.Name} (`{active.Id}`)",
            $"**Profile:** {active.Source}",
            $"**Connected:** {(diagnostics.Connected is { Count: > 0 } connected ? string.Join(" + ", connected) : "none")}",
            $"**State:** {Describe(_lastState, diagnostics)}",
            $"**Last read:** {LastRead(_lastState)}",
            $"**Protocol:** {diagnostics.ProtocolName ?? "not found"}"
        };
        if (diagnostics.ProbingPaused is string paused) lines.Add($"**Probing:** {paused}");
        if (diagnostics.LastError is string error) lines.Add($"**Last error:** {error}");
        KeyValuePair<string, DeviceBatteryState>[] others = [.. _deviceStates.Where(pair => !string.Equals(pair.Key, active.Id, StringComparison.OrdinalIgnoreCase))];
        if (others.Length == 0) return lines;
        lines.Add(string.Empty);
        lines.Add("**Other devices:**");
        int number = 1;
        foreach ((string id, DeviceBatteryState state) in others)
        {
            HidDiagnostics? other = _monitors.GetValueOrDefault(id)?.Diagnostics;
            lines.Add($"{number++}. {state.DeviceName} (`{id}`): {Describe(state, other)} / {other?.ProtocolName ?? "protocol not found"} / last read {LastRead(state)}");
        }
        return lines;
    }

    private static string Describe(DeviceBatteryState state, HidDiagnostics? diagnostics) =>
        $"{state.ConnectionState}, {(state.Percentage is int percentage ? $"{percentage}%" : "battery unknown")}"
        + (state.IsCharging == true ? $", charging ({diagnostics?.ChargingSource ?? "source unknown"})" : string.Empty)
        + (state.Transport == ConnectionTransport.Bluetooth ? ", Bluetooth" : string.Empty);

    private static string LastRead(DeviceBatteryState state)
    {
        if (state.LastSuccessfulRead is not DateTimeOffset time) return "never";
        TimeSpan age = DateTimeOffset.Now - time;
        string ago = age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 2 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} d ago";
        return $"{time.LocalDateTime:HH:mm:ss}, {ago}";
    }

    private void ReportBug(string description)
    {
        (string identifier, string report, Uri? issueUri, string? logFolder) = _updates.CreateBugReport(description,
            $"Refresh {_settings.RefreshIntervalSeconds} s / raw packets {(_settings.LogRawPackets ? "on" : "off")}", BugReportDetails());
        try
        {
            if (issueUri is not null)
            {
                Process.Start(new ProcessStartInfo(issueUri.AbsoluteUri) { UseShellExecute = true });
                if (logFolder is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logFolder}\"") { UseShellExecute = true });
                return;
            }
            System.Windows.Clipboard.SetText(report);
            AppDialog.Show(null, Strings.BugReportingUnavailable, identifier);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            _logger.Error("Cannot open bug report", ex);
        }
    }
    private async Task ExitAsync()
    {
        if (_closing) return; _closing = true;
        _lifetime.Cancel(); _tray.Hide();
        try
        {
            await Task.WhenAll(_monitors.Values.Select(monitor => monitor.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.Error("Monitor shutdown did not complete cleanly", ex);
        }
        finally
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _window?.Close(); _profileWatcher?.Dispose(); _tray.Dispose(); _updates.Dispose(); _lifetime.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }

    private ControlCenterWindow EnsureWindow()
    {
        if (_window is not null) return _window;
        _window = new ControlCenterWindow(_settings, _diagnosticsMode, _updates.Token, _logger);
        _window.Closed += (_, _) => _window = null;
        _window.OpenLogsRequested += (_, _) => OpenLogs();
        _window.CheckUpdatesRequested += async (_, _) => await CheckUpdatesAsync(true);
        _window.HidLaunchRequested += (_, _) => LaunchHid();
        _window.HidRemoveRequested += (_, _) => RemoveHid();
        _window.BugReportRequested += ReportBug;
        _window.ChangelogRequested += async (_, _) => await UpdateWindow.ShowChangelogAsync(_window!, _updates, _logger, null);
        _window.GitHubTokenChanged += SaveGitHubToken;
        _window.ChannelSwitchRequested += async (channel, token) => await SwitchChannelAsync(channel, token);
        _window.SettingsSaved += async settings => await SaveSettingsAsync(settings);
        _window.DevicesRefreshRequested += async (_, _) => { await ReloadDevicesAsync(); await RefreshAsync(); };
        _window.SetDevices(_devices.Select(entry => (entry.Id, entry.Name)).ToArray(), _settings.SelectedDeviceId);
        return _window;
    }

    private readonly record struct TrayIconVisual(DeviceConnectionState State, int? Percentage, bool? IsCharging, TrayIconStyle Style, int Scale, int NormalColor, int WarningColor, int CriticalColor, int WarningThreshold, int CriticalThreshold, int IconSize);
}

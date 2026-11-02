using GeBar.UI;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GeBar.Core;
using System.Globalization;
using System.Reflection;
using GeBar.Hid;
using Microsoft.Win32;
using System.Collections.ObjectModel;

namespace GeBar.HidApp;

internal partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private DeviceItem? _selectedDevice;
    private InterfaceItem? _selectedInterface;
    private HidCaptureSession? _capture;
    private readonly List<HidCaptureSession> _listeners = [];
    private byte[]? _lastCapturedPacket;
    private int _loggedPackets;
    private readonly List<string> _probeTrace = [];
    private readonly List<string> _packetTrace = [];
    private string? _capturedInterfacePath;
    private byte[]? _chargingBaseline;
    private int[]? _chargingCandidates;
    private bool _extendedMode;
    private CancellationTokenSource? _sweep;
    private CancellationTokenSource? _sleepWatch;
    private bool _updatingLabel;
    private readonly ObservableCollection<DeviceItem> _devices = [];
    private readonly Dictionary<string, HidProfileExport> _profileDocuments = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumPacketChars = 400_000;
    private const int LoggedPacketsPerCapture = 200;
    private const int ExportedPackets = 300;
    private readonly FileLogger _logger;

    private static string ProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "g-eBAR",
        "Profiles");

    public MainWindow(FileLogger logger)
    {
        _logger = logger;
        InitializeComponent();
        ExtendedModeCheck.Visibility = BuildChannel.Current == ReleaseChannel.Dev ? Visibility.Visible : Visibility.Collapsed;
        AppTheme.Apply(Resources);
        SourceInitialized += (_, _) => Windows11WindowStyler.Apply(this);
        Loaded += (_, _) => RefreshDevices();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Closed += async (_, _) => await StopCaptureAsync();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => AppTheme.ApplyTo(this));
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshDevices();

    private void AboutClick(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void ExtendedModeChanged(object sender, RoutedEventArgs e)
    {
        _extendedMode = ExtendedModeCheck.IsChecked == true;
        _logger.LogRawPackets = _extendedMode;
        SweepButton.Visibility = _extendedMode ? Visibility.Visible : Visibility.Collapsed;
        _logger.Info($"Extended mode: {(_extendedMode ? "on" : "off")}");
    }

    private void RefreshDevices()
    {
        try
        {
            _devices.Clear();
            HidDeviceProfile[] installed = LoadInstalledProfiles();
            HidDeviceProfile[] builtIn = [.. LoadBuiltInProfiles().Where(profile => !installed.Any(item => item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))];
            HidDeviceProfile[] scanned = HidDeviceScanner.Scan().ToArray();
            var consumed = new bool[scanned.Length];
            foreach ((HidDeviceProfile saved, bool isBuiltIn) in installed.Select(item => (item, false)).Concat(builtIn.Select(item => (item, true))))
            {
                HidDeviceProfile profile = saved;
                bool found = false;
                for (int index = 0; index < scanned.Length; index++)
                {
                    if (!saved.AllConnections().Any(connection => connection.VendorId == scanned[index].VendorId && connection.ProductId == scanned[index].ProductId)) continue;
                    profile = RefreshConnection(profile, scanned[index]);
                    consumed[index] = true;
                    found = true;
                }
                _devices.Add(new DeviceItem(profile, true, isBuiltIn) { IsOnline = found });
            }
            for (int index = 0; index < scanned.Length; index++)
                if (!consumed[index]) _devices.Add(new DeviceItem(scanned[index], false));
            DevicesList.ItemsSource = _devices;
            DevicesList.SelectedIndex = _devices.Count > 0 ? 0 : -1;
            StatusText.Text = Strings.ModelsFound(_devices.Count);
            _logger.Info($"Devices scanned: models={_devices.Count}, installed={installed.Length}, built-in={builtIn.Length}");
        }
        catch (Exception ex)
        {
            StatusText.Text = Strings.ScanError;
            _logger.Error("Device scan failed", ex);
        }
    }

    private async void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await CancelSleepWatchAsync();
        await StopCaptureAsync();
        _selectedDevice = DevicesList.SelectedItem as DeviceItem;
        _selectedInterface = null;
        bool live = _selectedDevice?.IsOnline == true;
        ExportButton.IsEnabled = _selectedDevice is not null;
        SendToAppButton.IsEnabled = _selectedDevice is not null;
        MergeButton.IsEnabled = DevicesList.SelectedItems.Count == 2;
        FindChargingButton.IsEnabled = live && _selectedDevice?.Profile.Battery.PercentageOffset is not null;
        SweepButton.IsEnabled = live;
        RememberOffButton.IsEnabled = live;
        _chargingBaseline = null;
        _chargingCandidates = null;
        UpdateProfileAction();
        BatteryRequestButton.IsEnabled = live;
        DeviceNameText.Text = _selectedDevice?.Name ?? Strings.SelectDevice;
        DeviceMetaText.Text = _selectedDevice switch
        {
            null => string.Empty,
            { Profile.Connections: { Count: > 0 } } device => Strings.MergedDevices(string.Join("  +  ", device.Profile.AllConnections().Select(Describe))),
            { Profile: HidDeviceProfile profile } => Strings.DeviceMetadata(profile.VendorId, profile.ProductId, profile.Manufacturer, profile.Interfaces.Count)
        };
        InterfacesList.ItemsSource = _selectedDevice?.Interfaces;
        InterfacesList.SelectedIndex = _selectedDevice is null ? -1 : 0;
        _updatingLabel = true;
        ProfileLabelBox.Text = _selectedDevice?.Profile.Label ?? string.Empty;
        ProfileLabelBox.IsEnabled = _selectedDevice is not null;
        _updatingLabel = false;
    }

    private static HidDeviceProfile AsProfile(HidDeviceProfile profile, HidDeviceConnectionProfile connection) =>
        new(profile.Id, profile.Name, connection.VendorId, connection.ProductId, connection.Manufacturer,
            connection.SerialNumber, connection.Interfaces, connection.Battery, connection.IsVerified, profile.Label, null);

    private static HidDeviceProfile WithBattery(HidDeviceProfile profile, HidDeviceConnectionProfile connection, HidBatteryProtocolProfile battery)
    {
        battery = HidProfileCompatibility.ReplaceProtocol(connection.Battery, battery);
        if (profile.VendorId == connection.VendorId && profile.ProductId == connection.ProductId)
            return profile with { Battery = battery, IsVerified = true };
        return profile with
        {
            Connections = [.. (profile.Connections ?? []).Select(item =>
                item.VendorId == connection.VendorId && item.ProductId == connection.ProductId
                    ? item with { Battery = battery, IsVerified = true }
                    : item)]
        };
    }

    private static string Describe(HidDeviceConnectionProfile connection) =>
        $"{connection.VendorId:X4}:{connection.ProductId:X4} {connection.Manufacturer ?? Strings.UnknownVendor} ({connection.Interfaces.Count} HID)";

    private void ProfileLabelChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingLabel || _selectedDevice is null) return;
        _selectedDevice.Profile = _selectedDevice.Profile with { Label = string.IsNullOrWhiteSpace(ProfileLabelBox.Text) ? null : ProfileLabelBox.Text.Trim() };
        DevicesList.Items.Refresh();
        DeviceNameText.Text = _selectedDevice.Name;
        UpdateProfileAction();
    }

    private void ProfileLabelLostFocus(object sender, RoutedEventArgs e) => UpdateProfileAction();

    private async void MergeProfilesClick(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItems.Cast<DeviceItem>().ToArray() is not [DeviceItem primary, DeviceItem secondary]) return;
        await StopCaptureAsync();
        var connections = primary.Profile.AllConnections()
            .Concat(secondary.Profile.AllConnections())
            .GroupBy(connection => $"{connection.VendorId:X4}:{connection.ProductId:X4}:{connection.SerialNumber ?? connection.Battery.DevicePath ?? string.Empty}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(connection => connection.Battery.Source != HidBatterySource.None)
            .ThenByDescending(connection => connection.IsVerified)
            .ToArray();
        HidDeviceConnectionProfile first = connections[0];
        primary.Profile = primary.Profile with
        {
            VendorId = first.VendorId,
            ProductId = first.ProductId,
            Manufacturer = first.Manufacturer,
            SerialNumber = first.SerialNumber,
            Interfaces = first.Interfaces,
            Battery = first.Battery,
            IsVerified = first.IsVerified,
            Label = string.IsNullOrWhiteSpace(ProfileLabelBox.Text) ? primary.Profile.Label : ProfileLabelBox.Text.Trim(),
            Connections = connections.Skip(1).ToArray()
        };
        bool persistMergedProfile = primary.IsAdded || secondary.IsAdded;
        if (persistMergedProfile)
        {
            Directory.CreateDirectory(ProfilesDirectory);
            if (!await TryWriteProfileAsync(GetProfilePath(primary.Profile), primary.Profile)) return;
            primary.MarkSaved();
        }
        string secondaryPath = GetProfilePath(secondary.Profile);
        if (secondary.IsAdded && !secondaryPath.Equals(GetProfilePath(primary.Profile), StringComparison.OrdinalIgnoreCase) && File.Exists(secondaryPath))
        {
            try { File.Delete(secondaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning($"Merged profile saved, but {Path.GetFileName(secondaryPath)} could not be removed: {ex.Message}");
            }
        }
        primary.IsAdded = persistMergedProfile;
        _devices.Remove(secondary);
        DevicesList.SelectedItem = primary;
        DevicesList.Items.Refresh();
        StatusText.Text = Strings.ProfilesMerged;
        _logger.Info($"Profiles merged: id={primary.Profile.Id}, connections={primary.Profile.AllConnections().Count()}");
    }

    private async void InterfaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedInterface = InterfacesList.SelectedItem as InterfaceItem;
        _lastCapturedPacket = null;
        _capturedInterfacePath = null;
        CaptureButton.IsEnabled = _selectedInterface is not null && _selectedInterface.Profile.InputReportLength > 0;
        UpdateBatteryRequestAvailability();
        await StopCaptureAsync();
    }

    private async void CaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capture is not null) { await StopCaptureAsync(); return; }
        if (_selectedInterface is null) return;
        try
        {
            await StartCaptureAsync(_selectedInterface);
            StatusText.Text = Strings.InterfaceOpened;
            _loggedPackets = 0;
            _logger.Info($"Capture started: IN={_selectedInterface.Profile.InputReportLength} path={_selectedInterface.Profile.DevicePath}");
            UpdateBatteryRequestAvailability();
        }
        catch (Exception ex)
        {
            StatusText.Text = Strings.NoInputInterface;
            CaptureStatusText.Text = Strings.CaptureUnavailable;
            _logger.Warning($"Capture could not start: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyProtocolConstraints(HidBatteryProtocolProfile battery)
    {
        FindChargingButton.IsEnabled = _selectedDevice?.IsOnline == true && battery.PercentageOffset is not null;
        CaptureButton.IsEnabled = _selectedInterface?.Profile.InputReportLength > 0;
    }

    private bool StillSelected(DeviceItem device)
    {
        if (ReferenceEquals(_selectedDevice, device)) return true;
        _logger.Info($"Result discarded, another device is selected: id={device.Profile.Id}");
        return false;
    }

    private async Task CancelSleepWatchAsync()
    {
        if (_sleepWatch is null) return;
        try { await _sleepWatch.CancelAsync(); }
        catch (ObjectDisposedException) { }
    }

    private async void FindChargingClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) return;
        DeviceItem device = _selectedDevice;
        HidBatteryProtocolProfile battery = device.Profile.Battery;
        if (battery.Source == HidBatterySource.None || battery.PercentageOffset is null)
        {
            StatusText.Text = Strings.ChargingNeedsProtocol;
            return;
        }
        try
        {
            FindChargingButton.IsEnabled = false;
            BatteryProbeResult? probe = await UniversalBatteryProbe.ProbeAsync(device.Profile, TimeSpan.FromSeconds(2), CancellationToken.None);
            if (!StillSelected(device)) return;
            if (probe?.Response is not { Length: > 0 } response) { StatusText.Text = Strings.ChargingNotFound; return; }

            if (_chargingBaseline is null)
            {
                _chargingBaseline = response;
                StatusText.Text = Strings.ChargingStepOne;
                AppendMessage($"CHARGING BASELINE {Convert.ToHexString(response.AsSpan(0, Math.Min(16, response.Length)))}");
                return;
            }

            byte[] baseline = _chargingBaseline;
            int[] changed = Diff(baseline, response, battery.PercentageOffset.Value);
            if (_chargingCandidates is int[] previous) changed = [.. changed.Where(previous.Contains)];
            _chargingBaseline = null;
            if (changed.Length == 0)
            {
                bool identical = baseline.Length == response.Length && baseline.SequenceEqual(response);
                StatusText.Text = identical ? Strings.ChargingUnsupported : Strings.ChargingNotFound;
                _logger.Warning($"Charging byte not found: id={device.Profile.Id}, answers {(identical ? "identical" : "differ outside the flag range")}");
                _chargingCandidates = null;
                return;
            }
            if (changed.Length > 1)
            {
                _chargingCandidates = changed;
                StatusText.Text = Strings.ChargingAmbiguous(changed.Length);
                _logger.Info($"Charging byte candidates: id={device.Profile.Id}, offsets={string.Join(", ", changed)}");
                return;
            }
            _chargingCandidates = null;

            int offset = changed[0];
            int value = response[offset];
            device.Profile = device.Profile with { Battery = battery with { ChargingOffset = offset, ChargingValue = value } };
            UpdateProfileAction();
            StatusText.Text = Strings.ChargingFound(offset, value);
            AppendMessage($"CHARGING SET byte {offset} = {value}");
            _logger.Info($"Charging byte found: id={device.Profile.Id}, offset={offset}, value={value}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            StatusText.Text = Strings.ChargingNotFound;
            _logger.Error("Charging byte search failed", ex);
        }
        finally { FindChargingButton.IsEnabled = _selectedDevice?.IsOnline == true && _selectedDevice?.Profile.Battery.PercentageOffset is not null; }
    }

    private static int[] Diff(byte[] first, byte[] second, int percentageOffset)
    {
        int length = Math.Min(first.Length, second.Length);
        var changed = new List<int>();
        for (int index = 0; index < length; index++)
        {
            if (index == percentageOffset || first[index] == second[index]) continue;
            if (!LooksLikeFlag(first[index], second[index])) continue;
            changed.Add(index);
        }
        return [.. changed];
    }

    private static bool LooksLikeFlag(byte before, byte after) =>
        (before | after) <= 3 || int.PopCount(before ^ after) == 1;

    private async void BatteryRequestClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) return;
        DeviceItem device = _selectedDevice;
        try
        {
            BatteryRequestButton.IsEnabled = false;
            StatusText.Text = Strings.CheckingBattery;
            _probeTrace.Clear();
            var trace = new TraceLogger(_logger, _probeTrace);
            HidBatteryProtocolProfile? firstResolved = null;
            int answered = 0;
            foreach (HidDeviceConnectionProfile connection in device.Profile.AllConnections().ToArray())
            {
                BatteryProbeResult? automatic = await UniversalBatteryProbe.ProbeAsync(AsProfile(device.Profile, connection), TimeSpan.FromSeconds(2), CancellationToken.None, trace);
                if (!StillSelected(device)) return;
                if (automatic?.Reading is not { } reading) continue;
                HidBatteryProtocolProfile resolved = automatic.Profile with
                {
                    OffPrefixHex = connection.Battery.OffPrefixHex,
                    StatusDevicePath = connection.Battery.StatusDevicePath
                };
                device.Profile = WithBattery(device.Profile, connection, resolved);
                answered++;
                if (firstResolved is null)
                {
                    firstResolved = resolved;
                    StatusText.Text = Strings.BatteryFound(reading.Percentage, reading.IsCharging, reading.Detail);
                }
                AppendMessage($"BATTERY {connection.VendorId:X4}:{connection.ProductId:X4} {reading.Percentage}% via {reading.Source} ({reading.Detail})");
                _logger.Info($"Battery detected: id={device.Profile.Id}, connection={connection.VendorId:X4}:{connection.ProductId:X4}, battery={reading.Percentage}%, protocol={reading.Detail}");
            }
            if (firstResolved is not null)
            {
                UpdateProfileAction();
                _chargingBaseline = null;
                _chargingCandidates = null;
                if (answered > 1) StatusText.Text = Strings.BatteryFoundOnConnections(answered);
                ApplyProtocolConstraints(firstResolved);
                return;
            }

            InterfaceItem? readableInterface = await StartFirstAvailableCaptureAsync(device.Interfaces);
            if (!StillSelected(device)) return;
            if (readableInterface is not null)
            {
                StatusText.Text = Strings.PassiveCaptureStarted;
                AppendMessage("PASSIVE HID ANALYSIS STARTED");
            }
            else StatusText.Text = Strings.NoInputInterface;
            _logger.Warning($"Battery not detected: id={device.Profile.Id}, passive capture={(readableInterface is not null ? "started" : "unavailable")}");
            foreach (HidInterfaceProfile item in device.Profile.Interfaces)
                _logger.Info($"  interface IN={item.InputReportLength} OUT={item.OutputReportLength} F={item.FeatureReportLength} usage={item.UsagePage?.ToString("X4") ?? "-"} path={item.DevicePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            StatusText.Text = Strings.NoInputInterface;
            _logger.Error("Battery detection failed", ex);
        }
        finally { UpdateBatteryRequestAvailability(); }
    }

    private async Task StartCaptureAsync(InterfaceItem item)
    {
        var capture = new HidCaptureSession(item.Profile.DevicePath);
        capture.PacketReceived += PacketReceived;
        capture.CaptureFailed += CaptureFailed;
        await capture.StartAsync();
        _capture = capture;
        _capturedInterfacePath = item.Profile.DevicePath;
        CaptureButton.Content = Strings.StopCapture;
        CaptureStatusText.Text = Strings.Capturing;
    }

    private async Task<InterfaceItem?> StartFirstAvailableCaptureAsync(IEnumerable<InterfaceItem> interfaces)
    {
        await StopCaptureAsync();
        InterfaceItem? first = null;
        foreach (InterfaceItem item in interfaces.Where(item => item.Profile.InputReportLength > 0 && item.IsPresent)
            .OrderByDescending(item => item.Profile.InputReportLength))
        {
            try
            {
                var listener = new HidCaptureSession(item.Profile.DevicePath);
                listener.PacketReceived += packet => PacketReceived(packet, item);
                listener.CaptureFailed += CaptureFailed;
                await listener.StartAsync();
                _listeners.Add(listener);
                _logger.Info($"Listening: IN={item.Profile.InputReportLength} path={item.Profile.DevicePath}");
                if (first is null) { first = item; InterfacesList.SelectedItem = item; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.Info($"Interface busy, not listening: {item.Profile.DevicePath}");
            }
        }
        if (first is null) return null;
        _capturedInterfacePath = first.Profile.DevicePath;
        CaptureButton.Content = Strings.StopCapture;
        CaptureStatusText.Text = Strings.Capturing;
        return first;
    }

    private void ClearPacketsClick(object sender, RoutedEventArgs e) => PacketsText.Clear();

    private void CaptureFailed(Exception exception)
    {
        _logger.Warning($"Capture stopped: {exception.GetType().Name}: {exception.Message}");
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => CaptureStatusText.Text = Strings.CaptureUnavailable);
    }

    private void PacketReceived(ReadOnlyMemory<byte> packet) => PacketReceived(packet, null);

    private void PacketReceived(ReadOnlyMemory<byte> packet, InterfaceItem? source)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            _lastCapturedPacket = packet.ToArray();
            AppendPacket(source is null ? "RX" : $"RX {source.Index}", packet.Span);
        });
    }

    private void CollectPacket(string line)
    {
        if (_packetTrace.Count > 0)
        {
            string last = _packetTrace[^1];
            int repeat = last.LastIndexOf(" x", StringComparison.Ordinal);
            string previous = repeat < 0 ? last : last[..repeat];
            if (previous == line)
            {
                int count = repeat < 0 ? 1 : int.Parse(last[(repeat + 2)..], CultureInfo.InvariantCulture);
                _packetTrace[^1] = $"{line} x{count + 1}";
                return;
            }
        }
        _packetTrace.Add(line);
        if (_packetTrace.Count > ExportedPackets) _packetTrace.RemoveAt(0);
    }

    private async void RememberOffClick(object sender, RoutedEventArgs e)
    {
        if (_sleepWatch is not null)
        {
            await _sleepWatch.CancelAsync();
            return;
        }
        if (_selectedDevice is null) return;
        DeviceItem device = _selectedDevice;
        var dialog = new SleepWatchWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!StillSelected(device)) return;

        await StopCaptureAsync();
        _sleepWatch = new CancellationTokenSource();
        RememberOffButton.Content = Strings.StopSleepWatch;
        _logger.Info($"Sleep watch started: id={device.Profile.Id}, sleep after {dialog.Seconds}s");
        try
        {
            var progress = new Progress<SleepWatchProgress>(update => StatusText.Text = update.Phase switch
            {
                SleepWatchPhase.Idle => Strings.SleepWatchIdle(update.SecondsLeft, update.Packets),
                SleepWatchPhase.Sleeping => Strings.SleepWatchListening(update.SecondsLeft, update.Packets),
                _ => Strings.SleepWatchWake(update.SecondsLeft, update.Packets)
            });
            SleepWatchResult? result = await SleepWatch.RunAsync(device.Profile, TimeSpan.FromSeconds(dialog.Seconds), progress, _sleepWatch.Token);
            ApplySleepWatch(device, result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Strings.SleepWatchStopped;
            _logger.Info("Sleep watch stopped");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = Strings.SleepWatchNoInterface;
            _logger.Error("Sleep watch failed", ex);
        }
        finally
        {
            _sleepWatch.Dispose();
            _sleepWatch = null;
            RememberOffButton.Content = Strings.RememberOff;
        }
    }

    private void ApplySleepWatch(DeviceItem device, SleepWatchResult? result)
    {
        if (!StillSelected(device)) return;
        if (result is null)
        {
            StatusText.Text = Strings.SleepWatchNoInterface;
            _logger.Warning("Sleep watch: no interface could be listened to");
            return;
        }
        _logger.Info($"Sleep watch finished: interfaces={result.Interfaces}, packets={result.Packets}, candidates={result.Candidates.Count}");
        foreach (string candidate in result.Candidates) _logger.Info($"  sleep candidate {candidate}");

        if (result.OffPrefixHex is not string prefix)
        {
            StatusText.Text = result.Candidates.Count == 0 ? Strings.SleepWatchSilent : Strings.SleepWatchAmbiguous(result.Candidates.Count);
            AppendMessage($"SLEEP no marker, candidates {result.Candidates.Count}");
            return;
        }
        device.Profile = device.Profile with
        {
            Battery = device.Profile.Battery with { OffPrefixHex = prefix, StatusDevicePath = result.DevicePath }
        };
        UpdateProfileAction();
        StatusText.Text = Strings.OffPacketSaved(prefix);
        AppendMessage($"OFF SET {prefix}");
        _logger.Info($"Off packet saved: id={device.Profile.Id}, prefix={prefix}, path={result.DevicePath}");
    }

    private async Task StopCaptureAsync()
    {
        if (_capture is not null)
        {
            await _capture.DisposeAsync();
            _capture = null;
            _logger.Info("Capture stopped");
        }
        if (_listeners.Count > 0)
        {
            foreach (HidCaptureSession listener in _listeners) await listener.DisposeAsync();
            _logger.Info($"Listening stopped on {_listeners.Count} interfaces");
            _listeners.Clear();
        }
        CaptureButton.Content = Strings.StartCapture;
        CaptureStatusText.Text = Strings.NotRunning;
        UpdateBatteryRequestAvailability();
    }

    private void UpdateBatteryRequestAvailability()
    {
        BatteryRequestButton.IsEnabled = _selectedDevice?.IsOnline == true;
    }

    private void AppendPacket(string direction, ReadOnlySpan<byte> packet)
    {
        string hex = string.Join(' ', Convert.ToHexString(packet).Chunk(2).Select(pair => new string(pair)));
        string line = $"{direction} [{packet.Length}] {hex}";
        if (_extendedMode || _loggedPackets++ < LoggedPacketsPerCapture) _logger.Info($"packet {line}");
        CollectPacket(line);
        Append($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    private void Append(string line)
    {
        if (PacketsText.Text.Length > MaximumPacketChars)
            PacketsText.Text = PacketsText.Text[(PacketsText.Text.Length - (MaximumPacketChars / 2))..];
        PacketsText.AppendText(line + Environment.NewLine);
        PacketsText.ScrollToEnd();
    }

    private void AppendMessage(string message) => Append($"{DateTime.Now:HH:mm:ss.fff} {message}");

    private async void SweepClick(object sender, RoutedEventArgs e)
    {
        if (_sweep is not null)
        {
            try { await _sweep.CancelAsync(); }
            catch (ObjectDisposedException) { }
            return;
        }
        if (_selectedDevice is null) return;
        DeviceItem device = _selectedDevice;
        if (!AppDialog.Confirm(this, Strings.SweepWarning, Strings.AppName, Strings.SweepCommands, Strings.Cancel)) return;

        await StopCaptureAsync();
        _sweep = new CancellationTokenSource();
        SweepButton.Content = Strings.StopSweep;
        _logger.Info($"Command sweep started: id={device.Profile.Id}");
        try
        {
            var progress = new Progress<string>(line =>
            {
                AppendMessage($"SWEEP {line}");
                _logger.Info($"  sweep {line}");
            });
            IReadOnlyList<SweepAnswer> answers = await VendorCommandSweep.RunAsync(device.Profile, progress, _sweep.Token);
            StatusText.Text = answers.Count == 0 ? Strings.SweepUnavailable : Strings.SweepFinished(answers.Count);
            _logger.Info($"Command sweep finished: id={device.Profile.Id}, answers={answers.Count}");
        }
        catch (SweepStopped stopped)
        {
            StatusText.Text = stopped.Answers.Count == 0 ? Strings.SweepStopped : Strings.SweepFinished(stopped.Answers.Count);
            _logger.Info($"Command sweep stopped: id={device.Profile.Id}, answers so far={stopped.Answers.Count}");
            foreach (SweepAnswer answer in stopped.Answers)
                _logger.Info($"  kept 0x{answer.Command:X2} {Convert.ToHexString(answer.Response.AsSpan(0, Math.Min(answer.Response.Length, 20)))}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Strings.SweepStopped;
            _logger.Info("Command sweep stopped");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = Strings.SweepStopped;
            _logger.Error("Command sweep failed", ex);
        }
        finally
        {
            _sweep.Dispose();
            _sweep = null;
            SweepButton.Content = Strings.SweepCommands;
        }
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) return;
        var dialog = new SaveFileDialog
        {
            Title = Strings.ExportTitle,
            Filter = Strings.JsonFilter,
            FileName = $"{_selectedDevice.Profile.Id}.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!await TryWriteProfileAsync(dialog.FileName, _selectedDevice.Profile, includeDiagnostics: true)) return;
        StatusText.Text = Strings.ProfileExported(dialog.FileName);
        _logger.Info($"Profile exported: id={_selectedDevice.Profile.Id}, file={Path.GetFileName(dialog.FileName)}");
    }

    private async void SendToAppClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null) return;
        string path = GetProfilePath(_selectedDevice.Profile);

        if (_selectedDevice.IsAdded && _selectedDevice.HasPendingChanges)
        {
            if (!await TryWriteProfileAsync(path, _selectedDevice.Profile)) return;
            _selectedDevice.MarkSaved();
            StatusText.Text = Strings.ProfileUpdatedInGeBar;
            _logger.Info($"Profile updated in g-eBAR: id={_selectedDevice.Profile.Id}, name={_selectedDevice.Name}");
            UpdateProfileAction();
            return;
        }

        if (_selectedDevice.IsAdded && File.Exists(path))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText.Text = Strings.ProfileWriteFailed;
                _logger.Error($"Profile could not be removed: {path}", ex);
                return;
            }
            _selectedDevice.IsAdded = false;
            StatusText.Text = Strings.ProfileRemoved;
            _logger.Info($"Profile removed from g-eBAR: id={_selectedDevice.Profile.Id}");
        }
        else
        {
            if (!await TryWriteProfileAsync(path, _selectedDevice.Profile)) return;
            _selectedDevice.IsAdded = true;
            _selectedDevice.MarkSaved();
            StatusText.Text = Strings.ProfileAdded;
            _logger.Info($"Profile added to g-eBAR: id={_selectedDevice.Profile.Id}, name={_selectedDevice.Name}");
            if (!_extendedMode)
            {
                AppDialog.Show(this, Strings.ProfileAddedNotice, Strings.AppName);
            }
        }

        DevicesList.Items.Refresh();
        UpdateProfileAction();
    }

    private void UpdateProfileAction()
    {
        SendToAppButton.Content = _selectedDevice switch
        {
            { IsBuiltIn: true } => Strings.UpdateInGeBar,
            { IsAdded: true, HasPendingChanges: true } => Strings.UpdateInGeBar,
            { IsAdded: true } => Strings.RemoveFromGeBar,
            _ => Strings.AddToGeBar
        };
        if (_selectedDevice is { IsBuiltIn: true } builtIn) SendToAppButton.IsEnabled = builtIn.HasPendingChanges;
    }

    private static string GetProfilePath(HidDeviceProfile profile) => Path.Combine(ProfilesDirectory, $"{profile.Id}.json");

    private static HidDeviceProfile[] LoadBuiltInProfiles()
    {
        try
        {
            using Stream? stream = typeof(MainWindow).Assembly.GetManifestResourceStream("GeBar.HidApp.built-in.json");
            if (stream is null) return [];
            using var reader = new StreamReader(stream);
            return [.. JsonSerializer.Deserialize<HidProfileExport>(reader.ReadToEnd(), JsonOptions)?.Devices ?? []];
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return []; }
    }

    private HidDeviceProfile[] LoadInstalledProfiles()
    {
        _profileDocuments.Clear();
        if (!Directory.Exists(ProfilesDirectory)) return [];
        var profiles = new List<HidDeviceProfile>();
        foreach (string file in Directory.GetFiles(ProfilesDirectory, "*.json"))
        {
            try
            {
                HidProfileExport? export = JsonSerializer.Deserialize<HidProfileExport>(File.ReadAllText(file), JsonOptions);
                if (export.Problem() is not null) continue;
                foreach (HidDeviceProfile profile in export!.Devices.Where(profile => profile.Problem() is null))
                {
                    profiles.Add(profile);
                    _profileDocuments[profile.Id] = export;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return profiles.ToArray();
    }

    private static HidDeviceProfile RefreshConnection(HidDeviceProfile saved, HidDeviceProfile scanned)
    {
        if (saved.VendorId == scanned.VendorId && saved.ProductId == scanned.ProductId)
            return saved with { Manufacturer = scanned.Manufacturer, SerialNumber = scanned.SerialNumber, Interfaces = HidProfileCompatibility.RefreshInterfaces(saved.Interfaces, scanned.Interfaces) };
        HidDeviceConnectionProfile[] connections = (saved.Connections ?? []).Select(connection =>
            connection.VendorId == scanned.VendorId && connection.ProductId == scanned.ProductId
                ? connection with { Manufacturer = scanned.Manufacturer, SerialNumber = scanned.SerialNumber, Interfaces = HidProfileCompatibility.RefreshInterfaces(connection.Interfaces, scanned.Interfaces) }
                : connection).ToArray();
        return saved with { Connections = connections };
    }

    private async Task WriteProfileAsync(string path, HidDeviceProfile profile, bool includeDiagnostics = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ProfilesDirectory);
        HidExportDiagnostics? diagnostics = !includeDiagnostics || (_probeTrace.Count == 0 && _packetTrace.Count == 0)
            ? null
            : new HidExportDiagnostics(CurrentVersion(), WindowsRelease.Describe(), [.. _probeTrace], [.. _packetTrace]);
        HidProfileExport? previous = _profileDocuments.GetValueOrDefault(profile.Id);
        if (File.Exists(path))
        {
            HidProfileExport? destination = JsonSerializer.Deserialize<HidProfileExport>(await File.ReadAllTextAsync(path), JsonOptions);
            if (destination.Problem() is string problem) throw new InvalidDataException(problem);
            previous = destination;
        }
        HidProfileExport export = HidProfileCompatibility.UpdateExport(previous, profile, diagnostics);
        string json = JsonSerializer.Serialize(export, JsonOptions);

        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, json);
        try
        {
            if (JsonSerializer.Deserialize<HidProfileExport>(await File.ReadAllTextAsync(temporary)).Problem() is string broken)
                throw new InvalidDataException($"written profile did not read back: {broken}");
            File.Move(temporary, path, true);
            foreach (HidDeviceProfile saved in export.Devices) _profileDocuments[saved.Id] = export;
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string CurrentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString(3) ?? "1.0.0" : informational;
    }

    private sealed class TraceLogger(FileLogger inner, List<string> sink) : IAppLogger
    {
        public string LogDirectory => inner.LogDirectory;
        public bool IsRawPacketLoggingEnabled => inner.IsRawPacketLoggingEnabled;
        public void Info(string message) { inner.Info(message); sink.Add(message.Trim()); }
        public void Warning(string message) { inner.Warning(message); sink.Add(message.Trim()); }
        public void Error(string message, Exception? exception = null) { inner.Error(message, exception); sink.Add($"{message}: {exception?.Message}"); }
    }

    private async Task<bool> TryWriteProfileAsync(string path, HidDeviceProfile profile, bool includeDiagnostics = false)
    {
        try
        {
            await WriteProfileAsync(path, profile, includeDiagnostics);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or JsonException or InvalidDataException)
        {
            StatusText.Text = Strings.ProfileWriteFailed;
            _logger.Error($"Profile could not be saved: {path}", ex);
            return false;
        }
    }

    private sealed class DeviceItem
    {
        private HidDeviceProfile _savedProfile;

        public DeviceItem(HidDeviceProfile profile, bool isAdded, bool isBuiltIn = false)
        {
            Profile = profile;
            _savedProfile = profile;
            IsAdded = isAdded;
            IsBuiltIn = isBuiltIn;
        }
        public HidDeviceProfile Profile { get; set; }
        public bool IsAdded { get; set; }
        public bool IsBuiltIn { get; }
        public bool IsOnline { get; init; } = true;
        public bool HasPendingChanges => IsAdded && Profile != _savedProfile;
        public void MarkSaved() => _savedProfile = Profile;
        public string Name => Profile.DisplayName;
        public string Identifier => (IsBuiltIn ? Strings.BuiltIn + " · " : string.Empty)
            + (IsOnline ? string.Empty : Strings.DeviceOffline + " · ")
            + (Profile.Connections is { Count: > 0 }
                ? string.Join(" + ", Profile.AllConnections().Select(connection => $"{connection.VendorId:X4}:{connection.ProductId:X4}"))
                : $"{Profile.VendorId:X4}:{Profile.ProductId:X4} · {Profile.Interfaces.Count} HID");
        public IReadOnlyList<InterfaceItem> Interfaces =>
        [
            .. Profile.AllConnections()
                .SelectMany(connection => connection.Interfaces.Select(item => (Connection: connection, Interface: item)))
                .Select((pair, index) => new InterfaceItem(pair.Interface, index + 1,
                    Profile.Connections is { Count: > 0 } ? $"{pair.Connection.VendorId:X4}:{pair.Connection.ProductId:X4}" : null,
                    HidSharp.DeviceList.Local.GetHidDevices(pair.Connection.VendorId, pair.Connection.ProductId).Any()))
        ];
    }


    private sealed class InterfaceItem
    {
        public InterfaceItem(HidInterfaceProfile profile, int index, string? connection = null, bool present = true)
        {
            Profile = profile;
            Index = index;
            Connection = connection;
            IsPresent = present;
        }
        public HidInterfaceProfile Profile { get; }
        public int Index { get; }
        public string? Connection { get; }
        public bool IsPresent { get; }
        public string ShortPath => (Connection is null ? string.Empty : $"{Connection} · ") + Strings.InterfacePath(Index, Profile.DevicePath);
        public string ReportSizes => $"IN {Profile.InputReportLength} / OUT {Profile.OutputReportLength} / F {Profile.FeatureReportLength}";
    }
}

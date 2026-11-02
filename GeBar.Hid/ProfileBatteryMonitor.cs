using GeBar.Core;
using HidSharp;
using System.Runtime.InteropServices;

namespace GeBar.Hid;

public sealed class ProfileBatteryMonitor(HidDeviceProfile profile, IAppLogger logger, TimeSpan interval) : IDeviceBatteryMonitor
{
    private readonly HidDeviceProfile profile = KnownBatteryProtocols.Normalize(profile);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    private static readonly TimeSpan SleepFreeze = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SleepPoll = TimeSpan.FromSeconds(30);
    private TimeSpan Freeze => interval * 2 > SleepFreeze ? interval * 2 : SleepFreeze;
    private static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan QuickWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SlopeSpan = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Echo = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ChargingIdle = TimeSpan.FromMinutes(30);
    private TimeSpan ChargingStall => interval * 3 > ChargingIdle ? interval * 3 : ChargingIdle;
    private const int TrendRise = 3;
    private const int QuietProbes = 3;
    private static readonly TimeSpan ProbeBackoff = TimeSpan.FromMinutes(30);
    private const int PlugRise = 5;
    private const int UnplugDrop = 4;
    private const int SlopeSamples = 4;
    private const double ChargingSlope = 1.5;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly Lock _state = new();
    private int _failures;
    private readonly List<(DateTimeOffset Time, int Percentage)> _trend = [];
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastRiseAt;
    private int? _lastLevel;
    private string? _topology;
    private bool? _trendCharging;
    private bool _refused;
    private DateTimeOffset? _offSince;
    private int? _offPercentage;
    private int? _lastKnown;
    private readonly Dictionary<(int Vendor, int Product), (int Quiet, DateTimeOffset Resume)> _probes = [];
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private HidBatteryProtocolProfile? _resolved;
    private HidBatteryProtocolProfile? _chargingSource;
    private bool _chargingSourceSearched;
    private Task? _listener;
    private CancellationTokenSource? _listenerLifetime;
    private string? _listeningPath;
    private int _listenerGeneration;
    private HidDeviceConnectionProfile? _activeConnection;
    private bool _disposed;

    public DeviceBatteryState CurrentState { get; private set; } = DeviceBatteryState.Missing with { DeviceName = profile.DisplayName };
    private HidDiagnostics _diagnostics = new(profile.Battery.DevicePath, null, null);
    private IReadOnlyList<string> _connected = [];
    private string? _chargingReason;
    private (string Connection, DateTimeOffset Resume)[] _paused = [];
    public HidDiagnostics Diagnostics => _diagnostics with
    {
        Connected = _connected,
        ChargingSource = _trendCharging == true ? _chargingReason : null,
        ProbingPaused = Paused()
    };
    public event EventHandler<DeviceBatteryState>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is not null) return Task.CompletedTask;
        _startedAt = DateTimeOffset.Now;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = RunAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        try { await _requests.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        try
        {
            Dictionary<(int Vendor, int Product), HidDevice[]> live = LiveHandles();
            Retopologize(live);
            ConnectionScan scan = await ScanConnectionsAsync(live, cancellationToken).ConfigureAwait(false);
            _resolved = scan.Protocol ?? _resolved;
            HidBatteryReading? reading = scan.Reading ?? ReadBluetooth();
            if (reading is null) PublishWithoutReading(scan, live, cancellationToken);
            else PublishReading(reading, scan.Protocol, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException or FormatException or COMException)
        {
            logger.Error($"Profile {profile.Name}: battery read failed", ex);
            Fail(ex.Message);
        }
        finally { _requests.Release(); }
    }

    private readonly record struct ConnectionScan(HidBatteryReading? Reading, HidBatteryProtocolProfile? Protocol, bool AnyHandles, bool Asleep);

    private Dictionary<(int Vendor, int Product), HidDevice[]> LiveHandles()
    {
        var live = new Dictionary<(int Vendor, int Product), HidDevice[]>();
        foreach (HidDeviceConnectionProfile connection in profile.AllConnections())
            if (!live.ContainsKey((connection.VendorId, connection.ProductId)))
                live[(connection.VendorId, connection.ProductId)] = [.. DeviceList.Local.GetHidDevices(connection.VendorId, connection.ProductId)];
        return live;
    }

    private async Task<ConnectionScan> ScanConnectionsAsync(Dictionary<(int Vendor, int Product), HidDevice[]> live, CancellationToken token)
    {
        HidBatteryReading? reading = null;
        HidBatteryProtocolProfile? protocol = null;
        bool anyHandles = false;
        _refused = false;
        (int Vendor, int Product)? answered = null;
        foreach (HidDeviceConnectionProfile connection in profile.AllConnections())
        {
            HidDevice[] handles = live[(connection.VendorId, connection.ProductId)];
            if (handles.Length == 0) continue;
            anyHandles = true;
            if (answered == (connection.VendorId, connection.ProductId)) continue;
            if (_activeConnection is not null && !Same(_activeConnection, connection)) _resolved = null;
            HidBatteryReading? candidate = await ReadAsync(connection, handles, token).ConfigureAwait(false);
            if (logger.IsRawPacketLoggingEnabled)
                logger.Info($"Profile {profile.Name}: connection {connection.VendorId:X4}:{connection.ProductId:X4} handles={handles.Length} answer={(candidate is null ? "none" : $"{candidate.Percentage}% charging={candidate.IsCharging?.ToString() ?? "unknown"} ({candidate.Detail})")}");
            if (candidate is null) continue;
            answered = (connection.VendorId, connection.ProductId);
            if (reading is null) { reading = candidate; _activeConnection = connection; protocol = _resolved ?? connection.Battery; }
            if (candidate.IsCharging == true) { reading = candidate; _activeConnection = connection; protocol = _resolved ?? connection.Battery; break; }
        }
        return new ConnectionScan(reading, protocol, anyHandles, reading is null && _refused);
    }

    private void PublishReading(HidBatteryReading reading, HidBatteryProtocolProfile? protocol, CancellationToken token)
    {
        DeviceBatteryState? published;
        lock (_state)
        {
            _failures = 0;
            _diagnostics = _diagnostics with { LastError = null, LastRx = $"{reading.Percentage}% ({reading.Detail})", ProtocolName = protocol?.ProtocolName ?? reading.Detail };
            published = Awake(reading)
                ? Commit(DeviceConnectionState.DeviceConnected, reading.Percentage, Trend(reading.Percentage, reading.IsCharging), DateTimeOffset.Now,
                    transport: reading.Source == HidBatterySource.None ? ConnectionTransport.Bluetooth : ConnectionTransport.Default)
                : Commit(DeviceConnectionState.DeviceOff, null, null, CurrentState.LastSuccessfulRead);
        }
        Announce(published);
        if (reading.Source != HidBatterySource.None) StartListener(token);
    }

    private void PublishWithoutReading(ConnectionScan scan, Dictionary<(int Vendor, int Product), HidDevice[]> live, CancellationToken token)
    {
        bool anyHandles = scan.AnyHandles;
        DeviceBatteryState? published;
        if (scan.Asleep)
        {
            lock (_state) published = Asleep();
            Announce(published);
            return;
        }
        if (anyHandles && Passive(live) is HidDeviceConnectionProfile passive)
        {
            _activeConnection = passive;
            _resolved = passive.Battery;
            StartListener(token);
            lock (_state) published = Idle();
            Announce(published);
            return;
        }
        lock (_state)
        {
            _failures++;
            published = anyHandles ? Failed("Profile returned no battery level.") : Offline();
        }
        Announce(published);
    }

    private DeviceBatteryState? Asleep()
    {
        _trend.Clear();
        _lastLevel = null;
        return Commit(DeviceConnectionState.DeviceOff, CurrentState.Percentage ?? _lastKnown, false, CurrentState.LastSuccessfulRead);
    }

    private bool Awake(HidBatteryReading reading)
    {
        if (_offSince is null) return true;
        if (DateTimeOffset.Now - _offSince > Freeze)
        {
            logger.Info($"Profile {profile.Name}: the off packet is {Freeze.TotalMinutes:0} min old and the device keeps answering, awake again");
            _offSince = null;
            _offPercentage = null;
            return true;
        }
        if (reading.IsCharging != true && reading.Percentage == _offPercentage) return false;
        logger.Info($"Profile {profile.Name}: device answered after the off packet, awake again");
        _offSince = null;
        _offPercentage = null;
        return true;
    }

    private DeviceBatteryState? Idle()
    {
        if (_offSince is not null) return Commit(DeviceConnectionState.DeviceOff, null, null, CurrentState.LastSuccessfulRead);
        if (CurrentState.LastSuccessfulRead is not DateTimeOffset lastRead) return Commit(DeviceConnectionState.Reading, null, null, null);
        if (DateTimeOffset.Now - lastRead <= Freeze) return null;
        _trend.Clear();
        _trendCharging = null;
        return Commit(DeviceConnectionState.DeviceNotFound, null, null, lastRead);
    }

    private DeviceBatteryState? Offline()
    {
        if (CurrentState.Percentage is int percentage
            && CurrentState.LastSuccessfulRead is DateTimeOffset lastRead
            && DateTimeOffset.Now - lastRead <= Freeze)
        {
            _trend.Clear();
            _trendCharging = null;
            return Commit(DeviceConnectionState.Reading, percentage, null, lastRead);
        }
        return Commit(DeviceConnectionState.DeviceNotFound, null, null, CurrentState.LastSuccessfulRead);
    }

    private async Task<HidBatteryReading?> ReadAsync(HidDeviceConnectionProfile connection, HidDevice[] handles, CancellationToken token)
    {
        HidBatteryProtocolProfile battery = _resolved ?? connection.Battery;
        HidDevice? handle = battery.DevicePath is null ? null : handles.FirstOrDefault(item => HidDeviceScanner.PathOf(item).Equals(battery.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (handle is not null && battery.RequestKind != HidRequestKind.None)
        {
            HidReplay replay = await HidBatteryReader.ReplayAsync(handle, battery, token, logger.IsRawPacketLoggingEnabled ? logger : null).ConfigureAwait(false);
            if (replay.Reading is not null) return await WithCharging(replay.Reading, handles, token).ConfigureAwait(false);
            if (replay.Answered)
            {
                _refused = true;
                _lastKnown = replay.LastKnown ?? _lastKnown;
                if (logger.IsRawPacketLoggingEnabled)
                    logger.Info($"Profile {profile.Name}: {connection.VendorId:X4}:{connection.ProductId:X4} returned a confirmed sleep response");
                return null;
            }
        }
        if (handle is not null && battery.Percentage is not null)
        {
            HidBatteryReading? direct = await HidBatteryReader.TryReadAsync(handle, battery, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (direct is not null) return direct;
        }
        (int Vendor, int Product) key = (connection.VendorId, connection.ProductId);
        if (_probes.TryGetValue(key, out var backoff) && DateTimeOffset.Now < backoff.Resume) return null;
        _resolved = null;
        if (logger.IsRawPacketLoggingEnabled)
            logger.Info($"Profile {profile.Name}: {connection.VendorId:X4}:{connection.ProductId:X4} did not answer the stored protocol, probing again");
        HidDeviceProfile live = HidDeviceScanner.ScanDevice(connection.VendorId, connection.ProductId)
            ?? new(profile.Id, profile.Name, connection.VendorId, connection.ProductId, connection.Manufacturer,
                connection.SerialNumber, connection.Interfaces, connection.Battery, connection.IsVerified, profile.Label, profile.Connections);
        BatteryProbeResult? probe = await UniversalBatteryProbe.ProbeAsync(live, TimeSpan.FromSeconds(3), token,
            logger.IsRawPacketLoggingEnabled ? logger : null).ConfigureAwait(false);
        if (probe?.Reading is null)
        {
            Quiet(key, connection);
            if (probe?.ForeignTraffic is byte[] traffic && logger.IsRawPacketLoggingEnabled)
                logger.Info($"Profile {profile.Name}: {connection.VendorId:X4}:{connection.ProductId:X4} answers no known protocol, its own traffic is {Convert.ToHexString(traffic.AsSpan(0, Math.Min(traffic.Length, 16)))}");
            if (probe?.RefusedAnswer is byte[] answer)
            {
                _refused = true;
                _resolved = Merge(connection.Battery, probe.Profile);
                _lastKnown = HidBatteryReader.ParseReply(answer, _resolved).LastKnown ?? _lastKnown;
                _diagnostics = _diagnostics with { SelectedDevicePath = _resolved.DevicePath };
                logger.Info($"Profile {profile.Name}: {connection.VendorId:X4}:{connection.ProductId:X4} confirmed sleep: {Convert.ToHexString(answer.AsSpan(0, Math.Min(answer.Length, 16)))}");
            }
            return null;
        }
        _probes.Remove(key);
        SnapshotPaused();
        _resolved = Merge(connection.Battery, probe.Profile);
        _diagnostics = _diagnostics with { SelectedDevicePath = _resolved.DevicePath };
        if (logger.IsRawPacketLoggingEnabled)
            logger.Info($"Profile {profile.Name}: {connection.VendorId:X4}:{connection.ProductId:X4} answered {probe.Reading.Percentage}% via {_resolved.ProtocolName ?? probe.Reading.Detail}, charging={probe.Reading.IsCharging?.ToString() ?? "unknown"}, path={_resolved.DevicePath}");
        return probe.Reading with { IsCharging = ReadCharging(probe.Response, _resolved) ?? probe.Reading.IsCharging };
    }

    private void Quiet((int Vendor, int Product) key, HidDeviceConnectionProfile connection)
    {
        int quiet = _probes.GetValueOrDefault(key).Quiet + 1;
        bool pause = quiet >= QuietProbes;
        _probes[key] = (pause ? 0 : quiet, pause ? DateTimeOffset.Now + ProbeBackoff : DateTimeOffset.MinValue);
        SnapshotPaused();
        if (pause) logger.Info($"Profile {profile.Name}: {connection.VendorId:X4}:{connection.ProductId:X4} answered no probe {QuietProbes} times in a row, probing paused for {ProbeBackoff.TotalMinutes:0} min");
    }

    private void SnapshotPaused() => _paused = [.. _probes
        .Where(item => item.Value.Resume != DateTimeOffset.MinValue)
        .Select(item => ($"{item.Key.Vendor:X4}:{item.Key.Product:X4}", item.Value.Resume))];

    private string? Paused()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string[] paused = [.. _paused.Where(item => item.Resume > now)
            .Select(item => $"{item.Connection} paused until {item.Resume.LocalDateTime:HH:mm} after {QuietProbes} empty probes")];
        return paused.Length == 0 ? null : string.Join("; ", paused);
    }

    private async Task<HidBatteryReading> WithCharging(HidBatteryReading reading, HidDevice[] handles, CancellationToken token)
    {
        if (reading.IsCharging is not null) return reading;
        if (!_chargingSourceSearched)
        {
            _chargingSourceSearched = true;
            _chargingSource = handles
                .Select(HidBatteryDescriptorResolver.TryResolve)
                .FirstOrDefault(found => found?.Charging is not null || found?.Discharging is not null);
            if (_chargingSource is not null)
                logger.Info($"Profile {profile.Name}: charging state taken from the HID battery descriptor, path={_chargingSource.DevicePath}");
        }
        if (_chargingSource is null) return reading;
        HidDevice? source = handles.FirstOrDefault(item => HidDeviceScanner.PathOf(item).Equals(_chargingSource.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (source is null) return reading;
        HidBatteryReading? descriptor = await HidBatteryReader.TryReadAsync(source, _chargingSource, TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        return descriptor?.IsCharging is null ? reading : reading with { IsCharging = descriptor.IsCharging };
    }

    private HidDeviceConnectionProfile? Passive(Dictionary<(int Vendor, int Product), HidDevice[]> live) => profile.AllConnections()
        .FirstOrDefault(connection => live.GetValueOrDefault((connection.VendorId, connection.ProductId)) is { Length: > 0 }
            && connection.Battery.RequestKind == HidRequestKind.None
            && connection.Battery.MatchPrefixHex is not null
            && connection.Battery.PercentageOffset is not null);

    private static bool Same(HidDeviceConnectionProfile left, HidDeviceConnectionProfile right) =>
        left.VendorId == right.VendorId && left.ProductId == right.ProductId
        && string.Equals(left.SerialNumber, right.SerialNumber, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Battery.DevicePath, right.Battery.DevicePath, StringComparison.OrdinalIgnoreCase);

    private static HidBatteryProtocolProfile Merge(HidBatteryProtocolProfile declared, HidBatteryProtocolProfile discovered) => discovered with
    {
        ChargingOffset = discovered.ChargingOffset ?? declared.ChargingOffset,
        ChargingValue = discovered.ChargingOffset is null ? declared.ChargingValue : discovered.ChargingValue,
        OffPrefixHex = declared.OffPrefixHex ?? discovered.OffPrefixHex,
        StatusDevicePath = declared.StatusDevicePath ?? discovered.StatusDevicePath,
        BluetoothNames = declared.BluetoothNames ?? discovered.BluetoothNames
    };

    private static bool? ReadCharging(byte[]? response, HidBatteryProtocolProfile battery)
    {
        if (response is null || battery.ChargingOffset is not int offset || response.Length <= offset) return null;
        return battery.ChargingValue is int expected ? response[offset] == expected : response[offset] != 0;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.Error($"Profile {profile.Name}: refresh loop error", ex);
                Fail(ex.Message);
            }
            try
            {
                await Task.Delay(NextDelay(), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private TimeSpan NextDelay()
    {
        if (_failures > 0 && _failures <= RetryDelays.Length) return RetryDelays[_failures - 1];
        if (_failures > 0 && Settling) return RetryDelays[^1];
        bool cheapToRetry = CurrentState.ConnectionState == DeviceConnectionState.DeviceOff && _resolved is not null;
        return cheapToRetry && interval > SleepPoll ? SleepPoll : interval;
    }

    private bool Settling => CurrentState.LastSuccessfulRead is null && DateTimeOffset.Now - _startedAt <= StartupGrace;

    private void Fail(string message)
    {
        DeviceBatteryState? published;
        lock (_state)
        {
            _failures++;
            published = Failed(message);
        }
        Announce(published);
    }

    private DeviceBatteryState? Failed(string message)
    {
        _diagnostics = _diagnostics with { LastError = message };
        bool fresh = CurrentState.LastSuccessfulRead is DateTimeOffset lastRead && DateTimeOffset.Now - lastRead <= Freeze;
        return _failures <= RetryDelays.Length || fresh || Settling
            ? Commit(DeviceConnectionState.Reading, CurrentState.Percentage, CurrentState.IsCharging, CurrentState.LastSuccessfulRead)
            : Commit(DeviceConnectionState.ReadError, null, null, CurrentState.LastSuccessfulRead, message);
    }

    private bool? Trend(int percentage, bool? reported)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        int? previous = _lastLevel;
        if (_lastLevel is null || percentage > _lastLevel) _lastRiseAt = now;
        _lastLevel = percentage;
        _trend.Add((now, percentage));
        _trend.RemoveAll(item => now - item.Time > TrendWindow);

        if (reported is bool flag)
        {
            _trendCharging = flag;
            _chargingReason = "from device";
            return flag;
        }
        if (percentage >= 100)
        {
            _trendCharging = false;
            return false;
        }
        if (previous is int jumpedFrom && percentage - jumpedFrom >= PlugRise)
        {
            if (_trendCharging != true) logger.Info($"Profile {profile.Name}: battery jumped {jumpedFrom}% -> {percentage}%, cable plugged in");
            _trendCharging = true;
            _chargingReason = "from battery jump";
            return true;
        }
        if (previous is int droppedFrom && droppedFrom - percentage >= UnplugDrop)
        {
            if (_trendCharging == true) logger.Info($"Profile {profile.Name}: battery dropped {droppedFrom}% -> {percentage}%, cable unplugged");
            _trendCharging = false;
            return false;
        }
        double? slope = Slope();
        if (_trendCharging == true)
        {
            if (slope < -ChargingSlope || _lastRiseAt is DateTimeOffset rise && now - rise > ChargingStall) _trendCharging = false;
            return _trendCharging;
        }
        int floor = _trend.Where(item => now - item.Time <= QuickWindow).Min(item => item.Percentage);
        _trendCharging = percentage - floor >= TrendRise || slope > ChargingSlope;
        _chargingReason = "from battery trend";
        return _trendCharging;
    }

    private double? Slope()
    {
        if (_trend.Count < SlopeSamples || _trend[^1].Time - _trend[0].Time < SlopeSpan) return null;
        DateTimeOffset origin = _trend[0].Time;
        double meanHours = _trend.Average(item => (item.Time - origin).TotalHours);
        double meanLevel = _trend.Average(item => item.Percentage);
        double covariance = 0, variance = 0;
        foreach ((DateTimeOffset time, int level) in _trend)
        {
            double hours = (time - origin).TotalHours - meanHours;
            covariance += hours * (level - meanLevel);
            variance += hours * hours;
        }
        return variance > 0 ? covariance / variance : null;
    }

    private void Retopologize(Dictionary<(int Vendor, int Product), HidDevice[]> live)
    {
        string topology = string.Join('|', live.Values
            .SelectMany(handles => handles)
            .Select(HidDeviceScanner.PathOf)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        if (_topology is not null && !string.Equals(_topology, topology, StringComparison.OrdinalIgnoreCase))
        {
            logger.Info($"Profile {profile.Name}: connected interfaces changed, charging state re-evaluated");
            _chargingSourceSearched = false;
            _chargingSource = null;
            _probes.Clear();
            SnapshotPaused();
            lock (_state)
            {
                _lastRiseAt = null;
                _lastLevel = null;
                _trend.Clear();
                _trendCharging = null;
                _offSince = null;
                _offPercentage = null;
            }
        }
        _topology = topology;
        _connected = [.. live.Where(item => item.Value.Length > 0).Select(item => $"{item.Key.Vendor:X4}:{item.Key.Product:X4}")];
    }

    private HidBatteryReading? ReadBluetooth()
    {
        string[] names = [.. profile.AllConnections().SelectMany(connection => connection.Battery.BluetoothNames ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (names.Length == 0 || WindowsBluetoothBatteryReader.TryRead(names) is not int percentage) return null;
        return new HidBatteryReading(percentage, null, HidBatterySource.None, "Bluetooth");
    }

    private void StartListener(CancellationToken token)
    {
        HidDeviceConnectionProfile connection = _activeConnection ?? profile.AllConnections().First();
        HidBatteryProtocolProfile battery = _resolved ?? connection.Battery;
        string? statusPath = battery.StatusDevicePath ?? battery.DevicePath;
        bool readsBatteryPackets = battery.MatchPrefixHex is not null && battery.PercentageOffset is not null;
        if (statusPath is null || battery.OffPrefixHex is null && !readsBatteryPackets) { StopListener(); return; }
        if (string.Equals(_listeningPath, statusPath, StringComparison.OrdinalIgnoreCase) && _listener is { IsCompleted: false }) return;
        StopListener();
        _listeningPath = statusPath;
        _listenerLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _listener = ListenAsync(connection, battery, statusPath, _listenerGeneration, _listenerLifetime.Token);
    }

    private Task StopListener()
    {
        CancellationTokenSource? lifetime = _listenerLifetime;
        Task? listener = _listener;
        lock (_state) _listenerGeneration++;
        _listenerLifetime = null;
        _listener = null;
        _listeningPath = null;
        if (lifetime is null) return Task.CompletedTask;
        lifetime.Cancel();
        _ = listener?.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default) ?? Task.Run(lifetime.Dispose);
        return listener ?? Task.CompletedTask;
    }

    private async Task ListenAsync(HidDeviceConnectionProfile connection, HidBatteryProtocolProfile battery, string statusPath, int generation, CancellationToken token)
    {
        HidDevice[] handles = [.. DeviceList.Local.GetHidDevices(connection.VendorId, connection.ProductId)];
        HidDevice? device = handles.FirstOrDefault(item => HidDeviceScanner.PathOf(item).Equals(statusPath, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            int expected = connection.Interfaces.FirstOrDefault(item => item.DevicePath.Equals(statusPath, StringComparison.OrdinalIgnoreCase))?.InputReportLength ?? 0;
            device = expected > 0 ? handles.FirstOrDefault(item => HidDeviceScanner.InputLengthOf(item) == expected) : null;
        }
        if (device is null) return;
        using HidStream? stream = HidRequestExchange.TryOpenShared(device);
        if (stream is null) return;
        var buffer = new byte[HidDeviceScanner.InputLengthOf(device)];
        if (buffer.Length == 0) return;
        BatteryCommand? known = KnownBatteryProtocols.Find(battery.ProtocolName);

        try
        {
            while (!token.IsCancellationRequested && generation == _listenerGeneration)
            {
                int count;
                try { count = await stream.ReadAsync(buffer, token).ConfigureAwait(false); }
                catch (TimeoutException) { continue; }
                if (count <= 0 || generation != _listenerGeneration) continue;
                var packet = buffer.AsSpan(0, count);

                DeviceBatteryState? published;
                if (battery.OffPrefixHex is not null && HidRequestExchange.MatchesPrefix(packet, battery.OffPrefixHex))
                {
                    if (logger.IsRawPacketLoggingEnabled) logger.Info($"RX [{count}] {Convert.ToHexString(packet[..Math.Min(count, 20)])} off");
                    lock (_state)
                    {
                        if (generation != _listenerGeneration) continue;
                        _offSince = DateTimeOffset.Now;
                        _offPercentage = CurrentState.Percentage;
                        published = Commit(DeviceConnectionState.DeviceOff, null, null, CurrentState.LastSuccessfulRead);
                    }
                    Announce(published);
                    continue;
                }
                if (battery.MatchPrefixHex is null || !HidRequestExchange.MatchesPrefix(packet, battery.MatchPrefixHex)) continue;
                if (logger.IsRawPacketLoggingEnabled) logger.Info($"RX [{count}] {Convert.ToHexString(packet[..Math.Min(count, 20)])}");
                if (known is not null && (!KnownBatteryProtocols.Accepts(packet.ToArray(), known) || KnownBatteryProtocols.IsAsleep(packet.ToArray(), known))) continue;
                if (battery.PercentageOffset is not int offset || count <= offset) continue;
                int percentage = packet[offset];
                if (percentage is < 1 or > 100) continue;
                bool? charging = battery.ChargingOffset is int chargingOffset && count > chargingOffset
                    ? battery.ChargingValue is int expected ? packet[chargingOffset] == expected : packet[chargingOffset] != 0
                    : null;
                lock (_state)
                {
                    if (generation != _listenerGeneration) continue;
                    _failures = 0;
                    _offSince = null;
                    _offPercentage = null;
                    published = Commit(DeviceConnectionState.DeviceConnected, percentage, Trend(percentage, charging), DateTimeOffset.Now);
                }
                Announce(published);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) logger.Warning($"Profile {profile.Name}: listener stopped ({ex.GetType().Name})");
        }
    }

    private DeviceBatteryState? Commit(DeviceConnectionState connection, int? percentage, bool? charging, DateTimeOffset? read, string? error = null, ConnectionTransport transport = ConnectionTransport.Default)
    {
        var state = new DeviceBatteryState(connection, percentage, charging, read, error, transport, profile.DisplayName);
        DeviceBatteryState previous = CurrentState;
        if (state == previous) return null;
        CurrentState = state;
        bool onlyTheClockMoved = state == previous with { LastSuccessfulRead = state.LastSuccessfulRead };
        return onlyTheClockMoved
            && state.LastSuccessfulRead is DateTimeOffset moment
            && previous.LastSuccessfulRead is DateTimeOffset earlier
            && moment - earlier < Echo
            ? null
            : state;
    }

    private void Announce(DeviceBatteryState? state)
    {
        if (state is null) return;
        logger.Info($"Device state: name={state.DeviceName ?? "unknown"}, id={profile.Id}, status={state.ConnectionState}, battery={state.Percentage?.ToString() ?? "-"}, charging={(state.IsCharging is null ? "unknown" : state.IsCharging.Value ? "yes" : "no")}, transport={state.Transport}");
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime?.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try { await _requests.WaitAsync().ConfigureAwait(false); _requests.Release(); }
        catch (ObjectDisposedException) { }
        Task listener = StopListener();
        try { await listener.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or UnauthorizedAccessException) { }
        _lifetime?.Dispose();
        _requests.Dispose();
    }
}

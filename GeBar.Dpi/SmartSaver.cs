using GeBar.Core;
using System.Text.Json;

namespace GeBar.Dpi;

internal sealed record SmartSaverOptions(bool Enabled = false, bool Notify = false, int NormalPollingRate = 1000) : ExtensibleData;
internal sealed record SmartSaverSettings : ExtensibleData
{
    public Dictionary<string, SmartSaverOptions> Devices { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class SmartSaver(IAppLogger logger, SemaphoreSlim requests, Func<HidDeviceProfile, IMouseSettingsDriver?> driverFor,
    string path, Func<bool> fullscreen)
{
    private SmartSaverSettings _settings = ReadSettings(path, logger);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private DateTimeOffset? _desktopSince;
    private bool _game;
    public event Action<string, string>? Notification;
    public event Action<string, MouseSettings>? Applied;
    public event Action? StateChanged;
    public string? Error(string id) => _sessions.GetValueOrDefault(id)?.Error;
    public int? Rate(string id) => _sessions.GetValueOrDefault(id)?.LastRate;

    public SmartSaverOptions Options(string id) => _settings.Devices.GetValueOrDefault(id) ?? new();
    public MouseSettings Editor(string id, MouseSettings settings, MouseSettingsCapabilities capabilities) => Options(id) is { Enabled: true } options
        ? settings with { PollingRate = SupportedRate(options.NormalPollingRate, capabilities) } : settings;
    public MouseSettings Physical(string id, MouseSettings settings) => Options(id).Enabled && _sessions.TryGetValue(id, out Session? session) && session.LastRate is int rate
        ? settings with { PollingRate = rate } : settings;

    public async Task StartAsync(IEnumerable<HidDeviceProfile> devices, CancellationToken token)
    {
        HidDeviceProfile[] profiles = devices.ToArray();
        foreach (HidDeviceProfile device in profiles)
        {
            if (_sessions.TryGetValue(device.Id, out Session? session)) session.Device = device;
            else if (driverFor(device) is IMouseSettingsDriver driver && driver is IMousePollingDriver)
                _sessions[device.Id] = new(device, driver);
        }
        foreach (Session session in _sessions.Values.ToArray())
        {
            bool present = profiles.Any(device => device.Id == session.Device.Id);
            bool removed = session.Present && !present;
            session.Present = present;
            if (removed && Options(session.Device.Id).Enabled) await RestoreAsync(session, token);
        }
        if (_worker is not null) return;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _worker = RunAsync(_lifetime.Token);
    }

    public async Task ConfigureAsync(HidDeviceProfile device, IMouseSettingsDriver driver, SmartSaverOptions options, CancellationToken token)
    {
        if ((!driver.Capabilities.PollingRates.Contains(options.NormalPollingRate) && options.NormalPollingRate != Options(device.Id).NormalPollingRate)
            || options.Enabled && (driver is not IMousePollingDriver || !driver.Capabilities.PollingRates.Contains(125)))
            throw new ArgumentOutOfRangeException(nameof(options));
        var devices = new Dictionary<string, SmartSaverOptions>(_settings.Devices, StringComparer.Ordinal) { [device.Id] = options };
        SmartSaverSettings updated = _settings with { Devices = devices };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, updated, new JsonSerializerOptions { WriteIndented = true }, token);
        bool wasEnabled = Options(device.Id).Enabled;
        if (!_sessions.TryGetValue(device.Id, out Session? session)) _sessions[device.Id] = session = new(device, driver);
        if (wasEnabled && !options.Enabled) await ApplyAsync(session, options.NormalPollingRate, token, disabling: true);
        File.Move(temporary, path, true);
        _settings = updated;
        session.LastRate = null;
        session.NextCheck = DateTimeOffset.MinValue;
        session.Error = null;
        if (options.Enabled) UpdateActivity(fullscreen(), DateTimeOffset.UtcNow);
        if (options.Enabled) await ApplyAsync(session, Target(session, options), token);
        StateChanged?.Invoke();
    }

    private static int SupportedRate(int rate, MouseSettingsCapabilities capabilities) => capabilities.PollingRates.Where(value => value <= rate).DefaultIfEmpty(125).Max();
    private int Target(Session session, SmartSaverOptions options) => _game ? SupportedRate(options.NormalPollingRate, session.Driver.Capabilities) : 125;

    internal bool UpdateActivity(bool fullscreenActive, DateTimeOffset now)
    {
        if (fullscreenActive) { _game = true; _desktopSince = null; }
        else
        {
            _desktopSince ??= now;
            if (now - _desktopSince >= TimeSpan.FromSeconds(3)) _game = false;
        }
        return _game;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_sessions.Values.Any(session => session.Present && Options(session.Device.Id).Enabled))
                {
                    UpdateActivity(fullscreen(), DateTimeOffset.UtcNow);
                    foreach (Session session in _sessions.Values.ToArray())
                    {
                        SmartSaverOptions options = Options(session.Device.Id);
                        if (!session.Present || !options.Enabled) continue;
                        int target = Target(session, options);
                        if (DateTimeOffset.UtcNow < session.NextCheck && (session.LastRate == target || session.Failed)) continue;
                        await requests.WaitAsync(token);
                        try
                        {
                            if (session.Present && Options(session.Device.Id).Enabled) await ApplyAsync(session, Target(session, Options(session.Device.Id)), token);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
                        {
                            session.Failed = true;
                            session.NextCheck = DateTimeOffset.UtcNow.AddSeconds(15);
                            session.Error = error.Message;
                            logger.Warning($"SmartSaver: {session.Device.Id}: {error.Message}");
                            StateChanged?.Invoke();
                        }
                        finally { requests.Release(); }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ApplyAsync(Session session, int target, CancellationToken token, bool disabling = false)
    {
        SmartSaverOptions options = Options(session.Device.Id);
        MouseSettings actual = await Task.Run(() => session.Driver.ReadAsync(session.Device, token), token);
        target = SupportedRate(target, session.Driver.Capabilities);
        if (!session.Driver.Capabilities.PollingRates.Contains(target)) throw new NotSupportedException(Strings.Unsupported);
        if (!disabling && session.LastRate is int previous && actual.PollingRate != previous && actual.PollingRate != SupportedRate(options.NormalPollingRate, session.Driver.Capabilities))
            throw new InvalidOperationException(Strings.ChangedExternally);
        bool switched = actual.PollingRate != target;
        if (switched)
        {
            session.PendingRate = target;
            actual = await Task.Run(() => ((IMousePollingDriver)session.Driver).SetPollingAsync(session.Device, target, token), token);
        }
        if (actual.PollingRate != target) throw new IOException(Strings.VerificationFailed);
        bool enteringGame = _game && session.LastRate != target;
        session.LastRate = target;
        session.PendingRate = null;
        session.Failed = false;
        session.NextCheck = DateTimeOffset.UtcNow.AddSeconds(30);
        session.Error = null;
        Applied?.Invoke(session.Device.Id, actual);
        StateChanged?.Invoke();
        if (!disabling && switched && enteringGame && options.Enabled && options.Notify) Notification?.Invoke(session.Device.DisplayName, Strings.GameNotification(target));
    }

    public async Task StopAsync(CancellationToken token)
    {
        _lifetime?.Cancel();
        if (_worker is not null) await _worker.WaitAsync(token);
        _worker = null;
        _lifetime?.Dispose();
        _lifetime = null;
        foreach (Session session in _sessions.Values.Where(session => Options(session.Device.Id).Enabled))
            await RestoreAsync(session, token);
    }

    private async Task RestoreAsync(Session session, CancellationToken token)
    {
        await requests.WaitAsync(token);
        try
        {
            MouseSettings actual = await Task.Run(() => session.Driver.ReadAsync(session.Device, token), token);
            int normal = SupportedRate(Options(session.Device.Id).NormalPollingRate, session.Driver.Capabilities);
            if ((actual.PollingRate == session.LastRate || actual.PollingRate == session.PendingRate || session.LastRate is null && actual.PollingRate == 125) && actual.PollingRate != normal)
                await Task.Run(() => ((IMousePollingDriver)session.Driver).SetPollingAsync(session.Device, normal, token), token);
            session.LastRate = null;
            session.PendingRate = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            logger.Warning($"SmartSaver: could not restore {session.Device.Id}: {error.Message}");
        }
        finally { requests.Release(); }
    }

    public void Cancel() => _lifetime?.Cancel();

    private static SmartSaverSettings ReadSettings(string path, IAppLogger logger)
    {
        try
        {
            if (!File.Exists(path)) return new();
            SmartSaverSettings settings = JsonSerializer.Deserialize<SmartSaverSettings>(File.ReadAllText(path)) ?? new();
            if (settings.Devices is null || settings.Devices.Any(pair => pair.Value is null || pair.Value.NormalPollingRate is not (125 or 250 or 500 or 1000 or 2000 or 4000 or 8000)))
                throw new JsonException("Invalid SmartSaver settings");
            return settings;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.Error("SmartSaver settings could not be loaded", error);
            return new();
        }
    }

    private sealed class Session(HidDeviceProfile device, IMouseSettingsDriver driver)
    {
        public HidDeviceProfile Device = device;
        public readonly IMouseSettingsDriver Driver = driver;
        public bool Present = true;
        public int? LastRate;
        public int? PendingRate;
        public DateTimeOffset NextCheck;
        public bool Failed;
        public string? Error;
    }
}

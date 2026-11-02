using System.Text.Json;
using GeBar.Core;

namespace GeBar.App;

internal sealed record AppSettings(bool StartWithWindows = false, bool NotifyLowBattery = true, int WarningThreshold = 20, int CriticalThreshold = 10, int RefreshIntervalSeconds = 60, bool LogRawPackets = false, int NormalColorArgb = -16728876, int WarningColorArgb = -23296, int CriticalColorArgb = -65536, TrayIconStyle IconStyle = TrayIconStyle.Battery, int TrayIconScale = 2, bool NotifyChargingConnected = true, bool NotifyFullyCharged = true, string SelectedDeviceId = DeviceCatalog.BuiltInId, bool ShowAllDevicesInTrayTooltip = false, int HistoryHours = 24, bool NotifyAllDevices = false, bool AutoSwitchDevice = true) : ExtensibleData
{
    public AppSettings Validate() => this with
    {
        WarningThreshold = Math.Clamp(WarningThreshold, 5, 50),
        CriticalThreshold = Math.Clamp(CriticalThreshold, 1, Math.Max(1, Math.Clamp(WarningThreshold, 5, 50) - 1)),
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 15, 86400),
        TrayIconScale = Math.Clamp(TrayIconScale, 1, 3),
        HistoryHours = HistoryHours == 168 ? 168 : 24
    };

    public static AppSettings Load(IAppLogger? logger = null, string? path = null)
    {
        try
        {
            path ??= AppPaths.Settings;
            if (!File.Exists(path)) return new();
            string json = File.ReadAllText(path);
            return (JsonSerializer.Deserialize<AppSettings>(json) ?? new()).Validate();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger?.Error("Settings load failed; defaults applied", ex);
            return new();
        }
    }

    private static readonly SemaphoreSlim Writes = new(1, 1);

    public async Task SaveAsync(string? path = null)
    {
        await Writes.WaitAsync().ConfigureAwait(false);
        try
        {
            path ??= AppPaths.Settings;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, Validate(), new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, path, true);
        }
        finally { Writes.Release(); }
    }
}

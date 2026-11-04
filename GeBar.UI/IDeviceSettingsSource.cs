using System.Globalization;
using GeBar.Core;

namespace GeBar.UI;

public sealed record DeviceSettingsSummary(string DeviceId, int Dpi, int PollingRate, double Lod)
{
    public string Display => string.Create(CultureInfo.InvariantCulture, $"({Dpi}dpi, {PollingRate}hz, {Lod:0.0}mm)");
}

public interface IDeviceSettingsSource
{
    DeviceSettingsSummary? CurrentSettings { get; }
    event EventHandler? SettingsChanged;
    Task RefreshSettingsAsync(CancellationToken token);
    Task PreloadSettingsAsync(IEnumerable<HidDeviceProfile> devices, CancellationToken token) => Task.CompletedTask;
}

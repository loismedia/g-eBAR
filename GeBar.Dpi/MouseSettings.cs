using GeBar.Core;

namespace GeBar.Dpi;

public sealed record MouseSettings(int Dpi, int PollingRate, double Lod);

public sealed record MouseSettingsCapabilities(int MinimumDpi, int MaximumDpi, int DpiStep, IReadOnlyList<int> PollingRates, IReadOnlyList<double> LodValues)
{
    public bool Accepts(MouseSettings settings) => settings.Dpi >= MinimumDpi && settings.Dpi <= MaximumDpi
        && settings.Dpi % DpiStep == 0 && PollingRates.Contains(settings.PollingRate) && LodValues.Contains(settings.Lod);
}

public interface IMouseSettingsDriver
{
    bool Supports(HidDeviceProfile device);
    MouseSettingsCapabilities Capabilities { get; }
    Task<MouseSettings> ReadAsync(HidDeviceProfile device, CancellationToken token);
    Task<MouseSettings> SaveAsync(HidDeviceProfile device, MouseSettings expected, MouseSettings requested, CancellationToken token);
}

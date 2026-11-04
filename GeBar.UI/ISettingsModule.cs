using GeBar.Core;
using System.Windows;

namespace GeBar.UI;

public interface ISettingsModule : IDisposable
{
    string Name { get; }
    string Version { get; }
    FrameworkElement View { get; }
    FrameworkElement? Actions => null;
    bool CanSave { get; }
    event EventHandler? SaveAvailabilityChanged;
    Task SetDeviceAsync(HidDeviceProfile? device, CancellationToken token);
    Task<bool> SaveAsync(CancellationToken token);
}

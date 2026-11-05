using GeBar.Core;

namespace GeBar.UI;

public interface IBackgroundSettingsModule
{
    event Action<string, string>? Notification;
    Task StartAsync(IEnumerable<HidDeviceProfile> devices, CancellationToken token);
    Task StopAsync(CancellationToken token);
}

using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GeBar.UI;

public static partial class Windows11WindowStyler
{
    private enum DwmWindowAttribute { UseImmersiveDarkMode = 20, WindowCornerPreference = 33, SystemBackdropType = 38 }
    private enum CornerPreference { Default, DoNotRound, Round, RoundSmall }
    private enum BackdropType { Auto, None, MainWindow, TransientWindow, TabbedWindow }
    public static bool IsDarkTheme()
    {
        object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        return value is int setting && setting == 0;
    }
    public static void Apply(System.Windows.Window window)
    {
        Apply(new System.Windows.Interop.WindowInteropHelper(window).Handle);
    }
    private static void Apply(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            int dark = IsDarkTheme() ? 1 : 0;
            int corner = (int)CornerPreference.Round;
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.UseImmersiveDarkMode, ref dark, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.WindowCornerPreference, ref corner, sizeof(int));
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                int backdrop = (int)BackdropType.MainWindow;
                _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.SystemBackdropType, ref backdrop, sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, DwmWindowAttribute attribute, ref int value, int size);
}

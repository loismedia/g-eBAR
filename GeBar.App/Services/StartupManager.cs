using Microsoft.Win32;
using System.Security;

namespace GeBar.App;

internal static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "g-eBAR";

    private static string Executable => Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        key.DeleteValue(ValueName, false);
        if (!enabled) return;

        string executable = Path.GetFullPath(Executable);
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }

    public static void Synchronize(bool enabled, FileLogger logger)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            string? stored = key.GetValue(ValueName) as string;
            if (!enabled)
            {
                if (stored is null) return;
                key.DeleteValue(ValueName, false);
                logger.Info("Startup entry removed");
                return;
            }
            string desired = $"\"{Path.GetFullPath(Executable)}\"";
            if (string.Equals(stored, desired, StringComparison.OrdinalIgnoreCase)) return;
            if (stored is not null && File.Exists(stored.Trim('\"')))
            {
                logger.Info($"Startup entry kept: {stored}; this copy runs from {desired}");
                return;
            }
            key.SetValue(ValueName, desired, RegistryValueKind.String);
            logger.Info(stored is null ? $"Startup entry created: {desired}" : $"Startup entry repaired: {stored} is gone, now {desired}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            logger.Error("Windows startup entry could not be synchronised", ex);
        }
    }
}

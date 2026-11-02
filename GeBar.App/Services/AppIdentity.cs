using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GeBar.App;

internal static partial class AppIdentity
{
    private const string ModelId = "LoisMedia.gEBAR";

    public static void Register(FileLogger logger)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(ModelId);
            using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{ModelId}", true);
            key.SetValue("DisplayName", Strings.AppName, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ExternalException)
        {
            logger.Warning($"Notification name could not be registered: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string modelId);
}

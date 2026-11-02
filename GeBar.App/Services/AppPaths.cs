namespace GeBar.App;

internal static class AppPaths
{
    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "g-eBAR");
    public static string Settings { get; } = Path.Combine(Directory, "settings.json");
    public static string Logs { get; } = Path.Combine(Directory, "Logs");
    public static string HidLogs { get; } = Path.Combine(Directory, "Logs", "gehid");
    public static string Profiles { get; } = Path.Combine(Directory, "Profiles");
    public static string Modules { get; } = Path.Combine(Directory, "modules");
    public static string GeHidExecutable { get; } = Path.Combine(Modules, "g-eHID.exe");
    public static string Updates { get; } = Path.Combine(Path.GetTempPath(), "g-eBAR", "Updates");
}

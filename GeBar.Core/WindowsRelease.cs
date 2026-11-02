namespace GeBar.Core;

public static class WindowsRelease
{
    private const int FirstWindows11Build = 22000;

    public static string Describe() => Describe(Environment.OSVersion.Version);

    public static string Describe(Version version) => version.Major == 10
        ? $"Windows {(version.Build >= FirstWindows11Build ? 11 : 10)} build {version.Build}"
        : $"Windows {version}";
}

using GeBar.UI;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace GeBar.HidApp;

internal partial class GeBarRequiredWindow : Window
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "g-eBAR");

    public static bool IsGeBarInstalled =>
        File.Exists(Path.Combine(Root, "settings.json"))
        || (Environment.ProcessPath?.StartsWith(Path.Combine(Root, "modules"), StringComparison.OrdinalIgnoreCase) ?? false);

    public GeBarRequiredWindow()
    {
        InitializeComponent();
        AppTheme.Apply(Resources);
        DownloadButton.Visibility = ReleasesUri is null ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) => Windows11WindowStyler.Apply(this);
    }

    private static Uri? ReleasesUri
    {
        get
        {
            string? url = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?.Value;
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/releases/latest")
                : null;
        }
    }

    private void DownloadClick(object sender, RoutedEventArgs e)
    {
        if (ReleasesUri is Uri uri)
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        Close();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}

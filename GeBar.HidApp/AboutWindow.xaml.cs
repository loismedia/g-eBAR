using GeBar.UI;
using System.Reflection;
using System.Windows;

namespace GeBar.HidApp;

internal partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AppTheme.Apply(Resources);
        VersionText.Text = Strings.Version(CurrentVersion());
        DeveloperText.Text = Strings.Developer;
        SourceInitialized += (_, _) => Windows11WindowStyler.Apply(this);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private static string CurrentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        string version = string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString(3) ?? "1.0.0" : informational;
        bool nightly = bool.TryParse(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "Nightly")?.Value, out bool value) && value;
        return version + (GeBar.Core.BuildChannel.Current == GeBar.Core.ReleaseChannel.Dev ? nightly ? " dev nightly" : " dev" : string.Empty);
    }
}

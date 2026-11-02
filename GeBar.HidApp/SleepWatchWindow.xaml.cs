using GeBar.UI;
using System.Globalization;
using System.Windows;

namespace GeBar.HidApp;

internal partial class SleepWatchWindow : Window
{
    private const int MinimumSeconds = 30;
    private const int MaximumSeconds = 3600;

    public int Seconds { get; private set; } = 300;

    public SleepWatchWindow()
    {
        InitializeComponent();
        AppTheme.Apply(Resources);
        SecondsBox.Text = Seconds.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => SecondsBox.SelectAll();
        SourceInitialized += (_, _) => Windows11WindowStyler.Apply(this);
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SecondsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            || seconds is < MinimumSeconds or > MaximumSeconds)
        {
            AppDialog.Show(this, Strings.SleepSecondsRange(MinimumSeconds, MaximumSeconds), Strings.AppName, warning: true);
            return;
        }
        Seconds = seconds;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

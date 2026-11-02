using GeBar.UI;
using System.Windows;

namespace GeBar.App;

internal partial class BugReportWindow : Window
{
    public string Description { get; private set; } = string.Empty;

    public BugReportWindow(bool rawPackets)
    {
        InitializeComponent();
        AppTheme.Apply(Resources);
        Title = Strings.ReportBug;
        HintText.Text = Strings.BugDescription;
        SendButton.Content = Strings.ReportBug;
        CancelButton.Content = Strings.Cancel;
        RawWarningText.Text = Strings.RawOffWarning;
        RawWarning.Visibility = rawPackets ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => { Windows11WindowStyler.Apply(this); DescriptionBox.Focus(); };
    }

    private void SendClick(object sender, RoutedEventArgs e)
    {
        string description = DescriptionBox.Text.Trim();
        if (description.Length == 0)
        {
            AppDialog.Show(this, Strings.BugDescriptionRequired, Strings.AppName);
            return;
        }
        Description = description;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

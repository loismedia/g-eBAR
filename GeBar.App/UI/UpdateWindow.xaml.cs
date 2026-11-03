using GeBar.UI;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace GeBar.App;

internal partial class UpdateWindow : Window
{
    private readonly GitHubRelease _release;
    private readonly GitHubUpdateService _service;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _installing;
    public bool InstallStarted { get; private set; }

    public UpdateWindow(GitHubRelease release, GitHubUpdateService service, FileLogger logger)
    {
        InitializeComponent();
        AppTheme.Apply(Resources);
        _release = release;
        _service = service;
        _logger = logger;
        bool stableRollback = release.TargetChannel is null && release.IsDowngrade;
        Title = Strings.NewVersion;
        AvailableText.Text = release.TargetChannel is ReleaseChannel target ? Strings.ChannelAvailable(target, release.Version.ToString()) : release.AppUpdateAvailable
            ? release.IsDowngrade ? Strings.AvailableStableRollback(release.Version.ToString())
                : release.IsNightly ? Strings.AvailableNightlyVersion(release.Version.ToString()) : Strings.AvailableVersion(release.Version.ToString())
            : Strings.HidAvailableVersion(release.HidVersion ?? string.Empty);
        CurrentText.Text = Strings.CurrentVersion(GitHubUpdateService.CurrentVersionLabel);
        CurrentText.Visibility = release.AppUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        ChangelogViewer.Document = CreateChangelogDocument(release.Changelog);
        ChangelogViewer.Document.Foreground = (System.Windows.Media.Brush)Resources["PrimaryText"];
        HistoryButton.Content = Strings.VersionHistory;
        DownloadButton.Content = stableRollback ? Strings.Rollback : Strings.Download;
        HidCheckBox.Content = release.HidVersion is null ? Strings.HidReleaseUnavailable : Strings.HidReleaseVersion(release.HidVersion);
        HidCheckBox.IsEnabled = release.HidUpdateAvailable && release.TargetChannel is null;
        HidCheckBox.IsChecked = release.HidUpdateAvailable && GitHubUpdateService.GetInstalledHidVersion() is not null;
        DownloadButton.IsEnabled = release.AppUpdateAvailable || HidCheckBox.IsChecked == true;
        DownloadProductText.Text = release.AppUpdateAvailable ? Strings.AppName : Strings.HidName;
        NightlyWarningText.Text = stableRollback ? Strings.StableRollbackWarning : Strings.NightlyWarning;
        NightlyWarning.Visibility = release.AppUpdateAvailable && (release.IsNightly || stableRollback) ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Windows11WindowStyler.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _lifetime.Cancel();
        };
    }

    internal static FlowDocument CreateChangelogDocument(string markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new MediaFontFamily("Segoe UI Variable Text"),
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(MediaColor.FromRgb(245, 245, 245))
        };
        System.Windows.Documents.List? list = null;
        foreach (string sourceLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0)
            {
                list = null;
                continue;
            }

            int headingLevel = line.TakeWhile(character => character == '#').Count();
            if (headingLevel is > 0 and <= 6 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                list = null;
                document.Blocks.Add(new Paragraph(new Run(line[(headingLevel + 1)..]))
                {
                    FontFamily = new MediaFontFamily("Segoe UI Variable Display"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = headingLevel <= 3 ? 19 : 16,
                    Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 12, 0, 5)
                });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (list is null)
                {
                    list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(18, 2, 0, 8) };
                    document.Blocks.Add(list);
                }
                list.ListItems.Add(new ListItem(new Paragraph(new Run(line[2..])) { Margin = new Thickness(0, 2, 0, 2) }));
                continue;
            }

            list = null;
            document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 2, 0, 6) });
        }
        return document;
    }

    private async void HistoryClick(object sender, RoutedEventArgs e)
    {
        HistoryButton.IsEnabled = false;
        await ShowChangelogAsync(this, _service, _logger, _release.Tag, _lifetime.Token, _release.TargetChannel);
        HistoryButton.IsEnabled = !_installing;
    }

    internal static async Task ShowChangelogAsync(Window owner, GitHubUpdateService service, FileLogger logger, string? releaseTag, CancellationToken token = default, ReleaseChannel? targetChannel = null)
    {
        try
        {
            string markdown = await service.DownloadChangelogAsync(releaseTag, token, targetChannel);
            if (token.IsCancellationRequested) return;
            var window = new Window
            {
                Title = Strings.VersionHistory,
                Owner = owner,
                Width = 620,
                Height = 520,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GeBar.UI;component/Theme.xaml") });
            AppTheme.Apply(window.Resources);
            window.Background = (System.Windows.Media.Brush)window.Resources["WindowBackground"];
            window.Foreground = (System.Windows.Media.Brush)window.Resources["PrimaryText"];
            var viewer = new FlowDocumentScrollViewer
            {
                Document = CreateChangelogDocument(markdown),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = window.Foreground,
                IsToolBarVisible = false
            };
            viewer.Document.Foreground = window.Foreground;
            window.Content = new Border { Padding = new Thickness(22), Child = viewer };
            window.Loaded += (_, _) => Windows11WindowStyler.Apply(window);
            window.ShowDialog();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            logger.Error("Changelog download failed", ex);
            AppDialog.Show(owner, Strings.ChangelogUnavailable, Strings.AppName);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            AppTheme.ApplyTo(this);
            ChangelogViewer.Document.Foreground = (System.Windows.Media.Brush)Resources["PrimaryText"];
        });
    }

    private async void DownloadClick(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (_release.TargetChannel is ReleaseChannel target
            && !AppDialog.Confirm(this, Strings.ChannelConfirmation(target), Strings.AppName, Strings.Download, Strings.Cancel))
            return;
        if (_release.TargetChannel is null && _release.AppUpdateAvailable && _release.IsNightly
            && !AppDialog.Confirm(this, Strings.NightlyConfirmation, Strings.NightlyConfirmationTitle, Strings.Download, Strings.Cancel))
            return;
        if (_release.TargetChannel is null && _release.AppUpdateAvailable && _release.IsDowngrade
            && !AppDialog.Confirm(this, Strings.StableRollbackConfirmation, Strings.StableRollbackConfirmationTitle, Strings.Rollback, Strings.Cancel))
            return;

        _installing = true;
        DownloadButton.IsEnabled = false;
        HistoryButton.IsEnabled = false;
        HidCheckBox.IsEnabled = false;
        DownloadStatusText.Text = Strings.DownloadStarting;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        StartIndeterminateProgressAnimation();
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            DownloadStatusText.Text = Strings.DownloadProgress(value.DownloadedBytes, value.TotalBytes);
            if (value.TotalBytes is not > 0) return;
            DownloadProgressBar.IsIndeterminate = false;
            StopIndeterminateProgressAnimation();
            DownloadProgressBar.Value = Math.Clamp(value.DownloadedBytes * 100d / value.TotalBytes.Value, 0, 100);
        });
        try
        {
            string? hidPackage = null;
            if (HidCheckBox.IsChecked == true)
            {
                DownloadProductText.Text = Strings.HidName;
                hidPackage = await _service.DownloadHidAsync(_release, progress, _lifetime.Token);
            }
            string? appPackage = null;
            if (_release.AppUpdateAvailable)
            {
                DownloadProductText.Text = Strings.AppName;
                appPackage = await _service.DownloadAsync(_release, progress, _lifetime.Token);
            }

            if (hidPackage is not null) GitHubUpdateService.ApplyHid(hidPackage);
            if (appPackage is not null)
            {
                AppDialog.Show(this, Strings.Downloaded, Strings.AppName);
                GitHubUpdateService.StartInstall(appPackage);
                InstallStarted = true;
                Close();
                return;
            }
            AppDialog.Show(this, Strings.HidInstalled, Strings.AppName);
            Close();
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Update {_release.Version} installation cancelled");
            Restore();
        }
        catch (Exception ex)
        {
            _logger.Error($"Update {_release.Version} installation failed", ex);
            AppDialog.Show(this, Strings.UpdateInstallError, Strings.AppName);
            Restore();
        }
    }

    private void Restore()
    {
        _installing = false;
        DownloadButton.IsEnabled = _release.AppUpdateAvailable || HidCheckBox.IsChecked == true;
        HistoryButton.IsEnabled = true;
        HidCheckBox.IsEnabled = _release.HidUpdateAvailable;
        StopIndeterminateProgressAnimation();
        DownloadProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void HidSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _installing) return;
        DownloadButton.IsEnabled = _release.AppUpdateAvailable || HidCheckBox.IsChecked == true;
    }

    private void StartIndeterminateProgressAnimation()
    {
        DownloadProgressBar.ApplyTemplate();
        if (DownloadProgressBar.Template.FindName("PART_Indicator", DownloadProgressBar) is not FrameworkElement indicator) return;
        indicator.BeginAnimation(MarginProperty, new ThicknessAnimation
        {
            From = new Thickness(-90, 0, 0, 0),
            To = new Thickness(520, 0, 0, 0),
            Duration = TimeSpan.FromMilliseconds(1150),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopIndeterminateProgressAnimation()
    {
        if (DownloadProgressBar.Template.FindName("PART_Indicator", DownloadProgressBar) is not FrameworkElement indicator) return;
        indicator.BeginAnimation(MarginProperty, null);
        indicator.Margin = new Thickness(0);
    }
}

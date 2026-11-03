using System.Runtime.ExceptionServices;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using GeBar.App;
using GeBar.Core;

namespace GeBar.Tests;

public class UpdateWindowTests
{
    [Theory]
    [InlineData(ReleaseChannel.Dev, false, true, false, false)]
    [InlineData(ReleaseChannel.Public, false, true, false, false)]
    [InlineData(ReleaseChannel.Dev, false, false, false, false)]
    [InlineData(ReleaseChannel.Dev, true, false, true, false)]
    [InlineData(null, true, false, true, false)]
    [InlineData(null, false, true, true, true)]
    public void ChannelSwitchDoesNotPretendToBeNightlyRollback(ReleaseChannel? target, bool nightly, bool downgrade, bool warning, bool rollback)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-update-ui-{Guid.NewGuid():N}");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            UpdateWindow? window = null;
            try
            {
                var logger = new FileLogger(directory, "test");
                using var service = new GitHubUpdateService(logger, new HttpClient(), string.Empty, ReleaseChannel.Public);
                var release = new GitHubRelease(new Version(1, 3, 12), "v1.3.12", "", new Uri("https://example.invalid/g-eBAR.exe"),
                    true, null, null, false, nightly, downgrade, TargetChannel: target);
                window = new UpdateWindow(release, service, logger);

                Assert.Equal(warning ? Visibility.Visible : Visibility.Collapsed, ((Border)window.FindName("NightlyWarning")).Visibility);
                Assert.Equal(rollback ? Strings.Rollback : Strings.Download, ((Button)window.FindName("DownloadButton")).Content);
                if (warning) Assert.Equal(rollback ? Strings.StableRollbackWarning : Strings.NightlyWarning,
                    ((TextBlock)window.FindName("NightlyWarningText")).Text);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                window?.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Update window initialization timed out");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

using System.Globalization;
using GeBar.App;
using GeBar.Core;
using GeBar.Hid;
using GeBar.UI;

namespace GeBar.Tests;

public class SmallPartsTests
{
    [Theory]
    [InlineData("0B000310", "510B000310000600", true)]
    [InlineData("0B000310", "510500FF031A0B00", false)]
    [InlineData("41", "0041008C01", true)]
    [InlineData("", "00", true)]
    [InlineData("0B000310", "510B", false)]
    public void PrefixIsMatchedAfterReportId(string prefix, string packet, bool expected) =>
        Assert.Equal(expected, HidRequestExchange.MatchesPrefix(Convert.FromHexString(packet), prefix));

    [Fact]
    public void SettingsAreClamped()
    {
        AppSettings settings = new AppSettings(WarningThreshold: 90, CriticalThreshold: 95, RefreshIntervalSeconds: 1, TrayIconScale: 7, HistoryHours: 5).Validate();

        Assert.Equal(50, settings.WarningThreshold);
        Assert.True(settings.CriticalThreshold < settings.WarningThreshold);
        Assert.Equal(15, settings.RefreshIntervalSeconds);
        Assert.Equal(3, settings.TrayIconScale);
        Assert.Equal(24, settings.HistoryHours);
    }

    [Fact]
    public void ScrollThumbNeverShorterThanMinimum()
    {
        var converter = new ScrollThumbLength { Minimum = 24 };
        double viewport = (double)converter.Convert([10d, 0d, 10_000d, 400d], typeof(double), null!, CultureInfo.InvariantCulture);

        double thumb = 400 * viewport / (10_000 + viewport);

        Assert.Equal(24, thumb, 6);
    }

    [Fact]
    public void ScrollThumbKeepsLongThumbAsIs()
    {
        var converter = new ScrollThumbLength();

        Assert.Equal(500d, converter.Convert([500d, 0d, 1_000d, 400d], typeof(double), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void LogLinesReadWhileLoggerWrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-tests-{Guid.NewGuid():N}");
        try
        {
            var logger = new FileLogger(directory, "test");
            logger.Info("first");
            string file = Directory.GetFiles(directory, "*.log").Single();
            using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            Assert.Contains(FileLogger.ReadLines(file), line => line.EndsWith("first", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RawPacketsAreHiddenUnlessEnabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-tests-{Guid.NewGuid():N}");
        try
        {
            var logger = new FileLogger(directory, "test");
            logger.Info("RX [65] 0041");
            logger.LogRawPackets = true;
            logger.Info("TX [65] 0041");
            string[] lines = [.. FileLogger.ReadLines(Directory.GetFiles(directory, "*.log").Single())];

            Assert.DoesNotContain(lines, line => line.Contains("RX [65]", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("TX [65]", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

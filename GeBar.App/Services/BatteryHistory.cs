using System.Globalization;
using System.Text.RegularExpressions;

namespace GeBar.App;

internal readonly record struct BatteryPoint(DateTimeOffset Time, int Percentage, bool? Charging);

internal static partial class BatteryHistory
{
    private const int MaximumPoints = 5000;
    private const int MinimumDrop = 3;
    private const int ShutdownReserve = 3;
    private const int ChargeRise = 5;
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumSpan = TimeSpan.FromHours(24);

    public static IReadOnlyList<BatteryPoint> Read(string deviceId, string deviceName, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(deviceName)) return [];
        DateTimeOffset since = DateTimeOffset.Now - window;
        var points = new List<BatteryPoint>();
        foreach (string file in Files(since))
        {
            try
            {
                foreach (string line in FileLogger.ReadLines(file))
                {
                    Match match = EntryRegex().Match(line);
                    if (!match.Success) continue;
                    Group id = match.Groups["id"];
                    if (id.Success
                        ? !id.Value.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
                        : !match.Groups["name"].Value.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!DateTimeOffset.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset time)) continue;
                    if (time < since) continue;
                    if (!int.TryParse(match.Groups["battery"].Value, out int percentage) || percentage is < 1 or > 100) continue;
                    string charging = match.Groups["charging"].Value;
                    points.Add(new BatteryPoint(time, percentage, charging == "unknown" ? null : charging == "yes"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        points.Sort((left, right) => left.Time.CompareTo(right.Time));
        return points.Count <= MaximumPoints ? points : points[^MaximumPoints..];
    }

    public static IReadOnlyList<BatteryPoint> Analysed(IReadOnlyList<BatteryPoint> points)
    {
        var analysed = new List<BatteryPoint>(points);
        for (int start = 0; start < analysed.Count; start++)
        {
            if (analysed[start].Charging != true) continue;
            int end = start;
            while (end + 1 < analysed.Count && analysed[end + 1].Charging == true && analysed[end + 1].Time - analysed[end].Time <= MaximumGap) end++;

            bool continuous = start > 0 && analysed[start].Time - analysed[start - 1].Time <= MaximumGap;
            int baseline = continuous ? analysed[start - 1].Percentage : analysed[start].Percentage;
            int confirmationEnd = end + 1 < analysed.Count && analysed[end + 1].Time - analysed[end].Time <= MaximumGap ? end + 1 : end;
            bool confirmed = analysed[end].Percentage >= 100
                || analysed.GetRange(start, confirmationEnd - start + 1).Any(point => point.Percentage > baseline);
            if (!confirmed)
                for (int index = start; index <= end; index++) analysed[index] = analysed[index] with { Charging = false };
            start = end;
        }
        return analysed;
    }

    public static TimeSpan? EstimateRemaining(IReadOnlyList<BatteryPoint> points)
    {
        if (points.Count < 3 || points[^1].Charging == true) return null;
        int last = points.Count - 1, drop = 0;
        double hours = 0;
        for (int index = last; index > 0; index--)
        {
            BatteryPoint current = points[index], previous = points[index - 1];
            if (previous.Charging == true || current.Percentage - previous.Percentage >= ChargeRise) break;
            TimeSpan gap = current.Time - previous.Time;
            if (gap <= TimeSpan.Zero) break;
            // the app was closed or the machine slept: keep the stretch, but do not count the dark time as discharge
            if (gap > MaximumGap)
            {
                if (previous.Percentage < current.Percentage) break;
                continue;
            }
            hours += gap.TotalHours;
            drop += previous.Percentage - current.Percentage;
            if (hours >= MaximumSpan.TotalHours) break;
            if (drop >= MinimumDrop && hours >= 1) break;
        }
        if (hours < 0.5 || drop < MinimumDrop || points[last].Percentage <= ShutdownReserve) return null;
        double remaining = (points[last].Percentage - ShutdownReserve) * hours / drop;
        return remaining is > 0 and < 720 ? TimeSpan.FromHours(remaining) : null;
    }

    private static IEnumerable<string> Files(DateTimeOffset since)
    {
        if (!System.IO.Directory.Exists(AppPaths.Logs)) return [];
        return System.IO.Directory.GetFiles(AppPaths.Logs, "*.log")
            .Select(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)))
            .Where(item => item.Written >= since.UtcDateTime.Date)
            .OrderBy(item => item.Written)
            .Select(item => item.Path);
    }

    [GeneratedRegex(@"^(?<time>\S+) \[INF\] Device state: name=(?<name>.*?)(?:, id=(?<id>[^,]*))?, status=DeviceConnected, battery=(?<battery>\d+), charging=(?<charging>yes|no|unknown)", RegexOptions.CultureInvariant)]
    private static partial Regex EntryRegex();
}

using GeBar.Core;
using HidSharp;

namespace GeBar.Hid;

public enum SleepWatchPhase { Idle, Sleeping, Waking }

public sealed record SleepWatchProgress(SleepWatchPhase Phase, int SecondsLeft, int Packets);

public sealed record SleepWatchResult(string DevicePath, string? OffPrefixHex, IReadOnlyList<string> Candidates, int Packets, int Interfaces);

public static class SleepWatch
{
    private const int PrefixBytes = 8;
    private const int MinimumPrefixBytes = 2;
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Wake = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly record struct Seen(DateTimeOffset Time, string Path, byte ReportId, byte[] Payload);

    public static async Task<SleepWatchResult?> RunAsync(HidDeviceProfile device, TimeSpan sleepAfter, IProgress<SleepWatchProgress>? progress, CancellationToken token)
    {
        var seen = new List<Seen>();
        var sessions = new List<HidCaptureSession>();
        foreach (string path in StatusPaths(device))
        {
            string captured = path;
            var session = new HidCaptureSession(path);
            session.PacketReceived += packet =>
            {
                ReadOnlySpan<byte> span = packet.Span;
                if (span.Length <= 1) return;
                lock (seen) seen.Add(new Seen(DateTimeOffset.Now, captured, span[0], span[1..].ToArray()));
            };
            try
            {
                await session.StartAsync().ConfigureAwait(false);
                sessions.Add(session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (sessions.Count == 0) return null;

        DateTimeOffset asleep = DateTimeOffset.Now + sleepAfter;
        DateTimeOffset woke = asleep + Confirm;
        try
        {
            await PhaseAsync(SleepWatchPhase.Idle, asleep, seen, progress, token).ConfigureAwait(false);
            await PhaseAsync(SleepWatchPhase.Sleeping, woke, seen, progress, token).ConfigureAwait(false);
            await PhaseAsync(SleepWatchPhase.Waking, woke + Wake, seen, progress, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (HidCaptureSession session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        }

        lock (seen) return Analyse([.. seen], asleep, woke, sessions.Count);
    }

    private static async Task PhaseAsync(SleepWatchPhase phase, DateTimeOffset until, List<Seen> seen, IProgress<SleepWatchProgress>? progress, CancellationToken token)
    {
        while (DateTimeOffset.Now < until)
        {
            int count;
            lock (seen) count = seen.Count;
            progress?.Report(new SleepWatchProgress(phase, (int)Math.Ceiling((until - DateTimeOffset.Now).TotalSeconds), count));
            await Task.Delay(Tick, token).ConfigureAwait(false);
        }
    }

    private static SleepWatchResult Analyse(IReadOnlyList<Seen> seen, DateTimeOffset asleep, DateTimeOffset woke, int interfaces)
    {
        Dictionary<(string Path, byte ReportId), int> awakeStable = Stable(seen.Where(item => item.Time < asleep));
        Dictionary<(string Path, byte ReportId), int> sleepStable = Stable(seen.Where(item => item.Time >= asleep && item.Time < woke));
        Dictionary<(string Path, byte ReportId), int> stable = awakeStable.Keys.Concat(sleepStable.Keys).Distinct()
            .ToDictionary(key => key, key => Math.Min(awakeStable.GetValueOrDefault(key, PrefixBytes), sleepStable.GetValueOrDefault(key, PrefixBytes)));

        HashSet<string> awake = [.. Prefixes(seen.Where(item => item.Time < asleep), stable)];
        HashSet<string> after = [.. Prefixes(seen.Where(item => item.Time >= woke), stable)];
        (string Path, string Prefix)[] candidates = [.. seen
            .Where(item => item.Time >= asleep && item.Time < woke)
            .Select(item => (item.Path, Prefix: Prefix(item, stable)))
            .Where(item => item.Prefix is not null && !awake.Contains(Key(item.Path, item.Prefix!)) && !after.Contains(Key(item.Path, item.Prefix!)))
            .Select(item => (item.Path, Prefix: item.Prefix!))
            .Distinct()];

        string[] listed = [.. candidates.Select(item => item.Prefix).Distinct()];
        return candidates.Length == 1
            ? new SleepWatchResult(candidates[0].Path, candidates[0].Prefix, listed, seen.Count, interfaces)
            : new SleepWatchResult(string.Empty, null, listed, seen.Count, interfaces);
    }

    private static Dictionary<(string Path, byte ReportId), int> Stable(IEnumerable<Seen> packets) =>
        packets.GroupBy(item => (item.Path, item.ReportId)).ToDictionary(group => group.Key, StableBytes);

    private static int StableBytes(IEnumerable<Seen> group)
    {
        byte[]? first = null;
        int stable = PrefixBytes;
        foreach (Seen item in group)
        {
            if (first is null) { first = item.Payload; stable = Math.Min(PrefixBytes, first.Length); continue; }
            stable = Math.Min(stable, item.Payload.Length);
            for (int index = 0; index < stable; index++)
                if (first[index] != item.Payload[index]) { stable = index; break; }
        }
        return first is null ? 0 : stable;
    }

    private static IEnumerable<string> Prefixes(IEnumerable<Seen> packets, Dictionary<(string Path, byte ReportId), int> stable) =>
        packets.Select(item => (item.Path, Prefix: Prefix(item, stable))).Where(item => item.Prefix is not null).Select(item => Key(item.Path, item.Prefix!));

    private static string? Prefix(Seen packet, Dictionary<(string Path, byte ReportId), int> stable)
    {
        int length = Math.Min(stable.GetValueOrDefault((packet.Path, packet.ReportId), PrefixBytes), packet.Payload.Length);
        return length < MinimumPrefixBytes ? null : Convert.ToHexString(packet.Payload.AsSpan(0, length));
    }

    private static string Key(string path, string prefix) => $"{path}|{prefix}";

    private static IEnumerable<string> StatusPaths(HidDeviceProfile device)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HidDeviceConnectionProfile connection in device.AllConnections())
        {
            HidDevice[] handles = [.. DeviceList.Local.GetHidDevices(connection.VendorId, connection.ProductId)];
            if (handles.Length == 0) continue;
            foreach (HidProbeTarget target in HidDeviceScanner.Pair(connection.Interfaces, handles).Where(target => target.Interface.InputReportLength > 1))
                if (listed.Add(target.Interface.DevicePath)) yield return target.Interface.DevicePath;
        }
    }
}

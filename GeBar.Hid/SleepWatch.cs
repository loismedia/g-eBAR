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

    public static async Task<SleepWatchResult?> RunAsync(HidDeviceProfile device, TimeSpan sleepAfter, IProgress<SleepWatchProgress>? progress, CancellationToken token)
    {
        var capture = new Capture();
        DateTimeOffset asleep = DateTimeOffset.MaxValue, woke = DateTimeOffset.MaxValue;
        var sessions = new List<HidCaptureSession>();
        foreach (string path in StatusPaths(device))
        {
            string captured = path;
            var session = new HidCaptureSession(path);
            session.PacketReceived += packet =>
            {
                lock (capture)
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    capture.Add(captured, packet.Span, now < asleep ? SleepWatchPhase.Idle : now < woke ? SleepWatchPhase.Sleeping : SleepWatchPhase.Waking);
                }
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

        lock (capture)
        {
            asleep = DateTimeOffset.Now + sleepAfter;
            woke = asleep + Confirm;
        }
        try
        {
            await PhaseAsync(SleepWatchPhase.Idle, asleep, capture, progress, token).ConfigureAwait(false);
            await PhaseAsync(SleepWatchPhase.Sleeping, woke, capture, progress, token).ConfigureAwait(false);
            await PhaseAsync(SleepWatchPhase.Waking, woke + Wake, capture, progress, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (HidCaptureSession session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        }

        lock (capture) return capture.Analyse(sessions.Count);
    }

    private static async Task PhaseAsync(SleepWatchPhase phase, DateTimeOffset until, Capture capture, IProgress<SleepWatchProgress>? progress, CancellationToken token)
    {
        while (DateTimeOffset.Now < until)
        {
            int count;
            lock (capture) count = capture.Packets;
            progress?.Report(new SleepWatchProgress(phase, (int)Math.Ceiling((until - DateTimeOffset.Now).TotalSeconds), count));
            await Task.Delay(Tick, token).ConfigureAwait(false);
        }
    }

    internal sealed class Capture
    {
        private readonly Dictionary<(string Path, byte ReportId), byte[]> _awake = [];
        private readonly Dictionary<(string Path, byte ReportId), byte[]> _sleeping = [];
        private Dictionary<(string Path, byte ReportId), int>? _stable;
        private HashSet<(string Path, string Prefix)>? _candidates;
        public int Packets { get; private set; }

        public void Add(string path, ReadOnlySpan<byte> packet, SleepWatchPhase phase)
        {
            if (packet.Length <= 1) return;
            Packets++;
            var key = (path, packet[0]);
            ReadOnlySpan<byte> payload = packet.Slice(1, Math.Min(PrefixBytes, packet.Length - 1));
            if (phase == SleepWatchPhase.Waking)
            {
                FindCandidates();
                int length = Math.Min(_stable!.GetValueOrDefault(key, PrefixBytes), payload.Length);
                if (length >= MinimumPrefixBytes) _candidates!.Remove((path, Convert.ToHexString(payload[..length])));
                return;
            }
            Dictionary<(string Path, byte ReportId), byte[]> prefixes = phase == SleepWatchPhase.Idle ? _awake : _sleeping;
            if (!prefixes.TryGetValue(key, out byte[]? previous)) { prefixes[key] = payload.ToArray(); return; }
            int common = 0;
            while (common < previous.Length && common < payload.Length && previous[common] == payload[common]) common++;
            if (common < previous.Length) prefixes[key] = previous[..common];
        }

        private void FindCandidates()
        {
            if (_candidates is not null) return;
            _stable = _awake.Keys.Concat(_sleeping.Keys).Distinct().ToDictionary(key => key,
                key => Math.Min(_awake.GetValueOrDefault(key)?.Length ?? PrefixBytes, _sleeping.GetValueOrDefault(key)?.Length ?? PrefixBytes));
            var awake = new HashSet<(string Path, string Prefix)>();
            foreach (var (key, payload) in _awake)
                if (_stable[key] >= MinimumPrefixBytes) awake.Add((key.Path, Convert.ToHexString(payload.AsSpan(0, _stable[key]))));
            _candidates = [];
            foreach (var (key, payload) in _sleeping)
                if (_stable[key] >= MinimumPrefixBytes) _candidates.Add((key.Path, Convert.ToHexString(payload.AsSpan(0, _stable[key]))));
            _candidates.ExceptWith(awake);
        }

        public SleepWatchResult Analyse(int interfaces)
        {
            FindCandidates();
            string[] listed = [.. _candidates!.Select(item => item.Prefix).Distinct()];
            return _candidates!.Count == 1
                ? new SleepWatchResult(_candidates.First().Path, _candidates.First().Prefix, listed, Packets, interfaces)
                : new SleepWatchResult(string.Empty, null, listed, Packets, interfaces);
        }
    }

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

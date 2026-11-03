using System.Collections.Concurrent;

namespace GeBar.Hid;

public static class HidDeviceAccess
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Requests = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<IDisposable> EnterAsync(string path, CancellationToken token)
    {
        SemaphoreSlim gate = Requests.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

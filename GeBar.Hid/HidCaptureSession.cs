using HidSharp;

namespace GeBar.Hid;

public sealed class HidCaptureSession : IAsyncDisposable
{
    private readonly string _devicePath;
    private readonly CancellationTokenSource _stopping = new();
    private HidStream? _stream;
    private Task? _reader;

    public HidCaptureSession(string devicePath) => _devicePath = devicePath;

    public event Action<ReadOnlyMemory<byte>>? PacketReceived;
    public event Action<Exception>? CaptureFailed;


    public Task StartAsync()
    {
        if (_stream is not null) return Task.CompletedTask;
        HidDevice? device = DeviceList.Local.GetHidDevices().FirstOrDefault(candidate => HidDeviceScanner.PathOf(candidate).Equals(_devicePath, StringComparison.OrdinalIgnoreCase));
        if (device is null) throw new InvalidOperationException("Выбранный HID-интерфейс больше не подключён.");
        HidStream stream;
        try
        {
            var configuration = new OpenConfiguration();
            configuration.SetOption(OpenOption.Exclusive, false);
            stream = device.Open(configuration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new IOException($"Не удалось открыть HID-интерфейс: {ex.Message}", ex);
        }
        _stream = stream;
        _reader = ReadLoopAsync(device.GetMaxInputReportLength(), _stopping.Token);
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        HidStream stream = _stream ?? throw new InvalidOperationException("Сначала запустите захват интерфейса.");
        await stream.WriteAsync(packet, token).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(int reportLength, CancellationToken token)
    {
        var buffer = new byte[Math.Max(1, reportLength)];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = await _stream!.ReadAsync(buffer, token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                if (count > 0) PacketReceived?.Invoke(buffer.AsMemory(0, count).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            if (!token.IsCancellationRequested) CaptureFailed?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _stream?.Dispose();
        if (_reader is not null)
        {
            try { await _reader.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stopping.Dispose();
    }
}

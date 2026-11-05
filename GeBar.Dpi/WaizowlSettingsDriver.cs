using System.Buffers.Binary;
using GeBar.Core;
using GeBar.Hid;
using HidSharp;

namespace GeBar.Dpi;

public sealed class WaizowlSettingsDriver(IAppLogger logger) : IMouseSettingsDriver, IMousePollingDriver, IMousePowerDriver
{
    private readonly Func<int, int, IEnumerable<HidDevice>> _devices = (vendor, product) => DeviceList.Local.GetHidDevices(vendor, product);

    internal WaizowlSettingsDriver(IAppLogger logger, Func<int, int, IEnumerable<HidDevice>> devices) : this(logger) => _devices = devices;

    public MouseSettingsCapabilities Capabilities { get; private set; } = Limits(true, 30000);
    public IReadOnlyList<int> PerformanceModes => [0, 1];

    public Task<MouseSettings> SetPollingAsync(HidDeviceProfile device, int pollingRate, CancellationToken token) =>
        UseDevice(device, (stream, product, cancellation) => SetPolling(stream, product, pollingRate, cancellation), token);

    internal async Task<MouseSettings> SetPolling(Stream stream, int product, int pollingRate, CancellationToken token)
    {
        Snapshot current = await ReadSnapshot(stream, product, token, logger).ConfigureAwait(false);
        MouseSettings requested = current.Settings with { PollingRate = pollingRate };
        if (requested == current.Settings) return requested;
        await Exchange(stream, WriteRequest(current, requested), token, logger).ConfigureAwait(false);
        Snapshot actual = await ReadSnapshot(stream, product, token, logger).ConfigureAwait(false);
        if (actual.Settings != requested || actual.Layout != current.Layout) throw new IOException(Strings.VerificationFailed);
        return actual.Settings;
    }

    public Task<MousePowerSettings> ReadPowerAsync(HidDeviceProfile device, CancellationToken token) =>
        UseDevice(device, async (stream, product, cancellation) => Power(await ReadSnapshot(stream, product, cancellation, logger).ConfigureAwait(false)), token, true);

    public Task<MousePowerSettings> SavePowerAsync(HidDeviceProfile device, MousePowerSettings expected, MousePowerSettings requested, CancellationToken token) =>
        UseDevice(device, (stream, product, cancellation) => SavePower(stream, product, expected, requested, cancellation), token);

    internal static MousePowerSettings Power(Snapshot snapshot)
    {
        int offset = SleepOffset(snapshot.Layout);
        int seconds = BinaryPrimitives.ReadUInt16LittleEndian(snapshot.Packet.AsSpan(offset, 2));
        if (seconds == 0) throw new InvalidDataException(Strings.InvalidResponse);
        return new(seconds, (snapshot.Packet[5] & 0x40) == 0 ? 0 : 1);
    }

    private static int SleepOffset(ModelLayout layout) => layout.LodOffset switch { 29 => 36, 37 => 44, 44 => 51, _ => throw new NotSupportedException(Strings.Unsupported) };

    internal async Task<MousePowerSettings> SavePower(Stream stream, int product, MousePowerSettings expected, MousePowerSettings requested, CancellationToken token)
    {
        if (requested.SleepSeconds is < 1 or > 65535 || requested.PerformanceMode is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(requested));
        Snapshot current = await ReadSnapshot(stream, product, token, logger).ConfigureAwait(false);
        if (Power(current) != expected) throw new InvalidOperationException(Strings.ChangedExternally);
        byte[] write = [.. current.Packet];
        write[4] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(write.AsSpan(SleepOffset(current.Layout), 2), (ushort)requested.SleepSeconds);
        write[5] = (byte)((write[5] & ~0x40) | (requested.PerformanceMode == 1 ? 0x40 : 0));
        Checksum(write);
        await Exchange(stream, write, token, logger).ConfigureAwait(false);
        await Flash(stream, product, token).ConfigureAwait(false);
        Snapshot actual = await ReadSnapshot(stream, product, token, logger).ConfigureAwait(false);
        if (Power(actual) != requested || actual.Settings != current.Settings || actual.Layout != current.Layout) throw new IOException(Strings.VerificationFailed);
        return Power(actual);
    }

    public bool Supports(HidDeviceProfile device) => device.AllConnections().Any(connection => connection.VendorId == 0x1915 && SupportedProduct(connection.ProductId));

    public Task<MouseSettings> ReadAsync(HidDeviceProfile device, CancellationToken token) => UseDevice(device, async (stream, product, cancellation) =>
    {
        Snapshot snapshot = await ReadSnapshot(stream, product, cancellation, logger).ConfigureAwait(false);
        Capabilities = Limits(product == 0x0728, snapshot.Layout.MaximumDpi);
        return snapshot.Settings;
    }, token, true);

    public Task<MouseSettings> SaveAsync(HidDeviceProfile device, MouseSettings expected, MouseSettings requested, CancellationToken token)
    {
        if (!Capabilities.Accepts(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        return UseDevice(device, (stream, product, cancellation) => Save(stream, product, expected, requested, cancellation), token);
    }

    internal async Task<MouseSettings> Save(Stream stream, int product, MouseSettings expected, MouseSettings requested, CancellationToken token)
    {
        Snapshot current = await ReadSnapshot(stream, product, token, logger).ConfigureAwait(false);
        if (!Limits(product == 0x0728, current.Layout.MaximumDpi).Accepts(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        if (current.Settings != expected) throw new InvalidOperationException(Strings.ChangedExternally);
        if (requested == expected) return expected;
        byte[] write = WriteRequest(current, requested);
        await Exchange(stream, write, token, logger).ConfigureAwait(false);
        await Flash(stream, product, token).ConfigureAwait(false);
        Snapshot actual = await ReadSnapshot(stream, product, token, logger).ConfigureAwait(false);
        if (actual.Settings != requested || actual.Layout != current.Layout) throw new IOException(Strings.VerificationFailed);
        logger.Info($"g-eDPI: Waizowl settings verified, model={actual.Layout.Name}, DPI={actual.Settings.Dpi}, polling={actual.Settings.PollingRate} Hz, LOD={actual.Settings.Lod} mm");
        return actual.Settings;
    }

    private static async Task Flash(Stream stream, int product, CancellationToken token)
    {
        using IDisposable? access = stream is HidStream hid ? await HidDeviceAccess.EnterAsync(hid.Device.DevicePath, token).ConfigureAwait(false) : null;
        await stream.WriteAsync(Request(product == 0x0728 ? (byte)0x4A : (byte)0x0A, 1), token).ConfigureAwait(false);
        await Task.Delay(60, token).ConfigureAwait(false);
    }

    internal sealed record ModelLayout(string Name, int LodOffset, int MaximumDpi);
    internal sealed record Snapshot(ModelLayout Layout, byte[] Packet, MouseSettings Settings);

    internal static async Task<Snapshot> ReadSnapshot(Stream stream, int product, CancellationToken token, IAppLogger? trace = null)
    {
        bool wireless = product == 0x0728;
        ModelLayout layout;
        if (wireless)
        {
            byte[] receiver = await Exchange(stream, Request(0x01, 1), token, trace).ConfigureAwait(false);
            layout = Identify(receiver.AsSpan(12, 4));
        }
        else layout = ProductLayout(product);
        byte[] status = await Exchange(stream, Request(wireless ? (byte)0x41 : (byte)0x01, 1), token, trace).ConfigureAwait(false);
        byte[] sensor = await Exchange(stream, Request(wireless ? (byte)0x44 : (byte)0x04, 1), token, trace,
            packet => IsSensorSnapshot(packet, layout.MaximumDpi, layout.LodOffset)).ConfigureAwait(false);
        int active = sensor[8];
        if (active > 4 || (sensor[7] & (1 << active)) == 0 || status[9] != active) throw new InvalidDataException(Strings.InvalidResponse);
        int dpi = BinaryPrimitives.ReadUInt16LittleEndian(sensor.AsSpan(9 + active * 4, 2));
        int rate = DecodePolling(status[10]);
        double lod = sensor[layout.LodOffset];
        var settings = new MouseSettings(dpi, rate, lod);
        if (!Limits(wireless, layout.MaximumDpi).Accepts(settings)) throw new InvalidDataException(Strings.InvalidResponse);
        return new(layout, sensor, settings);
    }

    internal static byte[] WriteRequest(Snapshot current, MouseSettings requested)
    {
        byte[] packet = [.. current.Packet];
        bool wireless = packet[1] == 0x44;
        if (packet.Length != 65 || packet[1] is not (0x44 or 0x04) || packet[4] is not (1 or 2)
            || !Limits(wireless, current.Layout.MaximumDpi).Accepts(requested)) throw new InvalidDataException(Strings.InvalidResponse);
        packet[4] = 2;
        if (requested.Dpi != current.Settings.Dpi)
        {
            int offset = 9 + packet[8] * 4;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset, 2), (ushort)requested.Dpi);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset + 2, 2), (ushort)requested.Dpi);
        }
        if (requested.PollingRate != current.Settings.PollingRate)
        {
            packet[6] = EncodePolling(requested.PollingRate);
            if (requested.PollingRate >= 2000) packet[5] |= 0x40;
        }
        if (requested.Lod != current.Settings.Lod) packet[current.Layout.LodOffset] = (byte)requested.Lod;
        Checksum(packet);
        return packet;
    }

    internal static ModelLayout Identify(ReadOnlySpan<byte> id) => Convert.ToHexString(id) switch
    {
        "F2070000" => new("OGM Cloud", 37, 26000),
        "F4070000" => new("Cloud 3950", 29, 30000),
        "F6070000" => new("Cloud XS", 29, 30000),
        "F3070000" or "F5070000" => new("OGM PRO V2", 29, 30000),
        "B0070000" => new("OGM PRO", 44, 26000),
        _ => throw new NotSupportedException($"{Strings.Unsupported} ({Convert.ToHexString(id)})")
    };

    private static ModelLayout ProductLayout(int product) => product switch
    {
        0x072C => new("OGM Cloud", 37, 26000),
        0x07A1 => new("Cloud 3950", 29, 30000),
        0x07A3 => new("Cloud XS", 29, 30000),
        0x07A0 or 0x07A2 => new("OGM PRO V2", 29, 30000),
        0x0726 => new("OGM PRO", 44, 26000),
        _ => throw new NotSupportedException(Strings.Unsupported)
    };

    private static bool SupportedProduct(int product) => product is 0x0728 or 0x072C or 0x07A1 or 0x07A3 or 0x07A0 or 0x07A2 or 0x0726;
    private static MouseSettingsCapabilities Limits(bool wireless, int maximum) => new(100, maximum, 50, wireless ? [125, 500, 1000, 2000, 4000, 8000] : [125, 500, 1000], [1, 2]);
    internal static byte EncodePolling(int rate) => rate switch { 125 => 0, 500 => 2, 1000 => 3, 2000 => 4, 4000 => 5, 8000 => 6, _ => throw new ArgumentOutOfRangeException(nameof(rate)) };
    internal static int DecodePolling(byte rate) => rate switch { 0 => 125, 2 => 500, 3 => 1000, 4 => 2000, 5 => 4000, 6 => 8000, _ => throw new InvalidDataException(Strings.InvalidResponse) };

    internal static byte[] Request(byte command, byte operation)
    {
        var packet = new byte[65];
        packet[1] = command;
        packet[3] = 0x81;
        packet[4] = operation;
        Checksum(packet);
        return packet;
    }

    internal static void Checksum(byte[] packet)
    {
        int sum = 0;
        for (int index = 1; index < 64; index++) sum += packet[index];
        packet[64] = unchecked((byte)(0xA1 - sum));
    }

    internal static bool MatchesReply(byte[] packet, byte[] request) => packet.Length == 65 && packet[0] == 0 && packet[1] == request[1]
        && (packet[4] == request[4] || request[1] is 0x04 or 0x44 && request[4] == 1 && packet[4] == 2);

    private static bool IsSensorSnapshot(byte[] packet, int maximumDpi, int lodOffset)
    {
        int active = packet[8];
        if (active > 4 || (packet[7] & (1 << active)) == 0 || packet[lodOffset] is not (1 or 2)) return false;
        int dpi = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(9 + active * 4, 2));
        return dpi >= 100 && dpi <= maximumDpi && dpi % 50 == 0;
    }

    private static async Task<byte[]> Exchange(Stream stream, byte[] request, CancellationToken token, IAppLogger? trace = null, Func<byte[], bool>? validate = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            if (trace?.IsRawPacketLoggingEnabled == true) trace.Info($"g-eDPI Waizowl TX [{request.Length}] {Convert.ToHexString(request)}");
            byte[]? response = await HidRequestExchange.WriteThenReadInputAsync(stream, request, 65, null, TimeSpan.FromMilliseconds(1200), token,
                unmatched: packet =>
                {
                    if (trace?.IsRawPacketLoggingEnabled == true) trace.Info($"g-eDPI Waizowl RX [{packet.Length}] {Convert.ToHexString(packet)} ignored");
                },
                accept: packet => MatchesReply(packet, request) && (validate is null || validate(packet))).ConfigureAwait(false);
            if (response is not null)
            {
                if (trace?.IsRawPacketLoggingEnabled == true) trace.Info($"g-eDPI Waizowl RX [{response.Length}] {Convert.ToHexString(response)}");
                return response;
            }
            token.ThrowIfCancellationRequested();
        }
        throw new IOException(Strings.Unavailable);
    }

    private async Task<T> UseDevice<T>(HidDeviceProfile device, Func<HidStream, int, CancellationToken, Task<T>> action, CancellationToken token, bool readOnly = false)
    {
        if (!Supports(device)) throw new NotSupportedException(Strings.Unsupported);
        bool attempted = false;
        foreach (HidDeviceConnectionProfile connection in device.AllConnections()
            .Where(connection => connection.VendorId == 0x1915 && SupportedProduct(connection.ProductId)).OrderBy(connection => connection.ProductId == 0x0728 ? 1 : 0))
        {
            HidDevice[] handles = [.. _devices(connection.VendorId, connection.ProductId)];
            foreach (HidProbeTarget target in HidDeviceScanner.Pair(connection.Interfaces, handles)
                .OrderByDescending(target => string.Equals(target.Interface.DevicePath, connection.Battery.DevicePath, StringComparison.OrdinalIgnoreCase)))
            {
                HidDevice handle = target.Handle;
                var descriptor = target.Interface with
                {
                    UsagePage = HidDeviceScanner.TopUsagePage(handle) ?? target.Interface.UsagePage,
                    InputReportLength = HidDeviceScanner.InputLengthOf(handle),
                    OutputReportLength = OutputLength(handle)
                };
                bool supported = IsSettingsInterface(descriptor, connection.Battery);
                if (logger.IsRawPacketLoggingEnabled)
                    logger.Info($"g-eDPI: Waizowl interface {connection.VendorId:X4}:{connection.ProductId:X4}, IN={descriptor.InputReportLength}, OUT={descriptor.OutputReportLength}, usagePage={descriptor.UsagePage?.ToString("X4") ?? "unknown"}, settings={supported}, path={descriptor.DevicePath}");
                if (!supported) continue;
                using HidStream? stream = HidRequestExchange.TryOpenShared(handle);
                if (stream is null)
                {
                    logger.Warning($"g-eDPI: Waizowl settings interface could not be opened, path={descriptor.DevicePath}");
                    continue;
                }
                attempted = true;
                logger.Info($"g-eDPI: opening Waizowl {connection.VendorId:X4}:{connection.ProductId:X4}, path={HidDeviceScanner.PathOf(handle)}");
                try { return await action(stream, connection.ProductId, token).ConfigureAwait(false); }
                catch (IOException error) when (readOnly) { logger.Warning($"g-eDPI: Waizowl settings read failed: {error.Message}"); }
            }
        }
        throw new IOException(attempted ? Strings.Unavailable : Strings.InterfaceUnavailable);
    }

    internal static bool IsSettingsInterface(HidInterfaceProfile descriptor, HidBatteryProtocolProfile battery) =>
        descriptor.InputReportLength == 65 && descriptor.OutputReportLength == 65
        && (descriptor.UsagePage >= 0xFF00
            || descriptor.UsagePage is null && battery.Source == HidBatterySource.VendorProtocol && battery.RequestKind == HidRequestKind.Output
                && battery.ProtocolName is "Waizowl status 0x41 (OGM family)" or "Waizowl status 0x01 (OGM wired)"
                && string.Equals(descriptor.DevicePath, battery.DevicePath, StringComparison.OrdinalIgnoreCase));

    private static int OutputLength(HidDevice device)
    {
        try { return device.GetMaxOutputReportLength(); }
        catch (Exception error) when (error is IOException or NotSupportedException) { return 0; }
    }
}

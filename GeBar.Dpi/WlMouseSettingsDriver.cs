using GeBar.Core;
using GeBar.Hid;
using HidSharp;

namespace GeBar.Dpi;

public sealed class WlMouseSettingsDriver(IAppLogger logger) : IMouseSettingsDriver
{
    public MouseSettingsCapabilities Capabilities { get; } = new(50, 30000, 50, [125, 250, 500, 1000, 2000, 4000, 8000], [0.7, 1, 2]);

    public bool Supports(HidDeviceProfile device) => device.AllConnections().Any(connection => connection.VendorId == 0x36A7 && connection.ProductId == 0xA864);

    public Task<MouseSettings> ReadAsync(HidDeviceProfile device, CancellationToken token) => UseDevice(device, (stream, cancellation) => Read(stream, cancellation), token, true);

    public Task<MouseSettings> SaveAsync(HidDeviceProfile device, MouseSettings expected, MouseSettings requested, CancellationToken token)
    {
        if (!Capabilities.Accepts(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        return UseDevice(device, (stream, cancellation) => Save(stream, expected, requested, cancellation), token);
    }

    internal async Task<MouseSettings> Save(HidStream stream, MouseSettings expected, MouseSettings requested, CancellationToken token)
    {
        if (!Capabilities.Accepts(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        Snapshot current = await ReadSnapshot(stream, token).ConfigureAwait(false);
        if (current.Settings != expected) throw new InvalidOperationException(Text.ChangedExternally);
        if (requested.Dpi != current.Settings.Dpi)
        {
            byte[] packet = DpiRequest(current, requested.Dpi);
            await Exchange(stream, packet, token).ConfigureAwait(false);
        }
        if (requested.PollingRate != current.Settings.PollingRate)
            await Exchange(stream, Request(2, 0x00, 1, current.Profile, EncodePolling(requested.PollingRate)), token).ConfigureAwait(false);
        if (requested.Lod != current.Settings.Lod)
            await Exchange(stream, Request(3, 0x08, 1, current.Profile, EncodeLod(requested.Lod), current.LodMode), token).ConfigureAwait(false);
        MouseSettings actual = await Read(stream, token).ConfigureAwait(false);
        if (actual != requested) throw new IOException(Text.VerificationFailed);
        logger.Info($"g-eDPI: settings verified, DPI={actual.Dpi}, polling={actual.PollingRate} Hz, LOD={actual.Lod} mm");
        return actual;
    }

    private async Task<MouseSettings> UseDevice(HidDeviceProfile profile, Func<HidStream, CancellationToken, Task<MouseSettings>> action, CancellationToken token, bool readOnly = false)
    {
        if (!Supports(profile)) throw new NotSupportedException(Text.Unsupported);
        foreach (HidDeviceConnectionProfile connection in profile.AllConnections().Where(connection => connection.VendorId == 0x36A7 && connection.ProductId is 0xA864 or 0xA863)
            .OrderBy(connection => connection.ProductId == 0xA864 ? 0 : 1))
        {
            foreach (HidDevice handle in DeviceList.Local.GetHidDevices(connection.VendorId, connection.ProductId))
            {
                if (HidDeviceScanner.TopUsagePage(handle) is not >= 0xFF00 || FeatureLength(handle) != 65) continue;
                using HidStream? stream = HidRequestExchange.TryOpenShared(handle);
                if (stream is null) continue;
                logger.Info($"g-eDPI: opening {connection.VendorId:X4}:{connection.ProductId:X4}, path={HidDeviceScanner.PathOf(handle)}");
                try { return await action(stream, token).ConfigureAwait(false); }
                catch (IOException error) when (readOnly)
                {
                    logger.Warning($"g-eDPI: settings read failed on {connection.VendorId:X4}:{connection.ProductId:X4}: {error.Message}");
                }
            }
        }
        throw new IOException(Text.Unavailable);
    }

    private static int FeatureLength(HidDevice device)
    {
        try { return device.GetMaxFeatureReportLength(); }
        catch (Exception error) when (error is IOException or NotSupportedException) { return 0; }
    }

    private static async Task<MouseSettings> Read(HidStream stream, CancellationToken token) => (await ReadSnapshot(stream, token).ConfigureAwait(false)).Settings;

    internal sealed record Snapshot(byte Profile, byte ActiveStage, byte[] Stages, bool Special, byte LodMode, MouseSettings Settings);

    internal static async Task<Snapshot> ReadSnapshot(HidStream stream, CancellationToken token)
    {
        byte profile = (await Exchange(stream, Request(1, 0x85, 0), token).ConfigureAwait(false))[7];
        if (profile is < 1 or > 3) throw new InvalidDataException(Text.InvalidResponse);
        byte[] stages = await Exchange(stream, Request(10, 0x81, 1, profile, 6), token).ConfigureAwait(false);
        byte count = stages[8];
        if (count is < 1 or > 6) throw new InvalidDataException(Text.InvalidResponse);
        byte active = (await Exchange(stream, Request(2, 0x82, 1, profile), token).ConfigureAwait(false))[8];
        if (active < 1 || active > count) throw new InvalidDataException(Text.InvalidResponse);
        byte[] special = await Exchange(stream, Request(5, 0x97, 1, profile), token).ConfigureAwait(false);
        bool isSpecial = special[12] != 0;
        int offset = 9 + (active - 1) * 4;
        int dpi = isSpecial ? ReadDpi(special, 8) : ReadDpi(stages, offset);
        byte rate = (await Exchange(stream, Request(2, 0x80, 1, profile), token).ConfigureAwait(false))[8];
        byte[] lod = await Exchange(stream, Request(3, 0x88, 1, profile), token).ConfigureAwait(false);
        var settings = new MouseSettings(dpi, DecodePolling(rate), DecodeLod(lod[8]));
        return new(profile, active, stages.AsSpan(9, count * 4).ToArray(), isSpecial, lod[9], settings);
    }

    internal static byte[] DpiRequest(Snapshot snapshot, int dpi)
    {
        if (snapshot.Special) return Request(5, 0x17, 1, snapshot.Profile, (byte)(dpi >> 8), (byte)dpi, (byte)(dpi >> 8), (byte)dpi);
        byte[] packet = Request(26, 0x01, 1, snapshot.Profile, (byte)(snapshot.Stages.Length / 4));
        snapshot.Stages.CopyTo(packet, 9);
        int offset = 9 + (snapshot.ActiveStage - 1) * 4;
        packet[offset] = packet[offset + 2] = (byte)(dpi >> 8);
        packet[offset + 1] = packet[offset + 3] = (byte)dpi;
        return packet;
    }

    internal static byte[] Request(byte size, byte command, byte group, params byte[] data)
    {
        var packet = new byte[65];
        packet[3] = 2;
        packet[4] = size;
        packet[5] = group;
        packet[6] = command;
        data.CopyTo(packet, 7);
        return packet;
    }

    private static async Task<byte[]> Exchange(HidStream stream, byte[] request, CancellationToken token)
    {
        byte[]? response = await HidRequestExchange.SendThenReadFeatureAsync(stream, request, 65, 5, 60, token,
            reply => reply.Length == 65 && reply[1] == 0xA1 && reply[3] == 2 && reply[5] == request[5] && reply[6] == request[6]
                && (request[5] == 0 || reply[7] == request[7])).ConfigureAwait(false);
        return response ?? throw new IOException(Text.Unavailable);
    }

    private static int ReadDpi(byte[] packet, int offset)
    {
        int dpi = packet[offset] << 8 | packet[offset + 1];
        return dpi is >= 50 and <= 30000 ? dpi : throw new InvalidDataException(Text.InvalidResponse);
    }

    internal static byte EncodePolling(int rate) => rate switch { 125 => 8, 250 => 4, 500 => 2, 1000 => 1, 2000 => 32, 4000 => 64, 8000 => 128, _ => throw new ArgumentOutOfRangeException(nameof(rate)) };
    internal static int DecodePolling(byte rate) => rate switch { 8 => 125, 4 => 250, 2 => 500, 1 or 16 => 1000, 32 => 2000, 64 => 4000, 128 => 8000, _ => throw new InvalidDataException(Text.InvalidResponse) };
    internal static byte EncodeLod(double lod) => lod switch { 0.7 => 0x87, 1 => 1, 2 => 2, _ => throw new ArgumentOutOfRangeException(nameof(lod)) };
    internal static double DecodeLod(byte lod) => lod switch { 0x87 => 0.7, 1 => 1, 2 => 2, _ => throw new InvalidDataException(Text.InvalidResponse) };
}

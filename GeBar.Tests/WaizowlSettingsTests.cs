using System.Buffers.Binary;
using System.Threading.Channels;
using GeBar.Core;
using GeBar.Dpi;
using HidSharp;

namespace GeBar.Tests;

public class WaizowlSettingsTests
{
    private static readonly MouseSettings Original = new(800, 1000, 1);

    [Theory]
    [InlineData("F2070000", 37, 26000)]
    [InlineData("F4070000", 29, 30000)]
    [InlineData("F6070000", 29, 30000)]
    [InlineData("F3070000", 29, 30000)]
    [InlineData("F5070000", 29, 30000)]
    [InlineData("B0070000", 44, 26000)]
    public async Task ReceiverIdentifiesLayoutBeforeReadingSettings(string model, int lodOffset, int maximum)
    {
        using var stream = new Firmware([Receiver(model), Status(Original), Sensor(Original, lodOffset)]);

        WaizowlSettingsDriver.Snapshot snapshot = await WaizowlSettingsDriver.ReadSnapshot(stream, 0x0728, CancellationToken.None);

        Assert.Equal(Original, snapshot.Settings);
        Assert.Equal(lodOffset, snapshot.Layout.LodOffset);
        Assert.Equal(maximum, snapshot.Layout.MaximumDpi);
        Assert.Equal(new byte[] { 0x01, 0x41, 0x44 }, stream.Requests.Select(packet => packet[1]));
        Assert.All(stream.Requests, packet => Assert.Equal(0xA1, packet.Sum(value => value) & 0xFF));
    }

    [Theory]
    [InlineData("F2070000", 37)]
    [InlineData("F3070000", 29)]
    [InlineData("B0070000", 44)]
    public async Task SavePreservesUnrelatedBytesAndVerifiesStoredValues(string model, int lodOffset)
    {
        var requested = new MouseSettings(400, 8000, 2);
        byte[] before = Sensor(Original, lodOffset);
        using var stream = new Firmware([
            Receiver(model), Status(Original), before,
            Packet(0x44, 2),
            Receiver(model), Status(requested), Sensor(requested, lodOffset)
        ]);
        var driver = new WaizowlSettingsDriver(new Logger());

        MouseSettings actual = await driver.Save(stream, 0x0728, Original, requested, CancellationToken.None);

        Assert.Equal(requested, actual);
        byte[] write = Assert.Single(stream.Requests, packet => packet[1] == 0x44 && packet[4] == 2);
        int[] changed = [4, 5, 6, 13, 14, 15, 16, lodOffset, 64];
        for (int index = 0; index < before.Length; index++)
            if (!changed.Contains(index)) Assert.Equal(before[index], write[index]);
        Assert.Equal(400, BinaryPrimitives.ReadUInt16LittleEndian(write.AsSpan(13, 2)));
        Assert.Equal(400, BinaryPrimitives.ReadUInt16LittleEndian(write.AsSpan(15, 2)));
        Assert.Equal(6, write[6]);
        Assert.Equal(2, write[lodOffset]);
        Assert.Equal(0xA1, write.Sum(value => value) & 0xFF);
        Assert.Equal(0x4A, stream.Requests[4][1]);
        Assert.Equal(8, stream.Requests.Count);
    }

    [Fact]
    public async Task UnknownPairedMouseCannotReceiveSettingsWrites()
    {
        using var stream = new Firmware([Receiver("00000000")]);
        var driver = new WaizowlSettingsDriver(new Logger());

        await Assert.ThrowsAsync<NotSupportedException>(() => driver.Save(stream, 0x0728, Original, Original with { Dpi = 400 }, CancellationToken.None));

        Assert.Single(stream.Requests);
        Assert.Equal(1, stream.Requests[0][1]);
    }

    [Fact]
    public async Task SleepingReceiverCannotReceiveSettingsWrites()
    {
        byte[] receiver = Receiver("F3070000");
        receiver[5] = 0;
        using var stream = new Firmware([receiver]);
        var driver = new WaizowlSettingsDriver(new Logger());

        await Assert.ThrowsAsync<IOException>(() => driver.Save(stream, 0x0728, Original, Original with { Dpi = 400 }, CancellationToken.None));

        Assert.Single(stream.Requests);
    }

    [Fact]
    public async Task ExternalChangesPreventWrites()
    {
        using var stream = new Firmware([Receiver("F3070000"), Status(Original), Sensor(Original, 29)]);
        var driver = new WaizowlSettingsDriver(new Logger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.Save(stream, 0x0728,
            Original with { Dpi = 1600 }, Original with { Dpi = 400 }, CancellationToken.None));

        Assert.Equal(3, stream.Requests.Count);
        Assert.DoesNotContain(stream.Requests, packet => packet[4] == 2 || packet[1] == 0x4A);
    }

    [Fact]
    public async Task ReadbackMismatchIsNotReportedAsSaved()
    {
        using var stream = new Firmware([
            Receiver("F3070000"), Status(Original), Sensor(Original, 29), Packet(0x44, 2),
            Receiver("F3070000"), Status(Original), Sensor(Original, 29)
        ]);
        var driver = new WaizowlSettingsDriver(new Logger());

        await Assert.ThrowsAsync<IOException>(() => driver.Save(stream, 0x0728, Original, Original with { Dpi = 400 }, CancellationToken.None));
    }

    [Fact]
    public async Task WiredCommandsUseWiredLayoutAndRejectWirelessOnlyPolling()
    {
        byte[] status = Status(Original);
        status[1] = 1;
        byte[] sensor = Sensor(Original, 29);
        sensor[1] = 4;
        using var stream = new Firmware([status, sensor]);
        var driver = new WaizowlSettingsDriver(new Logger());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.Save(stream, 0x07A0,
            Original, Original with { PollingRate = 8000 }, CancellationToken.None));

        Assert.Equal(new byte[] { 1, 4 }, stream.Requests.Select(packet => packet[1]));
    }

    [Fact]
    public async Task StaleActiveStageIsRejected()
    {
        byte[] status = Status(Original);
        status[9] = 2;
        using var stream = new Firmware([Receiver("F3070000"), status, Sensor(Original, 29)]);

        await Assert.ThrowsAsync<InvalidDataException>(() => WaizowlSettingsDriver.ReadSnapshot(stream, 0x0728, CancellationToken.None));
    }

    [Fact]
    public void OnlyKnownWaizowlInterfacesAreSupported()
    {
        var profile = new HidDeviceProfile("ogm", "OGM 8K", 0x1915, 0x0728, null, null, [], new());
        var driver = new WaizowlSettingsDriver(new Logger());

        Assert.True(driver.Supports(profile));
        Assert.False(driver.Supports(profile with { ProductId = 0xFFFF }));
        Assert.False(driver.Supports(profile with { VendorId = 0xFFFF }));
    }

    [Theory]
    [InlineData(0xFF00)]
    [InlineData(0xFF0A)]
    [InlineData(0xFF31)]
    public async Task DeviceEntryPointReadsAndSavesThroughVendorInterface(int usagePage)
    {
        var requested = Original with { PollingRate = 2000 };
        using var firmware = new Firmware([
            Receiver("F5070000"), Status(Original), Sensor(Original, 29),
            Receiver("F5070000"), Status(Original), Sensor(Original, 29), Packet(0x44, 2),
            Receiver("F5070000"), Status(requested), Sensor(requested, 29)
        ]);
        var keyboard = new TestDevice("keyboard-" + Guid.NewGuid(), firmware);
        var mouse = new TestDevice("vendor-" + Guid.NewGuid(), firmware);
        HidDeviceProfile profile = new("ogm", "OGM 8K", 0x1915, 0x0728, null, null,
            [new(keyboard.DevicePath, 65, 65, 0, 1), new(mouse.DevicePath, 65, 65, 0, usagePage)],
            Battery(mouse.DevicePath));
        var driver = new WaizowlSettingsDriver(new Logger(), (_, _) => [keyboard, mouse]);

        MouseSettings read = await driver.ReadAsync(profile, CancellationToken.None);
        MouseSettings saved = await driver.SaveAsync(profile, read, requested, CancellationToken.None);

        Assert.Equal(Original, read);
        Assert.Equal(requested, saved);
        Assert.Equal(0, keyboard.Opens);
        Assert.Equal(2, mouse.Opens);
        Assert.Equal(4, Assert.Single(firmware.Requests, packet => packet[1] == 0x44 && packet[4] == 2)[6]);
    }

    [Fact]
    public async Task VerifiedBatteryInterfaceWorksWhenUsageDescriptorIsUnavailable()
    {
        using var firmware = new Firmware([Receiver("F5070000"), Status(Original), Sensor(Original, 29)]);
        var mouse = new TestDevice("vendor-" + Guid.NewGuid(), firmware);
        HidDeviceProfile profile = new("ogm", "OGM 8K", 0x1915, 0x0728, null, null,
            [new(mouse.DevicePath, 65, 65, 0)], Battery(mouse.DevicePath));
        var driver = new WaizowlSettingsDriver(new Logger(), (_, _) => [mouse]);

        Assert.Equal(Original, await driver.ReadAsync(profile, CancellationToken.None));
        Assert.Equal(1, mouse.Opens);
    }

    [Theory]
    [InlineData(1, 65, 65, true, false)]
    [InlineData(12, 65, 65, true, false)]
    [InlineData(0xFF00, 8, 65, true, false)]
    [InlineData(0xFF00, 65, 8, true, false)]
    [InlineData(null, 65, 65, false, false)]
    [InlineData(null, 65, 65, true, true)]
    [InlineData(0xFF00, 65, 65, false, true)]
    public void InterfaceSelectionRequiresVendorReportsOrVerifiedProtocol(int? usagePage, int input, int output, bool known, bool expected)
    {
        HidBatteryProtocolProfile battery = known ? Battery("device") : new();
        var descriptor = new HidInterfaceProfile("device", input, output, 0, usagePage);

        Assert.Equal(expected, WaizowlSettingsDriver.IsSettingsInterface(descriptor, battery));
    }

    private static HidBatteryProtocolProfile Battery(string path) => new(Source: HidBatterySource.VendorProtocol, RequestKind: HidRequestKind.Output,
        DevicePath: path, ProtocolName: "Waizowl status 0x41 (OGM family)");

    private sealed class TestDevice(string path, Firmware firmware) : HidDevice
    {
        public int Opens { get; private set; }
        public override int VendorID => 0x1915;
        public override int ProductID => 0x0728;
        public override int ReleaseNumberBcd => 0;
        public override string DevicePath => path;
        public override string GetFileSystemName() => path;
        public override int GetMaxInputReportLength() => 65;
        public override int GetMaxOutputReportLength() => 65;
        public override int GetMaxFeatureReportLength() => 0;
        protected override DeviceStream OpenDeviceDirectly(OpenConfiguration configuration)
        {
            Opens++;
            return new TestStream(this, firmware);
        }
    }

    private sealed class TestStream(TestDevice device, Firmware firmware) : HidStream(device)
    {
        public override int ReadTimeout { get; set; } = 1000;
        public override int WriteTimeout { get; set; } = 1000;
        public override void GetFeature(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetFeature(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => firmware.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => firmware.WriteAsync(buffer, cancellationToken);
    }

    private static byte[] Packet(byte command, byte operation)
    {
        var packet = new byte[65];
        packet[1] = command;
        packet[3] = 0x8C;
        packet[4] = operation;
        return packet;
    }

    private static byte[] Receiver(string id)
    {
        byte[] packet = Packet(1, 1);
        packet[5] = 8;
        Convert.FromHexString(id).CopyTo(packet, 12);
        return packet;
    }

    private static byte[] Status(MouseSettings settings)
    {
        byte[] packet = Packet(0x41, 1);
        packet[7] = 74;
        packet[9] = 1;
        packet[10] = WaizowlSettingsDriver.EncodePolling(settings.PollingRate);
        return packet;
    }

    private static byte[] Sensor(MouseSettings settings, int lodOffset)
    {
        byte[] packet = Packet(0x44, 1);
        packet[5] = 0x35;
        packet[6] = WaizowlSettingsDriver.EncodePolling(settings.PollingRate);
        packet[7] = 31;
        packet[8] = 1;
        for (int index = 0; index < 5; index++)
        {
            int dpi = index == 1 ? settings.Dpi : (index + 1) * 400;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9 + index * 4, 2), (ushort)dpi);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(11 + index * 4, 2), (ushort)dpi);
        }
        for (int index = 29; index < 64; index++) packet[index] = (byte)(0x80 + index);
        packet[lodOffset] = (byte)settings.Lod;
        return packet;
    }

    private sealed class Firmware(byte[][] responses) : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Queue<byte[]> _responses = new(responses);
        public List<byte[]> Requests { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] reply = await _incoming.Reader.ReadAsync(cancellationToken);
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] request = buffer.ToArray();
            Requests.Add(request);
            if (request[1] is 0x4A or 0x0A) return ValueTask.CompletedTask;
            byte[] response = _responses.Dequeue();
            Assert.Equal(response[1], request[1]);
            Assert.Equal(response[4], request[4]);
            _incoming.Writer.TryWrite(response);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Logger : IAppLogger
    {
        public string LogDirectory => Path.GetTempPath();
        public bool IsRawPacketLoggingEnabled => false;
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}

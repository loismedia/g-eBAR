using GeBar.Core;
using GeBar.Hid;
using System.Threading.Channels;

namespace GeBar.Tests;

public class HidRequestExchangeTests
{
    private static byte[] Packet(string hex, int length = 65) => Convert.FromHexString(hex.PadRight(length * 2, '0'));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionPacketBeforeBatteryReplyIsSkipped(bool probing)
    {
        byte[] version = Packet("0001008C0102151928070601F507");
        byte[] battery = Packet("0001008C0100013E00020303");
        using var stream = new PacketStream(version, battery);
        BatteryCommand command = KnownBatteryProtocols.Find("Waizowl status 0x01 (OGM wired)")!;
        var profile = new HidBatteryProtocolProfile(Source: HidBatterySource.VendorProtocol, PercentageOffset: 7, ProtocolName: command.Name);
        var ignored = new List<byte[]>();

        byte[]? response = await HidRequestExchange.WriteThenReadInputAsync(stream, command.Request, 65, "01", TimeSpan.FromSeconds(1), CancellationToken.None,
            ignored.Add, packet => probing ? KnownBatteryProtocols.Accepts(packet, command) || KnownBatteryProtocols.IsSleepResponse(packet, command)
                : HidBatteryReader.ParseReply(packet, profile).Answered);

        Assert.Equal(battery, response);
        Assert.Equal(version, Assert.Single(ignored));
        HidReplay replay = HidBatteryReader.ParseReply(response!, profile);
        Assert.NotNull(replay.Reading);
        Assert.Equal(62, replay.Reading.Percentage);
        Assert.True(replay.Reading.IsCharging);
    }

    [Fact]
    public async Task VersionPacketAloneIsNotReturnedAsBatteryOrSleep()
    {
        byte[] version = Packet("0001008C0102151928070601F507");
        using var stream = new PacketStream(version);
        var profile = new HidBatteryProtocolProfile(Source: HidBatterySource.VendorProtocol, PercentageOffset: 7, ProtocolName: "Waizowl status 0x01 (OGM wired)");
        var ignored = new List<byte[]>();

        byte[]? response = await HidRequestExchange.WriteThenReadInputAsync(stream, KnownBatteryProtocols.BuildWaizowl(0x01), 65, "01", TimeSpan.FromMilliseconds(50), CancellationToken.None,
            ignored.Add, packet => HidBatteryReader.ParseReply(packet, profile).Answered);

        Assert.Null(response);
        Assert.Equal(version, Assert.Single(ignored));
    }

    [Fact]
    public async Task BatteryReplyAfterLogitechRefusalIsStillRead()
    {
        byte[] refusal = Packet("510500FF031A0B", 64);
        byte[] battery = Packet("510B000310000600040A4E4E0000", 64);
        using var stream = new PacketStream(refusal, battery);
        BatteryCommand command = KnownBatteryProtocols.Find("Logitech Gaming 0x51")!;
        var ignored = new List<byte[]>();

        byte[]? response = await HidRequestExchange.WriteThenReadInputAsync(stream, command.Request, 64, command.EchoHex, TimeSpan.FromSeconds(1), CancellationToken.None,
            ignored.Add, packet => KnownBatteryProtocols.Accepts(packet, command));

        Assert.Equal(battery, response);
        Assert.True(KnownBatteryProtocols.IsSleepResponse(Assert.Single(ignored), command));
    }

    private sealed class PacketStream(params byte[][] replies) : Stream
    {
        private readonly Channel<byte[]> _packets = Channel.CreateUnbounded<byte[]>();
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
            byte[] packet = await _packets.Reader.ReadAsync(cancellationToken);
            packet.AsMemory().CopyTo(buffer);
            return packet.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            foreach (byte[] reply in replies) _packets.Writer.TryWrite(reply);
            return ValueTask.CompletedTask;
        }
    }
}

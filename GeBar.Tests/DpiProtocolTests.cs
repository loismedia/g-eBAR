using GeBar.Core;
using GeBar.Dpi;
using GeBar.Hid;
using HidSharp;

namespace GeBar.Tests;

public class DpiProtocolTests
{
    private static readonly MouseSettings Original = new(800, 1000, 1);
    private static readonly string[] Captured = [
        "00A1000201008501",
        "00A100021A018101060190019003200320064006400C800C801900190075307530",
        "00A100020201820102",
        "00A10002060197010640064000",
        "00A100020201800101",
        "00A10002030188010100"
    ];

    [Fact]
    public async Task AutomaticPollingWritesOnlyRateAndVerifiesReadback()
    {
        string[] after = [.. Captured];
        after[4] = "00A100020201800108";
        using var stream = new Replies([.. Captured, "00A1000202010001", .. after]);
        Assert.Equal(Original with { PollingRate = 125 }, await WlMouseSettingsDriver.SetPolling(stream, 125, CancellationToken.None));
        byte[] write = Assert.Single(stream.Requests, packet => packet[6] < 0x80);
        Assert.Equal("000000020201000108", Convert.ToHexString(write.AsSpan(0, 9)));
    }

    [Fact]
    public async Task PowerDisablesTurboBeforeHighSpeedAndVerifiesSleepTimer()
    {
        using var stream = new Replies(["00A1000201008501", "00A1000203008701012C", "00A1000202018B0101", "00A100020201930101",
            "00A1000201008501", "00A1000203000701", "00A1000202011301", "00A1000202010B01",
            "00A1000201008501", "00A10002030087010258", "00A1000202018B0100", "00A100020201930100"]);
        Assert.Equal(new(600, 0), await WlMouseSettingsDriver.SavePower(stream, new(300, 2), new(600, 0), CancellationToken.None));
        byte[][] writes = [.. stream.Requests.Where(packet => packet[6] < 0x80)];
        Assert.Equal(new byte[] { 7, 0x13, 0x0B }, writes.Select(packet => packet[6]));
        Assert.Equal(0, writes[0][5]);
        Assert.Equal("0258", Convert.ToHexString(writes[0].AsSpan(8, 2)));
    }

    [Fact]
    public async Task ReadsHuanSettingsFromCapturedReplies()
    {
        using var stream = new Replies(Captured);

        WlMouseSettingsDriver.Snapshot snapshot = await WlMouseSettingsDriver.ReadSnapshot(stream, CancellationToken.None);

        Assert.Equal(Original, snapshot.Settings);
        Assert.Equal(1, snapshot.Profile);
        Assert.Equal(2, snapshot.ActiveStage);
        Assert.False(snapshot.Special);
        Assert.Equal(24, snapshot.Stages.Length);
        Assert.All(stream.Requests, request => Assert.True((request[6] & 0x80) != 0));
    }

    [Fact]
    public async Task SavingOneDpiPreservesOtherStagesAndVerifiesReadback()
    {
        string[] after = [.. Captured];
        after[1] = "00A100021A018101060190019004B004B0064006400C800C801900190075307530";
        using var stream = new Replies([.. Captured, "00A100021A010101", .. after]);
        var driver = new WlMouseSettingsDriver(new Logger());

        MouseSettings actual = await driver.Save(stream, Original, Original with { Dpi = 1200 }, CancellationToken.None);

        Assert.Equal(1200, actual.Dpi);
        Assert.Equal(13, stream.Requests.Count);
        byte[] write = Assert.Single(stream.Requests, request => (request[6] & 0x80) == 0);
        Assert.Equal("000000021A010101060190019004B004B0064006400C800C801900190075307530", Convert.ToHexString(write.AsSpan(0, 33)));
    }

    [Fact]
    public async Task ExternallyChangedSettingsPreventWrites()
    {
        using var stream = new Replies(Captured);
        var driver = new WlMouseSettingsDriver(new Logger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.Save(stream, Original with { Dpi = 1600 }, Original with { Dpi = 1200 }, CancellationToken.None));

        Assert.Equal(6, stream.Requests.Count);
        Assert.All(stream.Requests, request => Assert.True((request[6] & 0x80) != 0));
    }

    [Fact]
    public async Task AcknowledgedWriteWithoutMatchingReadbackIsNotSuccess()
    {
        using var stream = new Replies([.. Captured, "00A100021A010101", .. Captured]);
        var driver = new WlMouseSettingsDriver(new Logger());

        await Assert.ThrowsAsync<IOException>(() => driver.Save(stream, Original, Original with { Dpi = 1200 }, CancellationToken.None));
    }

    [Fact]
    public async Task ChangedPollingAndLodPreserveDpiAndLodMode()
    {
        string[] before = [.. Captured];
        before[5] = "00A10002030188010103";
        string[] after = [.. Captured];
        after[4] = "00A100020201800120";
        after[5] = "00A10002030188018703";
        using var stream = new Replies([.. before, "00A1000202010001", "00A1000203010801", .. after]);
        var driver = new WlMouseSettingsDriver(new Logger());

        MouseSettings actual = await driver.Save(stream, Original, Original with { PollingRate = 2000, Lod = 0.7 }, CancellationToken.None);

        Assert.Equal(new MouseSettings(800, 2000, 0.7), actual);
        Assert.Equal(new[] { "00000002020100012000", "00000002030108018703" },
            stream.Requests.Where(request => (request[6] & 0x80) == 0).Select(request => Convert.ToHexString(request.AsSpan(0, 10))));
    }

    [Fact]
    public async Task RejectedOrUnrelatedReplyIsNeverParsedAsSettings()
    {
        using var stream = new Replies(["00A2000201008501", "00A10002020083002E"]);

        await Assert.ThrowsAsync<IOException>(() => WlMouseSettingsDriver.ReadSnapshot(stream, CancellationToken.None));
    }

    [Fact]
    public void SpecialDpiUsesDedicatedCommand()
    {
        var snapshot = new WlMouseSettingsDriver.Snapshot(1, 2, new byte[24], true, 0, Original);

        byte[] request = WlMouseSettingsDriver.DpiRequest(snapshot, 1200);

        Assert.Equal("000000020501170104B004B0", Convert.ToHexString(request.AsSpan(0, 12)));
    }

    [Theory]
    [InlineData(0, 1000, 1)]
    [InlineData(30050, 1000, 1)]
    [InlineData(801, 1000, 1)]
    [InlineData(800, 750, 1)]
    [InlineData(800, 1000, 0.5)]
    public async Task UnsupportedValuesAreRejectedBeforeAccessingDevice(int dpi, int rate, double lod)
    {
        using var stream = new Replies([]);
        var driver = new WlMouseSettingsDriver(new Logger());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.Save(stream, Original, new(dpi, rate, lod), CancellationToken.None));

        Assert.Empty(stream.Requests);
    }

    [Fact]
    public async Task FeatureTransactionsForSameInterfaceDoNotOverlap()
    {
        string path = Guid.NewGuid().ToString();
        using var first = new Replies([Captured[0]], path);
        using var second = new Replies([Captured[0]], path);
        byte[] request = new byte[65];

        using IDisposable access = await HidDeviceAccess.EnterAsync(path, CancellationToken.None);
        Task<byte[]?> pending = HidRequestExchange.SendThenReadFeatureAsync(first, request, 65, 1, 0, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Empty(first.Requests);
        access.Dispose();
        await pending;
        await HidRequestExchange.SendThenReadFeatureAsync(second, request, 65, 1, 0, CancellationToken.None);

        Assert.Single(first.Requests);
        Assert.Single(second.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task FeatureReplyIsReadUsingTheRequestedReportId(byte reportId)
    {
        using var stream = new Replies(["050102"]);
        byte[] request = [reportId, 1];

        byte[]? response = await HidRequestExchange.SendThenReadFeatureAsync(stream, request, 65, 1, 0, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(reportId, Assert.Single(stream.ReadReportIds));
    }

    private sealed class Device(string path) : HidDevice
    {
        public override int VendorID => 0xFFFF;
        public override int ProductID => 0xFFFC;
        public override int ReleaseNumberBcd => 0;
        public override string DevicePath => path;
        public override string GetFileSystemName() => path;
        public override int GetMaxInputReportLength() => 0;
        public override int GetMaxOutputReportLength() => 0;
        public override int GetMaxFeatureReportLength() => 65;
        protected override DeviceStream OpenDeviceDirectly(OpenConfiguration configuration) => throw new NotSupportedException();
    }

    private sealed class Replies(string[] replies, string? path = null) : HidStream(new Device(path ?? Guid.NewGuid().ToString()))
    {
        private readonly Queue<byte[]> _replies = new(replies.Select(hex => Convert.FromHexString(hex.PadRight(130, '0'))));
        public List<byte[]> Requests { get; } = [];
        public List<byte> ReadReportIds { get; } = [];
        public override int ReadTimeout { get; set; } = 1000;
        public override int WriteTimeout { get; set; } = 1000;
        public override void SetFeature(byte[] buffer, int offset, int count) => Requests.Add(buffer.AsSpan(offset, count).ToArray());
        public override void GetFeature(byte[] buffer, int offset, int count)
        {
            ReadReportIds.Add(buffer[offset]);
            if (_replies.TryDequeue(out byte[]? reply)) reply.AsSpan(0, count).CopyTo(buffer.AsSpan(offset, count));
            else buffer.AsSpan(offset, count).Clear();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new TimeoutException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

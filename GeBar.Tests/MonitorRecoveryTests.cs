using System.Reflection;
using GeBar.Core;
using GeBar.Hid;
using HidSharp;

namespace GeBar.Tests;

public class MonitorRecoveryTests
{
    [Fact]
    public async Task DiscoveredProtocolRecoversAutomaticallyWhileFullProbingIsPaused()
    {
        var device = new Mouse(0xFFFE, "wireless");
        HidDeviceProfile profile = Profile(device);
        await using var monitor = Monitor(profile);
        Remember(monitor, profile.AllConnections().First(), Protocol(device));
        device.Reply = Packet("00A10002020083001A");
        await Refresh(monitor, device);
        Assert.Equal(DeviceConnectionState.DeviceConnected, monitor.CurrentState.ConnectionState);
        Assert.Equal(26, monitor.CurrentState.Percentage);

        device.Reply = new byte[65];
        for (int attempt = 0; attempt < 4; attempt++) await Refresh(monitor, device);

        Assert.NotNull(monitor.Diagnostics.ProbingPaused);
        Assert.Equal(DeviceConnectionState.Reading, monitor.CurrentState.ConnectionState);
        Assert.Equal(TimeSpan.FromSeconds(30), Delay(monitor));

        device.Reply = Packet("00A10002020083001A");
        await Refresh(monitor, device);

        Assert.Equal(DeviceConnectionState.DeviceConnected, monitor.CurrentState.ConnectionState);
        Assert.Equal(26, monitor.CurrentState.Percentage);
        Assert.Null(monitor.Diagnostics.ProbingPaused);
        Assert.Equal(6, device.Reads);
        Assert.Equal(TimeSpan.FromMinutes(5), Delay(monitor));
    }

    [Fact]
    public async Task EachConnectionKeepsItsOwnDiscoveredProtocol()
    {
        var wireless = new Mouse(0xFFFE, "wireless") { Reply = Packet("00A100020200830017") };
        var wired = new Mouse(0xFFFD, "wired") { Reply = Packet("00A100020200830047") };
        HidDeviceProfile profile = Profile(wireless) with
        {
            Connections = [new(wired.VendorID, wired.ProductID, null, null, [], new())]
        };
        await using var monitor = Monitor(profile);
        Remember(monitor, profile.AllConnections().First(), Protocol(wireless));
        Remember(monitor, profile.AllConnections().Last(), Protocol(wired));

        await Refresh(monitor, wireless, wired);

        Assert.Equal(DeviceConnectionState.DeviceConnected, monitor.CurrentState.ConnectionState);
        Assert.Equal(23, monitor.CurrentState.Percentage);
        Assert.Equal(1, wireless.Reads);
        Assert.Equal(1, wired.Reads);
    }

    [Fact]
    public async Task ConfirmedSleepWakesWithUnchangedBattery()
    {
        var device = new Mouse(0xFFFE, "wireless") { Reply = Packet("00002C0001010101FA") };
        HidBatteryProtocolProfile battery = Protocol(device) with
        {
            RequestHex = "00F700",
            ProtocolName = "FEA status 0xF7 (RY5088 dongle)",
            PercentageOffset = 2,
            ChargingOffset = null
        };
        await using var monitor = Monitor(Profile(device) with { Battery = battery });

        await Refresh(monitor, device);

        Assert.Equal(DeviceConnectionState.DeviceOff, monitor.CurrentState.ConnectionState);
        Assert.Equal(44, monitor.CurrentState.Percentage);
        Assert.Equal(TimeSpan.FromSeconds(30), Delay(monitor));
        Assert.Null(monitor.Diagnostics.ProbingPaused);

        device.Reply = Packet("00002C0000010101FA");
        await Refresh(monitor, device);

        Assert.Equal(DeviceConnectionState.DeviceConnected, monitor.CurrentState.ConnectionState);
        Assert.Equal(44, monitor.CurrentState.Percentage);
        Assert.Equal(2, device.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LearnedSleepRejectsCachedRepliesAndWakesAfterSilenceOrFreeze(bool silent)
    {
        var device = new Mouse(0xFFFE, "receiver") { Reply = Packet("00A10002020083001A") };
        HidDeviceProfile profile = Profile(device) with { Battery = Protocol(device) };
        await using var monitor = Monitor(profile);
        await Refresh(monitor, device);
        var status = new Mouse(0xFFFD, "status") { InputLength = 9, InputReply = Convert.FromHexString("000600000000000000") };
        HidBatteryProtocolProfile battery = profile.Battery with { OffPrefixHex = "0600000000000000" };
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sleeping = new TaskCompletionSource<DeviceBatteryState>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StateChanged += (_, state) => sleeping.TrySetResult(state);
        int generation = (int)typeof(ProfileBatteryMonitor).GetField("_listenerGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
        Task listener = (Task)Method("ListenDeviceAsync").Invoke(monitor, [status, battery, generation, lifetime.Token])!;
        await sleeping.Task.WaitAsync(lifetime.Token);
        Assert.Equal(DeviceConnectionState.DeviceOff, monitor.CurrentState.ConnectionState);
        Assert.Equal(26, monitor.CurrentState.Percentage);
        lifetime.Cancel();
        await listener;
        await Refresh(monitor, device);
        Assert.Equal(DeviceConnectionState.DeviceOff, monitor.CurrentState.ConnectionState);
        Assert.Equal(26, monitor.CurrentState.Percentage);

        if (silent)
        {
            typeof(ProfileBatteryMonitor).GetField("_probes", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor,
                new Dictionary<(int, int), (int, DateTimeOffset)> { [(device.VendorID, device.ProductID)] = (0, DateTimeOffset.Now.AddMinutes(30)) });
            device.Reply = new byte[65];
            for (int attempt = 0; attempt < 5; attempt++) await Refresh(monitor, device);
            Assert.Equal(DeviceConnectionState.DeviceOff, monitor.CurrentState.ConnectionState);
            Assert.Equal(26, monitor.CurrentState.Percentage);
            Assert.Null(monitor.Diagnostics.LastError);
            Assert.Equal(TimeSpan.FromSeconds(30), Delay(monitor));
            device.Reply = Packet("00A10002020083001A");
        }
        else typeof(ProfileBatteryMonitor).GetField("_offSince", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, DateTimeOffset.Now.AddMinutes(-11));
        await Refresh(monitor, device);
        Assert.Equal(DeviceConnectionState.DeviceConnected, monitor.CurrentState.ConnectionState);
        Assert.Equal(26, monitor.CurrentState.Percentage);
        Assert.Null(monitor.Diagnostics.ProbingPaused);
    }

    [Fact]
    public async Task StatusInterfaceCanBelongToAnotherConnection()
    {
        var device = new Mouse(0xFFFE, "battery");
        var receiver = new Mouse(0xFFFD, "receiver");
        var status = new HidDeviceConnectionProfile(receiver.VendorID, receiver.ProductID, null, null, [new("status", 9, 0, 0)], new());
        HidDeviceProfile profile = Profile(device) with
        {
            Battery = Protocol(device) with { OffPrefixHex = "0600000000000000", StatusDevicePath = "status" },
            Connections = [status]
        };
        await using var monitor = Monitor(profile);
        HidDeviceConnectionProfile fallback = profile.AllConnections().First();

        Assert.Equal(status, Method("StatusConnection").Invoke(monitor, ["STATUS", fallback]));
        Assert.Same(fallback, Method("StatusConnection").Invoke(monitor, ["unknown", fallback]));
        HidBatteryProtocolProfile resolved = (HidBatteryProtocolProfile)Method("RememberProtocol").Invoke(monitor, [status, Protocol(receiver)])!;
        Assert.Equal(profile.Battery.OffPrefixHex, resolved.OffPrefixHex);
        Assert.Equal(profile.Battery.StatusDevicePath, resolved.StatusDevicePath);
        Assert.Equal(receiver.DevicePath, resolved.DevicePath);
        Assert.Equal(Protocol(receiver).RequestHex, resolved.RequestHex);
        Assert.Equal(status, Method("StatusConnection").Invoke(monitor, [resolved.StatusDevicePath, fallback]));
    }

    [Fact]
    public void StatusInterfaceKeepsItsCollectionAfterPortChangesAndRejectsAmbiguity()
    {
        const string oldPath = @"\\?\hid#vid_ffff&pid_fffd&mi_01&col04#old#{hid}";
        var status = new Mouse(0xFFFD, @"\\?\hid#vid_ffff&pid_fffd&mi_01&col04#new#{hid}") { InputLength = 9, UsagePage = 0xFF60 };
        var mouse = new Mouse(0xFFFD, @"\\?\hid#vid_ffff&pid_fffd&mi_00#new#{hid}") { InputLength = 9, UsagePage = 9 };
        var other = new Mouse(0xFFFD, @"\\?\hid#vid_ffff&pid_fffd&mi_01&col05#new#{hid}") { InputLength = 9, UsagePage = 0xFFFF };
        var connection = new HidDeviceConnectionProfile(status.VendorID, status.ProductID, null, null,
            [new(oldPath, 9, 0, 65, 0xFF60)], new());
        MethodInfo match = typeof(ProfileBatteryMonitor).GetMethod("StatusDevice", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Same(status, match.Invoke(null, [connection, new HidDevice[] { mouse, other, status }, oldPath]));
        Assert.Null(match.Invoke(null, [connection, new HidDevice[] { mouse, other }, oldPath]));
        var duplicate = new Mouse(0xFFFD, @"\\?\hid#vid_ffff&pid_fffd&mi_01&col04#another#{hid}") { InputLength = 9, UsagePage = 0xFF60 };
        Assert.Null(match.Invoke(null, [connection, new HidDevice[] { status, duplicate }, oldPath]));
        Assert.Same(status, match.Invoke(null, [connection, new HidDevice[] { status, duplicate }, status.DevicePath]));
        var wrongUsage = new Mouse(0xFFFD, status.DevicePath) { InputLength = 9, UsagePage = 0xFFFF };
        Assert.Null(match.Invoke(null, [connection, new HidDevice[] { wrongUsage }, oldPath]));
    }

    [Fact]
    public async Task EndedInputStopsSleepCaptureAndBatteryReaders()
    {
        var device = new Mouse(0xFFFE, "ended") { InputLength = 9, InputReply = [] };
        await using var monitor = Monitor(Profile(device));
        Task listener = (Task)Method("ListenDeviceAsync").Invoke(monitor,
            [device, Protocol(device), 0, CancellationToken.None])!;
        await listener.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, device.InputReads);

        device.InputReply = [];
        await using var capture = new HidCaptureSession(device.DevicePath);
        typeof(HidCaptureSession).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(capture, new MouseStream(device));
        Task reader = (Task)typeof(HidCaptureSession).GetMethod("ReadLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(capture, [9, CancellationToken.None])!;
        await reader.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, device.InputReads);

        device.InputReply = [];
        var battery = new HidBatteryProtocolProfile(Source: HidBatterySource.HidUsage,
            Percentage: new(HidReportKind.Input, 0, 9, 0, 0, 8, 0, 100, HidBatteryUsages.RemainingCapacity));
        await HidBatteryReader.TryReadAsync(device, battery with { FullCapacity = battery.Percentage }, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(3, device.InputReads);
    }

    [Theory]
    [InlineData(DeviceConnectionState.Reading, 300, true, 30)]
    [InlineData(DeviceConnectionState.ReadError, 300, true, 30)]
    [InlineData(DeviceConnectionState.DeviceOff, 300, true, 30)]
    [InlineData(DeviceConnectionState.DeviceOff, 15, true, 15)]
    [InlineData(DeviceConnectionState.Reading, 300, false, 300)]
    public async Task RecoveryDelayUsesStoredProtocolAndRespectsFasterRefresh(DeviceConnectionState state, int interval, bool known, int seconds)
    {
        var device = new Mouse(0xFFFE, "wireless");
        HidDeviceProfile profile = Profile(device);
        await using var monitor = new ProfileBatteryMonitor(profile, new TestLogger(), TimeSpan.FromSeconds(interval));
        if (known) Remember(monitor, profile.AllConnections().First(), Protocol(device));
        typeof(ProfileBatteryMonitor).GetProperty(nameof(ProfileBatteryMonitor.CurrentState))!.SetValue(monitor,
            DeviceBatteryState.Missing with { ConnectionState = state });

        Assert.Equal(TimeSpan.FromSeconds(seconds), Delay(monitor));
    }

    private static HidDeviceProfile Profile(Mouse device) => new("test-mouse", "Test mouse", device.VendorID, device.ProductID, null, null, [], new());

    private static ProfileBatteryMonitor Monitor(HidDeviceProfile profile) => new(profile, new TestLogger(), TimeSpan.FromMinutes(5));

    private static HidBatteryProtocolProfile Protocol(Mouse device) => new(
        Source: HidBatterySource.VendorProtocol, DevicePath: device.DevicePath,
        RequestHex: "00000002020083", PercentageOffset: 8, ChargingOffset: 7,
        RequestKind: HidRequestKind.Feature, RequestReportLength: 65, ResponseDelayMs: 0, RequestRetries: 1,
        ProtocolName: "Feature 0x83 (WLMOUSE family)");

    private static byte[] Packet(string hex) => Convert.FromHexString(hex.PadRight(130, '0'));

    private static MethodInfo Method(string name) => typeof(ProfileBatteryMonitor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Remember(ProfileBatteryMonitor monitor, HidDeviceConnectionProfile connection, HidBatteryProtocolProfile battery) =>
        Method("RememberProtocol").Invoke(monitor, [connection, battery]);

    private static TimeSpan Delay(ProfileBatteryMonitor monitor) => (TimeSpan)Method("NextDelay").Invoke(monitor, null)!;

    private static async Task Refresh(ProfileBatteryMonitor monitor, params Mouse[] devices)
    {
        var live = devices.ToDictionary(device => (device.VendorID, device.ProductID), device => new HidDevice[] { device });
        var task = (Task)Method("ScanConnectionsAsync").Invoke(monitor, [live, CancellationToken.None])!;
        await task;
        object scan = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var reading = (HidBatteryReading?)scan.GetType().GetProperty("Reading")!.GetValue(scan);
        if (reading is null) Method("PublishWithoutReading").Invoke(monitor, [scan, live, CancellationToken.None]);
        else
        {
            object? protocol = scan.GetType().GetProperty("Protocol")!.GetValue(scan);
            typeof(ProfileBatteryMonitor).GetField("_resolved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, protocol);
            Method("PublishReading").Invoke(monitor, [reading, protocol, CancellationToken.None]);
        }
    }

    private sealed class Mouse(int product, string path) : HidDevice
    {
        public byte[] Reply { get; set; } = new byte[65];
        public byte[]? InputReply { get; set; }
        public int InputLength { get; set; }
        public int UsagePage { get; set; } = 0xFFFF;
        public int InputReads { get; set; }
        public int Reads { get; set; }
        public override int VendorID => 0xFFFF;
        public override int ProductID => product;
        public override int ReleaseNumberBcd => 0;
        public override string DevicePath => path;
        public override string GetFileSystemName() => path;
        public override int GetMaxInputReportLength() => InputLength;
        public override int GetMaxOutputReportLength() => 0;
        public override int GetMaxFeatureReportLength() => 65;
        public override byte[] GetRawReportDescriptor() =>
            [0x06, (byte)UsagePage, (byte)(UsagePage >> 8), 0x09, 1, 0xA1, 1, 0x15, 0, 0x26, 0xFF, 0, 0x75, 8, 0x95, 9, 0x09, 1, 0x81, 2, 0xC0];
        protected override DeviceStream OpenDeviceDirectly(OpenConfiguration configuration) => new MouseStream(this);
    }

    private sealed class MouseStream(Mouse device) : HidStream(device)
    {
        public override int ReadTimeout { get; set; } = 1000;
        public override int WriteTimeout { get; set; } = 1000;
        public override void SetFeature(byte[] buffer, int offset, int count) { }
        public override void GetFeature(byte[] buffer, int offset, int count)
        {
            device.Reads++;
            device.Reply.AsSpan(0, Math.Min(count, device.Reply.Length)).CopyTo(buffer.AsSpan(offset, count));
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new TimeoutException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            device.InputReads++;
            if (device.InputReply is not byte[] packet)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            device.InputReply = null;
            packet.CopyTo(buffer);
            return packet.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

}

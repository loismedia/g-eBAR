namespace GeBar.Core;

public enum DeviceConnectionState { DeviceNotFound, DeviceOff, DeviceConnected, Reading, ReadError }
public enum ConnectionTransport { Default, Bluetooth = 2 }
public sealed record HidReplay(HidBatteryReading? Reading, bool Answered, int? LastKnown = null)
{
    public static HidReplay Silent { get; } = new(null, false);
    public static HidReplay Refused { get; } = new(null, true);
}

public sealed record DeviceBatteryState(DeviceConnectionState ConnectionState, int? Percentage, bool? IsCharging, DateTimeOffset? LastSuccessfulRead, string? ErrorMessage, ConnectionTransport Transport = ConnectionTransport.Default, string? DeviceName = null)
{
    public static DeviceBatteryState Missing { get; } = new(DeviceConnectionState.DeviceNotFound, null, false, null, null);
}
public sealed record HidDiagnostics(string? SelectedDevicePath, string? LastRx, string? LastError, string? ProtocolName = null, IReadOnlyList<string>? Connected = null, string? ChargingSource = null, string? ProbingPaused = null);

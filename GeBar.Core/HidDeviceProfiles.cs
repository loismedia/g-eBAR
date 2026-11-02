namespace GeBar.Core;

public sealed record HidInterfaceProfile(
    string DevicePath,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength,
    int? UsagePage = null) : ExtensibleData;

public enum HidBatterySource { None, HidUsage, VendorProtocol, LearnedOffset }

public enum HidReportKind { Input, Output, Feature }

public enum HidRequestKind { None, Feature, Output }

public sealed record HidBatteryFieldSpec(
    HidReportKind ReportKind,
    byte ReportId,
    int ReportLength,
    int BitOffset,
    int ElementIndex,
    int ElementBits,
    int LogicalMinimum,
    int LogicalMaximum,
    uint Usage) : ExtensibleData;

public sealed record HidBatteryProtocolProfile(
    string? RequestHex = null,
    int? PercentageOffset = null,
    int? ChargingOffset = null,
    int? ChargingValue = null,
    HidBatterySource Source = HidBatterySource.None,
    string? DevicePath = null,
    string? MatchPrefixHex = null,
    HidBatteryFieldSpec? Percentage = null,
    HidBatteryFieldSpec? FullCapacity = null,
    HidBatteryFieldSpec? Charging = null,
    HidBatteryFieldSpec? Discharging = null,
    HidRequestKind RequestKind = HidRequestKind.None,
    int RequestReportLength = 0,
    int ResponseDelayMs = 100,
    int RequestRetries = 8,
    string? ProtocolName = null,
    string? OffPrefixHex = null,
    string? StatusDevicePath = null,
    IReadOnlyList<string>? BluetoothNames = null) : ExtensibleData;

public sealed record HidBatteryReading(
    int Percentage,
    bool? IsCharging,
    HidBatterySource Source,
    string? Detail = null);

public sealed record HidDeviceConnectionProfile(
    int VendorId,
    int ProductId,
    string? Manufacturer,
    string? SerialNumber,
    IReadOnlyList<HidInterfaceProfile> Interfaces,
    HidBatteryProtocolProfile Battery,
    bool IsVerified = false) : ExtensibleData;

public sealed record HidDeviceProfile(
    string Id,
    string Name,
    int VendorId,
    int ProductId,
    string? Manufacturer,
    string? SerialNumber,
    IReadOnlyList<HidInterfaceProfile> Interfaces,
    HidBatteryProtocolProfile Battery,
    bool IsVerified = false,
    string? Label = null,
    IReadOnlyList<HidDeviceConnectionProfile>? Connections = null) : ExtensibleData
{
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label.Trim();

    public IEnumerable<HidDeviceConnectionProfile> AllConnections()
    {
        yield return new(VendorId, ProductId, Manufacturer, SerialNumber, Interfaces, Battery, IsVerified) { Dev = Dev, AdditionalData = AdditionalData };
        foreach (HidDeviceConnectionProfile connection in Connections ?? []) yield return connection;
    }
}

public sealed record HidProfileExport(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<HidDeviceProfile> Devices,
    HidExportDiagnostics? Diagnostics = null) : ExtensibleData;

public sealed record HidExportDiagnostics(
    string Module,
    string Windows,
    IReadOnlyList<string> Probes,
    IReadOnlyList<string> Packets) : ExtensibleData;

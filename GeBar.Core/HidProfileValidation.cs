namespace GeBar.Core;

public static class HidProfileValidation
{
    public const int SupportedFormatVersion = 2;
    private const int MaximumReportLength = 4096;
    private const int MaximumRetries = 64;
    private const int MaximumDelayMs = 10_000;
    private const int MaximumInterfaces = 64;
    private const int MaximumConnections = 16;

    public static string? Problem(this HidProfileExport? export)
    {
        if (export is null) return "file holds no export";
        if (export.FormatVersion > SupportedFormatVersion) return $"format version {export.FormatVersion} is newer than {SupportedFormatVersion}";
        if (export.Devices is null) return "device list is missing";
        return null;
    }

    public static string? Problem(this HidDeviceProfile? profile)
    {
        if (profile is null) return "device entry is empty";
        if (string.IsNullOrWhiteSpace(profile.Id)) return "device has no id";
        if (string.IsNullOrWhiteSpace(profile.Name)) return $"device {profile.Id} has no name";
        if (profile.Connections is { Count: > MaximumConnections }) return $"device {profile.Id} lists more than {MaximumConnections} connections";

        if (profile.Connections is not null && profile.Connections.Any(connection => connection is null)) return $"device {profile.Id} has an empty connection";

        int index = 0;
        foreach (HidDeviceConnectionProfile? connection in profile.AllConnections())
        {
            string where = $"device {profile.Id}, connection {index++}";
            if (connection is null) return $"{where} is empty";
            if (connection.VendorId is < 0 or > 0xFFFF) return $"{where}: vendor id {connection.VendorId} is out of range";
            if (connection.ProductId is < 0 or > 0xFFFF) return $"{where}: product id {connection.ProductId} is out of range";
            if (connection.Interfaces is null) return $"{where}: interface list is missing";
            if (connection.Interfaces.Count > MaximumInterfaces) return $"{where}: more than {MaximumInterfaces} interfaces";
            foreach (HidInterfaceProfile? item in connection.Interfaces)
            {
                if (item is null) return $"{where}: interface entry is empty";
                if (string.IsNullOrWhiteSpace(item.DevicePath)) return $"{where}: interface without a path";
                if (Length(item.InputReportLength) is string input) return $"{where}: input report {input}";
                if (Length(item.OutputReportLength) is string output) return $"{where}: output report {output}";
                if (Length(item.FeatureReportLength) is string feature) return $"{where}: feature report {feature}";
            }
            if (connection.Battery.Problem() is string battery) return $"{where}: {battery}";
        }
        return null;
    }

    private static string? Problem(this HidBatteryProtocolProfile? battery)
    {
        if (battery is null) return "battery section is missing";
        if (Offset(battery.PercentageOffset) is string percentage) return $"percentage offset {percentage}";
        if (Offset(battery.ChargingOffset) is string charging) return $"charging offset {charging}";
        if (Length(battery.RequestReportLength) is string request) return $"request report {request}";
        if (battery.RequestRetries is < 0 or > MaximumRetries) return $"retries {battery.RequestRetries} are out of range";
        if (battery.ResponseDelayMs is < 0 or > MaximumDelayMs) return $"response delay {battery.ResponseDelayMs} is out of range";
        if (Hex(battery.RequestHex) is string requestHex) return $"request {requestHex}";
        if (Hex(battery.MatchPrefixHex) is string matchHex) return $"match prefix {matchHex}";
        if (Hex(battery.OffPrefixHex) is string offHex) return $"off prefix {offHex}";
        if (Field(battery.Percentage) is string percentageField) return $"percentage field {percentageField}";
        if (Field(battery.FullCapacity) is string capacityField) return $"capacity field {capacityField}";
        if (Field(battery.Charging) is string chargingField) return $"charging field {chargingField}";
        if (Field(battery.Discharging) is string dischargingField) return $"discharging field {dischargingField}";
        return null;
    }

    private static string? Field(HidBatteryFieldSpec? field)
    {
        if (field is null) return null;
        if (Length(field.ReportLength) is string length) return $"report {length}";
        if (field.ElementBits is < 1 or > 32) return $"element size {field.ElementBits} is out of range";
        if (field.BitOffset is < 0 or > MaximumReportLength * 8 || field.ElementIndex is < 0 or > MaximumReportLength * 8) return "position is out of range";
        return null;
    }

    private static string? Length(int value) => value is < 0 or > MaximumReportLength ? $"length {value} is out of range" : null;

    private static string? Offset(int? value) => value is < 0 or > MaximumReportLength ? $"{value} is out of range" : null;

    private static string? Hex(string? value)
    {
        if (value is null) return null;
        if (value.Length == 0 || value.Length % 2 != 0) return $"\"{value}\" is not a whole number of bytes";
        return value.All(Uri.IsHexDigit) ? null : $"\"{value}\" is not hexadecimal";
    }
}

namespace GeBar.Core;

public static class HidProfileCompatibility
{
    public static HidBatteryProtocolProfile ReplaceProtocol(HidBatteryProtocolProfile previous, HidBatteryProtocolProfile replacement) =>
        replacement with { Dev = previous.Dev, AdditionalData = previous.AdditionalData };

    public static HidProfileExport UpdateExport(HidProfileExport? previous, HidDeviceProfile profile, HidExportDiagnostics? diagnostics = null)
    {
        HidProfileExport export = previous ?? new(HidProfileValidation.SupportedFormatVersion, DateTimeOffset.Now, []);
        if (export.Problem() is string problem) throw new InvalidDataException(problem);
        return export with
        {
            FormatVersion = HidProfileValidation.SupportedFormatVersion,
            ExportedAt = DateTimeOffset.Now,
            Devices = export.Devices.Any(device => device.Id == profile.Id)
                ? [.. export.Devices.Select(device => device.Id == profile.Id ? profile : device)]
                : [profile],
            Diagnostics = diagnostics ?? export.Diagnostics
        };
    }

    public static IReadOnlyList<HidInterfaceProfile> RefreshInterfaces(IReadOnlyList<HidInterfaceProfile> saved, IReadOnlyList<HidInterfaceProfile> live) =>
        [.. live.Select(item =>
        {
            HidInterfaceProfile? previous = saved.FirstOrDefault(old => old.DevicePath.Equals(item.DevicePath, StringComparison.OrdinalIgnoreCase))
                ?? saved.FirstOrDefault(old => old.InputReportLength == item.InputReportLength && old.OutputReportLength == item.OutputReportLength
                    && old.FeatureReportLength == item.FeatureReportLength && old.UsagePage == item.UsagePage);
            return previous is null ? item : item with { Dev = previous.Dev, AdditionalData = previous.AdditionalData };
        })];
}

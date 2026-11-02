using GeBar.Core;
using HidSharp;
using HidSharp.Reports;

namespace GeBar.Hid;

public static class HidBatteryDescriptorResolver
{
    public static HidBatteryProtocolProfile? TryResolve(HidDevice device)
    {
        ReportDescriptor descriptor;
        try
        {
            descriptor = device.GetReportDescriptor();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }

        HidBatteryFieldSpec? directPercentage = null;
        HidBatteryFieldSpec? remainingCapacity = null;
        HidBatteryFieldSpec? fullCapacity = null;
        HidBatteryFieldSpec? charging = null;
        HidBatteryFieldSpec? discharging = null;

        foreach (Report report in OrderedReports(descriptor))
        {
            int bitOffset = descriptor.ReportsUseID ? 8 : 0;
            foreach (DataItem item in report.DataItems)
            {
                if (item.IsConstant || !item.IsVariable || item.ElementBits <= 0)
                {
                    bitOffset += item.TotalBits;
                    continue;
                }
                foreach (uint usage in item.Usages.GetAllValues())
                {
                    if (!item.Usages.TryGetIndexFromValue(usage, out int elementIndex)) continue;
                    if (elementIndex < 0 || elementIndex >= item.ElementCount) continue;
                    HidBatteryFieldSpec spec = CreateSpec(report, item, bitOffset, elementIndex, usage);
                    if (directPercentage is null && HidBatteryUsages.IsPercentageUsage(usage)) directPercentage = spec;
                    else if (remainingCapacity is null && usage is HidBatteryUsages.RemainingCapacity) remainingCapacity = spec;
                    else if (fullCapacity is null && usage is HidBatteryUsages.FullChargeCapacity) fullCapacity = spec;
                    else if (charging is null && HidBatteryUsages.IsChargingUsage(usage)) charging = spec;
                    else if (discharging is null && usage is HidBatteryUsages.Discharging) discharging = spec;
                }
                bitOffset += item.TotalBits;
            }
        }

        HidBatteryFieldSpec? percentage = directPercentage ?? (fullCapacity is not null ? remainingCapacity : null);
        if (percentage is null) return null;

        return new HidBatteryProtocolProfile(
            Source: HidBatterySource.HidUsage,
            DevicePath: HidDeviceScanner.PathOf(device),
            Percentage: percentage,
            FullCapacity: fullCapacity,
            Charging: charging,
            Discharging: discharging);
    }

    private static IEnumerable<Report> OrderedReports(ReportDescriptor descriptor) =>
        descriptor.FeatureReports.Concat(descriptor.InputReports);

    private static HidBatteryFieldSpec CreateSpec(Report report, DataItem item, int bitOffset, int elementIndex, uint usage) =>
        new(
            Map(report.ReportType),
            report.ReportID,
            report.Length,
            bitOffset,
            elementIndex,
            item.ElementBits,
            item.LogicalMinimum,
            item.LogicalMaximum,
            usage);

    private static HidReportKind Map(ReportType type) => type switch
    {
        ReportType.Output => HidReportKind.Output,
        ReportType.Feature => HidReportKind.Feature,
        _ => HidReportKind.Input
    };
}

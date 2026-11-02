using GeBar.Core;
using HidSharp;

namespace GeBar.Hid;

public sealed record BatteryProbeResult(
    HidBatteryReading? Reading,
    HidBatteryProtocolProfile Profile,
    string DevicePath,
    byte[]? Response = null,
    byte[]? RefusedAnswer = null,
    byte[]? ForeignTraffic = null);

public static class UniversalBatteryProbe
{
    private static readonly TimeSpan DefaultInputTimeout = TimeSpan.FromSeconds(4);

    public static async Task<BatteryProbeResult?> ProbeAsync(HidDeviceProfile device, TimeSpan? inputTimeout, CancellationToken token, IAppLogger? trace = null)
    {
        TimeSpan timeout = inputTimeout ?? DefaultInputTimeout;
        HidDevice[] candidates = DeviceList.Local.GetHidDevices(device.VendorId, device.ProductId).ToArray();
        IReadOnlyList<HidProbeTarget> targets = HidDeviceScanner.Pair(device.Interfaces, candidates);
        trace?.Info($"Probing {device.Name} {device.VendorId:X4}:{device.ProductId:X4}, interfaces={device.Interfaces.Count}, handles={candidates.Length}, targets={targets.Count}");
        HidBatteryProtocolProfile? fallback = null;
        string? fallbackPath = null;

        foreach (HidProbeTarget target in Ordered(targets))
        {
            HidBatteryProtocolProfile? descriptorProfile = HidBatteryDescriptorResolver.TryResolve(target.Handle);
            if (descriptorProfile is null) continue;

            HidBatteryReading? reading = await HidBatteryReader.TryReadAsync(target.Handle, descriptorProfile, timeout, token).ConfigureAwait(false);
            trace?.Info($"  descriptor battery on IN={target.Interface.InputReportLength}/OUT={target.Interface.OutputReportLength}/F={target.Interface.FeatureReportLength}: "
                + (reading is null
                    ? "declared but unreadable"
                    : $"{reading.Percentage}%, charging={reading.IsCharging?.ToString() ?? "not reported"} ({reading.Detail})"));
            if (reading is not null) return new BatteryProbeResult(reading, descriptorProfile, target.Interface.DevicePath);

            fallback ??= descriptorProfile;
            fallbackPath ??= target.Interface.DevicePath;
        }

        byte[]? refused = null;
        byte[]? foreign = null;
        HidBatteryProtocolProfile? spoken = null;
        var vendor = await KnownBatteryProtocols.TryReadAsync(device.VendorId, targets, token, trace, (profile, answer) =>
        {
            if (refused is not null) return;
            refused = answer;
            spoken = profile;
        }, other => foreign ??= other).ConfigureAwait(false);
        if (vendor is not null) return new BatteryProbeResult(vendor.Value.Reading, vendor.Value.Profile, vendor.Value.Profile.DevicePath ?? string.Empty, vendor.Value.Response);

        if (spoken is not null) return new BatteryProbeResult(null, spoken, spoken.DevicePath ?? string.Empty, RefusedAnswer: refused);
        if (fallback is not null) return new BatteryProbeResult(null, fallback, fallbackPath ?? string.Empty, ForeignTraffic: foreign);
        return foreign is null ? null : new BatteryProbeResult(null, new HidBatteryProtocolProfile(), string.Empty, ForeignTraffic: foreign);
    }

    private static IEnumerable<HidProbeTarget> Ordered(IReadOnlyList<HidProbeTarget> targets) =>
        targets
            .OrderByDescending(target => target.Interface.FeatureReportLength > 0)
            .ThenByDescending(target => target.Interface.InputReportLength);
}

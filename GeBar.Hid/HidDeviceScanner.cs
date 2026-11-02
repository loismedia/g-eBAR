using System.Globalization;
using System.Text.RegularExpressions;
using GeBar.Core;
using HidSharp;

namespace GeBar.Hid;

public sealed record HidProbeTarget(HidDevice Handle, HidInterfaceProfile Interface);

public static partial class HidDeviceScanner
{
    public static HidDeviceProfile? ScanDevice(int vendorId, int productId)
    {
        IReadOnlyList<HidDeviceProfile> found = Scan(DeviceList.Local.GetHidDevices(vendorId, productId));
        if (found.Count <= 1) return found.FirstOrDefault();
        return found[0] with { Interfaces = [.. found.SelectMany(profile => profile.Interfaces)] };
    }

    public static IReadOnlyList<HidProbeTarget> Pair(IReadOnlyList<HidInterfaceProfile> stored, IReadOnlyList<HidDevice> handles)
    {
        var targets = new List<HidProbeTarget>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HidInterfaceProfile candidate in stored)
        {
            HidDevice? handle = handles.FirstOrDefault(item => PathOf(item).Equals(candidate.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (handle is null || !taken.Add(candidate.DevicePath)) continue;
            targets.Add(new HidProbeTarget(handle, candidate));
        }
        foreach (HidDevice handle in handles)
        {
            string path = PathOf(handle);
            if (path.Length == 0 || !taken.Add(path)) continue;
            targets.Add(new HidProbeTarget(handle, Describe(handle, path)));
        }
        return targets;
    }

    private static HidInterfaceProfile Describe(HidDevice device, string path) => new(
        path,
        SafeLength(device.GetMaxInputReportLength),
        SafeLength(device.GetMaxOutputReportLength),
        SafeLength(device.GetMaxFeatureReportLength),
        TopUsagePage(device));

    public static string PathOf(HidDevice device) => SafeRead(() => device.DevicePath) ?? string.Empty;

    public static int InputLengthOf(HidDevice device) => SafeLength(device.GetMaxInputReportLength);

    public static IReadOnlyList<HidDeviceProfile> Scan() => Scan(DeviceList.Local.GetHidDevices());

    private static IReadOnlyList<HidDeviceProfile> Scan(IEnumerable<HidDevice> source)
    {
        var devices = source
            .Select(CreateCandidate)
            .OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Device.VendorID)
            .ThenBy(candidate => candidate.Device.ProductID)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return devices
            .GroupBy(candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(CreateProfile)
            .ToArray();
    }

    private static Candidate CreateCandidate(HidDevice device)
    {
        string path = SafeRead(() => device.DevicePath) ?? "unknown";
        string? product = SafeRead(device.GetProductName);
        string? manufacturer = SafeRead(device.GetManufacturer);
        string? serial = SafeRead(device.GetSerialNumber);
        string name = string.IsNullOrWhiteSpace(product)
            ? $"HID {device.VendorID:X4}:{device.ProductID:X4}"
            : product.Trim();
        string groupKey = $"{device.VendorID:X4}:{device.ProductID:X4}:{name}";
        return new Candidate(device, path, name, manufacturer?.Trim(), serial?.Trim(), groupKey);
    }

    private static HidDeviceProfile CreateProfile(IGrouping<string, Candidate> group)
    {
        Candidate first = group.First();
        string id = Slugify($"{first.Name}-{first.Device.VendorID:X4}-{first.Device.ProductID:X4}");
        HidInterfaceProfile[] interfaces = [.. group.Select(candidate => Describe(candidate.Device, candidate.Path))];

        return new HidDeviceProfile(
            id,
            first.Name,
            first.Device.VendorID,
            first.Device.ProductID,
            first.Manufacturer,
            first.Serial,
            interfaces,
            new HidBatteryProtocolProfile());
    }

    public static int? TopUsagePage(HidDevice device)
    {
        try
        {
            int[] pages = device.GetReportDescriptor().Reports
                .SelectMany(report => report.DataItems)
                .SelectMany(item => item.Usages.GetAllValues())
                .Select(usage => (int)(usage >> 16))
                .Where(page => page != 0)
                .ToArray();
            if (pages.Length == 0) return null;
            return pages.GroupBy(page => page).OrderByDescending(group => group.Count()).First().Key;
        }
        catch { return null; }
    }

    private static int SafeLength(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private static string? SafeRead(Func<string> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    private static string Slugify(string value)
    {
        string normalized = value.ToLower(CultureInfo.InvariantCulture);
        normalized = InvalidSlugCharacterRegex().Replace(normalized, "-").Trim('-');
        return RepeatedDashRegex().Replace(normalized, "-");
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidSlugCharacterRegex();

    [GeneratedRegex("-+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedDashRegex();

    private sealed record Candidate(HidDevice Device, string Path, string Name, string? Manufacturer, string? Serial, string GroupKey);
}

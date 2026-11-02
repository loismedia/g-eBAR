using GeBar.Core;
using GeBar.Hid;
using System.Text.Json;

namespace GeBar.App;

internal sealed record DeviceEntry(string Id, string Name, string Revision, string Source, Func<IDeviceBatteryMonitor> Create);

internal static class DeviceCatalog
{
    public const string BuiltInId = "built-in";

    public static IReadOnlyList<DeviceEntry> Build(IAppLogger logger, TimeSpan interval)
    {
        var order = new List<string>();
        var profiles = new Dictionary<string, (HidDeviceProfile Profile, string Origin)>(StringComparer.OrdinalIgnoreCase);
        foreach ((HidDeviceProfile profile, string origin) in BuiltInProfiles(logger).Select(profile => (profile, "built-in")).Concat(LoadProfiles(logger)))
        {
            if (!profiles.ContainsKey(profile.Id)) order.Add(profile.Id);
            profiles[profile.Id] = (profile, origin);
        }
        return [.. order.Select(id => profiles[id]).Select(item => new DeviceEntry(item.Profile.Id, item.Profile.DisplayName,
            Revision(item.Profile, interval),
            Source(item.Profile, item.Origin),
            () => new ProfileBatteryMonitor(item.Profile, logger, interval)))];
    }

    private static string Source(HidDeviceProfile profile, string origin)
    {
        int connections = profile.AllConnections().Count();
        return $"{origin} / {connections} {(connections == 1 ? "connection" : "connections")}";
    }

    private static string Revision(HidDeviceProfile profile, TimeSpan interval)
    {
        string payload = $"{interval.Ticks}|{JsonSerializer.Serialize(profile)}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)))[..16];
    }

    private static IEnumerable<HidDeviceProfile> BuiltInProfiles(IAppLogger logger)
    {
        try
        {
            using Stream? stream = typeof(DeviceCatalog).Assembly.GetManifestResourceStream("GeBar.App.built-in.json");
            if (stream is null) { logger.Warning("Built-in profile is missing from resources"); return []; }
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<HidProfileExport>(reader.ReadToEnd())?.Devices ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            logger.Error("Built-in profile is corrupted", ex);
            return [];
        }
    }

    private static IEnumerable<(HidDeviceProfile Profile, string Origin)> LoadProfiles(IAppLogger logger)
    {
        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(AppPaths.Profiles)) return [];
            files = System.IO.Directory.GetFiles(AppPaths.Profiles, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("Profiles folder could not be read", ex);
            return [];
        }

        var profiles = new List<(HidDeviceProfile Profile, string Origin)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                HidProfileExport? export = JsonSerializer.Deserialize<HidProfileExport>(File.ReadAllText(file));
                if (export.Problem() is string broken)
                {
                    logger.Warning($"Profile {name} skipped: {broken}");
                    continue;
                }
                foreach (HidDeviceProfile profile in export!.Devices)
                {
                    if (profile.Problem() is string invalid)
                    {
                        logger.Warning($"Profile {name} skipped: {invalid}");
                        continue;
                    }
                    if (profile.AllConnections().All(connection => connection.Battery.Source == HidBatterySource.None)) continue;
                    if (!seen.Add(profile.Id)) continue;
                    profiles.Add((profile, $"user file, exported {export.ExportedAt.LocalDateTime:yyyy-MM-dd}"));
                    logger.Info($"Profile loaded: name={profile.DisplayName}, id={profile.Id}, connections={profile.AllConnections().Count()}, source={name}");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException or NullReferenceException)
            {
                logger.Warning($"Profile {name} skipped: {ex.Message}");
            }
        }
        return profiles;
    }
}

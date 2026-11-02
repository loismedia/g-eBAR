using System.Text.Json;
using System.Text.Json.Nodes;
using GeBar.App;
using GeBar.Core;

namespace GeBar.Tests;

public class CompatibilityTests
{
    [Fact]
    public async Task PublicSettingsEditPreservesDevSectionAndUnknownFields()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"WarningThreshold":20,"dev":{"experimentalReader":true,"nested":{"gain":[1,2,null]}},"FutureSetting":{"enabled":true}}
                """);
            AppSettings settings = AppSettings.Load(path: path);
            await (settings with { WarningThreshold = 30 }).SaveAsync(path);
            JsonObject saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Equal(30, saved["WarningThreshold"]!.GetValue<int>());
            Assert.True(JsonNode.DeepEquals(settings.Dev, saved["dev"]));
            Assert.True(saved["FutureSetting"]!["enabled"]!.GetValue<bool>());
            Assert.True(AppSettings.Load(path: path).Dev!["experimentalReader"]!.GetValue<bool>());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProfileEditPreservesDevDataAtEveryLevel()
    {
        string json = """
            {
              "FormatVersion":2,"ExportedAt":"2026-11-02T12:00:00Z","dev":{"tool":"prototype"},"FutureEnvelope":7,
              "Devices":[{
                "Id":"mouse","Name":"Mouse","VendorId":1,"ProductId":2,"Manufacturer":null,"SerialNumber":null,
                "dev":{"calibration":[1,2]},"FutureDevice":true,
                "Interfaces":[{"DevicePath":"old","InputReportLength":65,"OutputReportLength":65,"FeatureReportLength":0,"dev":{"probe":3},"FutureInterface":4}],
                "Battery":{"Source":2,"PercentageOffset":7,"dev":{"decoder":"beta"},"FutureProtocol":{"mode":3}},
                "Connections":[{"VendorId":1,"ProductId":3,"Manufacturer":null,"SerialNumber":null,"Interfaces":[],"Battery":{"Source":2,"dev":{"offset":8}},"dev":{"transport":"beta"}}]
              }]
            }
            """;
        HidProfileExport original = JsonSerializer.Deserialize<HidProfileExport>(json)!;
        HidDeviceProfile edited = original.Devices[0] with { Label = "My mouse" };
        HidProfileExport result = HidProfileCompatibility.UpdateExport(original, edited);
        JsonObject saved = JsonSerializer.SerializeToNode(result)!.AsObject();
        JsonObject expected = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("My mouse", saved["Devices"]![0]!["Label"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(expected["dev"], saved["dev"]));
        Assert.Equal(7, saved["FutureEnvelope"]!.GetValue<int>());
        foreach (string location in new[] { "dev", "FutureDevice", "Interfaces", "Battery", "Connections" })
        {
            JsonNode? before = expected["Devices"]![0]![location];
            JsonNode? after = saved["Devices"]![0]![location];
            if (location is "Interfaces" or "Battery" or "Connections")
            {
                string normalizedBefore = location switch
                {
                    "Interfaces" => JsonSerializer.Serialize(original.Devices[0].Interfaces),
                    "Battery" => JsonSerializer.Serialize(original.Devices[0].Battery),
                    _ => JsonSerializer.Serialize(original.Devices[0].Connections)
                };
                before = JsonNode.Parse(normalizedBefore);
            }
            Assert.True(JsonNode.DeepEquals(before, after), location);
        }
    }

    [Fact]
    public void RefreshingInterfacePathPreservesDeveloperMetadata()
    {
        var saved = new HidInterfaceProfile("old", 65, 65, 0, 0xFF00) { Dev = JsonNode.Parse("{\"experiment\":true}")!.AsObject() };
        var live = new HidInterfaceProfile("new", 65, 65, 0, 0xFF00);

        HidInterfaceProfile refreshed = Assert.Single(HidProfileCompatibility.RefreshInterfaces([saved], [live]));

        Assert.Equal("new", refreshed.DevicePath);
        Assert.True(JsonNode.DeepEquals(saved.Dev, refreshed.Dev));
    }

    [Fact]
    public void ReplacingProtocolKeepsDeveloperMetadata()
    {
        var previous = new HidBatteryProtocolProfile(PercentageOffset: 7) { Dev = JsonNode.Parse("{\"experimentalDecoder\":true}")!.AsObject() };
        var replacement = new HidBatteryProtocolProfile(PercentageOffset: 8);

        HidBatteryProtocolProfile result = HidProfileCompatibility.ReplaceProtocol(previous, replacement);

        Assert.Equal(8, result.PercentageOffset);
        Assert.True(JsonNode.DeepEquals(previous.Dev, result.Dev));
    }

    [Fact]
    public void NewerProfileFormatIsNotOverwritten()
    {
        var profile = new HidDeviceProfile("one", "One", 1, 2, null, null, [], new());
        var export = new HidProfileExport(HidProfileValidation.SupportedFormatVersion + 1, DateTimeOffset.Now, [profile]);

        Assert.Throws<InvalidDataException>(() => HidProfileCompatibility.UpdateExport(export, profile));
    }

    [Fact]
    public void EditingOneProfileDoesNotDropOtherDevicesInTheExport()
    {
        var first = new HidDeviceProfile("one", "One", 1, 2, null, null, [], new());
        HidDeviceProfile second = first with { Id = "two", Name = "Two" };
        var export = new HidProfileExport(2, DateTimeOffset.Now, [first, second]);

        HidProfileExport saved = HidProfileCompatibility.UpdateExport(export, first with { Label = "Renamed" });

        Assert.Equal(2, saved.Devices.Count);
        Assert.Equal("Renamed", saved.Devices[0].Label);
        Assert.Equal(second, saved.Devices[1]);
    }
}

using GeBar.Core;

namespace GeBar.Hid;

public static class HidProbe
{
    public static async Task<int> RunAsync(TextWriter output, IAppLogger logger, CancellationToken token)
    {
        IReadOnlyList<HidDeviceProfile> devices = HidDeviceScanner.Scan();
        await output.WriteLineAsync($"HID devices found: {devices.Count}");

        int detected = 0;
        foreach (HidDeviceProfile device in devices)
        {
            BatteryProbeResult? result;
            try
            {
                result = await UniversalBatteryProbe.ProbeAsync(device, TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.Error($"Probe failed for {device.Name}", ex);
                continue;
            }
            if (result?.Reading is null) continue;

            detected++;
            HidBatteryReading reading = result.Reading;
            string charging = reading.IsCharging is null ? "unknown" : reading.IsCharging.Value ? "yes" : "no";
            await output.WriteLineAsync($"{device.Name} [{device.VendorId:X4}:{device.ProductId:X4}] {reading.Percentage}% charging={charging} via {reading.Detail}");
            await output.WriteLineAsync($"  path: {result.DevicePath}");
        }

        await output.WriteLineAsync(detected == 0 ? "No device reported a battery level." : $"Devices with battery: {detected}");
        return detected == 0 ? 3 : 0;
    }
}

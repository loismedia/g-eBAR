using GeBar.Core;
using HidSharp;

namespace GeBar.Hid;

public static class HidBatteryReader
{
    public static async Task<HidBatteryReading?> TryReadAsync(HidDevice device, HidBatteryProtocolProfile profile, TimeSpan inputTimeout, CancellationToken token)
    {
        if (profile.Source != HidBatterySource.HidUsage || profile.Percentage is null) return null;
        HidStream? stream = null;
        try
        {
            stream = HidRequestExchange.TryOpenShared(device);
            if (stream is null) return null;

            long? percentageRaw = await ReadFieldAsync(stream, profile.Percentage, inputTimeout, token).ConfigureAwait(false);
            if (percentageRaw is null) return null;

            double percentage;
            if (profile.Percentage.Usage == HidBatteryUsages.RemainingCapacity)
            {
                if (profile.FullCapacity is null) return null;
                long? fullRaw = await ReadFieldAsync(stream, profile.FullCapacity, inputTimeout, token).ConfigureAwait(false);
                if (fullRaw is null or <= 0) return null;
                percentage = percentageRaw.Value * 100d / fullRaw.Value;
            }
            else
            {
                long min = profile.Percentage.LogicalMinimum;
                long max = profile.Percentage.LogicalMaximum;
                if (max <= min) return null;
                percentage = (percentageRaw.Value - min) * 100d / (max - min);
            }

            int rounded = (int)Math.Round(percentage, MidpointRounding.AwayFromZero);
            if (rounded is < 1 or > 100) return null;

            bool? charging = null;
            if (profile.Charging is not null)
            {
                long? value = await ReadFieldAsync(stream, profile.Charging, inputTimeout, token).ConfigureAwait(false);
                if (value is not null) charging = value != 0;
            }
            else if (profile.Discharging is not null)
            {
                long? value = await ReadFieldAsync(stream, profile.Discharging, inputTimeout, token).ConfigureAwait(false);
                if (value is not null) charging = value == 0;
            }

            return new HidBatteryReading(rounded, charging, HidBatterySource.HidUsage, HidBatteryUsages.Describe(profile.Percentage.Usage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or TimeoutException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public static async Task<HidReplay> ReplayAsync(HidDevice device, HidBatteryProtocolProfile profile, CancellationToken token, IAppLogger? trace = null)
    {
        if (profile.RequestKind == HidRequestKind.None || profile.RequestHex is null) return HidReplay.Silent;
        if (profile.PercentageOffset is not int offset) return HidReplay.Silent;

        BatteryCommand? command = KnownBatteryProtocols.Find(profile.ProtocolName);
        byte[] request = command is null
            ? Pad(Convert.FromHexString(profile.RequestHex), profile.RequestKind, profile.RequestReportLength)
            : KnownBatteryProtocols.Request(command, profile.RequestKind, profile.RequestReportLength);
        using HidStream? stream = HidRequestExchange.TryOpenShared(device);
        if (stream is null) return HidReplay.Silent;
        trace?.Info($"TX [{request.Length}] {Convert.ToHexString(request.AsSpan(0, Math.Min(request.Length, 20)))}");

        byte[]? refusal = null;
        byte[]? response = profile.RequestKind switch
        {
            HidRequestKind.Feature => await HidRequestExchange.SendThenReadFeatureAsync(stream, request, profile.RequestReportLength, profile.RequestRetries, profile.ResponseDelayMs, token).ConfigureAwait(false),
            HidRequestKind.Output => await HidRequestExchange.WriteThenReadInputAsync(stream, request, HidDeviceScanner.InputLengthOf(device), profile.MatchPrefixHex, TimeSpan.FromSeconds(3), token, packet =>
            {
                if (command?.Refusal?.Invoke(packet) == true) refusal ??= packet;
                trace?.Info($"RX [{packet.Length}] {Convert.ToHexString(packet.AsSpan(0, Math.Min(packet.Length, 20)))} ignored");
            }, packet => ParseReply(packet, profile).Answered).ConfigureAwait(false),
            _ => null
        };

        if (response is null) return refusal is null ? HidReplay.Silent : HidReplay.Refused;
        trace?.Info($"RX [{response.Length}] {Convert.ToHexString(response.AsSpan(0, Math.Min(response.Length, 20)))}");
        return ParseReply(response, profile);
    }

    internal static HidReplay ParseReply(byte[] response, HidBatteryProtocolProfile profile)
    {
        if (profile.PercentageOffset is not int offset) return HidReplay.Silent;
        BatteryCommand? command = KnownBatteryProtocols.Find(profile.ProtocolName);
        if (command is not null && KnownBatteryProtocols.IsSleepResponse(response, command))
            return HidReplay.Refused with { LastKnown = KnownBatteryProtocols.Accepts(response, command) ? response[command.PercentageOffset] : null };
        if (command is not null && !KnownBatteryProtocols.Accepts(response, command)) return HidReplay.Silent;
        if (response.Length <= offset) return HidReplay.Silent;
        int percentage = response[offset];
        if (percentage is < 1 or > 100) return HidReplay.Silent;

        int? flagOffset = profile.ChargingOffset ?? command?.ChargingOffset;
        int? flagValue = profile.ChargingOffset is null ? command?.ChargingValue : profile.ChargingValue;
        bool? charging = flagOffset is int chargingOffset && response.Length > chargingOffset
            ? flagValue is int expected ? response[chargingOffset] == expected : response[chargingOffset] != 0
            : null;

        return new HidReplay(new HidBatteryReading(percentage, charging, profile.Source, profile.ProtocolName ?? "saved profile"), true);
    }

    private static byte[] Pad(byte[] request, HidRequestKind kind, int length)
    {
        if (kind != HidRequestKind.Output || length <= 0 || request.Length >= length) return request;
        var padded = new byte[length];
        request.CopyTo(padded, 0);
        return padded;
    }

    private static async Task<long?> ReadFieldAsync(HidStream stream, HidBatteryFieldSpec spec, TimeSpan timeout, CancellationToken token)
    {
        byte[]? report = spec.ReportKind == HidReportKind.Feature
            ? ReadFeature(stream, spec)
            : await ReadInputAsync(stream, spec, timeout, token).ConfigureAwait(false);
        return report is not null && TryReadRaw(report, spec, out long value) ? value : null;
    }

    private static byte[]? ReadFeature(HidStream stream, HidBatteryFieldSpec spec)
    {
        var buffer = new byte[Math.Max(spec.ReportLength, 1)];
        buffer[0] = spec.ReportId;
        stream.GetFeature(buffer);
        return buffer;
    }

    private static async Task<byte[]?> ReadInputAsync(HidStream stream, HidBatteryFieldSpec spec, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var buffer = new byte[Math.Max(stream.Device.GetMaxInputReportLength(), spec.ReportLength)];
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count <= 0) continue;
                if (spec.ReportId != 0 && buffer[0] != spec.ReportId) continue;
                if (count * 8 < spec.BitOffset + spec.ElementBits) continue;
                return buffer[..count];
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return null;
    }

    private static bool TryReadRaw(byte[] report, HidBatteryFieldSpec spec, out long value)
    {
        value = 0;
        int start = spec.BitOffset + (spec.ElementIndex * spec.ElementBits);
        if (spec.ElementBits is <= 0 or > 32) return false;
        if (start < 0 || start + spec.ElementBits > report.Length * 8) return false;

        ulong raw = 0;
        for (int index = 0; index < spec.ElementBits; index++)
        {
            int bit = start + index;
            int set = (report[bit >> 3] >> (bit & 7)) & 1;
            raw |= (ulong)set << index;
        }

        if (spec.LogicalMinimum < 0 && spec.ElementBits < 64)
        {
            ulong sign = 1UL << (spec.ElementBits - 1);
            if ((raw & sign) != 0) { value = (long)raw - (long)(sign << 1); return true; }
        }
        value = (long)raw;
        return true;
    }
}

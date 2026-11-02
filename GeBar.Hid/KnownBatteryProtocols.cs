using GeBar.Core;
using HidSharp;

namespace GeBar.Hid;

public sealed record BatteryCommand(
    string Name,
    HidRequestKind Kind,
    byte[] Request,
    int PercentageOffset,
    int? ChargingOffset,
    int? ChargingValue,
    string? EchoHex,
    int EchoOffset,
    Func<HidInterfaceProfile, bool> Shape,
    int[] PreferredVendors,
    Func<byte[], bool>? Verify = null,
    int[]? OnlyVendors = null,
    int? AsleepOffset = null,
    Func<byte[], bool>? Refusal = null);

public static class KnownBatteryProtocols
{
    private const int WlMouse = 0x36A7;
    private const int Sinowealth = 0x3151;
    private const int Akko = 0x25AA;
    private const int Compx = 0x3554;
    private const int Waizowl = 0x1915;
    private const int Logitech = 0x046D;
    private const int LogitechReportLength = 64;

    public static IReadOnlyList<BatteryCommand> All { get; } =
    [
        new BatteryCommand(
            "FEA status 0xF7 (RY5088 dongle)",
            HidRequestKind.Feature,
            [0x00, 0xF7, 0x00],
            PercentageOffset: 2, ChargingOffset: null, ChargingValue: null,
            EchoHex: null, EchoOffset: 0,
            Shape: item => item.FeatureReportLength >= 12 && item.InputReportLength == 0 && item.OutputReportLength == 0,
            PreferredVendors: [Sinowealth, Akko],
            Verify: r => r.Length > 4 && r[0] == 0 && r[3] == 0 && r[4] <= 1 && r[2] is > 0 and <= 100,
            AsleepOffset: 4),

        new BatteryCommand(
            "Compx report 8 cmd 0x04 (VGN)",
            HidRequestKind.Output,
            BuildCompx(0x04),
            PercentageOffset: 6, ChargingOffset: 7, ChargingValue: 1,
            EchoHex: "04", EchoOffset: 0,
            Shape: item => item.OutputReportLength == 17 && item.InputReportLength == 17,
            PreferredVendors: [Compx],
            Verify: r => r.Length > 9 && r[0] == 0x08 && r[6] <= 100),

        new BatteryCommand(
            "Feature 0x83 (WLMOUSE family)",
            HidRequestKind.Feature,
            [0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83],
            PercentageOffset: 8, ChargingOffset: 7, ChargingValue: null,
            EchoHex: "02020083", EchoOffset: 3,
            Shape: item => item.FeatureReportLength >= 9,
            PreferredVendors: [WlMouse, Sinowealth]),

        new BatteryCommand(
            "Feature 0x83 short (WLMOUSE family)",
            HidRequestKind.Feature,
            [0x00, 0x02, 0x02, 0x00, 0x83],
            PercentageOffset: 6, ChargingOffset: 5, ChargingValue: null,
            EchoHex: "02020083", EchoOffset: 1,
            Shape: item => item.FeatureReportLength >= 7,
            PreferredVendors: [Sinowealth]),

        new BatteryCommand(
            "Interrupt 0x04/0x1A (Beast X family)",
            HidRequestKind.Output,
            BuildPadded(64, (0, 0x04), (3, 0x1A)),
            PercentageOffset: 8, ChargingOffset: null, ChargingValue: null,
            EchoHex: "04", EchoOffset: 0,
            Shape: item => item.OutputReportLength >= 64 && item.InputReportLength >= 9,
            PreferredVendors: [WlMouse]),

        new BatteryCommand(
            "Logitech Gaming 0x51",
            HidRequestKind.Output,
            BuildPadded(LogitechReportLength, (0, 0x51), (1, 0x08), (3, 0x03), (4, 0x1A), (6, 0x03), (8, 0x04), (9, 0x0A)),
            PercentageOffset: 10, ChargingOffset: 12, ChargingValue: 0x02,
            EchoHex: "0B000310", EchoOffset: 1,
            Shape: item => item.OutputReportLength >= LogitechReportLength && item.InputReportLength >= 13,
            PreferredVendors: [Logitech],
            Refusal: r => r.Length > 5 && r[0] == 0x51 && r[3] == 0xFF && r[4] == 0x03 && r[5] == 0x1A),

        new BatteryCommand(
            "Waizowl status 0x41 (OGM family)",
            HidRequestKind.Output,
            BuildWaizowl(0x41),
            PercentageOffset: 7, ChargingOffset: 6, ChargingValue: 1,
            EchoHex: "41", EchoOffset: 0,
            Shape: item => item.OutputReportLength == 65 && item.InputReportLength == 65,
            PreferredVendors: [Waizowl],
            Verify: r => r.Length > 10 && r[1] == 0x41 && r[7] is > 0 and <= 100,
            OnlyVendors: [Waizowl]),

        new BatteryCommand(
            "Waizowl status 0x01 (OGM wired)",
            HidRequestKind.Output,
            BuildWaizowl(0x01),
            PercentageOffset: 7, ChargingOffset: 6, ChargingValue: 1,
            EchoHex: "01", EchoOffset: 0,
            Shape: item => item.OutputReportLength == 65 && item.InputReportLength == 65,
            PreferredVendors: [Waizowl],
            Verify: r => r.Length > 10 && r[1] == 0x01 && r[3] == 0x8C && r[4] == 0x01 && r[5] == 0x00 && r[7] is > 0 and <= 100,
            OnlyVendors: [Waizowl]),

        new BatteryCommand(
            "HID++ 2.0 battery (feature 0x1000)",
            HidRequestKind.Output,
            [0x10, 0xFF, 0x00, 0x0D, 0x10, 0x00, 0x00],
            PercentageOffset: 4, ChargingOffset: null, ChargingValue: null,
            EchoHex: "FF", EchoOffset: 0,
            Shape: item => item.OutputReportLength is 7 or 8 && item.InputReportLength is 7 or 8,
            PreferredVendors: [Logitech])
    ];

    public static async Task<(HidBatteryProtocolProfile Profile, HidBatteryReading Reading, byte[] Response)?> TryReadAsync(
        int vendorId,
        IReadOnlyList<HidProbeTarget> targets,
        CancellationToken token,
        IAppLogger? trace = null,
        Action<HidBatteryProtocolProfile, byte[]>? refused = null,
        Action<byte[]>? foreign = null)
    {
        foreach (BatteryCommand command in Ranked(vendorId).Where(command => command.OnlyVendors is null || command.OnlyVendors.Contains(vendorId)))
        {
            foreach (HidProbeTarget target in targets.Where(item => IsProbeSafe(item) && command.Shape(item.Interface)))
            {
                HidDevice handle = target.Handle;
                HidInterfaceProfile candidate = target.Interface;

                var notes = new ProbeNotes();
                Sample? sample = await ProbeAsync(handle, command, candidate, notes, token).ConfigureAwait(false);
                trace?.Info($"  probe '{command.Name}' on IN={candidate.InputReportLength}/OUT={candidate.OutputReportLength}/F={candidate.FeatureReportLength}: "
                    + (sample is not null ? $"{sample.Value.Percentage}% {Head(sample.Value.Response)}" : notes.Describe()));
                if (sample is null)
                {
                    if (notes.Refused is byte[] answer) refused?.Invoke(Spoken(command, candidate), answer);
                    else if (notes.Foreign is byte[] other) foreign?.Invoke(other);
                    continue;
                }

                return (Spoken(command, candidate), new HidBatteryReading(sample.Value.Percentage, sample.Value.Charging, HidBatterySource.VendorProtocol, command.Name), sample.Value.Response);
            }
        }
        return null;
    }

    private static HidBatteryProtocolProfile Spoken(BatteryCommand command, HidInterfaceProfile candidate) =>
        new(RequestHex: Convert.ToHexString(command.Request),
            PercentageOffset: command.PercentageOffset,
            ChargingOffset: command.ChargingOffset,
            ChargingValue: command.ChargingValue,
            Source: HidBatterySource.VendorProtocol,
            DevicePath: candidate.DevicePath,
            MatchPrefixHex: command.Kind == HidRequestKind.Output ? command.EchoHex : null,
            RequestKind: command.Kind,
            RequestReportLength: command.Kind == HidRequestKind.Feature ? candidate.FeatureReportLength : candidate.OutputReportLength,
            ProtocolName: command.Name);

    private static string Head(byte[] response) => Convert.ToHexString(response.AsSpan(0, Math.Min(response.Length, 16)));

    private readonly record struct Sample(int Percentage, bool? Charging, byte[] Response);

    private sealed class ProbeNotes
    {
        public byte[]? Foreign { get; set; }
        public byte[]? Refused { get; set; }

        public string Describe() =>
            Refused is not null ? $"answer refused {Head(Refused)}"
            : Foreign is not null ? $"no answer, other traffic {Head(Foreign)}"
            : "no answer";
    }

    private static async Task<Sample?> ProbeAsync(HidDevice handle, BatteryCommand command, HidInterfaceProfile candidate, ProbeNotes notes, CancellationToken token)
    {
        Sample? first = await ExchangeAsync(handle, command, candidate, notes, token).ConfigureAwait(false);
        if (first is null) return null;
        await Task.Delay(250, token).ConfigureAwait(false);
        Sample? second = await ExchangeAsync(handle, command, candidate, notes, token).ConfigureAwait(false);
        return second?.Percentage == first.Value.Percentage ? second : null;
    }

    private static async Task<Sample?> ExchangeAsync(HidDevice handle, BatteryCommand command, HidInterfaceProfile candidate, ProbeNotes notes, CancellationToken token)
    {
        using HidStream? stream = HidRequestExchange.TryOpenShared(handle);
        if (stream is null) return null;

        byte[]? response = command.Kind switch
        {
            HidRequestKind.Feature => await HidRequestExchange.SendThenReadFeatureAsync(stream, command.Request, candidate.FeatureReportLength, 4, 100, token).ConfigureAwait(false),
            HidRequestKind.Output => await HidRequestExchange.WriteThenReadInputAsync(stream, Pad(command.Request, candidate.OutputReportLength), candidate.InputReportLength, command.EchoHex, TimeSpan.FromSeconds(2), token, packet =>
            {
                if (command.Refusal?.Invoke(packet) == true) notes.Refused ??= packet;
                else notes.Foreign ??= packet;
            }, packet => Accepts(packet, command) || IsSleepResponse(packet, command)).ConfigureAwait(false),
            _ => null
        };

        if (response is null) return null;
        if (IsSleepResponse(response, command))
        {
            notes.Refused ??= response;
            return null;
        }
        if (!Accepts(response, command))
        {
            notes.Foreign ??= response;
            return null;
        }
        int percentage = response[command.PercentageOffset];

        bool? charging = command.ChargingOffset is int offset && response.Length > offset
            ? command.ChargingValue is int expected ? response[offset] == expected : response[offset] != 0
            : null;
        return new Sample(percentage, charging, response);
    }

    internal static bool IsAsleep(byte[] response, BatteryCommand command) =>
        command.AsleepOffset is int offset && response.Length > offset && response[offset] != 0;

    internal static bool IsSleepResponse(byte[] response, BatteryCommand command) =>
        command.Refusal?.Invoke(response) == true || Accepts(response, command) && IsAsleep(response, command);

    public static HidDeviceProfile Normalize(HidDeviceProfile device) => device with
    {
        Battery = Normalize(device.Battery),
        Connections = device.Connections?.Select(connection => connection with { Battery = Normalize(connection.Battery) }).ToArray()
    };

    private static HidBatteryProtocolProfile Normalize(HidBatteryProtocolProfile battery) => Find(battery.ProtocolName) switch
    {
        { AsleepOffset: int asleep } when battery.ChargingOffset == asleep => battery with { ChargingOffset = null, ChargingValue = null },
        { ChargingOffset: int offset } command when battery.ChargingOffset is null => battery with { ChargingOffset = offset, ChargingValue = command.ChargingValue },
        _ => battery
    };

    internal static BatteryCommand? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : All.FirstOrDefault(command => command.Name.Equals(name, StringComparison.Ordinal));

    internal static bool Accepts(byte[] response, BatteryCommand command) =>
        Validate(response, command)
        && response.Length > command.PercentageOffset
        && response[command.PercentageOffset] is >= 1 and <= 100;

    internal static byte[] Request(BatteryCommand command, HidRequestKind kind, int reportLength) =>
        kind == HidRequestKind.Output ? Pad(command.Request, reportLength) : command.Request;

    private static bool Validate(byte[] response, BatteryCommand command)
    {
        if (response.Length <= command.PercentageOffset) return false;
        if (response.All(value => value == 0)) return false;
        if (command.Verify is not null && !command.Verify(response)) return false;
        if (command.EchoHex is null || command.Kind != HidRequestKind.Feature) return true;
        int bytes = command.EchoHex.Length / 2;
        if (response.Length < command.EchoOffset + bytes) return false;
        return Convert.ToHexString(response.AsSpan(command.EchoOffset, bytes)).Equals(command.EchoHex, StringComparison.OrdinalIgnoreCase);
    }

    internal const int VendorUsagePage = 0xFF00;

    internal static bool IsProbeSafe(HidProbeTarget target) =>
        target.Interface.UsagePage is int page ? page >= VendorUsagePage : HidDeviceScanner.TopUsagePage(target.Handle) >= VendorUsagePage;

    private static IEnumerable<BatteryCommand> Ranked(int vendorId) =>
        All.OrderByDescending(command => command.PreferredVendors.Contains(vendorId));

    private static byte[] BuildCompx(byte command)
    {
        var payload = new byte[16];
        payload[0] = command;
        int sum = 0;
        for (int index = 0; index < payload.Length - 1; index++) sum += payload[index];
        payload[15] = (byte)((85 - (sum & 0xFF)) - 8);
        var report = new byte[17];
        report[0] = 8;
        payload.CopyTo(report, 1);
        return report;
    }

    internal static byte[] BuildWaizowl(byte command)
    {
        var report = new byte[65];
        report[1] = command;
        int sum = 0;
        for (int index = 0; index < report.Length - 1; index++) sum += report[index];
        report[64] = (byte)((0xA1 - sum) & 0xFF);
        return report;
    }

    private static byte[] BuildPadded(int length, params (int Index, byte Value)[] bytes)
    {
        var buffer = new byte[length];
        foreach ((int index, byte value) in bytes) buffer[index] = value;
        return buffer;
    }

    private static byte[] Pad(byte[] request, int length)
    {
        if (length <= 0 || request.Length >= length) return request;
        var padded = new byte[length];
        request.CopyTo(padded, 0);
        return padded;
    }
}

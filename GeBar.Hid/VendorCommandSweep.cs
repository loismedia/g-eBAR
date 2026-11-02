using GeBar.Core;
using HidSharp;

namespace GeBar.Hid;

public sealed record SweepAnswer(int VendorId, int ProductId, string DevicePath, byte Command, byte[] Response);

public sealed class SweepStopped(IReadOnlyList<SweepAnswer> answers) : OperationCanceledException
{
    public IReadOnlyList<SweepAnswer> Answers { get; } = answers;
}

public static class VendorCommandSweep
{
    private const int FrameLength = 65;
    private const int ProgressStep = 0x20;
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(250);

    public static async Task<IReadOnlyList<SweepAnswer>> RunAsync(HidDeviceProfile device, IProgress<string>? progress, CancellationToken token)
    {
        var answers = new List<SweepAnswer>();
        try
        {
            foreach (HidDeviceConnectionProfile connection in device.AllConnections())
            {
                HidDevice[] handles = [.. DeviceList.Local.GetHidDevices(connection.VendorId, connection.ProductId)];
                if (handles.Length == 0) continue;

                foreach (HidProbeTarget target in HidDeviceScanner.Pair(connection.Interfaces, handles).Where(Sweepable))
                {
                    progress?.Report($"{connection.VendorId:X4}:{connection.ProductId:X4} {target.Interface.DevicePath}");
                    await SweepTargetAsync(connection, target, answers, progress, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new SweepStopped(answers);
        }
        return answers;
    }

    private static bool Sweepable(HidProbeTarget target) =>
        KnownBatteryProtocols.IsProbeSafe(target)
        && target.Interface.OutputReportLength == FrameLength
        && target.Interface.InputReportLength == FrameLength;

    private static async Task SweepTargetAsync(HidDeviceConnectionProfile connection, HidProbeTarget target,
        List<SweepAnswer> answers, IProgress<string>? progress, CancellationToken token)
    {
        using HidStream? stream = HidRequestExchange.TryOpenShared(target.Handle);
        if (stream is null)
        {
            progress?.Report("interface busy");
            return;
        }
        var spontaneous = new HashSet<byte>();
        for (int sample = 0; sample < 4; sample++)
        {
            byte[]? ambient = await HidRequestExchange.ReadInputAsync(stream, FrameLength, Wait, token).ConfigureAwait(false);
            if (ambient is null) continue;
            progress?.Report($"ambient {Head(ambient)}");
            if (ambient.Length > 1) spontaneous.Add(ambient[1]);
        }

        for (int command = 0; command <= byte.MaxValue; command++)
        {
            token.ThrowIfCancellationRequested();
            if (command > 0 && command % ProgressStep == 0) progress?.Report($"reached 0x{command:X2}, answers {answers.Count}");
            byte[]? response = await HidRequestExchange.WriteThenReadInputAsync(
                stream, KnownBatteryProtocols.BuildWaizowl((byte)command), FrameLength, null, Wait, token).ConfigureAwait(false);
            if (response is null || response.All(value => value == 0)) continue;

            bool echoed = response.Length > 1 && response[1] == command;
            bool spontaneousEcho = echoed && spontaneous.Contains((byte)command);
            progress?.Report($"0x{command:X2} {(spontaneousEcho ? "own traffic" : echoed ? "answer" : "traffic")} {Head(response)}");
            if (echoed && !spontaneousEcho) answers.Add(new SweepAnswer(connection.VendorId, connection.ProductId, target.Interface.DevicePath, (byte)command, response));
        }
    }

    private static string Head(byte[] response) => Convert.ToHexString(response.AsSpan(0, Math.Min(response.Length, 20)));
}

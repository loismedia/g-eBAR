using HidSharp;

namespace GeBar.Hid;

public static class HidRequestExchange
{
    public static HidStream? TryOpenShared(HidDevice device)
    {
        try
        {
            var configuration = new OpenConfiguration();
            configuration.SetOption(OpenOption.Exclusive, false);
            return device.Open(configuration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task<byte[]?> SendThenReadFeatureAsync(HidStream stream, byte[] request, int reportLength, int retries, int delayMs, CancellationToken token)
    {
        if (reportLength <= 0) return null;
        for (int attempt = 0; attempt < Math.Max(1, retries); attempt++)
        {
            token.ThrowIfCancellationRequested();
            var buffer = new byte[reportLength];
            request.AsSpan(0, Math.Min(request.Length, reportLength)).CopyTo(buffer);
            try { stream.SetFeature(buffer); }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { continue; }

            if (delayMs > 0) await Task.Delay(delayMs, token).ConfigureAwait(false);

            var response = new byte[reportLength];
            try { stream.GetFeature(response); }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { continue; }
            return response;
        }
        return null;
    }

    public static async Task<byte[]?> WriteThenReadInputAsync(Stream stream, byte[] request, int inputLength, string? matchPrefixHex, TimeSpan timeout, CancellationToken token, Action<byte[]>? unmatched = null, Func<byte[], bool>? accept = null)
    {
        if (inputLength <= 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);

        Task<byte[]?> reader = ReadMatchingAsync(stream, inputLength, matchPrefixHex, deadline.Token, unmatched, accept);
        try { await stream.WriteAsync(request, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await reader.ConfigureAwait(false); } catch { }
            return null;
        }
        return await reader.ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadInputAsync(HidStream stream, int inputLength, TimeSpan timeout, CancellationToken token)
    {
        if (inputLength <= 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        return await ReadMatchingAsync(stream, inputLength, null, deadline.Token).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadMatchingAsync(Stream stream, int inputLength, string? matchPrefixHex, CancellationToken deadline, Action<byte[]>? unmatched = null, Func<byte[], bool>? accept = null)
    {
        var buffer = new byte[inputLength];
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                int count;
                try { count = await stream.ReadAsync(buffer, deadline).ConfigureAwait(false); }
                catch (TimeoutException) { continue; }
                if (count <= 0) continue;
                byte[] packet = buffer[..count];
                if ((matchPrefixHex is null || MatchesPrefix(packet, matchPrefixHex)) && (accept is null || accept(packet))) return packet;
                unmatched?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        return null;
    }

    public static bool MatchesPrefix(ReadOnlySpan<byte> packet, string prefixHex)
    {
        if (prefixHex.Length == 0) return true;
        int bytes = prefixHex.Length / 2;
        if (packet.Length < bytes + 1) return false;
        return Convert.ToHexString(packet.Slice(1, bytes)).Equals(prefixHex, StringComparison.OrdinalIgnoreCase);
    }
}

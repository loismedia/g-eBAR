using System.Reflection;

namespace GeBar.Core;

public sealed class FileLogger : IAppLogger
{
    private const long MaximumTotalBytes = 10 * 1024 * 1024;
    private const long MaximumFileBytes = 1024 * 1024;
    private const long EnforceEveryBytes = 256 * 1024;
    private readonly object _sync = new();
    private readonly string _name;
    private string? _path;
    private DateOnly _day;
    private long _size;
    private long _writtenSinceEnforce;
    public string LogDirectory { get; }
    public bool LogRawPackets { get; set; }
    public bool IsRawPacketLoggingEnabled => LogRawPackets;
    public FileLogger(string directory, string name)
    {
        LogDirectory = directory;
        _name = name;
        try
        {
            System.IO.Directory.CreateDirectory(LogDirectory);
            EnforceSizeLimit(null);
            Info($"{name} {EntryVersion()} started; {WindowsRelease.Describe()}");
        }
        catch (Exception) { }
    }
    private static string EntryVersion()
    {
        Assembly? assembly = Assembly.GetEntryAssembly();
        string? informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        return string.IsNullOrWhiteSpace(informational) ? assembly?.GetName().Version?.ToString() ?? "unknown" : informational;
    }

    public void Info(string message)
    {
        if (!LogRawPackets && (message.StartsWith("RX [", StringComparison.Ordinal) || message.StartsWith("TX [", StringComparison.Ordinal))) return;
        Write("INF", message);
    }
    public void Warning(string message) => Write("WRN", message);
    public void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}{Origin(exception)}");

    private static string Origin(Exception exception)
    {
        string[] all = (exception.StackTrace ?? string.Empty)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] ours = [.. all.Where(frame => frame.Contains("GeBar", StringComparison.Ordinal))];
        string[] frames = [.. (ours.Length > 0 ? ours : all).Take(3)];
        return frames.Length == 0 ? string.Empty : $" at {string.Join(" <- ", frames.Select(Trim))}";
    }

    private static string Trim(string frame)
    {
        int at = frame.StartsWith("at ", StringComparison.Ordinal) ? 3 : 0;
        int file = frame.IndexOf(" in ", StringComparison.Ordinal);
        string call = file < 0 ? frame[at..] : frame[at..file];
        int line = frame.LastIndexOf(":line ", StringComparison.Ordinal);
        return line < 0 ? call : $"{call}:{frame[(line + 6)..]}";
    }
    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is string line) yield return line;
    }

    private void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
            lock (_sync)
            {
                string path = CurrentLogPath();
                File.AppendAllText(path, line);
                long written = System.Text.Encoding.UTF8.GetByteCount(line);
                _size += written;
                _writtenSinceEnforce += written;
                if (_writtenSinceEnforce < EnforceEveryBytes) return;
                _writtenSinceEnforce = 0;
                EnforceSizeLimit(path);
            }
        }
        catch (Exception) { }
    }

    private string CurrentLogPath()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (_path is not null && _day == today && _size < MaximumFileBytes) return _path;

        string prefix = $"{_name}-{today:yyyy-MM-dd}";
        string candidate = Path.Combine(LogDirectory, $"{prefix}.log");
        for (int index = 1; File.Exists(candidate) && new FileInfo(candidate).Length >= MaximumFileBytes; index++)
            candidate = Path.Combine(LogDirectory, $"{prefix}-{index:00}.log");

        _path = candidate;
        _day = today;
        _size = File.Exists(candidate) ? new FileInfo(candidate).Length : 0;
        return candidate;
    }

    private void EnforceSizeLimit(string? activePath)
    {
        FileInfo[] files = new DirectoryInfo(LogDirectory).EnumerateFiles("*.log")
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files)
        {
            if (total <= MaximumTotalBytes) break;
            if (activePath is not null && file.FullName.Equals(activePath, StringComparison.OrdinalIgnoreCase)) continue;
            long length = file.Length;
            file.Delete();
            total -= length;
        }
    }
}

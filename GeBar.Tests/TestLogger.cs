using GeBar.Core;

namespace GeBar.Tests;

internal sealed class TestLogger : IAppLogger
{
    public string LogDirectory => Path.GetTempPath();
    public bool IsRawPacketLoggingEnabled => false;
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

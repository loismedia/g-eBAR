namespace GeBar.Core;

public interface IDeviceBatteryMonitor : IAsyncDisposable
{
    DeviceBatteryState CurrentState { get; }
    HidDiagnostics Diagnostics { get; }
    event EventHandler<DeviceBatteryState>? StateChanged;
    Task StartAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
}
public interface IAppLogger
{
    string LogDirectory { get; }
    bool IsRawPacketLoggingEnabled { get; }
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}
public enum TrayIconStyle { Battery, Percentage }
public enum UpdateAccess { Unknown, Granted, NoToken, TokenRejected, TokenWithoutRepository }

using GeBar.Hid;
using GeBar.UI;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace GeBar.App;

internal static partial class Program
{
    private const string MutexName = @"Local\g-eBAR";
    private static readonly string PipeName = $"g-eBAR.SingleInstance.{SessionId()}";

    [STAThread]
    private static int Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();

    private static async Task<int> MainAsync(string[] args)
    {
        var logger = new FileLogger(AppPaths.Logs, "g-eBAR");
        AppIdentity.Register(logger);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => logger.Error("Unhandled application exception", eventArgs.ExceptionObject as Exception);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (_, eventArgs) =>
        {
            logger.Error("Unhandled WPF dispatcher exception", eventArgs.Exception);
            eventArgs.Handled = true;
        };
        if (args.Length == 4 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            return await ApplyUpdateAsync(logger, args[1], args[2], args[3]);
        if (args.Length == 3 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            return await ApplyUpdateAsync(logger, Environment.GetCommandLineArgs()[0], args[1], args[2]);

        if (args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase)) return RunUiSmokeTest(logger);
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            _ = AttachConsole(-1) || AllocConsole();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await HidProbe.RunAsync(Console.Out, logger, cancellation.Token);
        }

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            await NotifyFirstInstanceAsync();
            return 1;
        }

        string? updateToDelete = args.Length == 2 && args[0].Equals("--cleanup-update", StringComparison.OrdinalIgnoreCase) ? args[1] : null;
        if (updateToDelete is not null) _ = DeleteUpdatePackageAsync(logger, updateToDelete);

        AppSettings settings = AppSettings.Load(logger);
        StartupManager.Synchronize(settings.StartWithWindows, logger);
        bool diagnostics = args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<DeviceEntry> devices = DeviceCatalog.Build(logger, TimeSpan.FromSeconds(settings.RefreshIntervalSeconds));
        if (devices.Count == 0)
        {
            logger.Error("No device profiles available; the built-in profile could not be loaded");
            AppDialog.Show(null, Strings.NoDevicesError, Strings.AppName);
            return 4;
        }
        var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var ui = new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher);
        SynchronizationContext.SetSynchronizationContext(ui);
        var context = new BatteryTrayApplicationContext(devices, logger, settings, diagnostics);
        using var pipeCancellation = new CancellationTokenSource();
        Task pipe = RunPipeServerAsync(() => ui.Post(_ => context.ShowMain(), null), pipeCancellation.Token);
        application.Run();
        pipeCancellation.Cancel();
        try { await pipe.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        return 0;
    }

    private static int RunUiSmokeTest(FileLogger logger)
    {
        try
        {
            var settings = new AppSettings();
            ShowAndClose(new ControlCenterWindow(settings, false, string.Empty, logger));
            ShowAndClose(new WpfColorPickerWindow(System.Windows.Media.Color.FromRgb(0, 166, 199)));
            ShowAndClose(new BugReportWindow(false));
            using var updates = new GitHubUpdateService(logger);
            var release = new GitHubRelease(new Version(99, 0, 0), "v99.0.0", "### Changelog\n\n#### Test\n\n- UI smoke test", new Uri("https://example.invalid/g-eBAR.exe"), true, "05.08-hid-A1B2C", new Uri("https://example.invalid/g-eHID.exe"), true, true, false);
            ShowAndClose(new UpdateWindow(release, updates, logger));
            var rollback = release with { Version = new Version(98, 0, 0), Tag = "v98.0.0", IsNightly = false, IsDowngrade = true };
            ShowAndClose(new UpdateWindow(rollback, updates, logger));
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("UI smoke test failed", ex);
            return 2;
        }
    }

    private static void ShowAndClose(System.Windows.Window window)
    {
        window.Show();
        window.Dispatcher.Invoke(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.Close();
    }

    private static async Task<int> ApplyUpdateAsync(FileLogger logger, string packagePath, string targetPath, string processIdValue)
    {
        try
        {
            if (!int.TryParse(processIdValue, out int processId) || processId <= 0) throw new ArgumentException("Invalid process ID.");
            string sourcePath = Path.GetFullPath(packagePath);
            string target = Path.GetFullPath(targetPath);
            if (!IsUpdatePackage(sourcePath)) throw new InvalidOperationException("Invalid update package path.");
            string targetFileName = Path.GetFileName(target);
            if (!targetFileName.StartsWith("g-eBAR", StringComparison.OrdinalIgnoreCase) || !targetFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid update target.");
            if (!File.Exists(target)) throw new FileNotFoundException("The installed application was not found.", target);
            logger.Info($"Update apply: package={Path.GetFileName(sourcePath)}, target={targetFileName}, pid={processId}");

            try
            {
                using Process previous = Process.GetProcessById(processId);
                await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (ArgumentException) { }

            await ReplaceAsync(sourcePath, target + ".update", target);
            logger.Info("Update apply: binary replaced, restarting");

            var startInfo = new ProcessStartInfo(target) { UseShellExecute = true };
            startInfo.ArgumentList.Add("--cleanup-update");
            startInfo.ArgumentList.Add(sourcePath);
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            logger.Error("Update apply failed", ex);
            AppDialog.Show(null, $"{Strings.UpdateInstallError}{Environment.NewLine}{ex.Message}", Strings.AppName);
            return 1;
        }
    }

    private static async Task ReplaceAsync(string source, string staged, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, staged, true);
                File.Move(staged, target, true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
        }
    }

    private static async Task DeleteUpdatePackageAsync(FileLogger logger, string path)
    {
        if (!IsUpdatePackage(path)) return;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await Task.Delay(500);
                File.Delete(Path.GetFullPath(path));
                DeleteEmptyUpdateDirectories();
                logger.Info($"Update package removed: {Path.GetFileName(path)}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        logger.Warning($"Update package not removed after 10 attempts: {Path.GetFileName(path)}");
    }

    private static void DeleteEmptyUpdateDirectories()
    {
        try
        {
            Directory.Delete(AppPaths.Updates, false);
            string? parent = Directory.GetParent(AppPaths.Updates)?.FullName;
            if (parent is not null) Directory.Delete(parent, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsUpdatePackage(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string updatesDirectory = Path.GetFullPath(AppPaths.Updates).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fileName = Path.GetFileName(fullPath);
        return fullPath.StartsWith(updatesDirectory, StringComparison.OrdinalIgnoreCase)
            && fileName.StartsWith("g-eBAR-", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task NotifyFirstInstanceAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("open"));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
    }

    private static async Task RunPipeServerAsync(Action open, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    using CancellationTokenRegistration registration = token.Register(pipe.Dispose);
                    await pipe.WaitForConnectionAsync(token);
                    var buffer = new byte[16];
                    int count = await pipe.ReadAsync(buffer, token);
                    if (Encoding.UTF8.GetString(buffer, 0, count).Equals("open", StringComparison.Ordinal)) open();
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    private static int SessionId()
    {
        using Process current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool AllocConsole();
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool AttachConsole(int processId);
}

using GeBar.Core;
using System.IO;

namespace GeBar.HidApp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose
        };
        if (!GeBarRequiredWindow.IsGeBarInstalled && !args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            new GeBarRequiredWindow().ShowDialog();
            return 0;
        }
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "g-eBAR");
        var logger = new FileLogger(Path.Combine(root, "Logs", "gehid"), "g-eHID");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => logger.Error("Unhandled application exception", eventArgs.ExceptionObject as Exception);
        application.DispatcherUnhandledException += (_, eventArgs) =>
        {
            logger.Error("Unhandled WPF dispatcher exception", eventArgs.Exception);
            eventArgs.Handled = true;
        };
        var window = new MainWindow(logger);
        if (args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
            window.UpdateLayout();
            window.Close();
            return 0;
        }
        application.Run(window);
        logger.Info("g-eHID closed");
        return 0;
    }
}

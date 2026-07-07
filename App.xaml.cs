using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PWRUHelper.Services;

namespace PWRUHelper;

/// <summary>Interaction logic for App.xaml</summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second copy (e.g. portable + installed) would fight over the
        // clipboard, global hotkeys and settings.json. Tell the user and bow out.
        _instanceMutex = new Mutex(initiallyOwned: true, "PWRUHelper.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("PWRU Helper is already running (check the taskbar or Ctrl+Alt+P).",
                "PWRU Helper", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Never die on an unhandled UI exception with a raw Windows error dialog — show a
        // friendly message and keep running where we safely can.
        DispatcherUnhandledException += OnUnhandledException;

        // Session marker in the log — makes it easy to see where one run ends and the next
        // begins when reading a copied error report.
        Logging.Info($"--- PWRU Helper v{UpdateService.CurrentVersion.ToString(3)} starting ---");

        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\n" +
                        "The app will try to keep running. If it misbehaves, restart it.",
                        "PWRU Helper", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

using System.Threading;
using System.Windows;
using System.Windows.Threading;

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

        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
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

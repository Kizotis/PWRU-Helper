using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Every WPF test runs on ONE shared STA thread, owned here. Two reasons it must be shared:
/// a second <see cref="Application"/> instance throws, and a control built on one thread cannot
/// be touched from another. Tests that use it are all in the <c>WPF</c> collection, so xUnit
/// never runs them in parallel (they also share the static <see cref="SettingsService.PathOverride"/>).
/// </summary>
internal static class StaTestHost
{
    private static Dispatcher? _dispatcher;
    private static readonly object Gate = new();

    public static void Run(Action action)
    {
        Exception? captured = null;
        Get().Invoke(() => { try { action(); } catch (Exception ex) { captured = ex; } });
        if (captured != null)
            throw new Xunit.Sdk.XunitException("WPF test failed on the STA thread:\n" + captured);
    }

    private static Dispatcher Get()
    {
        lock (Gate)
        {
            if (_dispatcher != null) return _dispatcher;

            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                // Loading App.xaml merges Theme.xaml into Application.Resources, which every
                // StaticResource / FindResource lookup in the windows and templates resolves against.
                var app = new App();
                app.InitializeComponent();
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };   // no explicit shutdown: it dies with the test host
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return _dispatcher!;
        }
    }
}

/// <summary>Marker so xUnit serialises every WPF test (one STA thread, one Application,
/// one static settings-path override).</summary>
[CollectionDefinition("WPF")]
public class WpfCollection { }

/// <summary>
/// Points <see cref="SettingsService"/> at a throwaway file for the duration of a test.
/// Constructing a <c>MainWindow</c> both LOADS and SAVES settings — without this, running the
/// test suite rewrites the developer's own %AppData%\PWRUHelper\settings.json (which is how the
/// "background filter resets to Off" bug was first reproduced).
/// </summary>
internal sealed class TempSettings : IDisposable
{
    private readonly DirectoryInfo _dir;
    public string Path { get; }

    public TempSettings(string json)
    {
        _dir = Directory.CreateTempSubdirectory("pwru-settings-");
        Path = System.IO.Path.Combine(_dir.FullName, "settings.json");
        File.WriteAllText(Path, json);
        SettingsService.PathOverride = Path;
    }

    public string Read() => File.ReadAllText(Path);

    public void Dispose()
    {
        SettingsService.PathOverride = null;
        try { _dir.Delete(recursive: true); } catch { /* the test already made its point */ }
    }
}

using System.Windows;
using System.Windows.Input;
using PWRUHelper.Services;

namespace PWRUHelper;

public partial class MainWindow
{
    // ============================================================
    //  FIRST-RUN TUTORIAL (the guided tour over the real window)
    // ============================================================

    /// <summary>The first launch — and, once, the first launch after updating to the version that
    /// ships the tour: <see cref="AppSettings.TutorialSeen"/> is a new property, so a saved
    /// settings.json without it reads false. Runs at the very end of OnWindowLoaded, i.e. after the
    /// update check, so it never covers the update dialog or its UAC prompt.</summary>
    private async Task MaybeStartFirstRunTutorialAsync()
    {
        if (_settings.TutorialSeen) return;
        await Task.Delay(_dataRefreshNotes.Count > 0 ? 1500 : 600);
        // Not over an app that is quitting for an install, not over compact mode or a live session
        // the player restored: leave it unseen and try again next launch.
        if (_closing || Dispatcher.HasShutdownStarted || !IsVisible || IsLive || _overlay is { IsVisible: true }) return;
        if (WindowState == WindowState.Minimized || !IsActive)
        {
            // The player is already in the game: an always-on-top dim they cannot answer (the keys go
            // to the game) would only be in the way. Start it when they come back to the window.
            EventHandler? onActivated = null;
            onActivated = (_, _) =>
            {
                Activated -= onActivated;
                if (!_settings.TutorialSeen && !IsLive && _overlay is not { IsVisible: true }) StartTutorial();
            };
            Activated += onActivated;
            return;
        }
        StartTutorial();
    }

    private void Tutorial_Click(object sender, RoutedEventArgs e) => StartTutorial();

    /// <summary>The tab the player was on when the tour started — what is saved as LastTab if the
    /// app closes mid-tour, rather than whichever tab the tour happened to be showing.</summary>
    private int _tutorialReturnTab;

    internal void StartTutorial()
    {
        if (TutorialLayer.IsActive) return;
        if (_overlay is { IsVisible: true }) ExitCompactMode();

        int returnTab = _tutorialReturnTab = MainTabs.SelectedIndex;
        // "Missing" in the idle sense: an install already running is not a reason to explain it.
        bool packMissing = !IsOcrReady() && InstallOcrButton.IsEnabled;
        TutorialLayer.Start(
            TutorialScript.Steps(packMissing),
            name => FindName(name) as FrameworkElement,
            tab => MainTabs.SelectedIndex = tab,
            finished =>
            {
                // Done, Skip, Esc or a hotkey all count as seen; closing the app mid-tour does not.
                _settings.TutorialSeen = true;
                SettingsService.Save(_settings);
                if (!finished) MainTabs.SelectedIndex = returnTab;   // Done stays on About, by the replay button
            });
    }

    /// <summary>A global hotkey during the tour: the player acted on purpose, so the tour ends (and
    /// counts as seen) before the hotkey does its job.</summary>
    private void CloseTutorialForHotkey() => TutorialLayer.Close(finished: false);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TutorialLayer.IsActive) TutorialLayer.HandleKey(e);
    }
}

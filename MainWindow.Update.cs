using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PWRUHelper.Models;
using PWRUHelper.Services;

namespace PWRUHelper;

public partial class MainWindow
{
    /// <summary>Show the real build version in the About tab (never hard-coded).</summary>
    private void ShowAppVersion()
        => VersionText.Text = $"v{UpdateService.CurrentVersion.ToString(3)}";

    // ============================================================
    //  UPDATE CHECK (GitHub Releases)
    // ============================================================
    private async Task CheckForUpdatesAsync()
    {
        var info = await _updates.CheckForUpdateAsync();
        if (info == null) return; // up to date, or the check couldn't run — stay quiet

        var choice = MessageBox.Show(this,
            "A new version of PWRU Helper is available!\n\n" +
            $"You have: {UpdateService.CurrentVersion.ToString(3)}\n" +
            $"Latest: {info.LatestVersion.ToString(3)}\n\n" +
            "Open the download page now?",
            "Update available",
            MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (choice != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
        }
        catch
        {
            // Browser association broken, etc. — try the canonical page, then give up gracefully.
            try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true }); }
            catch { ShowToast("Couldn't open the browser — see github.com/Kizotis/PWRU-Helper/releases"); }
        }
    }
}

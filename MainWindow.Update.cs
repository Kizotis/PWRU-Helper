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
    //  UPDATE CHECK + ONE-CLICK INSTALL (GitHub Releases)
    // ============================================================

    private bool _updateBusy;   // a download/install is already running

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    /// <summary>Check GitHub for a newer release. On the automatic startup check we stay quiet when
    /// up to date; a manual check reports the result. If an update exists, offer to download and
    /// install it in one click.</summary>
    private async Task CheckForUpdatesAsync(bool manual = false)
    {
        // Take the busy flag for the whole check, not just the download: the startup auto-check and
        // a manual button click could otherwise both pass the guard and run two dialogs + two
        // concurrent downloads to the same temp path (sharing-violation reported as a failure).
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (manual) SetUpdateStatus("Checking for updates…");

            var info = await _updates.CheckForUpdateAsync();
            if (info == null)
            {
                if (manual) SetUpdateStatus($"You're on the latest version (v{UpdateService.CurrentVersion.ToString(3)}). 🙂");
                return;
            }

            var choice = MessageBox.Show(this,
                "A new version of PWRU Helper is available!\n\n" +
                $"You have: v{UpdateService.CurrentVersion.ToString(3)}\n" +
                $"Latest: {info.TagName}\n\n" +
                "Download and install it now?",
                "Update available",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (choice == MessageBoxResult.Yes) await DownloadAndApplyAsync(info);
            else SetUpdateStatus($"Update {info.TagName} available — use “Check for updates” when you're ready.");
        }
        finally { _updateBusy = false; }
    }

    /// <summary>Download the right asset for how the app was installed and launch it. MSI installs
    /// silently-ish (installer UI) then the app quits so files can be replaced; a portable build
    /// downloads the new exe and reveals it for the user to swap in.</summary>
    private async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        bool installed = IsInstalledBuild();
        // Prefer the asset matching the install kind, but fall back to whatever the release has.
        string? url = installed ? (info.MsiUrl ?? info.ExeUrl) : (info.ExeUrl ?? info.MsiUrl);
        if (url == null)
        {
            SetUpdateStatus("No installer attached to that release — opening the page instead.");
            OpenReleasesPage();
            return;
        }

        // _updateBusy is owned by the caller (CheckForUpdatesAsync); this method only runs inside
        // that guarded region, so it neither sets nor clears the flag.
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(url).LocalPath));
            var progress = new Progress<double>(p => SetUpdateStatus($"Downloading {info.TagName}… {p:0}%"));
            SetUpdateStatus($"Downloading {info.TagName}… 0%");
            await _updates.DownloadAsync(url, dest, progress);

            bool isMsi = url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            if (isMsi)
            {
                // Launch the installer and quit so it can replace the running files.
                SetUpdateStatus("Starting the installer…");
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            else
            {
                // Portable: can't overwrite a running .exe, so reveal the new one for the user.
                SetUpdateStatus("Downloaded. Close PWRU Helper, then replace your PWRUHelper.exe with the new one.");
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dest}\"") { UseShellExecute = true }); }
                catch { /* Explorer missing? the status text still tells them where it is */ }
            }
        }
        catch (Exception ex)
        {
            Logging.Error("Update download/install failed", ex);
            SetUpdateStatus("Update failed to download — opening the releases page instead.");
            OpenReleasesPage();
        }
    }

    // MSI installs usually land under Program Files; a portable exe runs from anywhere else. But the
    // MSI wizard (WixUI_InstallDir) lets the user pick ANY folder, which the prefix check would miss
    // and wrongly treat as portable — so we also look for the app's Add/Remove-Programs entry.
    private static bool IsInstalledBuild()
    {
        try
        {
            var path = Environment.ProcessPath ?? "";
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var dir = Environment.GetFolderPath(f);
                if (dir.Length > 0 && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // The ARP (uninstall) entry is the reliable MSI marker — it survives a custom install
            // dir. Small false-positive when someone has BOTH an MSI install AND runs a portable
            // copy; pushing them the MSI update is the acceptable outcome there.
            if (HasArpEntry("PWRU Helper")) return true;
        }
        catch { /* fall through */ }
        return false;
    }

    /// <summary>True if any Add/Remove-Programs uninstall key (native or WOW6432Node) has a
    /// DisplayName equal to <paramref name="displayName"/>. Read-only; false on any error.</summary>
    private static bool HasArpEntry(string displayName)
    {
        try
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var root in roots)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") as string == displayName) return true;
                }
            }
        }
        catch { /* registry unreadable → treat as no entry */ }
        return false;
    }

    private void SetUpdateStatus(string msg)
    {
        if (UpdateStatus != null) UpdateStatus.Text = msg;
    }

    private void OpenReleasesPage()
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true }); }
        catch { ShowToast("Couldn't open the browser — see github.com/Kizotis/PWRU-Helper/releases"); }
    }
}

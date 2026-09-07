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
    private void CompactButton_Click(object sender, RoutedEventArgs e) => EnterCompactMode();

    private void ToggleCompact()
    {
        if (_overlay is { IsVisible: true }) ExitCompactMode();
        else EnterCompactMode();
    }

    internal void EnterCompactMode()
    {
        if (_overlay == null)
        {
            _overlay = new CompactOverlay(this);
            if (_settings.OverlayWidth is > 200 and { } ow) _overlay.Width = ow;
            if (_settings.OverlayHeight is > 150 and { } oh) _overlay.Height = oh;
            if (_settings.OverlayLeft is { } ol && _settings.OverlayTop is { } ot && IsOnScreen(ol, ot, 240, 180))
            { _overlay.Left = ol; _overlay.Top = ot; }
            else
            {
                // First time: park it near the top-right of the primary work area.
                _overlay.Left = SystemParameters.WorkArea.Right - _overlay.Width - 20;
                _overlay.Top = SystemParameters.WorkArea.Top + 40;
            }
        }
        _overlay.Show();
        // E7.S4 — the overlay never asks anything, so it is told: a window opened DURING a pause
        // would otherwise start its 600 ms blink on Show() (IsVisibleChanged) over a pipe that is
        // sending nothing, which is R-02 reached through the one door that does not go through the
        // countdown tick. After Show(), so the visibility change it answers has already happened.
        _overlay.SetPaused(_livePaused);
        // …and A8's button state, for the same reason and through the same door: Ctrl+Alt+R can be
        // reading while the player goes compact, and a "👁 Read once" button over a read in flight
        // is the second press doing nothing all over again.
        SetReadOnceCancelMode(_readingOnce);
        _overlay.Activate();
        Hide();
    }

    internal void ExitCompactMode()
    {
        if (_overlay != null) { SaveOverlayBounds(); _overlay.Hide(); }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SaveOverlayBounds()
    {
        if (_overlay == null) return;
        var b = _overlay.RestoreBounds;
        if (b.IsEmpty) return;
        _settings.OverlayLeft = b.Left; _settings.OverlayTop = b.Top;
        _settings.OverlayWidth = b.Width; _settings.OverlayHeight = b.Height;
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PWRUHelper;

/// <summary>
/// A small, borderless, always-on-top overlay for use while gaming. It shows the same
/// live-translation feed as the main window (bound to its shared collection) and offers
/// a one-line "type a Russian reply" box. All the real work stays in <see cref="MainWindow"/>;
/// this window is just a compact face onto it.
/// </summary>
public partial class CompactOverlay : Window
{
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _beat = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly INotifyCollectionChanged _feed;
    private bool _blink;

    /// <summary>Set by the owner when it really wants this window closed (app shutdown);
    /// otherwise an Alt+F4 / OS close is redirected back to the full window.</summary>
    public bool AllowClose { get; set; }

    public CompactOverlay(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;

        FeedItems.ItemsSource = owner.LiveItems;
        _feed = owner.LiveItems;
        _feed.CollectionChanged += Feed_Changed;
        ApplyFontScale(owner.FontScale);
        UpdateEmptyHint();

        _beat.Tick += (_, _) => UpdateLiveIndicator();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _beat.Start(); UpdateLiveIndicator(); } else _beat.Stop(); };
    }

    private void Feed_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyHint();
        FeedScroller.ScrollToEnd();
    }

    private void UpdateEmptyHint()
        => EmptyHint.Visibility = (_owner.LiveItems.Count == 0 && !_owner.IsLive)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Show the live status / a routed toast in the overlay's own status line.</summary>
    public void SetStatus(string msg)
    {
        OverlayStatus.Text = msg;
        OverlayStatus.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyHint();
    }

    public void ApplyFontScale(double scale)
        => FeedItems.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);

    private void UpdateLiveIndicator()
    {
        bool live = _owner.IsLive;
        LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (live) { LiveDot.Text = _blink ? "  ●  LIVE" : "  ○  LIVE"; _blink = !_blink; }
        LiveToggleButton.Content = live ? "■ Live" : "▶ Live";
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* mouse already released */ }
    }

    private void LiveToggle_Click(object sender, RoutedEventArgs e)
    {
        _owner.ToggleLive();
        UpdateLiveIndicator();
    }

    private void Expand_Click(object sender, RoutedEventArgs e) => _owner.ExitCompactMode();

    private void ReplyBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ReplyPlaceholder.Visibility = string.IsNullOrEmpty(ReplyBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    private async void ReplyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        var text = ReplyBox.Text.Trim();
        if (text.Length == 0) return;

        SetReplyResult("Translating…", error: false);
        var r = await _owner.QuickReplyTranslateAsync(text);

        if (!r.Ok)
        {
            // Keep what the user typed so they don't lose their message; show why it failed.
            SetReplyResult($"⚠ {r.Error ?? "couldn't translate"} — your text is kept, press Enter to retry.", error: true);
            return;
        }
        if (!r.Copied)
        {
            SetReplyResult($"→ {r.Russian}   (clipboard busy — press Enter again to copy)", error: true);
            return;
        }
        ReplyBox.Clear();
        SetReplyResult($"→ {r.Russian}    ✓ copied — paste in game with Ctrl+V", error: false);
    }

    private void SetReplyResult(string text, bool error)
    {
        ReplyResult.Text = text;
        ReplyResult.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            error ? "AccentBrush" : "GoldBrush");
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The main window is hidden while we're up, so actually closing this window would
        // leave the app running with no visible window. Redirect Alt+F4 / the OS close to
        // "return to the full window" instead — unless the owner is shutting the app down.
        if (!AllowClose)
        {
            e.Cancel = true;
            _owner.ExitCompactMode();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Detach from the shared collection so we don't keep it (or us) alive.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _beat.Stop();
        _feed.CollectionChanged -= Feed_Changed;
        base.OnClosed(e);
    }
}

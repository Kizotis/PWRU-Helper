using System.Collections.Specialized;
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

    public CompactOverlay(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;

        FeedItems.ItemsSource = owner.LiveItems;
        _feed = owner.LiveItems;
        _feed.CollectionChanged += Feed_Changed;
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
        => EmptyHint.Visibility = _owner.LiveItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
        _owner.ToggleLiveFromOverlay();
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

        ReplyResult.Text = "Translating…";
        var ru = await _owner.QuickReplyTranslateAsync(text);
        if (ru.Length == 0) { ReplyResult.Text = ""; return; }

        ReplyBox.Clear();
        ReplyResult.Text = $"→ {ru}    ✓ copied — paste in game with Ctrl+V";
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    /// <summary>Detach from the shared collection so we don't keep it (or us) alive.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _beat.Stop();
        _feed.CollectionChanged -= Feed_Changed;
        base.OnClosed(e);
    }
}

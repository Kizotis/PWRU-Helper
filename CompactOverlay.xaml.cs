using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PWRUHelper.Services;

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

    /// <summary>Max characters the game accepts in a single chat message. A reply longer
    /// than this is split into word-aligned blocks the user copies and sends one by one.</summary>
    private const int GameMessageLimit = 78;

    /// <summary>One copyable block of a split reply. <paramref name="Label"/> is like "1/2".</summary>
    private record ReplyChunk(string Label, string Text);

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
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _beat.Start(); UpdateLiveIndicator(); UpdateReplyHint(); }
            else _beat.Stop();
        };
        UpdateReplyHint();
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
    {
        ReplyPlaceholder.Visibility = string.IsNullOrEmpty(ReplyBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        // The split-block list is temporary: once the user starts composing the next reply,
        // clear the old blocks so they can't paste a stale one. (Clearing the box after a
        // send leaves an empty string, which keeps the blocks up so they can still copy them.)
        if (ReplyBox.Text.Length > 0) HideReplyBlocks();
    }

    private void UpdateReplyHint()
        => ReplyPlaceholder.Text = $"Type a reply → Enter ({_owner.MyLanguage.ToUpperInvariant()} → RU, copied)";

    private bool _replying;   // ignore extra Enter presses while a reply is in flight

    private async void ReplyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_replying) return;

        var text = ReplyBox.Text.Trim();
        if (text.Length == 0) return;

        _replying = true;
        SetReplyResult("Translating…", error: false);
        MainWindow.ReplyOutcome r;
        try { r = await _owner.QuickReplyTranslateAsync(text); }
        finally { _replying = false; }

        if (!r.Ok)
        {
            // Keep what the user typed so they don't lose their message; show why it failed.
            SetReplyResult($"⚠ {r.Error ?? "couldn't translate"} — your text is kept, press Enter to retry.", error: true);
            return;
        }

        // Long replies won't fit in one game chat message: split into word-aligned blocks the
        // user copies and sends in order. Short ones keep the simple auto-copy behaviour.
        var blocks = TextMatching.SplitForGameChat(r.Russian, GameMessageLimit);
        if (blocks.Count > 1)
        {
            ShowReplyBlocks(blocks);
            bool firstCopied = await _owner.CopyToClipboardAsync(blocks[0]);
            ReplyBox.Clear();
            SetReplyResult(firstCopied
                ? $"Split into {blocks.Count} messages — block 1 copied, paste it then copy the next below."
                : $"Split into {blocks.Count} messages — copy each block below and send them in order.",
                error: false);
            return;
        }

        HideReplyBlocks();
        if (!r.Copied)
        {
            SetReplyResult($"→ {r.Russian}   (clipboard busy — press Enter again to copy)", error: true);
            return;
        }
        ReplyBox.Clear();
        SetReplyResult($"→ {r.Russian}    ✓ copied — paste in game with Ctrl+V", error: false);
    }

    private void ShowReplyBlocks(List<string> blocks)
    {
        int n = blocks.Count;
        ReplyBlocks.ItemsSource = blocks.Select((b, i) => new ReplyChunk($"{i + 1}/{n}", b)).ToList();
        ReplyBlocksPanel.Visibility = Visibility.Visible;
    }

    private void HideReplyBlocks()
    {
        if (ReplyBlocksPanel.Visibility == Visibility.Collapsed) return;
        ReplyBlocks.ItemsSource = null;
        ReplyBlocksPanel.Visibility = Visibility.Collapsed;
    }

    private async void CopyBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReplyChunk chunk }) return;
        bool ok = await _owner.CopyToClipboardAsync(chunk.Text);
        SetReplyResult(ok
            ? $"✓ block {chunk.Label} copied — paste in game with Ctrl+V"
            : $"block {chunk.Label}: clipboard busy — click Copy again",
            error: !ok);
    }

    private void SetReplyResult(string text, bool error)
    {
        ReplyResult.Text = text;
        ReplyResult.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            error ? "AccentBrush" : "GoldBrush");
    }

    // ===== Resize from any edge/corner, like a normal window =====
    // The overlay is a borderless (WindowStyle=None + AllowsTransparency) window, so it has no
    // native sizing border. We hook WM_NCHITTEST and report the cursor as sitting on a window
    // edge/corner when it's within ResizeBorder of one; Windows then does the real resize —
    // native cursors, edge snapping, MinWidth/MinHeight all handled by the OS. (Replaces the old
    // bottom-right-only drag grip.)
    private const double ResizeBorder = 8.0;   // DIP band around the edge that starts a resize

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // lParam packs the cursor's SCREEN position (physical px) as two signed 16-bit halves.
        long lp = lParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));

        // Into window-local DIPs: (0,0) = top-left, (ActualWidth, ActualHeight) = bottom-right.
        var p = PointFromScreen(screen);
        int hit = ResizeHitTest(p.X, p.Y, ActualWidth, ActualHeight, ResizeBorder);
        if (hit == 0) return IntPtr.Zero;   // interior → let WPF route the input normally

        handled = true;
        return (IntPtr)hit;
    }

    /// <summary>Which window edge/corner (a Win32 HT* code) the point (<paramref name="x"/>,
    /// <paramref name="y"/>) sits on within <paramref name="border"/> DIPs of the window's
    /// bounds, or 0 for the interior. Pure geometry so it can be unit-tested.</summary>
    internal static int ResizeHitTest(double x, double y, double w, double h, double border)
    {
        bool left = x <= border, right = x >= w - border;
        bool top = y <= border, bottom = y >= h - border;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return 0;   // interior
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

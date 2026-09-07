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

    /// <summary>E7.S4 / AC 1 — <b>the read path is paused, so nothing is being sent.</b> Remembered
    /// on the window rather than applied and forgotten, because <see cref="IsVisibleChanged"/>
    /// restarts <see cref="_beat"/>: toggling to compact mode and back while paused would otherwise
    /// bring the blink back over a stopped pipe, which is R-02 again and harder to see.
    ///
    /// <para><b>The overlay never asks anything.</b> <c>MainWindow</c> tells it, the way it already
    /// does with <see cref="SetStatus"/> — one way, no dependency back.</para></summary>
    private bool _paused;

    /// <summary>The two forms of the heartbeat, named because three call sites write them and one
    /// of them must never write the other (AC 1).</summary>
    internal const string DotOn = "  ●  LIVE", DotOff = "  ○  LIVE";

    /// <summary>Max characters the game accepts in a single chat message. A reply longer than this
    /// is split into word-aligned blocks the user copies and sends one by one. Shared with the
    /// Translator tab, which highlights the same cut points instead of splitting.</summary>
    private const int GameMessageLimit = Services.TextMatching.GameChatLimit;

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
            SyncBeat();
            if (IsVisible) { UpdateLiveIndicator(); UpdateReplyHint(); }
        };
        UpdateReplyHint();
        // The read-once button has no Content in the XAML at all (A8, GAP-4): its copy changes, so
        // it lives in the deck. Written here so the button is never blank between construction and
        // the owner's first push — MainWindow.EnterCompactMode then hands it the real state, which
        // is not always "idle": Ctrl+Alt+R can start a read and the player can go compact during it.
        SetReadOnceCancelMode(reading: false);
    }

    private void Feed_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyHint();
        FeedScroller.ScrollToEnd();
    }

    private void UpdateEmptyHint()
        => EmptyHint.Visibility = (_owner.LiveItems.Count == 0 && !_owner.IsLive)
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Show the live status in the overlay's own status line.
    ///
    /// <para>It goes through E7.S4's arbitration: while a toast owns this line, the status is
    /// REMEMBERED rather than written, and lands the moment the toast's own lifetime ends. See
    /// <see cref="ShowToast"/> for why that is not the same as dropping it.</para></summary>
    public void SetStatus(string msg)
    {
        if (_toastOwnsTheLine) { _statusAfterToast = msg; return; }
        WriteStatus(msg);
    }

    /// <summary>The same status line, through E7.S2's repaint guard: the 1 Hz countdown writes this
    /// window once a second while something is paused, and above 90 s the rendered string only
    /// changes on a minute boundary. Comparing the <c>TextBlock</c>'s own text — rather than a field
    /// this window would have to keep in step with <see cref="SetStatus"/>' other callers — is what
    /// makes it one source of truth; the saving is the visibility flip and the empty-hint refresh
    /// <see cref="SetStatus"/> does on every call.
    ///
    /// <para>The overlay is handed the FORMATTED string and never learns what a countdown is:
    /// <c>MainWindow</c> formats, this window renders (I2).</para></summary>
    internal void SetStatusIfChanged(string msg)
    {
        if (_toastOwnsTheLine) { _statusAfterToast = msg; return; }
        if (!string.Equals(OverlayStatus.Text, msg, StringComparison.Ordinal)) WriteStatus(msg);
    }

    /// <summary>The write itself — the one place this window's status line is assigned, so the
    /// arbitration above cannot be got round by a caller that only knows about one of the two
    /// entry points.</summary>
    private void WriteStatus(string msg)
    {
        OverlayStatus.Text = msg;
        OverlayStatus.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyHint();
    }

    /// <summary>
    /// <b>E7.S4 — the toast/status arbitration, and the rule is "a toast owns the line".</b> This
    /// window has ONE status line, so a toast routed here (the main window is hidden in compact
    /// mode, <c>MainWindow.ShowToast</c>) and the 1 Hz countdown are writing the same
    /// <c>TextBlock</c>. Before this, the countdown won within a second and the player never read
    /// the toast — E7.S2's review flagged exactly that and left the arbitration open.
    ///
    /// <para>So the toast holds the line for <b>its</b> lifetime and the state comes back after: the
    /// status that would have been painted meanwhile is kept in <see cref="_statusAfterToast"/> —
    /// the LAST one, because a state is a fact about now and not a queue — and written by
    /// <see cref="EndToast"/>. Nothing is lost either way: a state line is re-derived once a second
    /// anyway, and a toast that is shown for a fifth of its 1.6 s is not shown at all.</para>
    ///
    /// <para><b>No timer here.</b> The hold ends when <c>MainWindow</c>'s existing <c>_toastTimer</c>
    /// says so (§2.4: one countdown for the whole app, and the 600 ms heartbeat is the only other
    /// thing that may tick). The overlay is told; it does not decide.</para></summary>
    internal void ShowToast(string message)
    {
        WriteStatus(message);
        _toastOwnsTheLine = true;
    }

    /// <summary>The toast's lifetime is over: the line goes back to describing the state rather than
    /// the last thing that happened. Idempotent, and a no-op for a toast that was shown on the main
    /// window instead — <c>MainWindow</c> calls it from one place, its toast timer.</summary>
    internal void EndToast()
    {
        if (!_toastOwnsTheLine) return;
        _toastOwnsTheLine = false;
        if (_statusAfterToast is not { } line) return;
        _statusAfterToast = null;
        SetStatusIfChanged(line);
    }

    private bool _toastOwnsTheLine;
    private string? _statusAfterToast;

    public void ApplyFontScale(double scale)
        => FeedItems.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);

    /// <summary>
    /// <b>E7.S4 / AC 1 — the heartbeat means "requests are flowing", so a paused path freezes it.</b>
    /// <c>MainWindow</c> owns the decision (<c>SetLivePaused</c>, from the one
    /// <c>ChainTranslator.PauseNow()</c> the LIVE loop itself uses) and hands it here.
    ///
    /// <para>When <paramref name="paused"/>, <see cref="_beat"/> <b>stops</b> — AC 2, and the whole
    /// of UX-DR18's payoff: the degraded state costs <i>less</i> CPU than the healthy one — and the
    /// dot is written once, on <c>○</c>. <see cref="_blink"/> is reset so a resume starts on a known
    /// glyph instead of on whatever parity the pause interrupted.</para>
    ///
    /// <para>Stopping the timer is <b>not enough</b>: <see cref="UpdateLiveIndicator"/> has four
    /// entry points (this one, the tick, <c>IsVisibleChanged</c> and the ▶/■ button), so the guard
    /// lives in the method as well.</para></summary>
    internal void SetPaused(bool paused)
    {
        if (_paused == paused) return;   // one write per transition (AC 4)
        _paused = paused;
        _blink = false;                  // a known state to resume from, whichever way we just went
        SyncBeat();
        UpdateLiveIndicator();
    }

    /// <summary>The 600 ms heartbeat runs when, and only when, <see cref="BeatShouldRun"/> says so.
    /// One call site for <c>_beat.Start()</c> in the whole file is the point: the trap this story
    /// exists to close is a <i>second</i> site re-enabling a blink that was correctly stopped.</summary>
    private void SyncBeat() => SyncBeat(IsVisible);

    /// <summary>The same, with the visibility handed in: AC 2 is about a timer, and a test may not
    /// <c>Show()</c> a 360 px always-on-top window on a build agent to watch one (CI-3/CI-4). The
    /// parameterless form above is what the two production call sites use.</summary>
    internal void SyncBeat(bool visible)
    {
        if (BeatShouldRun(visible, _paused)) _beat.Start();
        else _beat.Stop();
    }

    /// <summary>The rule, pure so both of its halves can be asserted without showing a window: the
    /// blink costs nothing while the overlay is hidden (as before), <b>and</b> nothing while the
    /// path is paused (AC 2) — which is also why toggling compact mode during a pause does not
    /// restart it.</summary>
    internal static bool BeatShouldRun(bool visible, bool paused) => visible && !paused;

    /// <summary>Whether the 600 ms heartbeat is actually running. AC 2 is about the timer and not
    /// about the text, so the test asserts this rather than waiting 600 ms for a glyph (CI-3).</summary>
    internal bool BeatRunning => _beat.IsEnabled;

    internal void UpdateLiveIndicator()
    {
        bool live = _owner.IsLive;
        LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LiveToggleButton.Content = live ? "■ Live" : "▶ Live";
        // Frozen on ○ — and it returns rather than falling through, so none of the four entry
        // points can advance _blink while nothing is being sent (R-02: a blinking dot over a
        // stopped pipe is the lie this whole story is about).
        if (_paused) { LiveDot.Text = Heartbeat(paused: true, blink: false); return; }
        if (live) { LiveDot.Text = Heartbeat(paused: false, _blink); _blink = !_blink; }
    }

    /// <summary>The heartbeat's one decision, pure so TP-LIVE-17 can drive N ticks of it headlessly:
    /// <b>the glyph is the same on both parities while paused</b> — which is what "does not
    /// alternate" means — and differs on them while requests are flowing.</summary>
    internal static string Heartbeat(bool paused, bool blink) => !paused && blink ? DotOn : DotOff;

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

    /// <summary>Same action as the Translator tab's "Select area &amp; read once" — the owner holds
    /// the logic so the two buttons can't drift apart. Compact mode is kept throughout: this
    /// overlay just steps aside for the drag itself, then the framed result lands in the feed.</summary>
    private async void ReadOnce_Click(object sender, RoutedEventArgs e)
        => await _owner.SelectAreaAndReadOnceAsync();

    /// <summary>Follow the main window's read-once button state (a Ctrl+Alt+R read can be running
    /// while the overlay is the only thing on screen) — <b>amendment A8</b>: the button is never
    /// disabled, it becomes the cancel. Icon-only at 360 px, so the tooltip carries what the main
    /// window's label says, and the automation name says it out loud.
    ///
    /// <para><c>MainWindow</c> decides and this window renders (I2), the way <see cref="SetStatus"/>
    /// and <see cref="SetPaused"/> already do: the flag that owns "a read is in flight" is
    /// <c>_readingOnce</c>, and it is not this window's.</para></summary>
    internal void SetReadOnceCancelMode(bool reading)
    {
        ReadOnceButton.Content = reading
            ? UserMessages.CancelReadOverlayLabel() : UserMessages.ReadOnceOverlayLabel();
        ReadOnceButton.ToolTip = reading
            ? UserMessages.CancelReadLabel() : UserMessages.ReadOnceOverlayTooltip();
        System.Windows.Automation.AutomationProperties.SetName(ReadOnceButton,
            reading ? UserMessages.CancelReadLabel() : UserMessages.ReadOnceLabel());
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
            //
            // The WHOLE line arrives from the copy deck now (amendment A3 / §3.4): this surface
            // takes a short form chosen by Kind, not a §3.1 sentence in a wrapper — the wrapper
            // alone was 46 of the ~60 characters §3.4 allows a 360 px window. `Error` is null only
            // for a reply with nothing typed, which never reaches this branch.
            SetReplyResult(r.Error ?? UserMessages.OverlayReply(null), error: true);
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

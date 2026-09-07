using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>
    /// <b>architecture-cible.md §9.3 — the rows that failed during a blip.</b> A row whose
    /// translation failed for a reason that could answer differently later keeps its "…" and waits
    /// here; the first tick that is not skipped re-translates the whole queue IN PLACE, before it
    /// looks at the screen (ruling E3-h, DoD V1.3).
    ///
    /// <para>It is declared in THIS file rather than with the other live fields in
    /// <c>MainWindow.xaml.cs</c> because AC 1 names the file, and because the queue's whole life —
    /// the enqueue in <see cref="AppendLinesToHistory"/>'s catch, the drain at the top of a tick, the
    /// clear in <see cref="StopLive"/> — is on the four screens below it. What AC 1 is really
    /// protecting is the other half of that sentence: <b><c>LiveDedup</c> is not touched at all</b>,
    /// which is the strongest possible guarantee that it cannot swallow anything.</para>
    ///
    /// <para>The queue holds row REFERENCES, and that is the whole of "never a duplicate row" — the
    /// one failure the epic calls worse than the failure it replaces. A drain writes
    /// <c>Row.TranslationBody</c>, which raises <c>PropertyChanged</c> and repaints both feeds where
    /// the row already is; it never touches <c>_ocrItems</c>, so no <c>CollectionChanged</c> fires,
    /// nothing re-sorts, and neither feed scrolls under the player's eyes.</para></summary>
    private readonly PendingRetryQueue<OcrResultItem> _pendingRetry = new();

    private void UpdateResumeLiveButton()
    {
        bool show = _liveCts == null && _settings.LastLiveRegion is { Length: 4 };
        ResumeLiveButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Single entry point for the live button / Resume / Ctrl+Alt+L / overlay:
    /// stop if running, else resume the saved area, else surface the picker.</summary>
    internal void ToggleLive()
    {
        if (_selectingRegion || _readingOnce) return;   // a read-once owns the shared OCR engine
        if (_liveCts != null) { StopLive(); return; }
        if (TryGetSavedRegion(out var rect)) { StartLive(rect); return; }

        // Nothing saved (or it was off-screen) — bring the full window up to pick an area.
        if (_overlay is { IsVisible: true }) ExitCompactMode(); else BringToFront();
        MainTabs.SelectedIndex = TabScreenOcr;
        ShowToast("Pick a screen area once — then ▶ Live (or Ctrl+Alt+L) resumes it.");
    }

    /// <summary>The saved live area, if there is one AND it still lands on a screen.</summary>
    private bool TryGetSavedRegion(out System.Drawing.Rectangle rect)
    {
        rect = default;
        if (_settings.LastLiveRegion is not { Length: 4 } r) return false;
        var candidate = new System.Drawing.Rectangle(r[0], r[1], r[2], r[3]);
        if (!RegionOnVirtualScreen(candidate))
        {
            _settings.LastLiveRegion = null;   // stale (resolution/monitor changed)
            SettingsService.Save(_settings);
            return false;
        }
        rect = candidate;
        return true;
    }

    // ============================================================
    //  LIVE SCREEN TRANSLATION
    // ============================================================
    private async void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingRegion) return;
        if (_liveCts != null) { StopLive(); return; }

        var region = await SelectRegionAsync();
        if (region is { } rect) StartLive(rect);
    }

    private void ResumeLive_Click(object sender, RoutedEventArgs e) => ToggleLive();

    private void StartLive(System.Drawing.Rectangle rect)
    {
        if (_readingOnce) return;   // a read-once is driving the shared OCR engine — don't race it

        if (!RegionOnVirtualScreen(rect))
        {
            _settings.LastLiveRegion = null;
            SettingsService.Save(_settings);
            UpdateResumeLiveButton();
            ShowToast("That area isn't on any screen any more — select it again.");
            return;
        }

        // Never leave a previous loop running — otherwise a live started via hotkey while
        // the area was being selected would be orphaned here (impossible to Stop).
        if (_liveCts != null) StopLive();

        // Remember the area so it can be resumed next session without re-selecting. This happens
        // BEFORE the OCR-pack check on purpose: the user has usually just dragged the rectangle, and
        // bailing out first threw that work away — after installing the pack they had to draw it all
        // over again, with no "Resume last area" button to help them.
        _settings.LastLiveRegion = new[] { rect.X, rect.Y, rect.Width, rect.Height };
        SettingsService.Save(_settings);
        UpdateResumeLiveButton();

        // No Russian engine = nothing readable. Don't run a loop that can only ever produce empty
        // frames (and, before the fallback was removed, a feed full of confident Latin gibberish).
        // Send the user to the one-click installer instead — that IS the fix, and it's one click.
        if (!IsOcrReady()) { ShowOcrPackNeeded("then start live again — your area is remembered."); return; }

        _liveRegion = rect;
        _dedup = new();
        _liveTicks = 0;
        _ocrItems.Clear();
        // The feed has just been emptied, so every row the queue could be holding is gone. Clearing
        // it here as well as in StopLive is belt and braces on the one property that matters: an
        // entry can only ever point at a row that is on the screen in front of the player. What it
        // was holding is discarded rather than given up (StopLive does the opposite) for that same
        // reason: there is nothing left to write on — _ocrItems.Clear() ran on the line above.
        _pendingRetry.Clear();
        SetLiveUi(true);
        MainTabs.SelectedIndex = TabTranslator;
        SetScreenStatus(UserMessages.LiveStarted());

        _liveCts = new CancellationTokenSource();
        _ = LiveLoop(rect, _liveCts.Token);
    }

    private void StopLive_Click(object sender, RoutedEventArgs e) => StopLive();

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensitivityValue != null)
            SensitivityValue.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void LiveSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LiveSpeedValue != null)
            LiveSpeedValue.Text = $"~{CurrentLiveIntervalMs() / 1000.0:0.0}s between reads";
    }

    private void MinFragmentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinFragmentValue != null)
        {
            int n = (int)Math.Round(e.NewValue);
            MinFragmentValue.Text = n == 1 ? "1 letter" : $"{n} letters";
        }
    }

    private void StabilitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StabilityValue != null)
            StabilityValue.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void StopLive()
    {
        // ■ Stop ends the thing the player is waiting on, and a read-once is one of those (AC 2,
        // E5.S4). It is BEFORE the guard below on purpose: that guard returns when no loop is
        // running, and a read-once has no loop. Harmless on the path that matters most —
        // SelectAreaAndReadOnceAsync calls StopLive() BEFORE it starts its read, so the token this
        // cancels is the previous read's (already gone) and never the one about to be created.
        CancelReadOnce();

        // AC 3 — ■ Stop clears the pending-retry queue, and it does so BEFORE the guard below for the
        // same reason CancelReadOnce is there: pressing Stop must end everything the player is
        // waiting on, and a queue that survived the loop that filled it would fill rows in on the
        // NEXT session's feed. Unconditional, so "press ■, then ▶, and no old row is resurrected"
        // (the story's manual step 8) holds however the session ended.
        //
        // The rows it was holding are GIVEN UP rather than left on "…" (review). Stop does not clear
        // the feed — StartLive does — so those rows stay on both surfaces, and the loop that owed
        // them their translation is the thing that just ended: nothing is coming for them, by
        // construction. §2.2 draws exactly this line — a pending row keeps "…" WHILE something is
        // coming, and a given-up row says so in parentheses — and a "…" that never resolves is the
        // amplifier (A7) the whole epic exists to remove. It is also what the auto-stop needs: that
        // path ends a long outage by calling StopLive() for the player.
        foreach (var entry in _pendingRetry.Clear()) GiveUpRow(entry.Row);

        if (_liveCts == null) return;
        _liveCts.Cancel();
        _liveCts.Dispose();
        _liveCts = null;
        _liveRegion = null;
        SetLiveUi(false);
        // NFR7's "never runs idle", at the one moment it can become false: the countdown paints the
        // LIVE status line, and there is no longer a LIVE status line to paint. It goes off BEFORE
        // the line below, so nothing repaints over "Live stopped." (the R-02 shape: a stopped loop
        // must not keep showing a status that says something is still coming). E7.S2.
        StopCountdown();
        SetScreenStatus("Live stopped.");
        // …and then the chip is asked whether IT still needs the tick (E7.S3). Stopping LIVE ends
        // the loop, not the pause: a provider inside a window keeps counting down whether or not
        // anything is reading the screen, and the chip is the surface that says so. This is exactly
        // the second start site E7.S2's code note warned about, and the tick's stop rule was widened
        // for it — the timer above still goes off first, so "Live stopped." is never repainted over,
        // and it comes back only if there is something left to count.
        UpdateEngineChip();
    }

    /// <summary>Set the screen-reading status on the main window AND (if shown) the overlay, so the
    /// state is visible whichever window the user is looking at. Used by the live loop AND by
    /// read-once: in compact mode ScreenReadStatus lives on a hidden window, so writing only there
    /// left "Reading…" and every OCR error invisible to someone working from the overlay.</summary>
    private void SetScreenStatus(string msg)
    {
        // §3.5's one-time notice RIDES on the next line the loop writes, rather than replacing one
        // (E7.S4). §3.2's "resumed" row asks for exactly that — "the normal running line, plus
        // §3.5's one-time `Back on {P}.`" — and it is what keeps the notice from being either
        // clobbered by the next tick 700 ms later or written over a fresh status of its own.
        // The overlay's column for that row is "chip cleared, normal status": forty characters do
        // not stretch to a notice, so it is the main window's line that carries it.
        ScreenReadStatus.Text = WithNotice(msg, _stateNotice);
        _stateNotice = null;
        // The overlay's third placement of the chip (E7.S3 AC 1): it has no TextBlock of its own
        // there — the window is 360 px wide — so MainWindow composes "{chip}  {status}" and the
        // overlay renders it (I2). OverlayLine returns the status untouched when the chain is
        // healthy ("shown only when not healthy") and when the chip is carrying a countdown, which
        // is E7.S2's AC 4: one line, one clock.
        _overlay?.SetStatus(OverlayLine(msg));
    }

    /// <summary>§3.2's "resumed" row, as a join: <i>the normal running line, plus §3.5's one-time
    /// notice</i>. Two spaces and no punctuation of its own — the notice is already a terminated
    /// sentence (§3.5) and the line it rides on ends in its own stop or ellipsis. Pure, so the
    /// composition is a unit test rather than something only a running loop could show.
    ///
    /// <para><b>And it is BOUNDED</b> (review, Winston): a join of two sentences that are each
    /// budgeted on their own is not itself budgeted, and the worst case — a paused row (79) plus
    /// <c>Translated by {P} — {P2} is paused.</c> — already passes <see cref="MainStatusBudget"/>.
    /// The half that gives way is the <b>status</b>, elided with an ellipsis: it is re-derived on
    /// the next tick, while the notice is said once per switch and never repeated (§3.5). The cut
    /// never leaves half a surrogate pair behind — every LIVE line in the deck opens on
    /// <c>🔴</c>.</para></summary>
    internal static string WithNotice(string status, string? notice)
    {
        if (notice is null) return status;
        if (string.IsNullOrEmpty(status)) return notice;

        string joined = status + "  " + notice;
        if (joined.Length <= MainStatusBudget) return joined;

        int room = MainStatusBudget - notice.Length - 3;      // the ellipsis + the two-space join
        if (room <= 0) return notice;                         // a notice that fills the line alone
        if (char.IsHighSurrogate(status[room - 1])) room--;   // never cut a pair in half
        return status[..room].TrimEnd() + "…  " + notice;
    }

    /// <summary>§3.2's own measure of the main window's status line: <i>"rendered length with the
    /// longest sentence in the deck is ~120 characters; the main window's status line wraps"</i>.
    /// It is a budget and not a hard limit for the deck's own rows — every one of them is pinned
    /// well under it — but a JOIN can grow past it, and an unbounded line is how a status becomes a
    /// paragraph.</summary>
    internal const int MainStatusBudget = 120;

    private void SetLiveUi(bool on)
    {
        // E7.S4 — the PAUSED write is authoritative. This line used to hard-set "●  LIVE" on every
        // call, so anything that touched the LIVE UI while the read path was paused silently
        // un-froze the heartbeat (a partially frozen indicator is R-02 again, harder to see).
        // Starting LIVE into a standing pause therefore shows the paused form at once, rather than
        // blinking for up to a second until the countdown's first tick.
        LiveIndicator.Text = _livePaused ? LiveIndicatorPaused : LiveIndicatorRunning;
        LiveIndicator.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // A notice that was waiting to ride on a running line has nothing to ride on any more:
        // "Live stopped.  Back on Google." is two states in one sentence.
        if (!on) _stateNotice = null;
        StopLiveButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LiveButton.Content = on ? "■  Stop live translation" : "▶  Start live translation";
        UpdateResumeLiveButton();
        LiveStatus.Text = on
            ? "🔴 Live is running — re-reading the area and re-translating whenever the text changes. Press Stop to end."
            : "Live mode keeps watching the chosen area and re-translates automatically whenever the text changes, until you press Stop.";
        // One of the events the chip is repainted on instead of on a timer (E7.S3 T5): starting or
        // stopping LIVE changes which window owns the clock, and therefore whether the chip shows
        // one (E7.S2's AC 4). Polling a breaker every 250 ms is exactly the cost this epic removes.
        UpdateEngineChip();
    }

    /// <summary>
    /// <b>E7.S4 / AC 1 and AC 2 — the paused heartbeat, on BOTH surfaces, from ONE decision.</b>
    /// The main window's <c>LiveIndicator</c> takes the fuller <c>○  LIVE (paused)</c> form (a
    /// frozen <c>●</c> is still a claim that something is being sent, NFR11: the word carries it,
    /// not the glyph alone), and the overlay is TOLD — one way, the way <c>SetStatus</c> already
    /// goes — so its own 600 ms blink timer stops.
    ///
    /// <para><b>Who decides.</b> The caller, from <c>ChainTranslator.PauseNow().AllPaused</c> — the
    /// very call the LIVE loop skips its tick on, so the indicator cannot disagree with the loop
    /// that owns the tick. S3 <i>with something below still serving</i> is deliberately NOT this:
    /// requests really are flowing and the heartbeat must keep blinking (flow (a).1). A rate-ceiling
    /// wait is not this either — it sets no <c>BlockedUntil</c>, so <c>PauseNow()</c> cannot see it
    /// (rulings E5-a/E5-b).</para>
    ///
    /// <para>It is <b>not</b> gated on a running loop: both indicators are <c>Collapsed</c> with
    /// LIVE off, so there is nothing to get wrong, and remembering the state is what lets
    /// <see cref="SetLiveUi"/> render the paused form the instant LIVE starts inside a window.</para>
    ///
    /// <para>Guarded on the transition, which is AC 4 in its cheapest form: at 1 Hz the countdown
    /// hands the same answer sixty times a minute and this writes nothing at all.</para></summary>
    internal void SetLivePaused(bool paused)
    {
        if (_livePaused == paused) return;
        _livePaused = paused;
        LiveIndicator.Text = paused ? LiveIndicatorPaused : LiveIndicatorRunning;
        _overlay?.SetPaused(paused);
    }

    /// <summary>The three forms of the main window's LIVE indicator. <c>(paused)</c> is AC 1's own
    /// wording and §2.2's: the state is legible with the palette stripped out (NFR11).</summary>
    internal const string LiveIndicatorRunning = "●  LIVE", LiveIndicatorOff = "○  LIVE",
                          LiveIndicatorPaused = "○  LIVE (paused)";

    private async Task LiveLoop(System.Drawing.Rectangle rect, CancellationToken ct)
    {
        // Both counters are LOCALS, so a session that ended — auto-stopped or stopped by the player
        // — leaves nothing behind for the next one: pressing ▶ after an auto-stop gets a clean five
        // and not one. StartLive builds a new loop, and a new loop builds these two.
        var errors = new LiveErrorTracker();   // §9.2's auto-stop: 5 sent failures in a row (E5.S2)
        int backoffSteps = 0;                  // how many ticks in a row have been SKIPPED (E5.S1)
        var sw = new System.Diagnostics.Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            sw.Restart();
            // Set by a skipped tick, and the difference matters: a tick that did nothing has no
            // elapsed time to subtract from the cadence — it waits the whole back-off.
            int? pausedWait = null;
            try
            {
                // ---- E5.S1 / owner's answer to OQ-B: the FULL pause -----------------------------
                // Every rung of the read chain is inside a block window, so this tick asks the chain
                // and then does NOTHING: no capture, no OCR filter, no _dedup.Next, no translation,
                // no request. That is the product's first requirement (nothing may lag the game)
                // applied to an outage, and it is what makes the burned-row class go away: the dedup
                // clock only advances inside Next, so a message that was on screen throughout the
                // pause is still genuinely new when the gate closes (AC 3).
                //
                // It asks the CHAIN, never ProviderGates — the registry is named exactly once
                // outside Services/ (TP-START-02) and that once is OnClosing's Flush.
                //
                // A rate-ceiling wait is deliberately NOT a pause (ruling E5-a): it sets no
                // BlockedUntil, so PauseNow() cannot see it, the tick runs, and HttpProviderCore
                // does the ≤ 2 bounded waits inside it. Pausing LIVE for a condition that clears in
                // 500 ms is the wrong fix this comment exists to stop.
                var pause = _readChain.PauseNow();
                if (pause.AllPaused)
                {
                    // The wait uses the CURRENT step count and the counter advances after it, so the
                    // first skipped tick waits the plain interval: 0.7 s → 1.4 → 2.8 → 5 → 5…
                    pausedWait = LiveTickPolicy.BackoffWaitMs(CurrentLiveIntervalMs(), backoffSteps);
                    backoffSteps = LiveTickPolicy.NextBackoffSteps(backoffSteps, LiveTickOutcome.Paused);

                    // _liveTicks is NOT advanced, and that one decision covers both things it drives:
                    // the ● / ○ heartbeat freezes (a blinking indicator over a stopped loop is the
                    // R-02 zombie, at exactly the moment it would matter most), and the "check #n"
                    // the player reads as progress does not count a check that never happened.
                    // The fuller "○  LIVE (paused)" form and the overlay's own 600 ms blink timer
                    // are E7.S4's — this loop does not reach into CompactOverlay.
                    //
                    // SetScreenStatus writes the main window AND the overlay (:157-161), which is
                    // all of AC 1's "both surfaces", and it is what makes the FIRST paused tick
                    // immediate rather than up to a second late.
                    //
                    // The STEPPING is E7.S2's, and it is not here: EnsureCountdownRunning starts the
                    // app's one 1 Hz countdown (MainWindow.xaml.cs) and hands it this same pause, so
                    // it repaints at once with the overlay's own short form and then once a second —
                    // in §2.4's bands, and only when the rendered string actually changed. Calling
                    // it on every skipped tick is deliberate and free: it is a no-op while the
                    // countdown already runs, so no site has to own it. It stops ITSELF on the first
                    // tick that finds nothing paused, and StopLive stops it too.
                    //
                    // pause.Now, never DateTimeOffset.UtcNow: RetryAt was produced against the
                    // GATES' clock, and subtracting a different one is the two-clocks bug IS-6 and
                    // ProviderGate.Now() exist to prevent (review, E5.S1). The countdown asks the
                    // same PauseNow() for the same reason.
                    //
                    // pause.NoNetwork is §2.1's S6 (E7.S4): the same full pause with the one cause
                    // the player can act on, and it comes from the SAME answer, so the sentence
                    // cannot come to disagree with the tick that chose it.
                    SetScreenStatus(LivePausedStatus(
                        LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now), pause.NoNetwork));
                    EnsureCountdownRunning(pause);

                    // The auto-stop is not reached from this branch AT ALL, and that is ruling E5-c
                    // made structural rather than kept true by a value: a SKIPPED tick is neither a
                    // success nor a failure (§9.2's table), so it may not feed the counter — and it
                    // cannot, because the tracker is only named in the other branch and in the
                    // catch. LivePauseTests scans this block for exactly that.
                    //
                    // E5.S2 finished the job E2.S5's escalation started ("no network ⇒ 5 REFUSED
                    // ticks inside the 5 s cooldown ⇒ LIVE auto-stops after ~3 s"): those ticks are
                    // skipped here, and the refusals that still arrive as a throw — a tick refused
                    // INSIDE itself for a reason that sets no BlockedUntil (the 1 s Background probe
                    // deferral, a rate-ceiling refusal), which reaches the catch as an
                    // AllProvidersPaused — now classify as Refused and reach no counter either. What
                    // counts is a request that was sent and failed: five of those in a row, with no
                    // translated line between them, and no clock anywhere in the rule.
                }
                else
                {
                    _liveTicks++;
                    // A tick that is NOT skipped is a tick that sends, so the heartbeat comes back
                    // here rather than up to a second later on the countdown's next tick — both
                    // surfaces at once (E7.S4), and a no-op on every tick but the first.
                    SetLivePaused(false);
                    LiveIndicator.Text = (_liveTicks % 2 == 0)
                        ? LiveIndicatorRunning : LiveIndicatorOff;                       // heartbeat

                    // ---- E5.S3 / §9.3: the rows that failed during the blip go FIRST ----------------
                    // "The first tick after a successful translation" is how §9.3 words it, from before
                    // E5.S1 existed; after it a tick is either skipped whole or runs, so the operative
                    // reading is THIS one — the first tick that is not skipped, draining before it looks
                    // at the screen. It is also the stronger rule: waiting for a fresh success first
                    // would leave the rows burned for ever in a chat that has gone quiet, because that
                    // success never comes. The drain IS the success.
                    //
                    // Before the capture, so a recovered row appears as fast as the gate allows, and
                    // in one batch through the same TranslateBodiesAsync a new line takes — one
                    // translation path, so slang expansion (I6) and the per-message ru/auto choice
                    // (I7) cannot come to have an exception for a retried row. E4 HAS landed, so the
                    // shared cache makes most of a drain free: a row whose text was translated once
                    // in this session (or the last) costs no request at all.
                    bool drained = await DrainPendingRetryAsync(ct);
                    if (ct.IsCancellationRequested) break;

                    using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
                    using var forOcr = ApplyOcrFilter(bmp);   // null when the filter is off
                    int minLetters = MinFragmentLetters();
                    // Split into whole chat messages by their "[Channel] Nick:" structure rather than
                    // by punctuation (players rarely type any) — see TextMatching.SplitChatMessages.
                    var lines = TextMatching.SplitChatMessages(await _ocr.ReadLinesAsync(forOcr ?? bmp))
                        .Select(TextMatching.StripNoise)                    // drop animated-emoji artifacts
                        .Where(l => TextMatching.LooksLikeText(l, minLetters)).ToList();
                    if (ct.IsCancellationRequested) break;

                    // Ask the de-dup filter which of these lines are genuinely new. It ignores
                    // emoji/colour flicker (compares on a letter-only signature) and only re-emits
                    // a message after it has really scrolled off screen for a while. The Sensitivity
                    // slider tunes "same message" strictness; Stability tunes the confirmation frame.
                    var confirmed = _dedup.Next(lines, SensitivityThreshold(), StabilityThreshold());

                    // Only a tick that actually TRANSLATED clears the back-off (E5.S1 AC 4) or the
                    // failure streak (E5.S2 / §9.2). An empty tick asked the providers nothing, so it
                    // is no evidence that they are back — one distinction, named once here and read
                    // by both counters below, because two notions of "a good tick" is how the line
                    // this story deleted became a bug in the first place.
                    // A drain that actually re-translated something is a tick that TRANSLATED: it
                    // asked the providers and they answered, which is exactly the evidence both
                    // counters are looking for. A drain with nothing to do says nothing at all, like
                    // any other empty tick.
                    var outcome = drained ? LiveTickOutcome.Translated : LiveTickOutcome.Empty;

                    if (confirmed.Count > 0)
                    {
                        var target = SelectedTag(OcrTargetCombo) ?? "en";
                        SetScreenStatus($"🔴 Live — {confirmed.Count} new line(s), translating…");
                        await AppendLinesToHistory(confirmed, target, ct);
                        if (ct.IsCancellationRequested) break;
                        outcome = LiveTickOutcome.Translated;   // AppendLinesToHistory throws on failure
                        SetScreenStatus($"🔴 Live — {_ocrItems.Count} message(s) so far (check #{_liveTicks}).");
                    }
                    else
                    {
                        // Reassure the user it's really working even before the first message
                        // (a calm chat can be silent for minutes) — and show it's reading text.
                        SetScreenStatus(_ocrItems.Count == 0
                            ? $"🔴 Live — watching (check #{_liveTicks}, sees {lines.Count} line(s), waiting for new text)…"
                            : $"🔴 Live — {_ocrItems.Count} message(s) so far (check #{_liveTicks}).");
                    }
                    // ONE outcome, both counters. `consecutiveErrors = 0` used to sit here (and,
                    // before E5.S1, at the end of the whole try) and ran on every non-throwing tick
                    // — empty ones included — so a calm chat forgave a real failure streak between
                    // two failures and the auto-stop never reached two: the app trickled failing
                    // requests all evening (analyse… A2, S4c). It is deleted, not moved: the tracker
                    // resets on Translated and leaves an Empty tick exactly where it was, which is
                    // §9.2's table and the same distinction backoffSteps already made.
                    //
                    // The verdict is discarded HERE on purpose: neither arm this line can reach can
                    // ask for a stop (Translated clears the streak, Empty leaves it, and the catch
                    // below breaks the loop the moment it reaches five). The auto-stop is a decision
                    // about failures, and this branch had none. Discarded EXPLICITLY, so that a
                    // later edit which lets a non-throwing tick count as one has to look at this
                    // line: the invariant is held by two distant pieces of code, not by the type.
                    _ = errors.Record(outcome);
                    backoffSteps = LiveTickPolicy.NextBackoffSteps(backoffSteps, outcome);
                }
            }
            // Only a genuine Stop (ct cancelled) breaks out cleanly. A translator TIMEOUT also
            // arrives as an OperationCanceledException (TaskCanceledException) but with ct NOT
            // cancelled — if we broke on that too, the loop would exit without StopLive()/
            // SetLiveUi(false), leaving the indicator stuck on "LIVE" with no loop running.
            // Let timeout-OCEs fall through to the generic handler below, which counts consecutive
            // errors, retries, and auto-stops with proper UI cleanup.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;

                // What kind of failure this was is LiveTickPolicy.Classify's to say (ruling E5-c):
                // a refusal that cost no request — AllProvidersPaused, or a NotSent refusal from
                // inside a tier — is a pause wearing an exception's clothes and may not feed the
                // auto-stop, or an app that was correctly waiting would stop itself (R-02/R6).
                // Everything else was a request that left the machine and failed, a timeout very
                // much included: it is counted here, and the filtered catch above is what keeps a
                // genuine Stop out of this handler (I3).
                if (errors.Record(LiveTickPolicy.Classify(ex)))
                {
                    Services.Logging.Error(
                        $"Live translation auto-stopped — {errors.ConsecutiveFailures} failed reads in a row " +
                        "since the last translated line (refusals and pauses do not count)", ex);
                    StopLive();   // this sets "Live stopped." first…
                    // …then the real reason, with the count and the way back in it (amendment A6):
                    // "repeated" was the app declining to say how many, and ▶ was nowhere in the
                    // sentence. ConsecutiveFailures is E5-d's five REALLY-SENT failures.
                    SetScreenStatus(UserMessages.LiveAutoStopped(errors.ConsecutiveFailures, Friendly(ex)));
                    break;
                }
                // Ruling E5-f (E5.S3). A tick REFUSED from inside itself cost no request, and until
                // this line nothing bounded a streak of them: the loop kept capturing and OCR-ing a
                // full frame every ~700 ms for as long as the refusal lasted — the very cost OQ-B's
                // full pause removes, reached through the branch that throws instead of the one that
                // skips. It now advances the same back-off curve a skipped tick does, and waits it.
                // The wait is computed from the CURRENT step count and the counter advances after
                // it, exactly as the skipped branch does, so the first refused tick still waits the
                // plain interval. It remains no error at all (E5-c): the tracker above has already
                // seen it and left the streak where it was — backing off is not the same as blaming.
                var outcome = LiveTickPolicy.Classify(ex);
                if (outcome == LiveTickOutcome.Refused)
                    pausedWait = LiveTickPolicy.BackoffWaitMs(CurrentLiveIntervalMs(), backoffSteps);
                backoffSteps = LiveTickPolicy.NextBackoffSteps(backoffSteps, outcome);

                // §3.2's own row, and the reason it no longer carries the failure's sentence: a
                // hiccup the loop is already retrying is a STATE, and §1's first principle is one
                // message per state. The engine that failed is named on the line the player reads
                // when the loop STOPS — not on one the next tick paints over in 700 ms.
                SetScreenStatus(UserMessages.LiveOneReadFailed());
            }

            // A SKIPPED tick waits the whole back-off (§9.1): there is no read+translate time to
            // subtract, and the point of the doubling is that a long outage costs a wake-up every
            // 5 s instead of one every 700 ms. A tick that DID work keeps the old steady cadence.
            int wait = pausedWait
                ?? Math.Max(150, CurrentLiveIntervalMs() - (int)sw.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>The status a SKIPPED tick shows on the MAIN WINDOW, with §2.4's countdown —
    /// <paramref name="secondsLeft"/> is null when there is nothing honest to count down to
    /// (see <see cref="LiveTickPolicy.CountdownSeconds"/>), and the sentence then simply drops the
    /// number rather than inventing one.
    ///
    /// <para><b>Three forms, and all three are §3.2's own rows</b> (E7.S2). The third exists
    /// because §2.4's floor renders a <i>clause</i> and not a duration: "next try in about to
    /// retry" is not a sentence, so under five seconds the deck replaces the whole line rather than
    /// substituting into it. E7.S1 owns the wording; this story owns only which of the deck's rows
    /// a given number selects.</para>
    ///
    /// <para>Static and pure so the copy can be asserted headlessly, exactly like
    /// <see cref="LiveIntervalMs"/>. It is written here rather than in <c>UserMessages</c> because
    /// that table is keyed by <c>TranslationErrorKind</c> and this is a LIVE <i>status</i>, not a
    /// failure — and because a countdown is formatting, which stays out of
    /// <c>Services/</c>.</para></summary>
    /// <param name="noNetwork">§2.1's <b>S6</b> rather than S5 — every rung is inside a window the
    /// gate recorded for <c>Network</c>, so nothing resolves (<c>ChainPause.NoNetwork</c>, E7.S4).
    /// It takes the whole line and not a clause: there is no honest countdown to a cable, and
    /// ruling <b>GAP-3</b> made S6 the same full pause as S5 in every other respect.</param>
    internal static string LivePausedStatus(int? secondsLeft, bool noNetwork = false)
        => noNetwork ? UserMessages.LivePausedNoNetwork() : CountdownText(secondsLeft) switch
        {
            null            => UserMessages.LivePausedNoCountdown(),
            AboutToRetry    => UserMessages.LivePausedAboutToRetry(),
            var t           => UserMessages.LivePausedNextTry(t),
        };

    /// <summary>§3.2's overlay column for the same three rows, in the compact window's <b>40
    /// character</b> budget. The overlay has one status line and the chip is a prefix of it, so
    /// there is structurally one clock on that window (AC 4) — and the long form would wrap it.
    ///
    /// <para><c>MainWindow</c> formats and <c>CompactOverlay</c> renders (I2, and the epic's
    /// technical note): the overlay is handed the finished string through its existing
    /// <c>SetStatus</c> / <c>SetStatusIfChanged</c> and never learns what a countdown is.</para></summary>
    /// <param name="noNetwork">S6's own row, in the same 40 characters (E7.S4).</param>
    internal static string LivePausedOverlayStatus(int? secondsLeft, bool noNetwork = false)
        => noNetwork ? UserMessages.LivePausedOverlayNoNetwork() : CountdownText(secondsLeft) switch
        {
            null            => UserMessages.LivePausedOverlayNoCountdown(),
            AboutToRetry    => UserMessages.LivePausedOverlayAboutToRetry(),
            var t           => UserMessages.LivePausedOverlayNextTry(t),
        };

    /// <summary>§2.4's floor, as a name rather than as a literal in four places: under five seconds
    /// a countdown stops counting and says what is about to happen instead, because "0:03 · 0:02 ·
    /// 0:01" is a promise the gate's own clock is under no obligation to keep. It is a CLAUSE,
    /// which is why the two statuses above fork on it rather than substitute it.</summary>
    internal const string AboutToRetry = "about to retry";

    /// <summary>The last second that still counts down as a stopwatch (§2.4: "≤ 90 s → m:ss").
    /// Ninety and not sixty, so a minute-and-a-half wait is watched rather than rounded to a "2 min"
    /// that is a third longer than the truth.</summary>
    private const int ClockBandSeconds = 90;

    /// <summary>§2.4's <i>display</i> cap. <b>Not</b> <see cref="LiveTickPolicy.MaxCountdownSeconds"/>
    /// (3600), which is the <i>honesty</i> guard: beyond an hour there is no number at all and the
    /// sentence drops it (that is what answers the <see cref="DateTimeOffset.MaxValue"/> sentinel a
    /// gate stores for <c>AuthFailed</c>). Two different rules, and both stay.</summary>
    private const int DisplayCapSeconds = 30 * 60;

    /// <summary>The <c>{t}</c> of every paused sentence, in §2.4's four bands (amendment A9):
    /// <c>about to retry</c> under five seconds · <c>0:58</c> to ninety · <c>about 4 min</c> above
    /// it, rounded UP inside the band so the number never promises the gate will reopen sooner than
    /// it will · and <c>about 30 min</c> at the display cap. Null in, null out, for the case where
    /// there is nothing honest to count down to.
    ///
    /// <para><b>The cap says "more than 30 min", and that is ruling E7-a.</b> E7.S2 shipped
    /// <c>about 30 min</c> there and flagged the collision behind it:
    /// <c>TranslationPolicy.QuotaOpenMinutes</c> is <b>60</b> while the display cap is 30, and
    /// <see cref="LiveTickPolicy.CountdownSeconds"/> only drops the number above an hour — so the
    /// first half of a quota block rendered a frozen "about" for a wait that was really up to twice
    /// it, which is the one direction §2.4's "rounded UP" promise may not break. "More than" is the
    /// smallest edit that makes the cap true again: it is still the cap, it is still frozen, and it
    /// no longer claims the wait is nearly over.</para>
    ///
    /// <para><b>Culture-invariant on purpose.</b> The <c>:</c> of a locale-aware time format is the
    /// culture's <c>TimeSeparator</c> — on a Russian-language Windows that is a real defect, and it
    /// is the same reason E2.S6's review made the gate log invariant. (Not the digits: .NET ignores
    /// <c>NativeDigits</c> when formatting an integer, so the separator is the whole of the risk —
    /// corrected at review, where the test that pins this said otherwise.)</para>
    ///
    /// <para>Shared by the LIVE status above, by read-once (E5.S4), by the About tab's key test
    /// (E6.S5) and by E7.S3's chip, so none of them can come to disagree about what a countdown
    /// looks like. It stays in the code-behind for the reason <c>UserMessages</c> states: formatting
    /// a time is not <c>Services/</c>' job (I2) — counting the seconds is, and
    /// <see cref="LiveTickPolicy.CountdownSeconds"/> is where that happens.</para></summary>
    internal static string? CountdownText(int? seconds)
    {
        if (seconds is not { } s) return null;
        if (s < 5) return AboutToRetry;
        if (s <= ClockBandSeconds)
            return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                   (s % 60).ToString("00", CultureInfo.InvariantCulture);

        return Minutes(s);
    }

    /// <summary>§2.4's minute band, shared by <see cref="CountdownText"/> and
    /// <see cref="CountdownJoinText"/> so the two cannot come to round differently. Rounded UP, so
    /// the number never promises the gate will reopen sooner than it will.
    ///
    /// <para>Ruling <b>E7-a</b> at the cap: there the number is not an approximation of the wait,
    /// it is a FLOOR under it, and the sentence says so. <c>QuotaOpenMinutes</c> is 60 against a
    /// 30-minute display cap, so "about 30 min" was rounding a possible hour DOWN — the one
    /// direction "rounded up" may not break.</para></summary>
    private static string Minutes(int seconds)
    {
        // Clamped BEFORE the ceiling arithmetic: `int.MaxValue + 59` overflows to a negative, and a
        // negative minute count would sail past the cap Math.Min is there to apply.
        int capped = Math.Min(seconds, DisplayCapSeconds);
        return (seconds >= DisplayCapSeconds ? "more than " : "about ")
             + ((capped + 59) / 60).ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>The <c>{t}</c> of a sentence that is written <b>once and never ticks</b> — the
    /// read-once summary, the About tab's key test, the deck's own <c>{t}</c>-bearing rows on the
    /// Translator tab and in the overlay. It is <see cref="CountdownText"/>'s COARSE band, and that
    /// is ruling <b>E7-a</b>: a frozen "0:05" on a line nobody repaints reads as a live clock, so
    /// under a minute there is no number at all and amendment <b>A12</b>'s substitution ("in {t}" →
    /// "shortly", "for {t}" → "briefly") does the talking. Only the 1 Hz LIVE status lines, which
    /// really are repainted every second, use <c>m:ss</c>.
    ///
    /// <para>It also answers the grammar problem it was written for (E7.S2): under §2.4's floor
    /// <see cref="CountdownText"/> renders a clause, and "Try again in about to retry." is not
    /// English. Both cases now take the same exit, because they are the same case — there is no
    /// duration worth joining.</para>
    ///
    /// <para>Not a second band table: the minute arithmetic is <see cref="Minutes"/>, which
    /// <see cref="CountdownText"/> calls for the same band, so the two cannot come to round
    /// differently. What differs is only where each stops having something worth writing — the
    /// ticking line at <see cref="ClockBandSeconds"/>, this one at
    /// <see cref="CoarseFloorSeconds"/>.</para></summary>
    internal static string? CountdownJoinText(int? seconds)
        => seconds is { } s && s >= CoarseFloorSeconds ? Minutes(s) : null;

    /// <summary>Ruling E7-a's floor: under a minute a non-ticking sentence shows no number at all
    /// and A12's clause takes over. It is <b>not</b> <see cref="ClockBandSeconds"/> — that is where
    /// the stopwatch stops being a stopwatch, and this is where a written-once sentence stops
    /// having anything worth writing.</summary>
    private const int CoarseFloorSeconds = 60;

    /// <summary>Add placeholder items, translate the batch (one request when possible),
    /// keep the last MaxHistory, and auto-scroll. Respects the live cancellation token.</summary>
    private async Task AppendLinesToHistory(List<string> newLines, string target, CancellationToken ct)
    {
        // Keep each speaker's nickname out of the translation — translate only the message body,
        // then re-attach "Nick: " to the result (a name is a proper noun, not something to translate).
        // SplitSpeakerStrict avoids stealing a body's leading "word:" (e.g. slang "тс:") as a nick.
        var parts = newLines.Select(TextMatching.SplitSpeakerStrict).ToList();
        var items = new List<OcrResultItem>();
        for (int i = 0; i < newLines.Count; i++)
        {
            var item = new OcrResultItem
            {
                Speaker = parts[i].Speaker,
                OriginalBody = parts[i].Body,
                TranslationBody = "…",
                Glossary = _slang.Decode(newLines[i]),
            };
            _ocrItems.Add(item);
            items.Add(item);
            while (_ocrItems.Count > MaxHistory) _ocrItems.RemoveAt(0);   // drop the oldest
        }
        ResultsScroller?.ScrollToEnd();

        List<string> translations;
        try
        {
            translations = await TranslateBodiesAsync(parts.Select(p => p.Body).ToList(), target,
                                                     _readTranslator, ct);
        }
        catch (Exception ex)
        {
            // ---- E5.S3 / §9.3, AC 2: a failure that could answer differently later is PENDING ----
            // These rows used to be burned for the session — the placeholder went terminal, LiveDedup
            // had already marked the line emitted, and a thirty-second blip cost the player every
            // message that arrived inside it. They now keep their "…", which is deliberately NOT
            // "("-prefixed so it reads as pending rather than as a failure, and wait on the queue for
            // the first tick that is not skipped.
            //
            // The RAW body is what is stored (I6): TranslateBodiesAsync runs SlangGlossary.Expand
            // itself, so queueing the expanded text would expand it twice on retry. parts[i] is
            // exactly what this call was given, and items[i] is the row it was given for — the two
            // lists are built together above and stay index-parallel.
            //
            // A genuine Stop DOES reach this catch (review): it is generic and it is inside the call,
            // so it runs long before the loop's filtered one — read-once has a filtered catch of its
            // own for exactly this, and this method has never had one. What keeps a cancelled row out
            // of the queue is therefore IsRetryable and not the shape of the catch: an
            // OperationCanceledException is not a TranslationException, so it is not retryable, and
            // the rows fall to the terminal branch below as they always did. The same test is where
            // every failure a retry cannot help is written down, once.
            if (PendingRetryQueue<OcrResultItem>.IsRetryable(ex))
                for (int i = 0; i < items.Count; i++)
                {
                    items[i].TranslationBody = UserMessages.PendingRetryRow();
                    EnqueueForRetry(items[i], parts[i].Body, target);
                }
            // Don't leave the placeholders stuck on "…" forever (e.g. a body we could not read):
            // mark them, then let the loop's error handling show the reason on the STATUS LINE.
            //
            // Amendment A5 — a row carries no §3.1 sentence, no provider name and no countdown, and
            // three row texts exist in the whole app. This used to stamp "({Friendly(ex)})", which
            // put the whole failure sentence on every row of the batch; the status line above them
            // was already saying it, once, which is §1's first principle.
            //
            // And the cancel arm is E1.S6's recorded finding (E7.S1 owns it): a user Stop landing
            // mid-batch reached this branch — an OperationCanceledException is not retryable — and
            // painted a timeout sentence over rows the player had abandoned, in the CLR's own
            // localised wording on a non-English Windows. §2.1 says a cancel must render nothing at
            // all; a row cannot render nothing (it would sit on "…" for ever with nothing coming),
            // so it renders the one thing that is true — the player stopped this.
            else if (ct.IsCancellationRequested)
                foreach (var it in items) it.TranslationBody = $"({UserMessages.ReadCancelledRow()})";
            else
                // Through GiveUpRow, so "given up" is written in exactly one place and cannot come
                // to mean two things in two of the branches that reach it (TP-LIVE-12's pin).
                foreach (var it in items) GiveUpRow(it);
            // Still rethrown, and that is load-bearing: E5.S2's tracker owes the counter its
            // increment and the status line its sentence, whichever branch above ran.
            throw;
        }
        if (ct.IsCancellationRequested) return;
        for (int i = 0; i < items.Count && i < translations.Count; i++)
            items[i].TranslationBody = translations[i];
        ResultsScroller?.ScrollToEnd();
    }

    /// <summary>
    /// <b>§9.3's drain — the rows that failed during a blip, re-translated IN PLACE.</b> Called at the
    /// top of every tick that is not skipped, before the capture; answers whether it actually
    /// translated anything, which the caller reads as the tick's outcome.
    ///
    /// <para><b>In place, and that is the story.</b> Each entry holds the <c>OcrResultItem</c> itself,
    /// so the drain assigns <c>Row.TranslationBody</c> and stops. <c>OcrResultItem</c> raises
    /// <c>PropertyChanged</c> for <c>TranslationBody</c> AND for the composed <c>Translation</c>, so
    /// both feeds' two-tone lines (<c>MainWindow.xaml:340</c>, <c>CompactOverlay.xaml:80</c>, which
    /// bind the body) and everything reading the composed line repaint where the row already is —
    /// for free, through bindings that already exist and are already <c>Mode=OneWay</c> (I15: this
    /// story adds no <c>Run.Text</c>; the retry badge is E7.S6's, with its own render case). Nothing
    /// here calls <c>_ocrItems.Add</c>, <c>Insert</c> or a re-sort: an <c>Add</c> would raise
    /// <c>CollectionChanged</c>, scroll both feeds to the bottom and reorder what the player is
    /// reading — and it is how the one failure this epic calls worse than the outage (a duplicated
    /// row) would arrive. The 🔑 glossary line is untouched for the same reason: it was set once at
    /// creation, so "keeping their 🔑 line" is satisfied by not writing it.</para>
    ///
    /// <para><b>The queue survives a throw, and every entry it handed over ends somewhere.</b>
    /// <c>TakeAll</c> empties the queue before the first request, so from that moment this method
    /// owes each entry an ending on EVERY exit: translated, back on the queue, or given up. The
    /// <c>catch</c> puts the failing batch back and the batches it never reached too — without that
    /// last part, one exception silently discarded every row queued for a target the player had
    /// switched away from, and turned the story into a no-op that passes its own happy-path
    /// test.</para>
    ///
    /// <para><b>I3.</b> The <c>await</c> below is inside the loop's <c>try</c>, so a translator
    /// TIMEOUT arrives here as a <c>TaskCanceledException</c> with the token NOT cancelled — and it
    /// must reach the counted handler as the failure it is. There is deliberately no unfiltered
    /// <c>OperationCanceledException</c> catch on this path; the generic <c>catch</c> does see a
    /// genuine Stop, and treats it as what it is — a row nothing is coming for any more, given up
    /// exactly as <c>StopLive</c> gives up the ones still on the queue — before rethrowing into the
    /// loop's filtered catch, which is what actually ends the loop.</para></summary>
    private async Task<bool> DrainPendingRetryAsync(CancellationToken ct)
    {
        if (_pendingRetry.IsEmpty) return false;

        // Membership is checked HERE and not at enqueue time: a row is evicted from the feed by the
        // MaxHistory trim (this file's, and read-once's) long after it was queued, and an evicted row
        // must never cost a request — nobody can see it. Contains on an ObservableCollection of a
        // class with no Equals override is reference equality, which is the comparison meant.
        var due = _pendingRetry.TakeAll(_ocrItems.Contains);
        if (due.Count == 0) return false;

        bool translated = false;
        // One batch per distinct target, in queue order — the queue can hold rows from before the
        // player changed the target combo, and a batch is per target by construction.
        //
        // Materialised as a LIST of groups rather than iterated lazily (review), because TakeAll has
        // already emptied the queue: every entry it handed over must end somewhere on every exit
        // from this method — translated, back on the queue, or given up — and a group the drain
        // never reaches is only reachable if it is still addressable. Iterating the GroupBy directly
        // lost the groups after the one that threw: a player who changed the target combo during the
        // outage had those rows silently dropped, off the queue and stuck on "…" for the session,
        // which is the "drain that quietly does nothing" the story names as its second risk.
        var groups = due.GroupBy(e => e.Target, StringComparer.Ordinal)
                        .Select(g => g.ToList()).ToList();
        for (int g = 0; g < groups.Count; g++)
        {
            var entries = groups[g];
            try
            {
                var translations = await TranslateBodiesAsync(entries.Select(e => e.Body).ToList(),
                                                              entries[0].Target, _readTranslator, ct);
                for (int i = 0; i < entries.Count && i < translations.Count; i++)
                    entries[i].Row.TranslationBody = translations[i];   // IN PLACE — never an Add
                translated = true;
            }
            catch (Exception ex)
            {
                // AC 5: back on the queue with one more attempt spent, or terminal once there is no
                // attempt left. RequeueOrGiveUp is what makes a row that never comes back eventually
                // say so rather than sit on "…" for the rest of the session.
                foreach (var entry in entries) RequeueOrGiveUp(entry, ex);
                // The groups this drain never got to keep their attempt count: they were not tried,
                // and charging one target's batch for another target's failure is how a row gives up
                // without ever having been asked about. On a Stop there is nothing to go back TO —
                // StopLive has already cleared the queue these entries are no longer on — so they
                // are given up instead, exactly as the ones Stop did find were.
                for (int rest = g + 1; rest < groups.Count; rest++)
                    foreach (var entry in groups[rest])
                    {
                        if (ct.IsCancellationRequested) GiveUpRow(entry.Row);
                        else EnqueueForRetry(entry.Row, entry.Body, entry.Target, entry.Attempts);
                    }
                throw;   // the loop's catch owns the counter and the status line
            }
            // A cancel observed HERE is the player's ■ and never an HttpClient timeout: this is our
            // own token, and a timeout arrives as an exception, in the catch above (I3). The rows
            // just written keep their translation — the answer was paid for and writing it in place
            // costs nothing — and whatever is left has nothing coming for it, so it says so.
            if (ct.IsCancellationRequested)
            {
                for (int rest = g + 1; rest < groups.Count; rest++)
                    foreach (var entry in groups[rest]) GiveUpRow(entry.Row);
                return translated;
            }
        }
        return translated;
    }

    /// <summary>Put a row on the pending-retry queue — and say something to whatever the bound
    /// pushed off the front, if the player can still see it.
    ///
    /// <para>The queue is bounded at the feed's own <c>MaxHistory</c> so the two normally forget the
    /// same row on the same tick and this loop does nothing. They are still two lists trimmed by
    /// three different call sites, though, and a read-once landing between a tick's append and its
    /// failure is enough to put their order out of step — at which point the entry that falls off
    /// the front belongs to a row that is still on screen. Every exit from the queue owes a visible
    /// row an answer (review); this is the one the queue itself cannot give, because only the window
    /// knows what is on screen.</para></summary>
    private void EnqueueForRetry(OcrResultItem row, string body, string target, int attempts = 0)
    {
        foreach (var dropped in _pendingRetry.Enqueue(row, body, target, attempts))
            if (_ocrItems.Contains(dropped.Row)) GiveUpRow(dropped.Row);
    }

    /// <summary><c>ux-mode-degrade.md</c> §2.2's <b>given-up</b> row — the ONLY one of the two forms
    /// that is parenthesised, which is the whole of AC 2's distinction, and I4's marker so nothing
    /// downstream mistakes it for a translation.
    ///
    /// <para>Named once and called from every place a row leaves the queue with nothing left that
    /// could fill it in: its attempts spent, a failure a retry cannot help, ■ Stop, a cancel
    /// mid-drain, or the bound dropping it while it is still on screen. One sentence for all of them
    /// on purpose — the row's job is not to explain WHY (the status line did that while it was
    /// pending, and E7.S1 owns the final copy) but to stop implying that something is still
    /// coming.</para></summary>
    private static void GiveUpRow(OcrResultItem row)
        => row.TranslationBody = $"({UserMessages.RetryGaveUpRow()})";

    /// <summary>One entry after a drain that failed: back on the queue if it has an attempt left, and
    /// otherwise the terminal <c>(…)</c> row of <c>ux-mode-degrade.md</c> §2.2 — the ONLY one of the
    /// two forms that is parenthesised, which is the whole of AC 2's distinction.
    ///
    /// <para>A row is also given up when the failure is one a retry cannot help
    /// (<see cref="PendingRetryQueue{TRow}.IsRetryable"/>): the outage the row was waiting out has
    /// been replaced by something else, and continuing to wait would be the pending row that never
    /// resolves — amplifier A7, which is what makes a player press the button again.</para>
    ///
    /// <para><b>An attempt is spent only by a drain that REACHED a provider</b> (review, ruling
    /// E5-c's principle applied to the queue: backing off is not the same as blaming). A drain
    /// refused at the gate sent nothing — the row was never actually asked about — and the drain runs
    /// on every non-skipped tick, so charging refusals would spend both of a row's attempts inside
    /// two ticks (≈1.4 s) and hand it the given-up sentence in the first second of the very outage
    /// this queue exists to survive. With this rule a row is charged once per window in which a
    /// request really left the machine, which is what "retried after recovery" means. It cannot
    /// spin: while every rung is blocked the tick is SKIPPED and no drain runs at all (E5.S1), a
    /// refusal that reaches a drain is by definition a gate that is not yet, or no longer, shut —
    /// and ■ Stop gives every row still waiting its terminal sentence.
    /// <see cref="LiveTickPolicy.Classify"/> is the app's one definition of "this cost a request",
    /// the same one the tick's own outcome is read from, so the queue and the counters cannot come to
    /// disagree about what a refusal is.</para></summary>
    private void RequeueOrGiveUp(PendingRetryEntry<OcrResultItem> entry, Exception ex)
    {
        bool costARequest = LiveTickPolicy.Classify(ex) != LiveTickOutcome.Refused;

        if (!PendingRetryQueue<OcrResultItem>.IsRetryable(ex)
            || (costARequest && PendingRetryQueue<OcrResultItem>.GivesUpAfter(entry)))
            GiveUpRow(entry.Row);
        else
            EnqueueForRetry(entry.Row, entry.Body, entry.Target,
                            costARequest ? entry.Attempts + 1 : entry.Attempts);
    }

    /// <summary>Translate message bodies READ FROM THE SCREEN, picking the source language per
    /// message: real Russian (Cyrillic) is translated FROM "ru", but English/other-language messages
    /// are sent with "auto" so Google detects them instead of mangling plain English into invented
    /// Cyrillic. The two groups are still batched (one request each) and reassembled in the original
    /// order. Always goes through a READ chain — never DeepL, whose one-time free million characters
    /// a live loop would burn through in days; that absence is structural (I8) and lives in
    /// <c>TranslationChains.BuildRead</c>.
    ///
    /// <para><paramref name="reader"/> is which read chain: the LIVE loop hands in
    /// <c>_readTranslator</c> (whose providers ask the gate as <c>Background</c>, §5.4's reserve)
    /// and read-once hands in <c>_readOnceTranslator</c> (<c>Interactive</c> — a user is waiting on
    /// it, ruling OQ-a). Both chains resolve the same process-global gates, so the two paths still
    /// share one view of what each provider is doing.</para></summary>
    private async Task<List<string>> TranslateBodiesAsync(List<string> bodies, string target,
        ITranslator reader, CancellationToken ct)
    {
        // Expand known slang to its Russian long form BEFORE translating, so the machine
        // translation is meaningful (e.g. "нужен хил" → "нужен лекарь" → "need a healer").
        // Only the text SENT to the translator changes — the displayed original and the 🔑 decode
        // still use the raw line. Terms without a Full form pass through unchanged.
        var texts = bodies.Select(b => _slang.Expand(b)).ToList();

        var ru = new List<string>(); var ruIdx = new List<int>();
        var auto = new List<string>(); var autoIdx = new List<int>();
        for (int i = 0; i < texts.Count; i++)
        {
            if (TextMatching.IsProbablyRussian(texts[i])) { ru.Add(texts[i]); ruIdx.Add(i); }
            else { auto.Add(texts[i]); autoIdx.Add(i); }
        }

        var result = new string?[bodies.Count];
        if (ru.Count > 0)
        {
            var t = await reader.TranslateLinesAsync(ru, "ru", target, ct);
            for (int i = 0; i < ruIdx.Count && i < t.Count; i++) result[ruIdx[i]] = t[i];
        }
        if (auto.Count > 0)
        {
            var t = await reader.TranslateLinesAsync(auto, "auto", target, ct);
            for (int i = 0; i < autoIdx.Count && i < t.Count; i++) result[autoIdx[i]] = t[i];
        }
        // Any gap (shouldn't happen) falls back to the (expanded) text rather than a null.
        return result.Select((r, i) => r ?? texts[i]).ToList();
    }

    /// <summary>Map the sensitivity slider (0–100%) to the "same message" fuzzy threshold used
    /// by <see cref="LiveDedup"/>. It runs on letter/digit-only signatures (emoji/colour noise
    /// already stripped), so genuine repeats of a message are near-identical — the band stays
    /// high (0.80…0.95) so two *different* short chat lines are never merged (which would drop a
    /// real message). Higher sensitivity → stricter → smaller wording changes count as new text.</summary>
    private double SensitivityThreshold()
    {
        double sens = SensitivitySlider?.Value ?? 5;    // default matches the XAML slider
        return 0.80 + (sens / 100.0) * 0.15;            // 0.80 (forgiving) … 0.95 (very sensitive)
    }

    /// <summary>Smallest text fragment (in letters) that live mode will bother translating.</summary>
    private int MinFragmentLetters()
        => (int)Math.Round(MinFragmentSlider?.Value ?? 2);

    /// <summary>Map the stability slider (0–100%) to the frame-confirmation threshold. Higher
    /// = a newly-appeared line must match itself more closely across a frame to be accepted
    /// (fewer false positives from OCR noise, but slightly slower to confirm real text).</summary>
    private double StabilityThreshold()
    {
        double v = StabilitySlider?.Value ?? 60;        // default matches the XAML slider
        return 0.50 + (v / 100.0) * 0.45;               // 0.50 (loose) … 0.95 (strict), ≈0.77 at 60%
    }

    /// <summary>Live re-read interval from the speed slider (higher speed = shorter wait):
    /// 3.0s at 0% … 0.5s at 100%. Pure, so a test can pin the shipped default to the interval it
    /// is meant to produce — the two used to be able to drift apart silently.</summary>
    internal static int LiveIntervalMs(double speedPercent)
        => (int)(3000 - (speedPercent / 100.0) * 2500);

    private int CurrentLiveIntervalMs()
        => LiveIntervalMs(LiveSpeedSlider?.Value ?? new AppSettings().LiveSpeedPercent);
}

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
        // entry can only ever point at a row that is on the screen in front of the player.
        _pendingRetry.Clear();
        SetLiveUi(true);
        MainTabs.SelectedIndex = TabTranslator;
        SetScreenStatus("🔴 Live — watching the area. Translations appear when new text shows up.");

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
        _pendingRetry.Clear();

        if (_liveCts == null) return;
        _liveCts.Cancel();
        _liveCts.Dispose();
        _liveCts = null;
        _liveRegion = null;
        SetLiveUi(false);
        SetScreenStatus("Live stopped.");
    }

    /// <summary>Set the screen-reading status on the main window AND (if shown) the overlay, so the
    /// state is visible whichever window the user is looking at. Used by the live loop AND by
    /// read-once: in compact mode ScreenReadStatus lives on a hidden window, so writing only there
    /// left "Reading…" and every OCR error invisible to someone working from the overlay.</summary>
    private void SetScreenStatus(string msg)
    {
        ScreenReadStatus.Text = msg;
        _overlay?.SetStatus(msg);
    }

    private void SetLiveUi(bool on)
    {
        LiveIndicator.Text = "●  LIVE";
        LiveIndicator.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StopLiveButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LiveButton.Content = on ? "■  Stop live translation" : "▶  Start live translation";
        UpdateResumeLiveButton();
        LiveStatus.Text = on
            ? "🔴 Live is running — re-reading the area and re-translating whenever the text changes. Press Stop to end."
            : "Live mode keeps watching the chosen area and re-translates automatically whenever the text changes, until you press Stop.";
    }

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
                    // all of AC 1's "both surfaces". The countdown is COARSE by design in A.2: it is
                    // recomputed by the skipped tick itself, so it steps at most every 5 s and there
                    // is no new timer anywhere. E7.S2 replaces exactly this line with the 1 Hz poll,
                    // §2.4's granularity bands and the repaint guard.
                    //
                    // pause.Now, never DateTimeOffset.UtcNow: RetryAt was produced against the
                    // GATES' clock, and subtracting a different one is the two-clocks bug IS-6 and
                    // ProviderGate.Now() exist to prevent (review, E5.S1).
                    SetScreenStatus(LivePausedStatus(
                        LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now)));

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
                    LiveIndicator.Text = (_liveTicks % 2 == 0) ? "●  LIVE" : "○  LIVE";  // heartbeat

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
                    SetScreenStatus($"Live stopped after repeated errors ({Friendly(ex)}).");   // …then the real reason
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

                SetScreenStatus($"Live hiccup ({Friendly(ex)}) — retrying…");
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

    /// <summary>The status a SKIPPED tick shows on both surfaces, with A.2's coarse countdown —
    /// <paramref name="secondsLeft"/> is null when there is nothing honest to count down to
    /// (see <see cref="LiveTickPolicy.CountdownSeconds"/>), and the sentence then simply drops the
    /// number rather than inventing one.
    ///
    /// <para>Static and pure so the copy can be asserted headlessly, exactly like
    /// <see cref="LiveIntervalMs"/>. The wording is provisional in Sally's §3.2 shape; <b>E7.S2</b>
    /// owns the final form together with the 1 Hz timer and §2.4's granularity bands. It is written
    /// here rather than in <c>UserMessages</c> because that table is keyed by
    /// <c>TranslationErrorKind</c> and this is a LIVE <i>status</i>, not a failure — and because a
    /// countdown is formatting, which stays out of <c>Services/</c>.</para></summary>
    internal static string LivePausedStatus(int? secondsLeft)
    {
        if (secondsLeft is not { } s)
            return "○ Live — paused. It resumes on its own; nothing is lost.";
        var t = CountdownText(s)!;
        return $"○ Live — paused, next try in {t}. It resumes on its own; nothing is lost.";
    }

    /// <summary>The <c>{t}</c> of every paused sentence: seconds up to a minute, then whole minutes
    /// rounded up — "in 90 s" reads as a stopwatch, and somebody waiting out a 30-minute window
    /// wants the shape and not the precision. Null in, null out, for the case where there is
    /// nothing honest to count down to.
    ///
    /// <para>Shared by the LIVE status above and by read-once's (E5.S4) so the two cannot come to
    /// disagree about what a countdown looks like. It stays in the code-behind for the reason
    /// <c>UserMessages</c> states: formatting a time is not <c>Services/</c>' job (I2).</para></summary>
    internal static string? CountdownText(int? seconds)
        => seconds is not { } s ? null
           : s < 60 ? $"{s} s"
           : $"{(s + 59) / 60} min";

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
            // A genuine Stop never reaches here: it arrives as an OperationCanceledException with the
            // loop's token cancelled, and the LOOP's filtered catch takes it (I3). What does reach
            // here and must not be queued is every failure a retry cannot help — see
            // PendingRetryQueue.IsRetryable, which is where that list is written down once.
            if (PendingRetryQueue<OcrResultItem>.IsRetryable(ex))
                for (int i = 0; i < items.Count; i++)
                {
                    items[i].TranslationBody = UserMessages.PendingRetryRow();
                    _pendingRetry.Enqueue(items[i], parts[i].Body, target);
                }
            else
                // Don't leave the placeholders stuck on "…" forever (e.g. a body we could not read):
                // mark them, then let the loop's error handling show the reason.
                foreach (var it in items) it.TranslationBody = $"({Friendly(ex)})";
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
    /// <c>PropertyChanged</c> for <c>TranslationBody</c> AND for <c>Translation</c>, so the main
    /// feed's two-tone line and the overlay's single string both repaint where the row already is —
    /// for free, through bindings that already exist and are already <c>Mode=OneWay</c> (I15: this
    /// story adds no <c>Run.Text</c>; the retry badge is E7.S6's, with its own render case). Nothing
    /// here calls <c>_ocrItems.Add</c>, <c>Insert</c> or a re-sort: an <c>Add</c> would raise
    /// <c>CollectionChanged</c>, scroll both feeds to the bottom and reorder what the player is
    /// reading — and it is how the one failure this epic calls worse than the outage (a duplicated
    /// row) would arrive. The 🔑 glossary line is untouched for the same reason: it was set once at
    /// creation, so "keeping their 🔑 line" is satisfied by not writing it.</para>
    ///
    /// <para><b>The queue survives a throw.</b> Entries are taken off the queue before the request and
    /// put back in the <c>catch</c> before it rethrows — without that, one exception would silently
    /// discard up to fifty rows and turn the whole story into a no-op that passes its own happy-path
    /// test.</para>
    ///
    /// <para><b>I3.</b> The <c>await</c> below is inside the loop's <c>try</c>, so a translator
    /// TIMEOUT arrives here as a <c>TaskCanceledException</c> with the token NOT cancelled. There is
    /// deliberately no unfiltered <c>OperationCanceledException</c> catch anywhere on this path: the
    /// loop's own filtered one takes the genuine Stop, and everything else must reach the counted
    /// handler.</para></summary>
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
        foreach (var group in due.GroupBy(e => e.Target, StringComparer.Ordinal))
        {
            var entries = group.ToList();
            try
            {
                var translations = await TranslateBodiesAsync(entries.Select(e => e.Body).ToList(),
                                                              group.Key, _readTranslator, ct);
                if (ct.IsCancellationRequested) return translated;
                for (int i = 0; i < entries.Count && i < translations.Count; i++)
                    entries[i].Row.TranslationBody = translations[i];   // IN PLACE — never an Add
                translated = true;
            }
            catch (Exception ex)
            {
                // AC 5: back on the queue with one more attempt spent, or terminal once there is no
                // attempt left. Requeue() is what makes a row that never comes back eventually say so
                // rather than sit on "…" for the rest of the session.
                foreach (var entry in entries) RequeueOrGiveUp(entry, ex);
                throw;   // the loop's catch owns the counter and the status line
            }
        }
        return translated;
    }

    /// <summary>One entry after a drain that failed: back on the queue if it has an attempt left, and
    /// otherwise the terminal <c>(…)</c> row of <c>ux-mode-degrade.md</c> §2.2 — the ONLY one of the
    /// two forms that is parenthesised, which is the whole of AC 2's distinction.
    ///
    /// <para>A row is also given up when the failure is one a retry cannot help
    /// (<see cref="PendingRetryQueue{TRow}.IsRetryable"/>): the outage the row was waiting out has
    /// been replaced by something else, and continuing to wait would be the pending row that never
    /// resolves — amplifier A7, which is what makes a player press the button again.</para></summary>
    private void RequeueOrGiveUp(PendingRetryEntry<OcrResultItem> entry, Exception ex)
    {
        if (PendingRetryQueue<OcrResultItem>.GivesUpAfter(entry)
            || !PendingRetryQueue<OcrResultItem>.IsRetryable(ex))
            entry.Row.TranslationBody = $"({UserMessages.RetryGaveUpRow()})";
        else
            _pendingRetry.Enqueue(entry.Row, entry.Body, entry.Target, entry.Attempts + 1);
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

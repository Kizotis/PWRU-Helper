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
    // ============================================================
    //  SCREEN OCR + TRANSLATE
    // ============================================================
    private bool IsOcrReady()
        => _ocr.IsAvailable && (_ocr.ActiveLanguage?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Bring the user to the Screen OCR tab and explain, THERE, that the Russian pack is
    /// what's missing. The explanation used to be written into ScreenReadStatus — which lives on the
    /// Translator tab, i.e. the one we just navigated them away from, so the only thing they ever saw
    /// was a toast that vanished. LiveStatus is on the tab they actually land on.</summary>
    private void ShowOcrPackNeeded(string whatToDoNext)
    {
        if (_overlay is { IsVisible: true }) ExitCompactMode(); else BringToFront();
        MainTabs.SelectedIndex = TabScreenOcr;
        LiveStatus.Text = "⚠ The Russian OCR pack isn't installed, so nothing can be read at all. " +
                          "Install it with the \"Install Russian OCR\" button on this tab — " + whatToDoNext;
        ShowToast("Russian OCR pack needed — install it on this tab (1 click).");
    }

    private bool CheckOcrAvailability()
    {
        var lang = _ocr.ActiveLanguage;
        bool ready = _ocr.IsAvailable && lang != null &&
                     lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

        if (ready)
        {
            OcrLangStatus.Text = "Russian OCR language pack: installed and ready ✓";
            OcrLangStatus.SetResourceReference(TextBlock.ForegroundProperty, "TealBrush");
            InstallOcrButton.Content = "Reinstall / repair Russian OCR";
        }
        else
        {
            // Not "won't read well" — without it nothing is read AT ALL. The app no longer falls back
            // to the Windows engine, because a Latin engine reads Cyrillic as confident gibberish.
            OcrLangStatus.Text = "Russian OCR language pack: NOT INSTALLED — screen reading is off until it is.";
            OcrLangStatus.SetResourceReference(TextBlock.ForegroundProperty, "GoldBrush");
            InstallOcrButton.Content = "Install Russian OCR (1 click)";
        }
        return ready;
    }

    private async void InstallOcr_Click(object sender, RoutedEventArgs e)
    {
        InstallOcrButton.IsEnabled = false;
        // Untick "Always on top" while installing so the Windows admin (UAC) prompt
        // can't hide behind our window.
        bool wasTopmost = Topmost;
        Topmost = false;
        OcrLangStatus.Text = "⏳ A Windows admin prompt is opening — click \"Yes\". " +
                             "If you don't see it, check the taskbar or Alt+Tab. Then wait a few minutes…";
        try
        {
            // Run the elevation on a background thread. Process.Start() with the
            // "runas" verb blocks until the UAC prompt is answered, so doing it on
            // the UI thread freezes the whole window (looks like it's stuck).
            int exitCode = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " +
                                $"\"Add-WindowsCapability -Online -Name '{OcrCapability}'\"",
                    Verb = "runas",              // triggers the UAC elevation prompt
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                if (proc == null) return -1;
                proc.WaitForExit();
                return proc.ExitCode;
            });

            // Rebuild the engine and re-check.
            _ocr = new OcrService("ru");
            if (CheckOcrAvailability())
                OcrLangStatus.Text = "Russian OCR installed ✓ — you're ready to read the screen.";
            else if (exitCode != 0)
                OcrLangStatus.Text = $"The install command finished with an error (code {exitCode}). " +
                                     "Make sure you're online, then try again — or run the command below in " +
                                     "an admin PowerShell.";
            else
                OcrLangStatus.Text = "Install finished, but Russian OCR still isn't detected. " +
                                     "Try restarting the app (or Windows) and check again.";
        }
        catch (Win32Exception w32) when (w32.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED: the user clicked "No" / closed the UAC prompt.
            OcrLangStatus.Text = "You didn't accept the Windows admin prompt, so nothing was installed. " +
                                 "Click the button again and choose \"Yes\".";
        }
        catch (Exception ex)
        {
            OcrLangStatus.Text = $"Install couldn't run ({ex.Message}). " +
                                 "You can also run the command below manually in an admin PowerShell.";
        }
        finally
        {
            Topmost = wasTopmost;
            InstallOcrButton.IsEnabled = true;
        }
    }

    private async void CopyOcrCommand_Click(object sender, RoutedEventArgs e)
    {
        if (await CopyToClipboardAsync(OcrCommandBox.Text)) ShowToast("Command copied");
    }

    /// <summary>Load the RU chat-slang glossary. Prefers an editable copy (next to the exe,
    /// or %AppData%) so the user can extend it, falling back to the embedded copy. Fully
    /// best-effort — any failure just leaves an empty glossary (nothing gets decoded).</summary>
    private void LoadSlang()
    {
        try
        {
            var embedded = ReadEmbeddedJson("slang.json");
            var path = FindOrCreateEditable("slang.json", embedded, out var backup);
            if (backup != null) NoteDataFileRefreshed("slang.json", backup);
            string? json = embedded;
            if (path != null)
                try { json = File.ReadAllText(path); } catch { /* keep embedded */ }
            _slang = SlangGlossary.FromJson(json ?? embedded);
        }
        catch { _slang = SlangGlossary.FromJson(null); }
    }

    /// <summary>Tuck away whatever window is currently on screen, let the user drag a rectangle,
    /// return it (physical px), and put that same window back.
    ///
    /// "Whatever window" is the point. In compact mode the main window is already hidden and must
    /// STAY hidden — what the user is looking at is the overlay. Unconditionally Show()ing the main
    /// window here is what used to force read-once to leave compact mode altogether, so the whole
    /// thing read as the app throwing you out of the mode you were playing in.
    ///
    /// The overlay still has to step aside for the drag itself: it is Topmost, like the selection
    /// window, so it would otherwise float over the dimmed layer — and sit inside the very region
    /// being captured. It comes straight back, so from the user's side the compact chat blinks
    /// rather than being replaced.</summary>
    private async Task<System.Drawing.Rectangle?> SelectRegionAsync()
    {
        bool compact = _overlay is { IsVisible: true };
        var wasTopmost = Topmost;
        _selectingRegion = true;   // block hotkey-live / a second selection while dragging
        if (compact) _overlay!.Hide(); else Hide();
        await Task.Delay(150);
        try
        {
            var overlay = new SelectionOverlay();
            return overlay.ShowDialog() == true ? overlay.SelectedRegion : null;
        }
        finally
        {
            _selectingRegion = false;
            if (compact)
            {
                _overlay!.Show();       // NOT EnterCompactMode: we never left it
                _overlay.Activate();
            }
            else
            {
                Show();
                Topmost = wasTopmost;
                Activate();
            }
        }
    }

    private async void SelectArea_Click(object sender, RoutedEventArgs e) => await SelectAreaAndReadOnceAsync();

    /// <summary>Single entry point for "select area &amp; read once": the Translator tab's button and
    /// the compact overlay's. Both must behave identically, so neither owns the logic.
    ///
    /// Compact mode is never left. <see cref="SelectRegionAsync"/> hides and restores whichever
    /// window was on screen, so from the overlay the compact chat simply blinks for the drag and
    /// the framed result lands in the feed the user was already watching. The earlier version
    /// bounced out to the full window and back, which looked exactly as bad as it sounds.</summary>
    internal async Task SelectAreaAndReadOnceAsync()
    {
        if (_selectingRegion) return;
        StopLive();
        var region = await SelectRegionAsync();
        if (region is { } rect) await ReadRegionOnceAsync(rect);
    }

    /// <summary>Capture a region once, OCR it into sentences and translate them onto the
    /// Translator tab. Shared by the "read once" button and the Ctrl+Alt+R shortcut.</summary>
    private async Task ReadRegionOnceAsync(System.Drawing.Rectangle rect)
    {
        // Only one read-once may run at a time: the OCR engine is shared and non-reentrant, so a
        // second Ctrl+Alt+R (or a live start) firing RecognizeAsync on it concurrently would race.
        // The flag gates ToggleLive/StartLive/ReadLastAreaOnce too; button disabling stays a UI cue.
        //
        // The second press is no longer SWALLOWED, though (AC 2): it cancels the read in flight.
        // Pressing again is the natural thing to do when nothing has happened for twenty seconds,
        // and until this story that press did nothing at all — there was no way to end a read but
        // the window. It still does not START a second read; the guard owns the OCR engine and
        // cancellation does not replace it (TP-ONCE-06).
        if (_readingOnce) { CancelReadOnce(); return; }

        // Same reason as StartLive: with no Russian engine there is nothing to read, and the app has
        // to say so — rather than show an empty result (or, before the fallback was removed, a
        // confidently garbled one).
        if (!IsOcrReady()) { ShowOcrPackNeeded("then read the area again."); return; }

        // ---- ruling E5-g (E5.S3): there is no pause check here any more ---------------------------
        // E5.S4 asked the chain BEFORE the capture and returned. It was the right instinct — OQ-B's
        // "a paused app costs the player nothing" — applied one step too early, and it cost two
        // things a read is entitled to. A capture and an OCR are LOCAL: they cost no request, which
        // is the only currency a paused provider cares about. Refusing them meant (a) a read whose
        // every line was already in the cache was turned away although it needed no provider at all,
        // and (b) the sentence could not say how many lines were on the screen, because nothing had
        // looked yet — §3.3's "Read {n} line(s) — all engines are paused" was unwritable.
        //
        // So the read runs, the cache serves what it can, and the pause is REPORTED rather than
        // predicted: the chain raises AllProvidersPaused without sending anything (it reads
        // ProviderGate.Snapshot, which is side-effect free by contract), TranslateSentencesInto hands
        // that back typed, and ReadOnceSummary turns it into the paused sentence with {n} in it.
        // Zero requests either way — that half of AC 3 is unchanged and is what TP-ONCE-04 asserts.
        MainTabs.SelectedIndex = TabTranslator;   // results show on the Translator page
        SetReadOnceEnabled(false);
        LiveButton.IsEnabled = false;        // don't let live start mid-read (shared OCR engine)
        // NOT _ocrItems.Clear(): the result is appended to the feed and framed instead (see
        // TranslateSentencesInto). Wiping the history to show one answer threw away the live lines
        // the user was reading — most obviously from the overlay, where the feed IS the window.
        SetScreenStatus("Reading…");

        // One CancellationTokenSource per read, and the budget lives on it rather than on any one
        // request: what took the worst case to ≈36.9 s of uncancellable UI was the FAN-OUT (two
        // source groups × two tiers × retries), each leg of it inside its own honest 12 s timeout.
        // Nothing below the UI can bound that, because nothing below the UI knows a person is
        // waiting. Cancelled by this token: a second press, ■ Stop, closing the window — and the
        // 30 s budget itself, which is why _readOnceStopped is reset here and not just set below.
        CancelReadOnce();                 // nothing should be in flight (the guard above) — never leak one
        _readOnceStopped = false;
        var cts = _readOnceCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(TranslationPolicy.ReadOnceBudgetSeconds));

        int lines = 0;                    // what the status says it READ, set once OCR has answered
        // LAST, and immediately above the try (review): the flag is only cleared in the finally, so
        // every statement standing between the two is a statement that can leave both read-once
        // buttons and LiveButton greyed until restart. The setup above cannot throw today — they are
        // property sets and a CancellationTokenSource over a compile-time constant — and this
        // ordering is what keeps that true of whatever gets added there next.
        _readingOnce = true;
        try
        {
            using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
            using var forOcr = ApplyOcrFilter(bmp);   // null when the filter is off
            var sentences = TextMatching.SplitChatMessages(await _ocr.ReadLinesAsync(forOcr ?? bmp))
                .Select(TextMatching.StripNoise)
                .Where(l => l.Length > 0).ToList();
            // The capture and the OCR take no token of their own (ScreenCapture is synchronous GDI;
            // OcrService.ReadLinesAsync has no ct parameter), so a Stop or a budget expiry landing
            // DURING them is only observed here — and it has to be observed here, before the two
            // things below that would otherwise happen for a read that is already over: telling the
            // player "No text detected there. Try a tighter box" (a diagnosis of a read nobody
            // finished) and, worse, creating the rows AC 3 exists to prevent (review).
            cts.Token.ThrowIfCancellationRequested();
            if (sentences.Count == 0)
            {
                SetScreenStatus(IsOcrReady()
                    ? "No text detected there. Try a tighter box around the text."
                    : "No text detected — the Russian OCR pack isn't installed. Install it on the Screen OCR tab (1 click).");
                return;
            }
            lines = sentences.Count;
            var target = SelectedTag(OcrTargetCombo) ?? "en";
            SetScreenStatus($"Read {lines} line(s). Translating…");

            // The four-way branch of §3.3, and "Done" is one branch of it (never the fall-through):
            // ReadOnceSummary reaches it only when every line read carries a translation, which is
            // UX hint 4 and the whole of DoD V1.5. The counts come from what the rows ACTUALLY got,
            // never from lines — reading N lines has never meant translating N lines.
            var (translated, error) = await TranslateSentencesInto(sentences, target, cts.Token);
            SetScreenStatus(ReadOnceSummary.Status(lines, translated, error, PausedTryAgainIn(error)));
        }
        // I3, in the shape ChainTranslator.RunAsync uses: OUR token really is cancelled, so this is
        // a person or the budget — never an HttpClient timeout, whose OperationCanceledException
        // arrives with the token untouched and is classified as the Timeout it is further down.
        //
        // The two are told apart by the FLAG and not by the exception, because they cannot be told
        // apart by the exception: the 30 s budget is a failure and gets §3.3's failure sentence like
        // any other, while a person's Stop gets a sentence that says so.
        //
        // BOTH branches say something, and the review is why (E5.S4). The story shipped the stop
        // rendering NOTHING, on §2.1's "Cancelled is not a state" — true of the eight-state model
        // and of the feed rows, and not true of the status line, which would have been left reading
        // "Reading…" over a read that had stopped. §1's principles win: one message per state (1)
        // and honest status only (4). A stale "Reading…" is the same lie as "Done" over an empty
        // result, told the other way round.
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetScreenStatus(_readOnceStopped
                ? UserMessages.ReadCancelledStatus()
                : ReadOnceSummary.Status(lines, 0, BudgetExpired()));
        }
        catch (Exception ex)
        {
            SetScreenStatus(UserMessages.ReadFailed(ex));
        }
        finally
        {
            SetReadOnceEnabled(true);
            LiveButton.IsEnabled = true;
            _readingOnce = false;
            // Clear the field only if it is still ours — a second press has already cancelled and
            // cleared it, and may even have started the next read — and only THEN dispose, so the
            // field is never left pointing at a disposed source for even one statement.
            if (ReferenceEquals(_readOnceCts, cts)) _readOnceCts = null;
            cts.Dispose();
        }
    }

    /// <summary>End the read-once in flight, if there is one, and record that a PERSON ended it.
    /// Called by the second press, by <see cref="StopLive"/> and by <c>OnClosing</c> — the three
    /// cancels AC 2 names. The flag is what makes a stop render nothing while the 30 s budget, which
    /// cancels the identical token, renders a failure.</summary>
    private void CancelReadOnce()
    {
        if (_readOnceCts is not { } cts) return;
        // Only claim the cancel if there was still one to make. The budget cancels the SAME token,
        // and it does it on a timer thread — so a press landing in the gap between the budget firing
        // and the read's continuation reaching its catch would otherwise relabel a 30 s timeout as
        // "Read cancelled." and the player would never be told the app gave up (I3: the flag is the
        // only thing that distinguishes the two, so the flag has to be right). Review, E5.S4.
        if (!cts.IsCancellationRequested) _readOnceStopped = true;
        _readOnceCts = null;    // cleared BEFORE the cancel, so nothing re-entered can cancel it twice
        cts.Cancel();
        // NOT disposed here: the read that owns it is still inside its await and holds the token.
        // Its finally disposes on every path, which is the one place that knows the read is over —
        // disposing from the outside is the documented way to hand a live consumer a dead source.
    }

    /// <summary>The "{t}" of a paused read's status, or null when there is nothing honest to count
    /// down to — E5-g's other half. The countdown is only asked for when the read really did come
    /// back "every engine is paused"; every other failure gets §3.3's failure sentence and no timer.
    ///
    /// <para>It asks the CHAIN and never <c>ProviderGates</c> (TP-START-02), for the instant AND for
    /// the clock that instant was produced against: <c>PauseNow()</c> answers both, and subtracting
    /// our own <c>UtcNow</c> from a gate's <c>RetryAt</c> is the two-clocks bug IS-6 and
    /// <c>ProviderGate.Now()</c> exist to prevent. <c>_readChain</c> is the LIVE loop's and answers
    /// for read-once too, because both chains resolve the same process-global gates (I9). The call is
    /// side-effect free by contract (ruling R-2), so asking after the fact costs nothing — and it
    /// reads the windows as they are NOW, so a window that elapsed while the read ran simply drops
    /// the number instead of promising a time that has passed.</para></summary>
    private string? PausedTryAgainIn(Exception? error)
    {
        if (!ReadOnceSummary.IsAllPaused(error)) return null;
        var pause = _readChain.PauseNow();
        return CountdownText(LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now));
    }

    /// <summary>The read-once budget, as the failure it is. A budget expiry is a <b>timeout</b> and
    /// not a cancel: the player asked for this read and the app gave up on it, so they are told —
    /// with the same sentence a provider timeout gets, because from where they sit it is the same
    /// thing. Built here because nothing below the UI knows a budget exists.</summary>
    private static TranslationException BudgetExpired()
        => new(TranslationErrorKind.Timeout,
               $"the read-once budget of {TranslationPolicy.ReadOnceBudgetSeconds} s elapsed");

    /// <summary>Grey out BOTH read-once buttons while a read is in flight. Ctrl+Alt+R can start one
    /// without ever leaving compact mode, so the overlay's copy has to follow the main window's.</summary>
    private void SetReadOnceEnabled(bool enabled)
    {
        SelectAreaButton.IsEnabled = enabled;
        _overlay?.SetReadOnceEnabled(enabled);
    }

    /// <summary>Fill the reading list with each Russian message and its translation. Only the
    /// message body is translated; the speaker's nickname is kept verbatim as a prefix.
    ///
    /// <para><b>Returns what actually happened</b>, and that return type is the story:
    /// <c>Translated</c> is how many rows carry a real translation and <c>Error</c> is the failure
    /// to say out loud, so the caller cannot write "Done" over a read that translated nothing. It
    /// used to return <c>Task</c> and its catch used to <c>return</c> — the swallow that let
    /// <c>Done — N line(s) translated.</c> print over a list of "(no internet connection)" rows
    /// (V1.5, amplifier A7).</para></summary>
    private async Task<(int Translated, TranslationException? Error)> TranslateSentencesInto(
        List<string> sentences, string target, CancellationToken ct)
    {
        // APPENDS to the feed — it used to Clear() it first. Reading once from the compact overlay
        // is meant to drop an answer INTO the live flow you are watching, not to wipe the flow to
        // show it. Every item is flagged IsReadOnce so both feeds frame it and you can find it
        // among the live lines; the same MaxHistory cap as the live loop keeps the list bounded.
        // SplitSpeakerStrict, not SplitSpeaker: a body that itself starts "word:" (e.g. the slang
        // "тс: сбор у входа") must not lose its first word as a fake nickname and skip translation.
        var parts = sentences.Select(TextMatching.SplitSpeakerStrict).ToList();   // (Speaker, Body)
        var items = new List<OcrResultItem>();
        for (int i = 0; i < sentences.Count; i++)
        {
            var item = new OcrResultItem
            {
                Speaker = parts[i].Speaker,
                OriginalBody = parts[i].Body,
                TranslationBody = "…",
                Glossary = _slang.Decode(sentences[i]),
                IsReadOnce = true,
            };
            _ocrItems.Add(item);
            items.Add(item);
            while (_ocrItems.Count > MaxHistory) _ocrItems.RemoveAt(0);   // drop the oldest
        }
        ResultsScroller?.ScrollToEnd();   // the overlay's feed scrolls itself on CollectionChanged

        List<string> translations;
        // _readOnceTranslator, not _readTranslator: this is a click, and a person is waiting on it,
        // so its providers ask the gate as Interactive (ruling OQ-a). Same free chain, same gates,
        // same I8 — the LIVE loop's Background reserve exists to keep a token free for exactly this.
        try { translations = await TranslateBodiesAsync(parts.Select(p => p.Body).ToList(), target,
                                                       _readOnceTranslator, ct); }
        // I3, and FIRST for the same reason the chain puts it first: our token really is cancelled,
        // so this is the player's Stop or the 30 s budget — never an HttpClient timeout, which
        // arrives as a TaskCanceledException with the token NOT cancelled and falls to the catch
        // below to be classified as the Timeout it is.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The rows may NOT be left on "…" (E5.S4 review). Nothing is coming for them — the read
            // that owned them is over — and a row that stays pending for ever is precisely what
            // makes a player press the button again, which is amplifier A7 and the reason this
            // story exists. They get the same "(" marker every other non-translation gets (I4), so
            // nothing downstream mistakes one for a result.
            //
            // E5.S3: a row carrying UserMessages.ReadCancelledRow() is FINISHED, not failed. The
            // player refused this read; re-sending it would spend the request they just declined,
            // so the retry pass must skip these — that is what tells them apart from the
            // "({Friendly(ex)})" rows of the catch below, which ARE retry candidates.
            foreach (var it in items) it.TranslationBody = $"({UserMessages.ReadCancelledRow()})";
            throw;   // the caller owns the status; it knows whether a person or the budget did this
        }
        catch (Exception ex)
        {
            // E5.S3, and the answer to the question the test plan left open ("read-once rows?").
            // These are ordinary failed rows in the same feed as the live ones, so they are retried
            // in place by the same drain — but ONLY while the LIVE loop is running, because the loop
            // is the only thing that drains. A read taken with LIVE stopped has nothing coming for
            // it, and a row left pending for ever with nothing behind it is precisely the amplifier
            // (A7) that made a player press the button again. So: a drain is coming ⇒ the row waits
            // on "…"; no drain is coming ⇒ it says what went wrong, exactly as it always did.
            if (_liveCts != null && PendingRetryQueue<OcrResultItem>.IsRetryable(ex))
                for (int i = 0; i < items.Count; i++)
                {
                    items[i].TranslationBody = UserMessages.PendingRetryRow();
                    _pendingRetry.Enqueue(items[i], parts[i].Body, target);
                }
            else
                foreach (var it in items) it.TranslationBody = $"({Friendly(ex)})";
            return (0, AsTranslationFailure(ex, ct));
        }
        for (int i = 0; i < items.Count && i < translations.Count; i++)
            items[i].TranslationBody = translations[i];
        // Not items.Count, and not sentences.Count: TranslateBodiesAsync fills a gap with the
        // (expanded) source text rather than a null, and a provider's per-line fallback (E3.S8)
        // returns "(…)" placeholders for the lines it could not do. ReadOnceSummary holds the app's
        // one definition of "this is a translation" — the very test CachingTranslator.IsCacheable
        // applies before it stores anything, which is not a coincidence.
        return (ReadOnceSummary.CountTranslated(translations), null);
    }

    /// <summary>The failure a read-once reports, typed. A <see cref="TranslationException"/> already
    /// carries its Kind; anything else goes through <c>ProviderErrorMapper</c> rather than being
    /// re-classified here — E1.S3 owns that vocabulary, and a second switch over the same statuses
    /// is how two of them came to disagree. The two guards around the mapper are the chain's own
    /// (<c>ChainTranslator.RunAsync</c>): a cancelled token may not reach it, because the Kind it
    /// would answer with is the one nothing in this app may construct (I3).</summary>
    private static TranslationException AsTranslationFailure(Exception ex, CancellationToken ct)
    {
        if (ex is TranslationException typed) return typed;
        ct.ThrowIfCancellationRequested();
        var kind = ProviderErrorMapper.Classify(null, null, ex, false, ct);
        ct.ThrowIfCancellationRequested();
        return new TranslationException(kind, UserMessages.Sentence(kind) ?? ex.Message);
    }

    /// <summary>Ctrl+Alt+R: read the saved area once (no live loop). If live is already
    /// running we leave it alone; if there's no saved area we surface the picker.</summary>
    private async void ReadLastAreaOnce()
    {
        if (_selectingRegion) return;
        // A second Ctrl+Alt+R does not start a second read — the OCR engine is shared and
        // non-reentrant — but it no longer does nothing either: it cancels the read in flight
        // (AC 2). ReadRegionOnceAsync's own guard says the same thing for the button path; this
        // one exists because the hotkey never reaches it.
        if (_readingOnce) { CancelReadOnce(); return; }
        if (_liveCts != null) { ShowToast("Live is already running (Ctrl+Alt+L to stop)."); return; }
        if (!TryGetSavedRegion(out var rect))
        {
            BringToFront();
            MainTabs.SelectedIndex = TabScreenOcr;
            ShowToast("Pick a screen area once — then Ctrl+Alt+R reads it again.");
            return;
        }
        BringToFront();
        await ReadRegionOnceAsync(rect);
    }

    // ============================================================
    //  RESET TO THE RECOMMENDED READING SETTINGS
    // ============================================================

    /// <summary>
    /// A settings file written by an older version keeps its values forever — which means a default
    /// we tune later (calmer sensitivity, faster reads, the background filter) only ever reaches NEW
    /// installs. Everyone else stays on numbers nobody chose, without knowing it. This is the way back.
    ///
    /// Deliberately limited to what this tab tunes for READING. The capture method is not a
    /// preference but a compatibility choice — someone on "Windows Graphics" picked it because GDI
    /// gave them a black screen, and resetting it would break their capture for no reason.
    /// </summary>
    private void ResetTuning_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();   // the shipped defaults — one source of truth, never re-typed here
        var answer = MessageBox.Show(this,
            "Put the reading settings back to the recommended values?\n\n" +
            $"•  OCR sensitivity: {d.SensitivityPercent}%\n" +
            $"•  Live speed: ~{LiveIntervalMs(d.LiveSpeedPercent) / 1000.0:0.0}s between reads\n" +
            $"•  Smallest text fragment: {d.MinFragmentLetters} letters\n" +
            $"•  Stability: {d.StabilityPercent}%\n" +
            "•  Background filter: Boost contrast\n\n" +
            "Your capture method, and everything on the other tabs, stay as they are.",
            "PWRU Helper", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        ApplyRecommendedTuning();
        ShowToast("Reading settings are back to the recommended values.");
    }

    /// <summary>Put the reading settings back to the shipped defaults, in memory, on screen and on
    /// disk. Split out of the click handler so it can be tested without a dialog.</summary>
    internal void ApplyRecommendedTuning()
    {
        var d = new AppSettings();
        _settings.SensitivityPercent = d.SensitivityPercent;
        _settings.LiveSpeedPercent = d.LiveSpeedPercent;
        _settings.MinFragmentLetters = d.MinFragmentLetters;
        _settings.StabilityPercent = d.StabilityPercent;
        _settings.OcrFilterMode = d.OcrFilterMode;
        _settings.OcrKeepColorHex = d.OcrKeepColorHex;
        _settings.OcrColorTolerance = d.OcrColorTolerance;

        // Pushing values into the controls fires their change handlers, which would write the
        // half-applied UI state straight back to disk — the same trap ApplySettings has to dodge.
        _restoringSettings = true;
        try
        {
            SensitivitySlider.Value = _settings.SensitivityPercent;
            LiveSpeedSlider.Value = _settings.LiveSpeedPercent;
            MinFragmentSlider.Value = _settings.MinFragmentLetters;
            StabilitySlider.Value = _settings.StabilityPercent;
            OcrColorHexBox.Text = _settings.OcrKeepColorHex;
            OcrToleranceSlider.Value = _settings.OcrColorTolerance;
            SetOcrFilterCombo(_settings.OcrFilterMode);
            UpdateOcrFilterUi(_settings.OcrFilterMode);
        }
        finally { _restoringSettings = false; }

        SettingsService.Save(_settings);
    }

    // ============================================================
    //  BACKGROUND FILTER (optional pre-OCR clean-up)
    // ============================================================

    /// <summary>Apply the configured pre-OCR filter to a capture. Returns a NEW bitmap, or null
    /// when filtering is off (the caller then reads the original). Best-effort: any failure just
    /// falls back to the unfiltered capture.</summary>
    private System.Drawing.Bitmap? ApplyOcrFilter(System.Drawing.Bitmap capture)
    {
        try
        {
            switch ((_settings.OcrFilterMode ?? "off").ToLowerInvariant())
            {
                case "contrast":
                    return OcrImageFilter.BoostContrast(capture);
                case "color":
                    var c = ParseHexColor(_settings.OcrKeepColorHex) ?? System.Drawing.Color.White;
                    return OcrImageFilter.KeepColor(capture, c, Math.Clamp(_settings.OcrColorTolerance, 0, 441));
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            Logging.Warn("OCR background filter failed, using raw capture: " + ex.Message);
            return null;
        }
    }

    private static System.Drawing.Color? ParseHexColor(string? hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return System.Drawing.Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        return null;
    }

    // Four thin event handlers (different WPF delegate signatures) feed one save routine. Hex is
    // saved on every keystroke (TextChanged), not just LostFocus, so starting live via Ctrl+Alt+L
    // while the caret is still in the box uses the colour the user just typed, not the stale one.
    private void OcrFilterCombo_Changed(object sender, SelectionChangedEventArgs e) => SaveOcrFilterSettings();
    private void OcrColorHex_LostFocus(object sender, RoutedEventArgs e) => SaveOcrFilterSettings();
    private void OcrColorHex_Changed(object sender, TextChangedEventArgs e) => SaveOcrFilterSettings();
    private void OcrTolerance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => SaveOcrFilterSettings();

    private void SaveOcrFilterSettings()
    {
        // While the UI is being built or restored, setting the controls fires these handlers; writing
        // that transient state back would clobber the very settings we're loading. Bail out.
        if (_restoringSettings) return;
        // Handlers can fire while the XAML is still being built; ignore until all controls exist.
        if (OcrFilterCombo is null || OcrColorHexBox is null || OcrToleranceSlider is null) return;

        // No selection yet = nothing the user chose. Never fall back to "off" here: doing that is
        // exactly how a saved "contrast" got overwritten during startup (see _restoringSettings).
        if (SelectedTag(OcrFilterCombo) is not string mode) return;
        _settings.OcrFilterMode = mode;
        _settings.OcrKeepColorHex = (OcrColorHexBox.Text ?? "#FFFFFF").Trim();
        _settings.OcrColorTolerance = (int)Math.Round(OcrToleranceSlider.Value);

        UpdateOcrFilterUi(mode);

        SettingsService.Save(_settings);
    }

    /// <summary>Sync the filter-mode-dependent UI (colour-options panel + tolerance label) to the
    /// current settings. Shared by SaveOcrFilterSettings and the restore path in ApplySettings —
    /// where the change handlers are suppressed — so the two can't drift apart.</summary>
    private void UpdateOcrFilterUi(string mode)
    {
        if (OcrToleranceValue != null) OcrToleranceValue.Text = _settings.OcrColorTolerance.ToString();
        if (OcrColorOptions != null)
            OcrColorOptions.Visibility = mode == "color" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Select the filter-mode combo item whose Tag matches the saved mode.</summary>
    private void SetOcrFilterCombo(string mode)
    {
        SelectTag(OcrFilterCombo, mode);
        if (OcrFilterCombo.SelectedItem == null) OcrFilterCombo.SelectedIndex = 0;   // "off"
    }

    // ============================================================
    //  CAPTURE METHOD (GDI default, experimental Windows.Graphics)
    // ============================================================

    private void CaptureBackend_Changed(object sender, SelectionChangedEventArgs e)
    {
        // ApplySettings sets this combo during restore; don't write the transient state back.
        if (_restoringSettings) return;
        if (CaptureBackendCombo is null) return;   // still building the XAML
        if (SelectedTag(CaptureBackendCombo) is not string mode) return;   // no selection = not a user choice
        _settings.CaptureBackend = mode;
        ScreenCapture.SetMode(mode);
        SettingsService.Save(_settings);
    }

    private void SetCaptureBackendCombo(string mode)
    {
        SelectTag(CaptureBackendCombo, mode);
        if (CaptureBackendCombo.SelectedItem == null) CaptureBackendCombo.SelectedIndex = 0;   // "gdi"
    }
}

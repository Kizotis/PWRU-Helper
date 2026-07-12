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

        // Remember the area so it can be resumed next session without re-selecting.
        _settings.LastLiveRegion = new[] { rect.X, rect.Y, rect.Width, rect.Height };
        SettingsService.Save(_settings);

        _liveRegion = rect;
        _dedup = new();
        _liveTicks = 0;
        _ocrItems.Clear();
        SetLiveUi(true);
        MainTabs.SelectedIndex = TabTranslator;
        SetLiveStatus("🔴 Live — watching the area. Translations appear when new text shows up.");
        if (!IsOcrReady())
            ShowToast("Tip: install the Russian OCR pack (Screen OCR tab) for good Cyrillic reading.");

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
        if (_liveCts == null) return;
        _liveCts.Cancel();
        _liveCts.Dispose();
        _liveCts = null;
        _liveRegion = null;
        SetLiveUi(false);
        SetLiveStatus("Live stopped.");
    }

    /// <summary>Set the live status text on the main window AND (if shown) the overlay,
    /// so the state is visible whichever window the user is looking at.</summary>
    private void SetLiveStatus(string msg)
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
        int consecutiveErrors = 0;
        var sw = new System.Diagnostics.Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            sw.Restart();
            try
            {
                _liveTicks++;
                LiveIndicator.Text = (_liveTicks % 2 == 0) ? "●  LIVE" : "○  LIVE";  // heartbeat

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

                if (confirmed.Count > 0)
                {
                    var target = SelectedTag(OcrTargetCombo) ?? "en";
                    SetLiveStatus($"🔴 Live — {confirmed.Count} new line(s), translating…");
                    await AppendLinesToHistory(confirmed, target, ct);
                    if (ct.IsCancellationRequested) break;
                    SetLiveStatus($"🔴 Live — {_ocrItems.Count} message(s) so far (check #{_liveTicks}).");
                }
                else
                {
                    // Reassure the user it's really working even before the first message
                    // (a calm chat can be silent for minutes) — and show it's reading text.
                    SetLiveStatus(_ocrItems.Count == 0
                        ? $"🔴 Live — watching (check #{_liveTicks}, sees {lines.Count} line(s), waiting for new text)…"
                        : $"🔴 Live — {_ocrItems.Count} message(s) so far (check #{_liveTicks}).");
                }
                consecutiveErrors = 0;
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
                if (++consecutiveErrors >= 5)
                {
                    Services.Logging.Error("Live translation auto-stopped after 5 consecutive errors", ex);
                    StopLive();   // this sets "Live stopped." first…
                    SetLiveStatus($"Live stopped after repeated errors ({Friendly(ex)}).");   // …then the real reason
                    break;
                }
                SetLiveStatus($"Live hiccup ({Friendly(ex)}) — retrying…");
            }

            // Keep a roughly steady cadence: subtract the time the read+translate just took.
            int wait = Math.Max(150, CurrentLiveIntervalMs() - (int)sw.ElapsedMilliseconds);
            try { await Task.Delay(wait, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

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
            translations = await TranslateBodiesAsync(parts.Select(p => p.Body).ToList(), target, ct);
        }
        catch (Exception ex)
        {
            // Don't leave the placeholders stuck on "…" forever (e.g. Google rate-limit):
            // mark them, then let the loop's error handling show the reason.
            foreach (var it in items) it.TranslationBody = $"({Friendly(ex)})";
            throw;
        }
        if (ct.IsCancellationRequested) return;
        for (int i = 0; i < items.Count && i < translations.Count; i++)
            items[i].TranslationBody = translations[i];
        ResultsScroller?.ScrollToEnd();
    }

    /// <summary>Translate message bodies READ FROM THE SCREEN, picking the source language per
    /// message: real Russian (Cyrillic) is translated FROM "ru", but English/other-language messages
    /// are sent with "auto" so Google detects them instead of mangling plain English into invented
    /// Cyrillic. The two groups are still batched (one request each) and reassembled in the original
    /// order. Always goes through <c>_readTranslator</c> (free Google) — never DeepL, whose quota a
    /// live loop would burn through in an evening.</summary>
    private async Task<List<string>> TranslateBodiesAsync(List<string> bodies, string target, CancellationToken ct)
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
            var t = await _readTranslator.TranslateLinesAsync(ru, "ru", target, ct);
            for (int i = 0; i < ruIdx.Count && i < t.Count; i++) result[ruIdx[i]] = t[i];
        }
        if (auto.Count > 0)
        {
            var t = await _readTranslator.TranslateLinesAsync(auto, "auto", target, ct);
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

    /// <summary>Live re-read interval from the speed slider (higher speed = shorter wait).</summary>
    private int CurrentLiveIntervalMs()
    {
        double speed = LiveSpeedSlider?.Value ?? 80;    // 0..100, default matches the XAML slider
        return (int)(3000 - (speed / 100.0) * 2500);    // 3.0s (slow) … 0.5s (fast)
    }
}

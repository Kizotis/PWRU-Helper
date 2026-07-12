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
            OcrLangStatus.Text = "Russian OCR language pack: not installed — Cyrillic won't read well until it is.";
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
            var path = FindOrCreateEditable("slang.json", embedded);
            string? json = embedded;
            if (path != null)
                try { json = File.ReadAllText(path); } catch { /* keep embedded */ }
            _slang = SlangGlossary.FromJson(json ?? embedded);
        }
        catch { _slang = SlangGlossary.FromJson(null); }
    }

    /// <summary>Hide the window, let the user drag a rectangle, return it (physical px).</summary>
    private async Task<System.Drawing.Rectangle?> SelectRegionAsync()
    {
        var wasTopmost = Topmost;
        _selectingRegion = true;   // block hotkey-live / a second selection while dragging
        Hide();
        await Task.Delay(150);
        try
        {
            var overlay = new SelectionOverlay();
            return overlay.ShowDialog() == true ? overlay.SelectedRegion : null;
        }
        finally
        {
            _selectingRegion = false;
            Show();
            Topmost = wasTopmost;
            Activate();
        }
    }

    private async void SelectArea_Click(object sender, RoutedEventArgs e)
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
        // second Ctrl+Alt+R (or a live start) that fired RecognizeAsync on it concurrently would
        // race — and its _ocrItems.Clear() below would wipe a just-started live feed. The flag
        // gates ToggleLive/StartLive/ReadLastAreaOnce too; button disabling stays as a UI cue.
        if (_readingOnce) return;
        _readingOnce = true;
        MainTabs.SelectedIndex = TabTranslator;   // results show on the Translator page
        SelectAreaButton.IsEnabled = false;
        LiveButton.IsEnabled = false;        // don't let live start mid-read (shared OCR engine)
        _ocrItems.Clear();
        ScreenReadStatus.Text = "Reading…";
        try
        {
            using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
            using var forOcr = ApplyOcrFilter(bmp);   // null when the filter is off
            var sentences = TextMatching.SplitChatMessages(await _ocr.ReadLinesAsync(forOcr ?? bmp))
                .Select(TextMatching.StripNoise)
                .Where(l => l.Length > 0).ToList();
            if (sentences.Count == 0)
            {
                ScreenReadStatus.Text = IsOcrReady()
                    ? "No text detected there. Try a tighter box around the text."
                    : "No text detected — the Russian OCR pack isn't installed. Install it on the Screen OCR tab (1 click).";
                return;
            }
            var target = SelectedTag(OcrTargetCombo) ?? "en";
            ScreenReadStatus.Text = $"Read {sentences.Count} line(s). Translating…";
            await TranslateSentencesInto(sentences, target);
            ScreenReadStatus.Text = $"Done — {sentences.Count} line(s) translated.";
        }
        catch (Exception ex)
        {
            ScreenReadStatus.Text = $"OCR failed: {Friendly(ex)}";
        }
        finally
        {
            SelectAreaButton.IsEnabled = true;
            LiveButton.IsEnabled = true;
            _readingOnce = false;
        }
    }

    /// <summary>Fill the reading list with each Russian message and its translation. Only the
    /// message body is translated; the speaker's nickname is kept verbatim as a prefix.</summary>
    private async Task TranslateSentencesInto(List<string> sentences, string target)
    {
        _ocrItems.Clear();
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
            };
            _ocrItems.Add(item);
            items.Add(item);
        }

        List<string> translations;
        try { translations = await TranslateBodiesAsync(parts.Select(p => p.Body).ToList(), target, default); }
        catch (Exception ex)
        {
            foreach (var it in items) it.TranslationBody = $"({Friendly(ex)})";
            return;
        }
        for (int i = 0; i < items.Count && i < translations.Count; i++)
            items[i].TranslationBody = translations[i];
    }

    /// <summary>Ctrl+Alt+R: read the saved area once (no live loop). If live is already
    /// running we leave it alone; if there's no saved area we surface the picker.</summary>
    private async void ReadLastAreaOnce()
    {
        if (_selectingRegion || _readingOnce) return;   // ignore a double Ctrl+Alt+R (shared OCR engine)
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

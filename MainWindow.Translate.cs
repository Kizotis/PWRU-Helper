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
    /// <summary>Result of a quick reply from the overlay.</summary>
    internal readonly record struct ReplyOutcome(bool Ok, string Russian, bool Copied, string? Error);

    /// <summary>Translate a short reply from the user's language into Russian and copy it.
    /// Never throws; returns success/failure explicitly so the overlay can react.</summary>
    internal async Task<ReplyOutcome> QuickReplyTranslateAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return new ReplyOutcome(false, "", false, null);

        var from = _settings.MyLanguage;   // NOT FromCombo, which the auto-flip can set to "ru"
        if (from is null or "ru" or "auto") from = "en";
        try
        {
            // The user is WRITING — this is the one place (with the Translator tab) where a DeepL
            // key is worth spending: _writeTranslator, not the OCR feed's Google-only reader.
            var ru = await _writeTranslator.TranslateAsync(text, from, "ru");
            bool copied = ru.Length > 0 && await CopyToClipboardAsync(ru);
            return new ReplyOutcome(true, ru, copied, null);
        }
        catch (Exception ex) { return new ReplyOutcome(false, "", false, Friendly(ex)); }
    }

    // ============================================================
    //  TRANSLATOR
    // ============================================================
    private async void Translate_Click(object sender, RoutedEventArgs e) => await RunTranslation();

    /// <summary>Enter translates, like the compact overlay's reply box (and like every chat box the
    /// user is already in). The input is multi-line, so Shift+Enter keeps the plain new-line — and
    /// Ctrl+Enter, the old shortcut, still works, for the muscle memory it built.</summary>
    internal static bool IsTranslateKey(Key key, ModifierKeys modifiers)
        => key == Key.Enter && (modifiers & ModifierKeys.Shift) == 0;

    private async void TranslateInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsTranslateKey(e.Key, Keyboard.Modifiers)) return;

        e.Handled = true;              // don't ALSO insert the line break
        await RunTranslation();        // no-ops on empty input or while one is already running
    }

    private async Task RunTranslation()
    {
        if (!TranslateButton.IsEnabled) return;   // a translation is already running

        var text = TranslateInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        var from = SelectedTag(FromCombo) ?? "en";
        var to = SelectedTag(ToCombo) ?? "ru";

        // Auto-detect: if the text is mostly Russian but we're not translating FROM Russian,
        // flip the direction so pasting Russian "just works". We require a real share of
        // Cyrillic (not a single stray character) so a French sentence with one Cyrillic
        // smiley or name doesn't wrongly flip to translating FROM Russian.
        if (from != "ru" && TextMatching.CyrillicShare(text) >= 0.3)
        {
            from = "ru";
            if (to == "ru") to = "en";
            SelectTag(FromCombo, from);
            SelectTag(ToCombo, to);
        }

        TranslateButton.IsEnabled = false;
        TranslateStatus.Text = "Translating…";
        try
        {
            // When the effective source is Russian, expand known slang to its long form BEFORE
            // translating — exactly like the live path does (see TranslateBodiesAsync). Only the
            // string SENT to the translator changes; TranslateInput/TranslateOutput keep showing
            // the user's raw text. Don't expand when the source isn't Russian.
            var toTranslate = from == "ru" ? _slang.Expand(text) : text;
            var result = await _writeTranslator.TranslateAsync(toTranslate, from, to);
            int blocks = ShowTranslation(result);
            ShowTranslateStatus(from, to, blocks, result.Length);

            if (AutoCopyCheck.IsChecked == true && result.Length > 0 && await CopyToClipboardAsync(result))
                ShowToast(blocks > 1
                    ? $"Translated & copied whole — but the game takes {TextMatching.GameChatLimit} characters " +
                      "per message, so send the highlighted blocks one by one"
                    : "Translated & copied — paste in game with Ctrl+V");
        }
        catch (Exception ex)
        {
            TranslateStatus.Text = $"Failed: {Friendly(ex)}";
            // Reset the colour too: gold means "too long", and an error left in gold after a long
            // translation reads as if the failure were about the length.
            TranslateStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Show the translation, marking where the game would cut it. The game only accepts
    /// <see cref="TextMatching.GameChatLimit"/> characters per chat message, and a long translation
    /// silently doesn't fit — so every second block is tinted and the space where the cut falls is
    /// painted red: the user can see exactly how far to select before pasting.
    ///
    /// Nothing is INSERTED into the text (no marker character): whatever they select and copy is
    /// exactly what was translated, never a stray glyph pasted into the game chat. That's also why
    /// the blocks come from <see cref="TextMatching.GameChatBlockSpans"/> (offsets into the original)
    /// rather than from re-joining the split pieces, which would corrupt a hard-split long word.
    /// Returns how many chat messages the translation needs.
    /// </summary>
    internal int ShowTranslation(string text)
    {
        _lastTranslation = text ?? "";
        var paragraph = new System.Windows.Documents.Paragraph { Margin = new Thickness(0) };
        var spans = TextMatching.GameChatBlockSpans(_lastTranslation, TextMatching.GameChatLimit);

        if (spans.Count <= 1)
        {
            paragraph.Inlines.Add(new System.Windows.Documents.Run(_lastTranslation));
        }
        else
        {
            var tint = (System.Windows.Media.Brush)FindResource("ChatBlockBrush");
            var cut = (System.Windows.Media.Brush)FindResource("ChatCutBrush");
            int cursor = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                var (start, length) = spans[i];
                if (start > cursor)
                {
                    // Whatever separates two blocks is the cut itself — paint it red. Before the
                    // FIRST block there is no cut, only leading whitespace: painting that red would
                    // mark a boundary that isn't one.
                    var gap = new System.Windows.Documents.Run(_lastTranslation[cursor..start]);
                    if (i > 0) gap.Background = cut;
                    paragraph.Inlines.Add(gap);
                }

                var run = new System.Windows.Documents.Run(_lastTranslation.Substring(start, length));
                if (i % 2 == 1) run.Background = tint;   // alternate, so each block's extent is obvious
                paragraph.Inlines.Add(run);
                cursor = start + length;
            }
            if (cursor < _lastTranslation.Length)
                paragraph.Inlines.Add(new System.Windows.Documents.Run(_lastTranslation[cursor..]));
        }

        TranslateOutput.Document.Blocks.Clear();
        TranslateOutput.Document.Blocks.Add(paragraph);
        return Math.Max(1, spans.Count);
    }

    /// <summary>The line under the buttons: the direction, and — when the translation needs several
    /// chat messages — how many and why. Gold is reserved for that warning, so it must be cleared
    /// again when it no longer applies.</summary>
    private void ShowTranslateStatus(string from, string to, int blocks, int characters)
    {
        TranslateStatus.Text = blocks > 1
            ? $"{from} → {to}  ·  {characters} characters — too long for one chat message: " +
              $"send it as {blocks}, one highlighted block at a time"
            : $"{from} → {to}";
        TranslateStatus.SetResourceReference(TextBlock.ForegroundProperty,
            blocks > 1 ? "GoldBrush" : "TextMutedBrush");
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        var from = SelectedTag(FromCombo);
        var to = SelectedTag(ToCombo);
        // "auto" can't be a target; fall back to english when swapping it in.
        SelectTag(FromCombo, to ?? "en");
        SelectTag(ToCombo, from == "auto" ? "en" : from ?? "ru");

        // Swap the text too, so a round-trip is easy. The output is a FlowDocument now, so the
        // translation it is showing is kept in _lastTranslation rather than read back off the box.
        var previous = _lastTranslation;
        var swappedIn = TranslateInput.Text ?? "";
        int blocks = ShowTranslation(swappedIn);
        TranslateInput.Text = previous;

        // The status described the OLD output. Left alone it would keep claiming "send it as 3
        // blocks" over a text that is now three words long — or say nothing over one that isn't.
        ShowTranslateStatus(SelectedTag(FromCombo) ?? "en", SelectedTag(ToCombo) ?? "ru",
                            blocks, swappedIn.Length);
    }

    private async void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastTranslation) && await CopyToClipboardAsync(_lastTranslation))
            ShowToast("Result copied");
    }

    // Copy the original Russian of a screen-read message.
    private async void CopyOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OcrResultItem item } && await CopyToClipboardAsync(item.Original))
            ShowToast("Russian copied");
    }

    // ============================================================
    //  TRANSLATION BACKEND (Google default, optional DeepL)
    // ============================================================

    /// <summary>Build the WRITING translator from settings: DeepL (with Google fallback) when an API
    /// key is set, otherwise plain Google — always wrapped in the cache. Rebuilt on key change.
    /// The screen-reading side never comes through here: it uses <c>_readTranslator</c> (Google only),
    /// because a live loop translating every new chat line would eat a DeepL quota in one session.</summary>
    private ITranslator BuildTranslator()
    {
        var key = (_settings.DeepLApiKey ?? "").Trim();
        ITranslator backend = key.Length > 0
            ? new FallbackTranslator(new DeepLTranslator(key), new TranslationService())
            : new TranslationService();
        return new CachingTranslator(backend);
    }

    private void DeepLSaveKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeepLApiKey = (DeepLKeyBox.Password ?? "").Trim();
        SettingsService.Save(_settings);
        _writeTranslator = BuildTranslator();   // apply immediately (starts with a fresh cache)
        UpdateDeepLStatus();
        ShowToast(_settings.DeepLApiKey.Length > 0
            ? "DeepL key saved — used when you write (screen reading stays on Google)"
            : "DeepL key cleared — using Google");
    }

    private void UpdateDeepLStatus()
    {
        bool on = (_settings.DeepLApiKey ?? "").Trim().Length > 0;
        DeepLStatus.Text = on
            ? "● DeepL for what you write (Translator + quick reply) — falls back to Google if it errors. Screen reading uses Google."
            : "○ Using Google (free, no key needed)";
    }
}

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
            TranslateOutput.Text = result;
            TranslateStatus.Text = $"{from} → {to}";

            if (AutoCopyCheck.IsChecked == true && result.Length > 0 && await CopyToClipboardAsync(result))
                ShowToast("Translated & copied — paste in game with Ctrl+V");
        }
        catch (Exception ex)
        {
            TranslateStatus.Text = $"Failed: {Friendly(ex)}";
        }
        finally
        {
            TranslateButton.IsEnabled = true;
        }
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        var from = SelectedTag(FromCombo);
        var to = SelectedTag(ToCombo);
        // "auto" can't be a target; fall back to english when swapping it in.
        SelectTag(FromCombo, to ?? "en");
        SelectTag(ToCombo, from == "auto" ? "en" : from ?? "ru");

        // Swap the text too, so a round-trip is easy.
        (TranslateInput.Text, TranslateOutput.Text) = (TranslateOutput.Text, TranslateInput.Text);
    }

    private async void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TranslateOutput.Text) && await CopyToClipboardAsync(TranslateOutput.Text))
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

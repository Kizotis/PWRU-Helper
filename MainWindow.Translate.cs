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

    /// <summary>Build the WRITING translator from settings (§8.1): the user's DeepL key in front of
    /// the free tiers when one is set, the free tiers alone when it is not — always wrapped in the
    /// shared cache, which is <see cref="TranslationChains"/>' and not this chain's (§8.2, E4.S4).
    /// Rebuilt on key change; the store is not.
    ///
    /// <para><b>DeepL is absent from the READ chain by construction, not by configuration (I8).</b>
    /// That is <see cref="TranslationChains.BuildRead"/>'s doing and no setting can undo it — which
    /// is why this comment no longer says the read side is "Google only": E6 lands Azure-for-reading
    /// behind an opt-in, and the invariant that survives it is the one about DeepL.</para>
    ///
    /// <para>The composition itself lives in <see cref="TranslationChains"/>, in <c>Services/</c>:
    /// a chain needs gates, and this file may not name <c>ProviderGates</c> (TP-START-02). Since
    /// E4.S4 it needs the shared cache store too, and the same rule applies for the same reason —
    /// the builder returns the chain already decorated, so this file names neither.</para></summary>
    private ITranslator BuildWriteChain() => TranslationChains.BuildWrite(_settings);

    private void DeepLSaveKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.DeepLApiKey = (DeepLKeyBox.Password ?? "").Trim();
        SettingsService.Save(_settings);
        // The same line the Azure save makes below, and DeepL needs it just as badly (E6.S3
        // review): an AuthFailed block is MaxValue and ClearAuthBlock is its ONLY exit, so a
        // refused key used to disable DeepL for the rest of the session — pasting the corrected
        // one changed nothing until the app was restarted. Ruling E2-a: no state may lock the
        // user out without a way back. E2-i bounds it to this key's own account-scoped rows.
        TranslationChains.OnKeySaved(ProviderIds.DeepL);
        // Apply immediately: a corrected key takes effect on the very next translation. What is
        // rebuilt is the CHAIN — the cache store behind it is TranslationChains' and outlives this
        // line (§8.2, E4.S4), so the session's accumulated translations survive the save. That was
        // amplifier A5, and it is why this handler is not something a user pays for twice.
        _writeTranslator = BuildWriteChain();
        UpdateDeepLStatus();
        ShowToast(_settings.DeepLApiKey.Length > 0
            ? "DeepL key saved — used when you write (screen reading stays on Google)"
            : "DeepL key cleared — using Google");
    }

    /// <summary>Rebuild the three READ references — <b>together</b>, which is the whole point of
    /// the method existing (E6.S3). A key save has to reach the read chain as well as the write
    /// one, because <c>UseKeyForReading</c> may already be on from a previous session; and a
    /// rebuild that reassigned <c>_readTranslator</c> but left <c>_readChain</c> pointing at the
    /// old chain would still work — same gates (I9) — which is exactly why it would ship unnoticed.
    ///
    /// <para>Like <see cref="BuildWriteChain"/> this rebuilds the CHAINS and not the cache: the
    /// store is <c>TranslationChains</c>' (§8.2), so the session's translations survive a key save
    /// on the read side too.</para></summary>
    private void RebuildReadChains()
    {
        _readTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Background, out _readChain);
        _readOnceTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Interactive);
    }

    /// <summary>
    /// The region the user picked or typed. <b>The first line is not optional.</b> XAML LOADING
    /// raises <c>SelectionChanged</c> during <c>InitializeComponent()</c>, long before
    /// <c>ApplySettings</c> has restored anything, so a handler that writes settings here persists
    /// an unrestored control — that is the v0.12.3 bug, and <c>SettingsService.Migrate</c>'s v2 step
    /// exists solely because v1 lost that very race.
    /// </summary>
    private void AzureRegionCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSettings) return;

        // An editable combo raises this for every item the user arrows past in the open list AND
        // for every keystroke that moves the type-ahead match — so the handler must be free when
        // nothing actually changed, or one interaction is a burst of settings.json writes and
        // chain rebuilds on the UI thread, against the one requirement the whole product has
        // (nothing may lag the game).
        var region = ReadAzureRegion();
        if (region == _settings.AzureRegion) return;

        _settings.AzureRegion = region;
        SettingsService.Save(_settings);

        // …and then the app must actually USE what it just wrote down. The region is baked into
        // AzureTranslator when the tier is built, so persisting alone would leave settings.json
        // saying `francecentral`, the running chain still calling `westeurope` and the status line
        // naming a third thing — until a restart. Divergence between the file, the chain and the
        // line that reports them is the failure class this whole story is about, so the handler
        // ends where the Save button does: both chains rebuilt, one status line refreshed. The
        // gate is deliberately NOT cleared here — lifting an account-scoped block is what pressing
        // Save means (AC 6, ruling E2-i), not what brushing a dropdown means.
        _writeTranslator = BuildWriteChain();
        RebuildReadChains();
        UpdateEngineStatusUi();
    }

    /// <summary>The region as the app will store it. <c>SelectedTag</c> returns null for typed
    /// text — there is no <c>ComboBoxItem</c> behind it — so every read of an editable combo is
    /// the tag OR the text, and the normalisation is the one <c>SettingsService.Sanitize</c>
    /// applies to the same field.</summary>
    private string ReadAzureRegion() =>
        (SelectedTag(AzureRegionCombo) ?? AzureRegionCombo.Text ?? "").Trim().ToLowerInvariant();

    private void AzureSaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = (AzureKeyBox.Password ?? "").Trim();
        var region = ReadAzureRegion();

        // Validate the PAIR before anything else. A key without a region is a guaranteed 401, and
        // the provider's guard raises it as a failed TRANSLATION (AuthFailed without NotSent, since
        // ruling E3-b gives that flag one writer) — so the chain would count a tier that tried and
        // failed, and the player would be told their key was refused by a request nobody should
        // have sent. Nothing is persisted, nothing is rebuilt and nothing is sent. Both halves
        // empty is NOT this case: that is clearing the key, and it falls through.
        var problem = TranslationChains.AzureCredentialProblem(key, region);
        if (problem != null)
        {
            AzureStatus.Text = problem;
            return;
        }

        _settings.AzureApiKey = key;
        _settings.AzureRegion = region;
        SettingsService.Save(_settings);

        // A corrected key must take effect NOW, not after the AuthFailed window a wrong one opened
        // — ruling E2-a: no state may lock the user out without a way back. The registry is not
        // named here on purpose (TP-START-02): TranslationChains owns the composition and the gates
        // for the same reason it owns the cache, so this file names a chain builder and never
        // ProviderGates. E2-i bounds what the reset touches: this key's own block, never a
        // rate limit, which is about this connection and not about this key.
        TranslationChains.OnKeySaved(ProviderIds.Azure);

        // BOTH chains. The write one is where an Azure key is used today; the read one because
        // UseKeyForReading (E6.S4) may already be on from a previous session, and a rebuild the
        // user cannot see is worth more than a stale chain they can. What is rebuilt is the CHAIN —
        // the cache store behind it is TranslationChains' and outlives this line (§8.2, E4.S4), so
        // the session's accumulated translations survive the save (amplifier A5).
        _writeTranslator = BuildWriteChain();
        RebuildReadChains();

        UpdateEngineStatusUi();
        ShowToast(key.Length > 0
            ? UserMessages.AzureKeySavedToast()
            : UserMessages.AzureKeyClearedToast());
    }

    /// <summary>
    /// The About tab's engine lines, refreshed in one place. Called explicitly from
    /// <c>ApplySettings</c> (where every change handler is suppressed, so a side effect that is not
    /// applied by hand does not happen at all — I12) and after each key save.
    ///
    /// <para>Deliberately small, with one job: <b>E6.S4</b> adds the read opt-in's side effect and
    /// <b>E7.S7</b> grows it into §4.2's "In use now" + chain block. One method, so three stories
    /// extend it instead of fighting over three.</para>
    /// </summary>
    private void UpdateEngineStatusUi()
    {
        UpdateDeepLStatus();

        var region = (_settings.AzureRegion ?? "").Trim();
        var hasKey = (_settings.AzureApiKey ?? "").Trim().Length > 0;
        // Both halves, matching what TranslationChains.BuildWrite actually does with them: a status
        // line claiming a configured engine over a credential that adds no tier is the lie this
        // story is here to prevent.
        AzureStatus.Text = hasKey && region.Length > 0
            ? UserMessages.AzureKeySetStatus(region)
            : UserMessages.AzureNoKeyStatus();
    }

    private void UpdateDeepLStatus()
    {
        bool on = (_settings.DeepLApiKey ?? "").Trim().Length > 0;
        DeepLStatus.Text = on
            ? "● DeepL for what you write (Translator + quick reply) — falls back to Google if it errors. Screen reading uses Google."
            : "○ Using Google (free, no key needed)";
    }
}

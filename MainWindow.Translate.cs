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
        // §3.4's SHORT form, not the §3.1 sentence: the overlay is 360 px and the wrapper
        // "⚠ … — your text is kept, press Enter to retry." costs 46 characters on its own
        // (amendment A3). The whole line is the deck's — the overlay renders what it is handed.
        catch (Exception ex)
        {
            return new ReplyOutcome(false, "", false, UserMessages.OverlayReply(ex, TryAgainIn(ex)));
        }
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
            // "Failed: " is RETIRED (§3.0): with {P} restored the sentence names the engine and
            // says what happened, so a "Failed:" in front of it is the app saying "bad news" twice
            // and demoting the sentence to a sub-clause. This is the surface §3.1 writes its
            // sentences for — rendered alone — so it is the one that adds the terminal stop.
            TranslateStatus.Text = UserMessages.TranslatorTabStatus(Friendly(ex));
            // Reset the colour too: gold means "too long", and an error left in gold after a long
            // translation reads as if the failure were about the length.
            TranslateStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        }
        finally
        {
            TranslateButton.IsEnabled = true;
            // The chip, on an event that already exists rather than on a timer (E7.S3 T5). A call
            // has just finished, so ChainTranslator.LastOutcome is fresh and this is the cheapest
            // honest moment to read it — success or failure, which is why it is in the finally: a
            // failed attempt is exactly when the chip has something new to say. It runs AFTER the
            // two status writes above, so §3.5's one-time "Back on Google." lands on top of them
            // rather than under them.
            UpdateEngineChip();
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
    private ITranslator BuildWriteChain() => TranslationChains.BuildWrite(_settings, _offlineTier);

    private void DeepLSaveKey_Click(object sender, RoutedEventArgs e)
    {
        // A test in flight is about the key that WAS in the box; this one supersedes it (E6.S5
        // review). Without the cancel the late result overwrites the line this handler is about to
        // write, and describes a credential that is no longer in effect.
        CancelKeyTests();
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
        _readTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Background, out _readChain, _offlineTier);
        _readOnceTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Interactive, _offlineTier);
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
        // …and the narrow one, for the one gesture that empties this combo in code: ruling E6-e's
        // clearing inside AzureSaveKey_Click, which persists and rebuilds properly a few lines
        // later. Second, never first: I12's rule is that the RESTORE guard is the opening line of
        // every handler, and this is a different question asked by a different writer.
        if (_suppressAzureRegionHandler) return;

        // An editable combo raises this for every item the user arrows past in the open list AND
        // for every keystroke that moves the type-ahead match — so the handler must be free when
        // nothing actually changed, or one interaction is a burst of settings.json writes and
        // chain rebuilds on the UI thread, against the one requirement the whole product has
        // (nothing may lag the game).
        var region = ReadAzureRegion();
        if (region == _settings.AzureRegion) return;

        // The region really changed, so a test in flight is about the old one — and its sentence
        // NAMES a region (E6.S5 review). Cancelled after the no-op guard above, never before it:
        // an editable combo raises this handler for keystrokes that change nothing.
        CancelKeyTests();

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

    /// <summary>
    /// <b>E6.S4 — the opt-in that lets the LIVE loop and read-once spend the user's Azure key.</b>
    /// The first line is not optional and is the same rule as the region combo's above: XAML
    /// LOADING raises <c>Checked</c> during <c>InitializeComponent()</c>, long before
    /// <c>ApplySettings</c> restores anything, so a handler that writes settings there persists an
    /// unrestored control — the v0.12.3 bug (I12).
    ///
    /// <para>Only the READ chains are rebuilt: the write chain does not read this setting, and
    /// rebuilding it would throw away nothing except the reader's confidence that the two are
    /// separate decisions. The rebuild takes effect on the next LIVE tick — both read fields are
    /// read per tick and never captured at <c>StartLive</c> — which is the semantics E6.S3
    /// recorded and the one this handler inherits. The cache survives it (§8.2): the store is
    /// <c>TranslationChains</c>' and not the chain's, so a tick of the box costs the session's
    /// accumulated translations nothing.</para>
    ///
    /// <para>The gate is deliberately NOT cleared here. Lifting an account-scoped block is what
    /// pressing <b>Save</b> on a key means (ruling E2-i); choosing where an existing key may be
    /// spent says nothing about whether that key was refused.</para>
    /// </summary>
    private void AzureForReading_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoringSettings) return;

        // IsChecked is bool? — `== true`, never `.Value`. No "has it actually changed" guard, unlike
        // the region combo above: a CheckBox raises Checked/Unchecked only when its state really
        // changes, so there is no burst to absorb and a guard would only be a second place for the
        // field and the control to disagree.
        _settings.UseKeyForReading = AzureForReadingCheck.IsChecked == true;
        SettingsService.Save(_settings);

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
        // Same reason as the DeepL save above: this gesture supersedes a test in flight, whose
        // answer is about the pair that was in the boxes when it was pressed (E6.S5 review).
        CancelKeyTests();

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
            AzureFeedback.Text = problem;   // transient (E7.S7): AzureStatus keeps the standing state
            return;
        }

        // Ruling E6-e: an empty KEY is not half a pair, it is the gesture that removes Azure — so
        // it takes the region with it, and the boxes are emptied to match. E6.S3 refused this and
        // told the user to clear the region box too; its own review called that a dead end and
        // referred the AC change upward. One box cleared, one engine gone, and settings.json, the
        // two chains and the status line all say the same thing afterwards.
        if (key.Length == 0)
        {
            region = "";
            // Emptying the combo raises SelectionChanged, and letting AzureRegionCombo_Changed run
            // would persist the OLD key with no region and rebuild both chains, half a gesture
            // before this handler does it properly. Silenced with its OWN flag and not with
            // _restoringSettings (review, E6.S4): that one means "a restore is in progress", it is
            // TRUE for the whole of startup, and a save handler raising it would make one flag
            // answer two questions — see the field's comment in MainWindow.xaml.cs. try/finally
            // because a suppression flag that can be left up is the bug it was meant to prevent.
            _suppressAzureRegionHandler = true;
            try
            {
                AzureRegionCombo.SelectedItem = null;
                AzureRegionCombo.Text = "";
            }
            finally { _suppressAzureRegionHandler = false; }
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

    // ============================================================
    //  E6.S5 — "Test key"
    // ============================================================

    /// <summary>
    /// The two labels and Azure's cost tooltip, set ONCE, from the constructor. Not from
    /// <see cref="UpdateEngineStatusUi"/>: that method runs on every save and every region change,
    /// and a refresh landing while the other button reads "Testing…" would rewrite a label the
    /// in-flight <c>finally</c> is about to restore. They are not persisted state either, so
    /// <c>_restoringSettings</c> has nothing to say about them (I12).
    ///
    /// <para>The copy is <see cref="UserMessages"/>' and not the XAML's (ruling GAP-4): the labels
    /// differ — DeepL's check is free, Azure's is not — and AC 3's "the copy must not claim a free
    /// check that is not free" needs exactly one place to be right.</para>
    /// </summary>
    private void SetKeyTestLabels()
    {
        DeepLTestButton.Content = UserMessages.TestKeyLabel();
        AzureTestButton.Content = UserMessages.TestKeyLabelCosts();
        AzureTestButton.ToolTip = UserMessages.TestKeyCostsTooltip();
    }

    /// <summary>
    /// The window-lifetime half of a key test's bound (T3, review). Every in-flight test links its
    /// own budget to this token, so one gesture ends them all; it is REPLACED rather than reused
    /// after a cancel, because the next press must not find a token that is already spent.
    /// </summary>
    private CancellationTokenSource _keyTestsCts = new();

    /// <summary>
    /// End any key test still in flight, because something just made its answer wrong or unwanted.
    /// The shape is <c>CancelReadOnce</c>'s (<c>MainWindow.Ocr.cs</c>) and so are the callers:
    ///
    /// <list type="bullet">
    /// <item><b>Closing the window</b> — T3 asked for this and the review found it missing. The
    ///       surfaces the result would land on are going away with the window, and a 12 s probe
    ///       that outlives them has nothing to render into.</item>
    /// <item><b>A key save or a region change</b> — the answer in flight is about credentials that
    ///       are no longer the ones in effect. Without this the LATE test result overwrites the
    ///       honest line the save just wrote, which is the ownership rule below read backwards: a
    ///       cleared Azure key could be followed, seconds later, by "✓ Key works (westeurope)".</item>
    /// </list>
    ///
    /// <para>Cancelling is enough on its own: <see cref="RunKeyTestAsync"/> tells its own budget
    /// from this cancel and renders nothing when it is this one, so the line the save wrote
    /// stands.</para>
    /// </summary>
    private void CancelKeyTests()
    {
        var live = _keyTestsCts;
        _keyTestsCts = new CancellationTokenSource();   // replaced FIRST, so nothing re-entered cancels the new one
        // Not disposed, for the reason CancelReadOnce does not dispose either: a source with no
        // timer holds nothing, and a linked child built before the swap still has a registration
        // on it — disposing underneath that child buys nothing and costs an edge case.
        live.Cancel();
    }

    /// <summary>
    /// DeepL's check, which spends nothing (<c>GET /v2/usage</c>, ruling E6-b).
    ///
    /// <para>The key is read off the BOX and handed in as a parameter, so this path cannot write
    /// <c>_settings</c> even by accident: AC 1's "a failed test does not clear the key" is a
    /// property of the method's shape and not of its discipline. Nothing here is persisted, so no
    /// <c>_restoringSettings</c> guard and no <c>SettingsVersion</c> step (I12/I13).</para>
    /// </summary>
    private async void DeepLTestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = (DeepLKeyBox.Password ?? "").Trim();
        if (key.Length == 0)
        {
            // Nothing to test, and §3.7's "cleared" row is what the line already says when there is
            // no key — so the refresh IS the answer, and no request is made to earn it.
            UpdateDeepLStatus();
            return;
        }

        await RunKeyTestAsync(DeepLTestButton, DeepLFeedback, ProviderIds.DeepL, "",
            ct => new DeepLTranslator(key).TestKeyAsync(ct));
    }

    /// <summary>
    /// Azure's check, which sends one five-character translation and whose button says so (AC 3).
    /// The half-entered credential is refused by the same predicate the Save button uses, because
    /// a key with no region is a guaranteed 401 that would cost a real gate strike (E6.S3 AC 5).
    /// </summary>
    private async void AzureTestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = (AzureKeyBox.Password ?? "").Trim();
        var region = ReadAzureRegion();

        if (key.Length == 0)
        {
            // The CONFIGURED state, re-derived — not the "no key" literal (review). An emptied box
            // over a key that is still saved is a real gesture (select-all, then paste), and the
            // literal claimed Azure was gone while both chains were still spending it. This is the
            // same answer the DeepL branch gives four lines up, from the same source of truth.
            UpdateEngineStatusUi();
            return;
        }

        var problem = TranslationChains.AzureCredentialProblem(key, region);
        if (problem != null)
        {
            AzureFeedback.Text = problem;   // transient (E7.S7): AzureStatus keeps the standing state
            return;
        }

        await RunKeyTestAsync(AzureTestButton, AzureFeedback, ProviderIds.Azure, region,
            ct => new AzureTranslator(key, region).TestKeyAsync(ct));
    }

    /// <summary>
    /// The in-flight state both buttons share (AC 1): disabled, relabelled, bounded, and put back
    /// in a <c>finally</c> — and the result rendered into the block's own status line,
    /// <b>never</b> a <c>MessageBox</c> (UX principle 3, NFR12, <c>ux</c> §4.3's control table).
    ///
    /// <para><b>The <c>finally</c> is not optional.</b> A throw between the relabel and the restore
    /// leaves the button permanently "Testing…" and disabled, with no way back but a restart — the
    /// same failure class E5.S4's review found with <c>_readingOnce</c> sitting above its
    /// <c>try</c>. For the same reason the label is captured rather than assumed: the two buttons
    /// carry different text.</para>
    ///
    /// <para><b>Re-entrancy</b> is the button itself — a second press while one is in flight finds
    /// it disabled and returns. This is not the shared-OCR-engine case that needed a field.</para>
    ///
    /// <para><b>Bounded</b>, because a test that hangs leaves the button dead — by
    /// <see cref="TranslationPolicy.KeyTestBudgetSeconds"/>, which is a BUDGET and not a request
    /// timeout (review). The probe goes through <c>HttpProviderCore</c>, so it is one logical call
    /// of up to <see cref="TranslationPolicy.MaxAttempts"/> requests: bounding it by a single
    /// request's 12 s deleted the core's retry from this path and, because the cut is a genuine
    /// cancel the core lets past unreported, stranded a half-open probe that then refused every
    /// real translation until the gate re-armed itself. <b>I3</b> applies to the catch — an
    /// <c>HttpClient</c> timeout is an <c>OperationCanceledException</c> whose token is NOT
    /// cancelled, so the filter asks the SOURCE and never the exception type. A cancelled token is
    /// either that budget, rendered as the timeout it is, or <see cref="CancelKeyTests"/> — a
    /// close, a save, a region change — which renders nothing, because whatever cancelled it has
    /// already written the truer line.</para>
    ///
    /// <para><c>internal</c> so the suite can drive it with its own probe: the failure path, the
    /// restored label and the untouched settings are the assertions that matter here, and none of
    /// them may need a network.</para>
    /// </summary>
    /// <param name="budget">The bound, injectable for the suite alone (IS-7's rule: a test may
    /// never assert a timing by waiting for one — CI-3). Production passes nothing and gets the
    /// 12 s a single request already gets.</param>
    internal async Task RunKeyTestAsync(Button button, TextBlock status, string providerId,
        string region, Func<CancellationToken, Task<KeyTestResult>> probe, TimeSpan? budget = null)
    {
        if (!button.IsEnabled) return;              // a test is already running — the button is the flag

        var label = button.Content;                 // restore EXACTLY what was there (the labels differ)
        button.IsEnabled = false;
        button.Content = UserMessages.TestingLabel();

        // Two bounds, one token. The window-lifetime source is what a close, a save or a region
        // change cancels (CancelKeyTests); the budget is this press's own ceiling. `superseded` is
        // the read-once `_readOnceStopped` idea in its smallest form: only the BUDGET has something
        // to say, because the other three gestures have already written the line a result would
        // overwrite with an answer about credentials that are no longer current.
        var lifetime = _keyTestsCts;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cts.CancelAfter(budget ?? TimeSpan.FromSeconds(TranslationPolicy.KeyTestBudgetSeconds));
        try
        {
            var result = await probe(cts.Token);
            // CountdownJoinText: §3.7's paused row joins "Try again in {t}.", so under §2.4's floor
            // there is no duration to join and the clause is dropped instead (E7.S2 — the same rule
            // read-once's PausedTryAgainIn follows, for the same grammatical reason).
            status.Text = UserMessages.KeyTestSentence(providerId, result, region,
                CountdownJoinText(result.SecondsUntilRetry));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
                status.Text = UserMessages.KeyTestSentence(providerId,
                    KeyTestResult.Failed(TranslationErrorKind.Timeout), region, null);
        }
        catch (Exception ex)
        {
            status.Text = UserMessages.KeyTestSentence(providerId, ex, region);
        }
        finally
        {
            button.Content = label;
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// The About tab's engine lines, refreshed in one place. Called explicitly from
    /// <c>ApplySettings</c> (where every change handler is suppressed, so a side effect that is not
    /// applied by hand does not happen at all — I12) and after each key save.
    ///
    /// <para>Deliberately small, with one job: <b>E6.S4</b> adds the read opt-in's side effect and
    /// <b>E7.S7</b> grows it into §4.2's "In use now" + chain block. One method, so three stories
    /// extend it instead of fighting over three.</para>
    ///
    /// <para><b>Who owns the two status lines</b> (E6.S5, and it is the rule that keeps a test
    /// result from being erased half a second later): this method and the Save handlers write the
    /// <i>configured</i> state — what the app will do with the key it has. A <b>Test</b> result
    /// overwrites that with what a real request just found out, and the next Save, region change or
    /// restart resets it.</para>
    ///
    /// <para>"The next Save resets it" is only true because a Save also <b>cancels</b> the test
    /// (<see cref="CancelKeyTests"/>) — the review found the ordering claimed here read backwards
    /// without it. A test can run for a whole budget, so the last writer would otherwise be the
    /// STALE one: clear the Azure key, press Save, and the probe launched beforehand would land
    /// afterwards saying "✓ Key works (westeurope)" over a settings file with no Azure key in
    /// it.</para>
    /// </summary>
    private void UpdateEngineStatusUi()
    {
        UpdateDeepLStatus();

        // A key save adds or removes a tier, so the chip's tooltip has a different chain to list
        // and a keyed provider may have just stopped being "— not set" (E7.S3 T5: repaint on the
        // events that already exist). ClearAuthBlock has run by now on the save paths, so a
        // corrected key stops reading "⚠ DeepL key refused" here rather than after a translation.
        UpdateEngineChip();

        var region = (_settings.AzureRegion ?? "").Trim();
        // E7.S7 — the SENDABILITY predicate, not `hasKey && region.Length > 0`. E6.S4's review
        // recorded that expression as not the rule the builder applies: a region carrying a control
        // character (a line break pasted from the portal, persisted by AzureRegionCombo_Changed,
        // which trims and lower-cases but does not filter) made this line claim an engine BuildWrite
        // adds no tier for. One expression of the rule, in TranslationChains, shared with the Save
        // button's refusal and with the read chain's own predicate below.
        var configured = TranslationChains.AzureWritesWhatYouType(_settings);
        // Both halves, matching what TranslationChains.BuildWrite actually does with them: a status
        // line claiming a configured engine over a credential that adds no tier is the lie this
        // story is here to prevent.
        //
        // …and since E6.S4 the line also has to say WHICH PATHS spend the key, because that is now
        // the user's choice and not a constant. `AzureReadsTheScreen` is the predicate BuildRead
        // itself is written from (Services/TranslationChains.cs), so the sentence and the read
        // chain's first tier cannot disagree — the whole point of asking the rule rather than
        // re-deriving it from three fields here.
        AzureStatus.Text = !configured
            ? UserMessages.AzureNoKeyStatus()
            : TranslationChains.AzureReadsTheScreen(_settings)
                ? UserMessages.AzureKeySetForReadingStatus(region)
                : UserMessages.AzureKeySetStatus(region);

        // The opt-in's own controls. Disabled with nothing to opt into: a tickable box over an
        // empty key box promises a choice the chain would ignore. IsEnabled raises no Checked /
        // Unchecked, so this is safe to run inside the restore (I12) — and it must run there,
        // because ApplySettings suppresses the handler that would otherwise have done it.
        AzureForReadingCheck.IsEnabled = configured;
        AzureForReadingHint.Text = UserMessages.AzureForReadingHint();

        // E7.S7 — the transient lines are cleared by the same refresh that rewrites the standing
        // ones. Every caller of this method is an event that SUPERSEDES a transient answer: a key
        // save, a region change, the opt-in toggling, the restore. A refusal or a Test result that
        // survived one of those would be an answer about credentials that are no longer current —
        // which is the ownership problem E6.S3's review recorded when both shared one TextBlock.
        DeepLFeedback.Text = "";
        AzureFeedback.Text = "";
    }

    /// <summary>
    /// The About block's static copy, set ONCE from the constructor — the same shape and the same
    /// reason as <see cref="SetKeyTestLabels"/>: the sentences <c>ux</c> §4.2 specifies live in
    /// <see cref="UserMessages"/> (ruling GAP-4 / UX-DR19) so the "exactly once" scan can see them
    /// and E7.S8's README can quote the same words, and none of them is persisted state, so
    /// <c>_restoringSettings</c> has nothing to say about them (I12).
    ///
    /// <para><b>T5's rule, written down</b>: the sentences §3/§4 specify go here; the About tab's
    /// static page prose — the two key paragraphs with their <c>Hyperlink</c>s, the shortcut list,
    /// the author links — stays in the XAML. Moving all of it into a code table would be silly, and
    /// leaving the specified sentences in the XAML would make UX-DR19 unassertable.</para>
    /// </summary>
    private void SetAboutBlockCopy()
    {
        EnginesIntroText.Text = UserMessages.AboutEnginesIntro();
        EnginesPausesText.Text = UserMessages.AboutEnginesPauses();
        FirstLaunchExpectationText.Text = UserMessages.FirstLaunchExpectation();
        KeysIntroText.Text = UserMessages.AboutKeysIntro();
        // The offline block's row and button are NOT set here any more (E8.S3): they have two states
        // and a transient third, so they belong to UpdateOfflineEngineUi, which ApplySettings calls
        // explicitly a few lines later (I12). A Content attribute or a one-shot assignment here
        // would be a second spelling of a label that changes.
        CachePrivacyText.Text = UserMessages.CachePrivacyLine();
        ClearCacheButton.Content = UserMessages.ClearCacheLabel();
        // …and the offline block's ONE label that does not change: Cancel is Cancel whether it is
        // visible or not, so it belongs with the static copy rather than in UpdateOfflineEngineUi,
        // which would rewrite it on every restore for no reason.
        OfflineCancelButton.Content = UserMessages.OfflineCancelLabel();
    }

    /// <summary>
    /// <b>Amendment A10's button.</b> A statement that the app stores your chat with no way to
    /// remove it is the exact shape principle 2 forbids, so the privacy sentence above it comes with
    /// this: the store is emptied in memory and its file deleted, and the count comes back for the
    /// sentence that reports it.
    ///
    /// <para><b>No confirmation dialog</b> (§4.3) — unlike <c>Remove</c> for the offline engine,
    /// clearing the cache destroys nothing the app cannot rebuild; the cost is a few extra requests.
    /// <b>No <c>MessageBox</c> either</b>: the answer goes to this block's own status line, like
    /// every other result on this tab.</para>
    ///
    /// <para><b>It names a chain and never a store.</b>
    /// <c>TranslationCachePersistenceTests.No_source_outside_Services_names_the_store…</c> asserts
    /// that no production source outside <c>Services/</c> mentions the store type at all — the same
    /// rule that keeps <c>ProviderGates</c> out of this file (ruling E3-c) — so the facade is
    /// <c>TranslationChains.ClearCache()</c>, and the ordering that makes a save queued before the
    /// click unable to resurrect the file lives with the store itself.</para>
    ///
    /// <para>Nothing is persisted, so there is no <c>_restoringSettings</c> guard and no
    /// <c>SettingsVersion</c> step (I12/I13). Synchronous on purpose: the delete is one file
    /// operation and the load it may do first is ≈10 ms for a full cache (E4.S3/E4.S5), on a gesture
    /// the player is waiting for an answer to.</para>
    /// </summary>
    private void ClearCache_Click(object sender, RoutedEventArgs e)
        => CacheStatus.Text = UserMessages.CacheClearedStatus(TranslationChains.ClearCache());

    private void UpdateDeepLStatus()
    {
        bool on = (_settings.DeepLApiKey ?? "").Trim().Length > 0;
        // Both lines are the deck's since E7.S1 (GAP-4) — they were the last user-facing
        // sentences still living in a code-behind. The "off" one is §3.7's `cleared` row, which
        // E6.S5's review found was not in the codebase at all for DeepL: "○ Using Google (free, no
        // key needed)" names the fallback without ever naming what is missing.
        DeepLStatus.Text = on ? UserMessages.DeepLKeySetStatus() : UserMessages.DeepLNoKeyStatus();
    }
}

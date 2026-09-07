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

public partial class MainWindow : Window
{
    private readonly List<Phrase> _allPhrases = new();
    private CollectionViewSource? _phrasesView;
    private bool _recentsDirty;   // a phrase was copied; refresh "Recent" next time the tab is shown

    // True while the UI is being built or restored, i.e. whenever a control change does NOT mean
    // "the user chose this". Change handlers (SaveOcrFilterSettings, CaptureBackend_Changed,
    // SquadUppercase_Changed, AzureRegionCombo_Changed, AzureForReading_Changed) fire as a side
    // effect of setting a slider / combo / tick — and an EDITABLE combo raises SelectionChanged the
    // same way — and would then write that transient UI state back to disk, clobbering the very
    // settings we're loading.
    //
    // It starts TRUE and is only cleared at the end of ApplySettings, because XAML LOADING ITSELF
    // fires these handlers: `<Slider Value="70" ValueChanged="OcrTolerance_Changed"/>` raises
    // ValueChanged during InitializeComponent(), long before ApplySettings runs. That handler then
    // read the still-unselected filter combo (SelectedTag → null → "off") and persisted "off" —
    // which is why a saved "Boost contrast" came back Off on every launch (fixed in v0.13.0).
    private bool _restoringSettings = true;

    // …and the one thing _restoringSettings is deliberately NOT (review, E6.S4). Ruling E6-e makes
    // an empty Azure key clear the region with it, which means AzureSaveKey_Click empties the region
    // combo in code — raising SelectionChanged, which AzureRegionCombo_Changed would answer by
    // persisting the OLD key with no region, half a gesture before the save handler does it
    // properly. The obvious silencer is _restoringSettings, and it is the wrong one: that flag means
    // "a RESTORE is in progress, no handler may persist", it starts TRUE for the whole of startup,
    // and borrowing it inside a SAVE handler makes one flag answer two questions — the next reader
    // cannot tell which, and a handler that should have run (or one that persists mid-save) is the
    // exact bug class it was introduced to guard against. This flag is narrow on purpose: ONE
    // handler, ONE gesture, always raised and lowered in a try/finally around the two lines that
    // empty the combo.
    private bool _suppressAzureRegionHandler;

    // Three translators on purpose (the chains themselves are TranslationChains', §8.1):
    //   _writeTranslator     — what the USER writes (Translator tab, compact quick reply). The
    //                          user's DeepL key first when they have one, then the free tiers.
    //                          Rebuilt when the key changes (BuildWriteChain, Translate.cs).
    //   _readTranslator      — the LIVE feed. Free tiers only — DeepL is absent BY CONSTRUCTION
    //                          (I8), because a loop translating every new chat line would drain
    //                          DeepL's one-time free million characters in days. Its providers ask
    //                          the gate as Background (§5.4's reserve): the loop may draw the token
    //                          bucket down but never takes the last token.
    //   _readOnceTranslator  — the same free chain over the same gates, but Interactive: read-once
    //                          is a user click waiting on an answer (ruling OQ-a). A second chain
    //                          instance rather than a per-call argument, because ITranslator may
    //                          not grow a priority parameter (I1) and the gates are process-global,
    //                          so the two instances cost a few bytes and share their state.
    //
    // All three are cache decorators over the SAME store since E4.S4 (§8.2): the one a chain
    // translated is the one the other two get for free, and it is the same store across a DeepL key
    // save, which rebuilds _writeTranslator alone.
    //
    // ALL THREE ARE ASSIGNED IN THE CONSTRUCTOR BODY, not here: field initializers run in
    // declaration order, and _settings (:65) is initialised AFTER these lines. A chain needs the
    // settings, so an initializer here would read a null. See the ctor.
    //
    // NONE OF THE THREE IS readonly SINCE E6.S3, and the read pair lost it for a reason worth
    // stating: a key save has to rebuild the READ chain too, because the key may already be opted
    // into reading from a previous session (AppSettings.UseKeyForReading). RebuildReadChains()
    // (Translate.cs) is the only writer, and it reassigns _readTranslator, _readOnceTranslator and
    // _readChain TOGETHER — see _readChain's own comment for why forgetting one of the three would
    // not show up as a failing behaviour test.
    private ITranslator _writeTranslator;
    private ITranslator _readTranslator;
    private ITranslator _readOnceTranslator;

    // The CHAIN inside _readTranslator — the same object, one decorator down. It is a field because
    // the LIVE loop has to ask a question a translator cannot answer: "is every rung of you inside a
    // block window right now?" (ChainTranslator.PauseNow, E5.S1). On a yes the loop skips the whole
    // tick — no capture, no OCR, no request — and a translation outage costs the game nothing.
    //
    // ONE field for both read paths on purpose: read-once's chain is a second instance over the SAME
    // process-global gates (I9), so this one's answer is also its own. Asking the registry directly
    // would be the obvious alternative and is exactly what TP-START-02 forbids outside Services/ —
    // the code-behind names a chain, never ProviderGates.
    //
    // E7.S3 reads ChainTranslator.LastOutcome from this same field.
    //
    // It is reassigned — with the two read translators, in the same method — whenever a key save
    // rebuilds the read chain (E6.S3). A rebuild that replaced _readTranslator and left this
    // pointing at the old chain would still WORK, because both resolve the same process-global
    // gates (I9), which is exactly why it would go unnoticed: the pause check would be answering
    // for a chain nothing translates through any more. All three, or none.
    private ChainTranslator _readChain;

    // What the Translator tab is currently showing. Its output is a RichTextBox (so the 78-character
    // chat blocks can be tinted), and a FlowDocument's text can't be read back cleanly — so the
    // plain string lives here, and that is what Copy / Swap use.
    private string _lastTranslation = "";
    private readonly UpdateService _updates = new();
    private readonly AppSettings _settings = SettingsService.Load();
    private OcrService _ocr = new("ru");
    private SlangGlossary _slang = SlangGlossary.FromJson(null);
    private SquadCatalog _squad = SquadCatalog.FromJson(null);
    private readonly ObservableCollection<OcrResultItem> _ocrItems = new();

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    /// <summary>The app's ONE countdown timer (ux-mode-degrade §2.4, ruling OQ-c/R-2 — "poll at
    /// 1 Hz, no event out of <c>Services/</c>"). It is a performance object as much as a UI one:
    /// NFR7 allows one 1 Hz repaint while something is paused and nothing at all while nothing is,
    /// and the 600 ms heartbeat stays the fastest thing on screen.
    ///
    /// <para>Constructed, never started: it runs only between <see cref="EnsureCountdownRunning"/>
    /// and the first tick that finds nothing paused, which is why <b>a healthy app has no countdown
    /// timer running at all</b> — and why nothing here happens before the first paint (I10).</para>
    ///
    /// <para>It is not the overlay's 600 ms blink (a different window, a different rate, a different
    /// owner — E7.S4) and not <see cref="_toastTimer"/>; §2.4's "one <c>DispatcherTimer</c> for the
    /// whole app" means one COUNTDOWN, not one timer in the process.</para></summary>
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Tab indices (order must match the TabControl in XAML):
    // Phrasebook(0) · Squad(1) · Translator(2) · Screen OCR(3) · About(4).
    private const int TabTranslator = 2, TabScreenOcr = 3;

    // The Windows OCR language pack we install / show the command for (single source).
    private const string OcrCapability = "Language.OCR~~~ru-RU~0.0.1.0";

    // --- live screen translation ---
    private CancellationTokenSource? _liveCts;
    private bool _selectingRegion;                       // a screen-area drag is in progress
    private bool _readingOnce;                            // a one-shot Ctrl+Alt+R / read-once is mid-flight
    // The read-once in flight, and WHY it was cancelled. Both are needed: a person's Stop and the
    // 30 s budget cancel the very same token, so the exception they raise is identical by design
    // (I3) and the reason cannot be recovered from it. It is recorded at the cancel site instead —
    // a stop renders nothing at all (ux-mode-degrade §2.1: "Cancelled" is not a state), a budget
    // expiry is a failure the player has to be told about (E5.S4).
    private CancellationTokenSource? _readOnceCts;
    private bool _readOnceStopped;
    private System.Drawing.Rectangle? _liveRegion;
    private LiveDedup _dedup = new();                     // decides which lines are genuinely new
    private int _liveTicks;
    private const int MaxHistory = 50;                   // keep the last 50 translated messages
    // Which lines are "new enough" to translate is decided by LiveDedup: it works on a
    // letter/digit-only signature (so animated emojis and colour flicker don't register as new
    // text), remembers what it has already shown, and only re-translates a message after it has
    // actually scrolled off screen for a while. A two-frame confirmation still guards OCR
    // garbage. The Sensitivity slider tunes how similar counts as "the same message", the
    // Stability slider how strict the confirmation is.

    public MainWindow()
    {
        // The three chains, built ONCE, here, and above InitializeComponent() (§8.1). Two reasons,
        // both of which have cost this codebase a bug:
        //   · _settings is a field initializer (:65) and therefore already loaded, while a chain in
        //     a field initializer of its own would run BEFORE it and read a null. That is why
        //     _readTranslator lost its initializer. It is no longer readonly either (E6.S3: a key
        //     save rebuilds the read chain), so the guarantee is now RebuildReadChains() being the
        //     only other writer — see the field comments above.
        //   · InitializeComponent() fires change handlers (see _restoringSettings above), so
        //     anything a handler could reach must already exist by the time it runs.
        // Nothing here touches a control, and nothing here reads provider-state.json OR
        // translation-cache.json: the registry only CONSTRUCTS gates at this point and the shared
        // cache store only constructs a map (I10) — the first request loads the one, the first cache
        // MISS loads the other, both after first paint.
        //
        // Each of the three is a thin cache decorator over ONE shared store — TranslationChains'
        // (§8.2, E4.S4) — so a line any of the three translated is free to the other two. That
        // wrapping is the builder's, not this file's: the code-behind names a chain and never a
        // decorator or a store, for the same reason it never names ProviderGates (ruling E3-c).
        _writeTranslator = BuildWriteChain();
        _readTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Background, out _readChain);
        _readOnceTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Interactive);
        InitializeComponent();                  // fires change handlers — _restoringSettings guards them
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        // Subscribing is not starting (I10): the countdown ticks for the first time only when
        // something tells it a pause has begun, which cannot happen before the first request and so
        // cannot happen before the first paint.
        _countdownTimer.Tick += (_, _) => CountdownTick(_readChain.PauseNow());

        // E6.S5 — the two "Test key" labels, from the copy deck, once. Not in the XAML (GAP-4) and
        // not in UpdateEngineStatusUi: a refresh landing mid-test would rewrite a label the
        // in-flight finally is about to restore. Nothing persisted, so _restoringSettings does not
        // apply — but it must run after InitializeComponent, because the buttons exist only then.
        SetKeyTestLabels();

        OcrResults.ItemsSource = _ocrItems;
        OcrCommandBox.Text = $"Add-WindowsCapability -Online -Name \"{OcrCapability}\"";
        ShowAppVersion();
        PopulateLanguageCombos();
        LoadPhrases();
        LoadSlang();
        LoadSquad();
        BuildSquadTab();
        // NOT CheckOcrAvailability() — it reads OcrService.IsAvailable, and the first such read is
        // what builds the Windows OCR engine (26–38 ms, measured). Doing it here put that in front
        // of the first paint on every launch. It now runs from OnWindowLoaded, off the UI thread,
        // once the window is already on screen. The Screen OCR tab it writes to is not the one you
        // land on, so nobody sees the difference — except in the time to first paint.
        ApplySettings();
        // The chip's first paint (E7.S3). It is a REPAINT and never a start: RefreshEngineChip
        // reads snapshots and never a file — ProviderGates.All() enumerates the registry the three
        // chains above have just populated and asks each gate for its immutable record — so nothing
        // here reads provider-state.json (I10). What it renders is therefore "checking…", because
        // _gateStateKnown is false until OnWindowLoaded's warm-up lands (ruling E6-a); it is written
        // now rather than left blank so the chip never appears out of nothing a second later.
        RefreshEngineChip();
        // Track "my language" only from here on, so the init-time combo changes above
        // (and the translator's auto-flip to Russian) don't overwrite it.
        FromCombo.SelectionChanged += FromCombo_SelectionChanged;
        Loaded += OnWindowLoaded;
    }

    // Remember the user's own language whenever they pick a real (non-Russian) source,
    // so quick replies from the compact overlay always translate FROM the right language.
    private void FromCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var code = SelectedTag(FromCombo);
        if (code is not (null or "ru" or "auto"))
        {
            _settings.MyLanguage = code;
            SettingsService.Save(_settings);
        }
    }

    // Set when a data file the user could have edited (squad.json, slang.json) was replaced by a
    // newer shipped one at startup. Replacing it silently would be rude — their edits are in the
    // backup, and they have to be told the backup exists. Shown once the window is up.
    private readonly List<string> _dataRefreshNotes = new();

    /// <summary>Remember that an editable data file was refreshed, to tell the user when the window
    /// appears (LoadPhrases/LoadSquad/LoadSlang run in the constructor, before there is anything to
    /// show it on). A LIST, not one slot: an upgrade that bumps two files at once used to report
    /// only the last one, silently hiding that the other had been backed up too.</summary>
    private void NoteDataFileRefreshed(string fileName, string backupName)
        => _dataRefreshNotes.Add($"{fileName} was updated to this version's list — your previous copy is saved as {backupName}");

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // (No first-run welcome dialog — the app opens straight to the tabs.)

        // Build the OCR engine now that the window is up, on a worker thread so its WinRT work
        // doesn't freeze the UI we just showed. Touching IsAvailable is what forces the lazy
        // engine; CheckOcrAvailability then only reads the finished result and writes the Screen
        // OCR tab's status labels.
        await Task.Run(() => _ocr.IsAvailable);
        CheckOcrAvailability();

        if (_dataRefreshNotes.Count > 0) ShowToast(string.Join("   ·   ", _dataRefreshNotes));

        // Run the update check once the window is up, so the dialog has an owner and
        // appears in front of our always-on-top window instead of behind it.
        await CheckForUpdatesAsync();

        // ---- Ruling E6-a: the gate state, read AFTER first paint, on a pool thread -------------
        //
        // TP-START-04 wants the chip to show a saved pause "at first paint"; I10 forbids reading
        // provider-state.json before it. E6-a is the settlement, and this is the whole of it: the
        // file is read here — last in this handler, so it is behind every await above and therefore
        // long after the window is on screen — and the chip reads "checking…" until it lands.
        //
        // It names TranslationChains and not ProviderGates (ruling E3-c / TP-START-02), it runs on
        // the pool because a sub-kilobyte synchronous read still has no business on the dispatcher,
        // and it is guarded because a status warm-up may not be able to fail a launch. The flag is
        // set whatever happens: a chip stuck on "checking…" for ever would be a worse lie than a
        // chip reporting an unseeded gate as ready.
        //
        // The chain is captured into a local first: the lambda runs on the pool, and _readChain is
        // reassigned on the dispatcher by a key save (RebuildReadChains). Either instance would be
        // correct — both resolve the same process-global gates (I9) — and reading the field once
        // here says so deliberately instead of by luck.
        var chain = _readChain;
        try { await Task.Run(() => TranslationChains.EnsureGateStateLoaded(chain)); }
        catch (Exception ex) { Logging.Warn("gate state warm-up did not finish: " + ex.Message); }
        _gateStateKnown = true;
        // …and the first paint of the chip that is worth anything. UpdateEngineChip, not
        // RefreshEngineChip: a pause restored from disk has a countdown to step, and this is the one
        // start site that exists before any request has been made.
        UpdateEngineChip();
    }

    // ============================================================
    //  SETTINGS (persist between runs)
    // ============================================================
    private void ApplySettings()
    {
        // Suppress the OCR-filter / capture-backend / squad-case change handlers for the whole
        // restore: setting a slider, combo or tick below fires them, and they'd write the
        // half-restored UI state back to disk (e.g. clobber a saved "color" mode with "off"
        // because the combo isn't set yet). Already true since construction — see the field.
        _restoringSettings = true;
        try
        {
            var s = _settings;
            SelectTag(FromCombo, s.TranslatorFrom);
            SelectTag(ToCombo, s.TranslatorTo);
            SelectTag(OcrTargetCombo, s.OcrTargetLang);
            SensitivitySlider.Value = Math.Clamp(s.SensitivityPercent, 0, 100);
            LiveSpeedSlider.Value = Math.Clamp(s.LiveSpeedPercent, 0, 100);
            MinFragmentSlider.Value = Math.Clamp(s.MinFragmentLetters, 1, 6);
            StabilitySlider.Value = Math.Clamp(s.StabilityPercent, 0, 100);
            TopmostCheck.IsChecked = s.AlwaysOnTop;
            Topmost = s.AlwaysOnTop;
            AutoCopyCheck.IsChecked = s.AutoCopyTranslation;
            SquadUppercaseCheck.IsChecked = s.SquadUppercase;
            RebuildSquadPhrase();   // the tick is restored above; re-apply its casing to the phrase
            if (s.LastTab >= 0 && s.LastTab < MainTabs.Items.Count)
                MainTabs.SelectedIndex = s.LastTab;

            // Restore window placement only if it still lands on a visible monitor.
            if (s.WindowLeft is { } l && s.WindowTop is { } t &&
                s.WindowWidth is > 200 and { } w && s.WindowHeight is > 200 and { } h &&
                IsOnScreen(l, t, w, h))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t; Width = w; Height = h;
            }

            DeepLKeyBox.Password = s.DeepLApiKey ?? "";
            AzureKeyBox.Password = s.AzureApiKey ?? "";
            // SelectTag silently does nothing when no item matches — and with IsEditable="True"
            // that is the NORMAL case, not an edge one: any region outside the nine seeded ones is
            // free text and has no ComboBoxItem to select. Without the .Text fallback the box comes
            // back blank on every launch while settings.json still holds "norwayeast".
            SelectTag(AzureRegionCombo, s.AzureRegion ?? "");
            if (AzureRegionCombo.SelectedItem == null) AzureRegionCombo.Text = s.AzureRegion ?? "";
            // E6.S4 / AC 4, and the work is in what is NOT here: UseKeyForReading is a new field
            // with default false, so an old settings.json from a user who already has an Azure key
            // deserialises it to false and the box comes back unticked — no Migrate step, no
            // SettingsVersion bump (I13). The way to break AC 4 is to "helpfully" seed it from
            // AzureApiKey.Length > 0; an opt-in that arrives pre-ticked is not an opt-in (OQ-12).
            AzureForReadingCheck.IsChecked = s.UseKeyForReading;
            // The change handlers are suppressed for this whole method, so the UI side-effects they
            // would have produced are applied EXPLICITLY — the same rule as UpdateOcrFilterUi
            // below, applied to the key boxes (I12). UpdateDeepLStatus is now one line inside it.
            UpdateEngineStatusUi();

            OcrColorHexBox.Text = s.OcrKeepColorHex ?? "#FFFFFF";
            OcrToleranceSlider.Value = Math.Clamp(s.OcrColorTolerance, 0, 441);
            SetOcrFilterCombo(s.OcrFilterMode ?? "off");
            // The change handler is suppressed above, so apply the mode-dependent UI side-effects
            // (colour-options panel visibility + tolerance label) it would otherwise have produced.
            UpdateOcrFilterUi(s.OcrFilterMode ?? "off");

            ScreenCapture.SetMode(s.CaptureBackend);
            SetCaptureBackendCombo(s.CaptureBackend ?? "gdi");

            UpdateResumeLiveButton();
            ApplyFontScale();
        }
        finally { _restoringSettings = false; }
    }

    private static bool IsOnScreen(double left, double top, double w, double h)
    {
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        // The title bar must be reachable: some horizontal overlap and the top on-screen.
        return left + w > vx + 60 && left < vx + vw - 60 && top >= vy && top < vy + vh - 20;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    /// <summary>True if a capture rectangle (PHYSICAL pixels) still overlaps the desktop.
    /// Uses GetSystemMetrics (physical px), not SystemParameters (DIP).</summary>
    private static bool RegionOnVirtualScreen(System.Drawing.Rectangle r)
    {
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                  SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (r.Width < 4 || r.Height < 4) return false;
        var screen = new System.Drawing.Rectangle(vx, vy, vw, vh);
        var hit = System.Drawing.Rectangle.Intersect(screen, r);
        // Require a meaningful overlap, not just a corner touching.
        return hit.Width >= 20 && hit.Height >= 10;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // A read-once in flight is ended by closing the window (AC 2, E5.S4). Nothing renders after
        // it — the surfaces are going away — which is exactly what a cancel is supposed to show.
        CancelReadOnce();

        // …and a "Test key" in flight, for the same reason and by the same shape (E6.S5 T3, which
        // asked for this and did not get it until the review). It also puts the cancel ABOVE
        // ProviderGates.Flush() below, so nothing is still racing to report a gate outcome into a
        // registry that has already been written to disk.
        CancelKeyTests();

        // …and the 1 Hz countdown, for a reason of its own: a DispatcherTimer roots its handler,
        // and this one's handler closes over the window that is going away (E7.S2).
        StopCountdown();

        // Write out any provider pause that is still inside its 1-second debounce, so a block the
        // user is waiting out survives the restart instead of being re-earned on the first request
        // (E2.S4, architecture §5.7). The ONE ProviderGates reference outside Services/ (ruling
        // E2-e) and the one piece of UI wiring in the whole of Epic 2: no control, no binding, so
        // _restoringSettings is not engaged. Best-effort and BOUNDED — not asynchronous: like the
        // SettingsService.Save immediately below it, this is one sub-kilobyte write to a directory
        // that is already being written on this very path. Outside the settings try/catch, because
        // neither save may be skipped because the other threw.
        ProviderGates.Flush();

        // …and the translation cache that is still inside its 5-second debounce, so a session's
        // last few translations survive the restart instead of being re-earned (E4.S2,
        // architecture §8.2). Same shape and same reasoning as the line above it: one bounded
        // synchronous write, outside the settings try/catch because neither save may be skipped
        // because the other threw; no control, no binding, so _restoringSettings is not engaged.
        // The facade, not the store: TranslationChains already owns the composition the code-behind
        // is not allowed to name (ruling E3-c), and it owns the shared cache for the same reason.
        // Since E4.S4 all three chains cache into that one store, so what this writes is the whole
        // session — the LIVE feed's lines and the Translator tab's alike, in one file.
        TranslationChains.FlushCache();

        try
        {
            var s = _settings;
            s.SensitivityPercent = (int)Math.Round(SensitivitySlider.Value);
            s.LiveSpeedPercent = (int)Math.Round(LiveSpeedSlider.Value);
            s.MinFragmentLetters = (int)Math.Round(MinFragmentSlider.Value);
            s.StabilityPercent = (int)Math.Round(StabilitySlider.Value);
            s.OcrTargetLang = SelectedTag(OcrTargetCombo) ?? s.OcrTargetLang;
            s.TranslatorFrom = SelectedTag(FromCombo) ?? s.TranslatorFrom;
            s.TranslatorTo = SelectedTag(ToCombo) ?? s.TranslatorTo;
            s.AlwaysOnTop = TopmostCheck.IsChecked == true;
            s.AutoCopyTranslation = AutoCopyCheck.IsChecked == true;
            s.LastTab = MainTabs.SelectedIndex;

            var b = RestoreBounds;   // correct even if maximised/minimised
            if (!b.IsEmpty)
            {
                s.WindowLeft = b.Left; s.WindowTop = b.Top;
                s.WindowWidth = b.Width; s.WindowHeight = b.Height;
            }
            SaveOverlayBounds();
            SettingsService.Save(s);
        }
        catch { /* saving preferences is best-effort */ }
    }

    // ---- text size ----
    internal double FontScale => _settings.FontScale;

    /// <summary>The user's own language code (for the overlay's quick-reply hint).</summary>
    internal string MyLanguage => _settings.MyLanguage is { Length: > 0 } m && m is not ("ru" or "auto") ? m : "en";

    private void ApplyFontScale()
    {
        // Clamp here too: a hand-edited settings.json could carry an absurd value that
        // would make the whole UI invisible or freeze layout.
        double s = _settings.FontScale = Math.Clamp(_settings.FontScale, 1.0, 1.6);
        PhraseList.LayoutTransform = new System.Windows.Media.ScaleTransform(s, s);
        OcrResults.LayoutTransform = new System.Windows.Media.ScaleTransform(s, s);
        _overlay?.ApplyFontScale(s);
    }

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => ChangeFontScale(-0.1);
    private void FontLarger_Click(object sender, RoutedEventArgs e) => ChangeFontScale(+0.1);

    private void ChangeFontScale(double delta)
    {
        _settings.FontScale = Math.Clamp(Math.Round(_settings.FontScale + delta, 1), 1.0, 1.6);
        ApplyFontScale();
        SettingsService.Save(_settings);
        ShowToast($"Text size {(int)Math.Round(_settings.FontScale * 100)}%");
    }

    // ============================================================
    //  COMPACT OVERLAY MODE
    // ============================================================
    private CompactOverlay? _overlay;

    /// <summary>The live feed, shared with the compact overlay so both show the same thing.</summary>
    internal ObservableCollection<OcrResultItem> LiveItems => _ocrItems;
    internal bool IsLive => _liveCts != null;

    // ============================================================
    //  SHARED HELPERS
    // ============================================================
    private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
        => Topmost = TopmostCheck.IsChecked == true;

    private void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { ShowToast("Couldn't open the link"); }
        e.Handled = true;
    }

    private async void CopyDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (await CopyToClipboardAsync("kizotis")) ShowToast("Discord copied: kizotis");
    }

    private async void CopyErrorReport_Click(object sender, RoutedEventArgs e)
    {
        var report = Services.Logging.ReadRecent();
        if (string.IsNullOrWhiteSpace(report))
        {
            ShowToast("No errors logged — nothing to copy 🙂");
            return;
        }
        if (await CopyToClipboardAsync(report))
            ShowToast("Error report copied — paste it to me on Discord");
    }

    // Single source of truth for the language dropdowns (was duplicated 3× in XAML).
    private readonly record struct Lang(string Name, string Code);
    private static readonly Lang[] SourceLangs =
    {
        new("English", "en"), new("French", "fr"), new("Spanish", "es"),
        new("German", "de"), new("Russian", "ru"), new("Auto-detect", "auto"),
    };
    private static readonly Lang[] TargetLangs =
    {
        new("Russian", "ru"), new("English", "en"), new("French", "fr"),
        new("Spanish", "es"), new("German", "de"),
    };
    private static readonly Lang[] OcrLangs =
    {
        new("English", "en"), new("French", "fr"), new("Spanish", "es"), new("German", "de"),
    };

    private void PopulateLanguageCombos()
    {
        Fill(FromCombo, SourceLangs, "en");
        Fill(ToCombo, TargetLangs, "ru");
        Fill(OcrTargetCombo, OcrLangs, "en");

        static void Fill(ComboBox combo, Lang[] langs, string defaultCode)
        {
            combo.Items.Clear();
            foreach (var l in langs)
                combo.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
            foreach (var obj in combo.Items)
                if (obj is ComboBoxItem { Tag: string code } ci && code == defaultCode)
                { combo.SelectedItem = ci; break; }
        }
    }

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static void SelectTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
            if (obj is ComboBoxItem ci && (ci.Tag?.ToString() == tag))
            {
                combo.SelectedItem = ci;
                return;
            }
    }

    private bool _copying;   // one copy at a time; extra requests just report false

    /// <summary>Copy text via <see cref="ClipboardService"/> (raw Win32 on a worker
    /// thread). The UI never freezes, whatever is holding the clipboard. WPF's
    /// Clipboard.SetDataObject was abandoned on purpose: its hidden blocking retries and
    /// OLE flush froze the app for seconds and failed on machines where Windows
    /// clipboard history (Win+V) is enabled.</summary>
    internal async Task<bool> CopyToClipboardAsync(string text)
    {
        if (_copying) return false;
        _copying = true;
        try
        {
            if (await ClipboardService.SetTextAsync(text)) return true;
            ShowToast("Couldn't copy — another app is blocking the clipboard. Try again.");
            return false;
        }
        finally { _copying = false; }
    }

    private void ShowToast(string message)
    {
        // In compact mode the main window (and its toast) are hidden — show it in the overlay.
        if (_overlay is { IsVisible: true }) { _overlay.SetStatus(message); return; }
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        // Give longer messages more time to be read.
        _toastTimer.Interval = TimeSpan.FromSeconds(message.Length > 40 ? 3.5 : 1.6);
        _toastTimer.Start();
    }

    // ============================================================
    //  THE COUNTDOWN (ux-mode-degrade §2.4 / amendment A9, E7.S2)
    // ============================================================
    //
    // One 1 Hz poll for the whole app, and it is the poll ruling OQ-c chose over a state-changed
    // event: Services/ stays passive, ChainTranslator.PauseNow() is side-effect free by contract
    // (R-2), and the repaint budget is a single text assignment a second — only when the rendered
    // string actually changed. E7.S3's chip is the second consumer of this same tick.

    /// <summary>Whether the countdown is running. Exposed for the tests that pin AC 1's
    /// "never runs idle": asserting the flag is how a countdown is tested without waiting for a
    /// real second (CI-3), which would be the first flaky test in the suite.</summary>
    internal bool CountdownRunning => _countdownTimer.IsEnabled;

    /// <summary>Called by anything that LEARNS of a pause — today the LIVE loop's skipped tick,
    /// tomorrow E7.S3's chip. It starts the countdown if it is not already running and repaints at
    /// once from the pause it was handed, so the first paused second is not a second of stale text.
    ///
    /// <para>Idempotent on purpose: <c>Start()</c> on a running <c>DispatcherTimer</c> is a no-op,
    /// so no site has to own the timer and no site has to ask whether another one already started
    /// it. The <paramref name="pause"/> is passed in rather than re-read because the caller has just
    /// asked for it, and asking twice would compare two instants of the gates' clock (IS-6).</para>
    ///
    /// <para><b>I10, and TP-START-04 until E7.S3 lands E6-a.</b> Nothing calls this before the
    /// window is up, because nothing issues a request before it: <c>ProviderGates</c> constructs its
    /// gates at startup and loads <c>provider-state.json</c> on the first <c>TryEnter</c>. So TODAY
    /// a saved pause becomes visible on the first tick AFTER the first request, not at first
    /// paint.</para>
    ///
    /// <para><b>That is a gap with an owner, not a law.</b> Ruling <b>E6-a</b> settled it and
    /// <b>E7.S3</b> implements it: <c>TranslationChains.EnsureGateStateLoaded()</c> — routed
    /// through <c>Services/</c>, so <c>OnWindowLoaded</c> still names no <c>ProviderGates</c> and
    /// TP-START-02's scan still passes — called from <c>OnWindowLoaded</c> on a POOL thread, AFTER
    /// first paint. A saved pause is then on screen within about a second of the window rather than
    /// after the first request, and the chip may read "checking…" for that second. Do not close the
    /// gap any earlier than that: reading the file BEFORE first paint is what I10 forbids and what
    /// P1 exists to protect, and it is why <c>ProviderGates.EnsureLoaded</c>'s own comment rejects a
    /// warm-up that names the registry from the startup path — E6-a routes around that comment, it
    /// does not overrule it.</para></summary>
    internal void EnsureCountdownRunning(ChainPause pause)
    {
        if (!_countdownTimer.IsEnabled) _countdownTimer.Start();
        CountdownTick(pause);
    }

    /// <summary>Stop it. Called when the surface it paints goes away — <c>StopLive</c> — and when
    /// the window does: a <c>DispatcherTimer</c> roots its handler, and this one's handler closes
    /// over the window.</summary>
    internal void StopCountdown() => _countdownTimer.Stop();

    /// <summary>One tick, with the pause it is about.
    ///
    /// <para><b>The tick is what stops the timer</b> (AC 1). "Is anything still paused?" is
    /// <c>PauseNow()</c>'s <c>BlockedUntil &gt; Now()</c> and never <c>State == Open</c>, which
    /// deliberately outlives its window (ruling E3-a) — a state test here would leave this running
    /// for ever. A rate-ceiling <c>Wait</c> sets no <c>BlockedUntil</c> and is not a pause either
    /// (rulings E5-a/E5-b), so the countdown never starts for one.</para>
    ///
    /// <para><b>At most one countdown per window</b> (AC 4): the main window's is on its status line
    /// and the overlay's is its single status line, of which the chip is a prefix — so there is
    /// structurally one clock on each, and no row is written from here at all (TP-RENDER-06).
    /// Both go through the repaint guard.</para>
    ///
    /// <para>It repaints only while LIVE owns those lines. A read-once's own paused summary is a
    /// past-tense report of a finished read on the very same <c>TextBlock</c>; stepping a countdown
    /// over it would be a second clock on one window and would overwrite a sentence about something
    /// else.</para>
    ///
    /// <para>Internal so the tests can drive it with a <c>ChainPause</c> of their own: nothing in
    /// this story may sleep (CI-3), and nothing in it may reach the process-global gates.</para>
    ///
    /// <para><b>The stop rule was widened by E7.S3, exactly as E7.S2's note asked.</b> It used to be
    /// the single condition <c>!AllPaused</c>, with the <c>_liveCts</c> guard returning WITHOUT
    /// stopping — safe only while <c>StopLive</c> was the sole writer of <c>_liveCts</c> and stopped
    /// the countdown itself. The chip is the second start site, and it can need this tick with LIVE
    /// off: a provider counting down does not care whether a loop is running. So the tick now asks
    /// TWO questions and stops when BOTH say there is nothing left to paint — the chip's own
    /// <c>NeedsTick</c> and LIVE's <c>AllPaused</c> — and the <c>_liveCts</c> guard is no longer
    /// reached before that decision.</para></summary>
    /// <remarks>The chip is repainted FIRST and unconditionally — it is the surface that can need
    /// this tick with no LIVE loop, and it goes through its own guard, so a tick that changes
    /// nothing costs nothing. Its answer is then the first half of the stop rule.</remarks>
    internal void CountdownTick(ChainPause pause) => CountdownTick(pause, RefreshEngineChip());

    /// <summary>The tick with the chip's answer handed in — the seam AC 1's lifetime is pinned
    /// through. A window in a headless test has no paused registry behind it, and giving it one to
    /// prove a STOP RULE would mean driving the process-global gates from the WPF collection, which
    /// is the cross-collection hazard <c>GatesCollection</c> exists to prevent. The one-argument
    /// form above is what the timer calls; this one is the same method with its first line already
    /// evaluated.</summary>
    internal void CountdownTick(ChainPause pause, bool chipNeedsIt)
    {
        // AC 1 / NFR7 — "never runs idle", now as two questions rather than one. The chip's half
        // deliberately excludes the AuthFailed sentinel (DateTimeOffset.MaxValue): that window
        // counts down to nothing, for ever, and its exit is a key save rather than a second.
        if (!chipNeedsIt && !pause.AllPaused) { _countdownTimer.Stop(); return; }

        // LIVE's status lines are not this tick's to paint when no loop owns them (E7.S2's
        // deviation 3: read-once writes a past-tense summary on the very same TextBlock). Unlike
        // before, nothing is skipped by returning here — the stop decision has already been made
        // above, and the chip has already been repainted.
        if (_liveCts == null || !pause.AllPaused) return;

        int? left = LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now);
        SetIfChanged(ScreenReadStatus, LivePausedStatus(left));
        _overlay?.SetStatusIfChanged(LivePausedOverlayStatus(left));
    }

    // ============================================================
    //  THE PROVIDER CHIP (ux-mode-degrade §2.1-§2.3, E7.S3)
    // ============================================================
    //
    // One TextBlock in three places — the write path, the read path, and a prefix of the compact
    // overlay's single status line — showing which engine is serving the player and, when one is
    // paused, how long for. It is the ONLY always-on indicator in the app (§2.2's first reading
    // rule), which is what lets every status line go quiet and say something only when the state
    // changes.
    //
    // The state itself is Services/' (TranslationChains.EngineStatus over ProviderGate.Snapshot()
    // and ChainTranslator.LastOutcome, rulings R-2 / R-3); this file turns it into a glyph, a word,
    // a Theme brush key and a countdown — I2, and the reason ChipFor below is a pure static that a
    // unit test drives with eight literals.

    /// <summary>Ruling <b>E6-a</b>: has <c>provider-state.json</c> been read yet? False until the
    /// warm-up <see cref="OnWindowLoaded"/> starts on a pool thread — AFTER first paint — has
    /// finished, and the chip says "checking…" for that moment rather than claiming a health it has
    /// not verified. A per-window field and not a registry read on purpose: it is this window's
    /// account of its own startup, so it is deterministic in a test and cannot be flipped by
    /// whatever else the process has already done.</summary>
    private bool _gateStateKnown;

    /// <summary>Whether the state the chip last rendered was a degraded one — §3.5's notice is shown
    /// "once per switch", so something has to remember the switch. Without it "Back on Google."
    /// would be written at the first successful translation of every session, which is the opposite
    /// of a notice.</summary>
    private bool _chipWasDegraded;

    /// <summary>The three glyphs §2.3 allows, and no fourth (§6: no new fonts, images or colours).
    /// <c>●</c> serving · <c>○</c> paused or not sending · <c>⚠</c> you may need to act.</summary>
    private const string ChipServingGlyph = "●", ChipQuietGlyph = "○", ChipWarnGlyph = "⚠";

    /// <summary>
    /// <b>§2.1's eight states, as a pure function.</b> Values in, strings out: an
    /// <see cref="EngineStatus"/> (itself pure over gate snapshots) and one flag, giving a glyph, a
    /// word and a <c>Theme.xaml</c> resource KEY.
    ///
    /// <para><b>A key and not a <c>Brush</c></b> (UX-DR18): the two existing status writes already
    /// use <c>SetResourceReference</c>, so the palette stays in one file and a chip cannot introduce
    /// a colour. It also keeps this function UI-free, which is what makes the eight-state test L1 —
    /// a function that read <c>_readChain</c> and wrote <c>WriteChip.Text</c> could not be tested
    /// without a window.</para>
    ///
    /// <para><b>The order of the arms is the specification.</b> S6 and S5 first because a chain with
    /// nothing left to try outranks any single engine's news; then the two that ask the player to
    /// act on a KEY (S7, S8), because an Azure quota is a better answer than "Azure paused"; then
    /// S3, S4, S2 and finally S1. Reordering them changes what a player is told, not just how.</para>
    /// </summary>
    /// <param name="statusLineOwnsTheClock">E7.S2's AC 4, decided there and obeyed here: at most ONE
    /// countdown per window. While LIVE's own paused status line is stepping a clock on this window,
    /// the chip drops its number and keeps its glyph and its word — which is where NFR11 says the
    /// information lives anyway.</param>
    internal static EngineChip ChipFor(EngineStatus status, bool statusLineOwnsTheClock = false)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Ruling E6-a's first second. Not a state of any engine, so it takes no name and asks for
        // no tick: "checking" ends when a task finishes, not when a second passes.
        if (!status.StateKnown)
            return new EngineChip(ChipQuietGlyph, UserMessages.EngineChipChecking(), "TextMutedBrush");

        var readLines = status.ReadTiers.Select(status.For).OfType<EngineLine>().ToList();

        if (status.AllReadTiersPaused)
        {
            // S6 — ruling GAP-3: the full pause is universal, so this is S5 with a cause the player
            // can actually do something about, and it is worth saying instead of "all paused".
            if (readLines.Count > 0 && readLines.All(l => l.Kind == TranslationErrorKind.Network))
                return new EngineChip(ChipWarnGlyph, UserMessages.EngineChipNoInternet(), "AccentBrush");

            // S5
            var all = statusLineOwnsTheClock ? null : Countdown(status.SoonestReadRetry, status.Now);
            return new EngineChip(ChipQuietGlyph,
                UserMessages.EngineChipAllPaused() + (all is null ? "" : " " + all),
                "GoldBrush", HasClock: all is not null);
        }

        // S7 / S8 — the two the user's OWN key can be in. AuthFailed has no honest countdown (the
        // MaxValue sentinel), so S7 says the state and stops; S8 names who is serving instead, which
        // is the reassurance half of "your quota ran out".
        if (KeyedPause(status, TranslationErrorKind.AuthFailed) is { } refused)
            return new EngineChip(ChipWarnGlyph,
                UserMessages.EngineChipKeyRefused(refused.ProviderId), "AccentBrush");

        if (KeyedPause(status, TranslationErrorKind.QuotaExhausted) is { } spent)
            return new EngineChip(ChipServingGlyph,
                UserMessages.EngineChipQuotaOut(Serving(status, readLines), spent.ProviderId),
                "GoldBrush");

        var serving = Serving(status, readLines);

        // S3 — §2.1's own wording: "the PREFERRED provider is gated; something below it still
        // serves". Ruling E3-a decided what "paused" means; EngineStatus applied it. What is
        // decided HERE is which pause the chip speaks for, and it is not simply the first one in
        // chain order: a tier paused BELOW the engine that is answering is not S3. The player is
        // getting exactly the engine they would have got, so a muted "paused" chip there is S1 told
        // wrong — and it would go on to prefix the compact overlay ("shown only when not healthy")
        // and arm §3.5's recovery notice for a recovery from nothing. It is reachable the ordinary
        // way round: the backup tier keeps its own window after the preferred one's has elapsed.
        if (PreferredPause(readLines, serving) is { } paused)
        {
            var t = statusLineOwnsTheClock ? null : Countdown(paused.PausedUntil, status.Now);
            return new EngineChip(ChipQuietGlyph,
                UserMessages.EngineChipPaused(paused.ProviderId, t),
                "TextMutedBrush", HasClock: t is not null);
        }

        // S4 — the offline engine answered. E8 has not shipped, so this is unreachable today; the
        // mapping is total over ProviderIds.All all the same, and a synthetic outcome proves it.
        if (string.Equals(serving, ProviderIds.Bergamot, StringComparison.Ordinal))
            return new EngineChip(ChipServingGlyph, UserMessages.EngineChipServing(serving), "TealBrush");

        // S2 — a lower tier answered because a higher one was SKIPPED (ruling E3-b). Gold, because
        // it is serving you but it is not the engine you would have got.
        if (status.FellBack)
            return new EngineChip(ChipServingGlyph, UserMessages.EngineChipBackup(serving), "GoldBrush");

        // S1
        return new EngineChip(ChipServingGlyph, UserMessages.EngineChipServing(serving), "TealBrush",
            IsHealthy: true);
    }

    /// <summary>A keyed tier (the user's own DeepL or Azure) sitting in a window this gate recorded
    /// <paramref name="kind"/> for. Only those two: a free engine has no key to refuse and no quota
    /// of the player's to run out, so S7 and S8 are questions about a credential.</summary>
    private static EngineLine? KeyedPause(EngineStatus status, TranslationErrorKind kind)
    {
        foreach (var id in new[] { ProviderIds.DeepL, ProviderIds.Azure })
            if (status.For(id) is { State: EngineState.Paused } line && line.Kind == kind) return line;
        return null;
    }

    /// <summary>The pause §2.1's <b>S3</b> is about: the topmost paused read tier that does not sit
    /// <i>below</i> the engine currently serving. A window on a lower rung costs the player nothing
    /// and must not demote a healthy chip.
    ///
    /// <para>The serving tier itself counts — a gate the write path closed under a tier that has
    /// already answered is a pause the player is about to feel — and an unknown <paramref
    /// name="serving"/> (null, or an id that is not a read tier) falls back to the whole chain, so
    /// a state this function cannot place is still reported rather than hidden.</para></summary>
    private static EngineLine? PreferredPause(IReadOnlyList<EngineLine> readLines, string? serving)
    {
        int lowest = readLines.Count - 1;
        for (int i = 0; i < readLines.Count; i++)
            if (string.Equals(readLines[i].ProviderId, serving, StringComparison.Ordinal))
            {
                lowest = i;
                break;
            }

        for (int i = 0; i <= lowest && i < readLines.Count; i++)
            if (readLines[i].State == EngineState.Paused) return readLines[i];
        return null;
    }

    /// <summary>Who the chip names as serving. <c>LastAnswered</c> first — that is the provider that
    /// really answered (R-3), never a guess — and the first read tier that is neither paused nor off
    /// when nothing has been translated yet, which is the honest reading of "this is who you would
    /// get".</summary>
    private static string? Serving(EngineStatus status, IReadOnlyList<EngineLine> readLines)
        => status.LastAnswered
           ?? readLines.FirstOrDefault(l => l.State is EngineState.Ready or EngineState.Answering)
                       ?.ProviderId;

    /// <summary>The chip's <c>{t}</c>: §2.4's bands, from the gates' own clock (IS-6 — the instant
    /// came from <see cref="EngineStatus.Now"/>, never <c>DateTimeOffset.UtcNow</c>), or null when
    /// there is nothing honest to count down to.</summary>
    private static string? Countdown(DateTimeOffset? until, DateTimeOffset now)
        => CountdownText(LiveTickPolicy.CountdownSeconds(until, now));

    /// <summary>
    /// <b>§2.3's tooltip</b> — the whole chain, one line per <see cref="ProviderIds.All"/> member,
    /// in chain order, aligned. It is the only place outside the About tab that lists every tier.
    ///
    /// <para><b>Plain text, and that is load-bearing</b> (AC 4, and <c>project-context.md</c> says
    /// so): assigned to <c>ToolTip</c> as a <c>string</c>, the dark <c>ToolTip</c> style in
    /// <c>Theme.xaml</c> applies untouched. A <c>StackPanel</c>, a <c>ContentTemplate</c> or a
    /// second <c>Style</c> here is how that style gets bypassed by accident.</para>
    ///
    /// <para><b>Aligned with spaces, in the composer</b> (T4) — not with a <c>Grid</c>, which would
    /// be a visual tree inside a tooltip and the same mistake wearing a layout hat. The pad is
    /// computed from the longest name rather than hardcoded, so a name added to
    /// <c>ProviderNames</c> cannot silently break the column.</para>
    ///
    /// <para><b>No jargon and no HTTP codes</b>: the parenthetical is a
    /// <see cref="TranslationErrorKind"/> rendered through <c>UserMessages</c>, and the ids
    /// themselves are internals that may not appear in copy (I11).</para>
    /// </summary>
    internal static string EngineTooltip(EngineStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        int width = 0;
        foreach (var line in status.Lines) width = Math.Max(width, line.DisplayName.Length);

        var rows = new List<string>(status.Lines.Count);
        foreach (var line in status.Lines)
        {
            var state = line.State switch
            {
                EngineState.Answering => UserMessages.EngineLineInUse(),
                EngineState.Ready => UserMessages.EngineLineReady(),
                EngineState.Paused => UserMessages.EngineLinePaused(Countdown(line.PausedUntil, status.Now)),
                _ => UserMessages.EngineLineOff(line.OffReason),
            };
            // The parenthetical belongs to a PAUSE and to nothing else: a "ready" tier that once
            // answered a 429 is ready, and saying why it used to be paused would be the app
            // explaining a state it is no longer in.
            var why = line.State == EngineState.Paused
                ? UserMessages.EngineLineReason(line.Kind) : null;

            rows.Add(line.DisplayName.PadRight(width) + "  " + state
                     + (why is null ? "" : "   (" + why + ")"));
        }
        return string.Join("\n", rows);
    }

    /// <summary>
    /// <b>The repaint</b> — the one place the three chip surfaces are written, and the answer to
    /// "does the 1 Hz tick still have anything to do?".
    ///
    /// <para>Everything goes through <see cref="SetIfChanged"/>, including the foreground: at 1 Hz,
    /// above the 90-second band, the rendered string changes once a minute and this method then
    /// assigns nothing at all (NFR7, hint 7).</para>
    ///
    /// <para><c>LastOutcome</c> is read ONCE, inside <c>TranslationChains.EngineStatus</c>, into the
    /// record this method renders: it is <c>Volatile</c>-read because a call may be in flight on a
    /// pool thread, and reading it twice in one repaint can mix two calls' accounts.</para>
    /// </summary>
    internal bool RefreshEngineChip()
    {
        var status = TranslationChains.EngineStatus(_settings, _readChain, _gateStateKnown);

        // E7.S2's AC 4: while LIVE's paused line is stepping a clock on this window, the chip does
        // not step a second one. It is the only cross-surface fact the pure function is told.
        var chip = ChipFor(status, statusLineOwnsTheClock: _liveCts != null && status.AllReadTiersPaused);
        PaintEngineChip(chip, EngineTooltip(status));

        // §3.5's third line, once per recovery: the chip just changed back, and the player is told
        // why rather than left to notice. It is written AFTER the chip so the two agree, and only
        // when a degraded state was really observed first — otherwise every session's first
        // translation would announce a recovery from nothing.
        if (_chipWasDegraded && chip.IsHealthy && UserMessages.BackOn(status.LastAnswered) is { } back)
            SetIfChanged(TranslateStatus, back);
        // "checking…" is NOT a degraded state, and the distinction is the whole notice: every
        // session starts unchecked, so counting it would announce "Back on Google." at the first
        // successful translation of every launch — a recovery from nothing.
        _chipWasDegraded = status.StateKnown && !chip.IsHealthy;

        return status.NeedsTick;
    }

    /// <summary>The two main-window surfaces, written from one <see cref="EngineChip"/> — the whole
    /// of what this feature puts on a control, in one method, so <b>TP-RENDER-03</b> can drive all
    /// eight states through the real <c>TextBlock</c>s without a chain, a gate or a request.
    ///
    /// <para>The foreground follows the text through the SAME guard: a resource reference re-applied
    /// once a second is the churn hint 7 exists to remove, and one comparison for both is what stops
    /// a cached brush field from disagreeing with the string it belongs to. The tooltip is compared
    /// as a <c>string</c> — which is also the assertion that it IS one, and therefore that the dark
    /// <c>ToolTip</c> style in <c>Theme.xaml</c> still applies (AC 4).</para></summary>
    internal void PaintEngineChip(EngineChip chip, string tooltip)
    {
        foreach (var surface in new[] { WriteChip, ReadChip })
        {
            if (SetIfChanged(surface, chip.Label))
                surface.SetResourceReference(TextBlock.ForegroundProperty, chip.BrushKey);
            if (!string.Equals(surface.ToolTip as string, tooltip, StringComparison.Ordinal))
                surface.ToolTip = tooltip;
        }
    }

    /// <summary>Repaint the chip from the events that already exist — a translation finishing, LIVE
    /// starting or stopping, a key save — and start the 1 Hz tick if the new state needs one.
    ///
    /// <para>The tick itself calls <see cref="RefreshEngineChip"/> directly and never this: the tick
    /// owns the STOP decision, and a repaint that could restart the timer it is about to stop would
    /// be a loop with no exit.</para></summary>
    internal void UpdateEngineChip()
    {
        if (RefreshEngineChip() && !_countdownTimer.IsEnabled) _countdownTimer.Start();
    }

    /// <summary>The compact overlay's half of AC 1: the chip is a <b>prefix of the one status
    /// line</b>, "shown only when not healthy" — the window is 360 px wide and a healthy chain needs
    /// no words there.
    ///
    /// <para>It is suppressed a second time when the chip carries a countdown, and that is E7.S2's
    /// AC 4 again: the overlay has ONE line, the status half of it is already stepping a clock while
    /// the chain is paused, and two clocks on one line is exactly what "at most one countdown per
    /// window" forbids. <c>MainWindow</c> composes, <c>CompactOverlay</c> renders (I2).</para>
    ///
    /// <para>An empty status stays empty: <c>SetStatus</c> collapses the line on an empty string,
    /// and a chip prefix must not resurrect a line the overlay had deliberately hidden.</para></summary>
    internal string OverlayLine(string status)
        => OverlayLine(ChipFor(TranslationChains.EngineStatus(_settings, _readChain, _gateStateKnown)),
                       status);

    /// <summary>The composition itself, pure, so all eight states can be asserted without a window
    /// (the impure half above is one line: which chip).</summary>
    internal static string OverlayLine(EngineChip chip, string status)
        => string.IsNullOrEmpty(status) || chip.IsHealthy || chip.HasClock
            ? status
            : chip.Label + "  " + status;

    /// <summary>The repaint guard (AC 3, UX hint 7): assign <c>.Text</c> only when the rendered
    /// string differs from what the surface already shows. Above 90 s the band changes only on a
    /// minute boundary, so that is one assignment a minute rather than sixty.
    ///
    /// <para>It compares the control's OWN text rather than a cached field — one source of truth,
    /// and it stays correct when something else writes the same surface, which
    /// <c>SetScreenStatus</c> does on both windows.</para>
    ///
    /// <para><b>It answers whether it wrote</b> (E7.S3). The chip has a second property to keep in
    /// step with its text — the foreground, through <c>SetResourceReference</c> — and re-applying a
    /// resource reference a second is exactly the churn this guard exists to remove. Returning a
    /// bool keeps ONE comparison for both, rather than a cached brush field that could disagree with
    /// the text it is supposed to belong to.</para></summary>
    internal static bool SetIfChanged(TextBlock target, string text)
    {
        if (string.Equals(target.Text, text, StringComparison.Ordinal)) return false;
        target.Text = text;
        return true;
    }

    /// <summary>
    /// Turn an exception into a short, non-technical message for the user.
    ///
    /// It used to render whatever sentence the throw site happened to carry, which is how one dead
    /// network could read three different ways depending on which provider noticed it first. It now
    /// renders one sentence per <see cref="TranslationErrorKind"/>, from the single copy-deck table
    /// (<c>Services/UserMessages.cs</c>, ruling GAP-4) — the exception's own message stays what the
    /// diagnostic log records, and stops being what the player reads.
    ///
    /// The mapping is a pure function and lives in <c>Services/</c> so the suite can assert on the
    /// copy without an STA host; this stays the display-time entry point, which is where the
    /// countdown IS formatted from <see cref="TranslationException.RetryAt"/> (I2:
    /// <c>Services/</c> counts the seconds and never formats them).
    ///
    /// <para><b>Both parameters of the deck's sentence are resolved here</b> (E7.S1).
    /// <c>{P}</c> comes from <see cref="TranslationException.ProviderId"/> through
    /// <c>ProviderNames</c> — from the failure itself, never guessed, and never
    /// <c>ChainTranslator.LastOutcome</c>, whose <c>ProviderId</c> is null on exactly the exit that
    /// produces a sentence. <c>{t}</c> comes from <see cref="TryAgainIn"/>.</para>
    ///
    /// <para>"— another engine is being tried" is NOT offered here and the default <c>false</c> is
    /// the honest value: this method is reached once an attempt has already failed (amendment A4).
    /// The clause is for a status line rendered while a chain is still walking, and the caller that
    /// builds one passes it from its own position — see <c>UserMessages.Sentence</c>.</para>
    ///
    /// <c>internal</c>, not <c>private</c>: the suite reaches it through <c>InternalsVisibleTo</c>.
    /// A feed row no longer renders this at all (amendment A5).
    /// </summary>
    internal static string Friendly(Exception ex) => UserMessages.For(ex, TryAgainIn(ex));

    /// <summary>The <c>{t}</c> of a failure's own sentence, in the <b>coarse</b> band (ruling
    /// <b>E7-a</b>): a §3.1 sentence is written once onto a status line and never ticks, so it may
    /// not show "0:05" — a frozen stopwatch reads as a live clock, which is the one thing a
    /// countdown must not do. <c>CountdownJoinText</c> is that coarse band; only the 1 Hz LIVE lines
    /// use <c>m:ss</c>.
    ///
    /// <para>The clock is <c>UtcNow</c> and not a gate's <c>Now()</c>: a static display-time helper
    /// holds no chain, and reaching for <c>ProviderGates</c> from the code-behind is what TP-START-02
    /// forbids. The two agree in production (a gate's default clock IS <c>UtcNow</c>) and diverge
    /// only under an injected test clock — where the countdown degrades to A12's "briefly" /
    /// "shortly" rather than printing a wrong number.</para></summary>
    internal static string? TryAgainIn(Exception ex)
        => ex is TranslationException te
            ? CountdownJoinText(LiveTickPolicy.CountdownSeconds(te.RetryAt, DateTimeOffset.UtcNow))
            : null;

    // ============================================================
    //  GLOBAL HOTKEYS (work even while the game has focus)
    //    Ctrl+Alt+P — bring PWRU Helper to the front
    //    Ctrl+Alt+T — bring to front + focus the translator input
    //    Ctrl+Alt+L — start/stop live on the last area
    //    Ctrl+Alt+M — toggle the compact overlay
    //    Ctrl+Alt+R — read the last area once (no live loop)
    // ============================================================
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const int HK_SHOW = 1, HK_TRANSLATE = 2, HK_LIVE = 3, HK_COMPACT = 4, HK_READ = 5;
    private HwndSource? _hwnd;
    private IntPtr _hwndHandle;   // cached so OnClosed unregisters against the real handle

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndHandle = new WindowInteropHelper(this).Handle;
        _hwnd = HwndSource.FromHwnd(_hwndHandle);
        _hwnd?.AddHook(HotkeyHook);

        uint mod = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        // If another app already owns a combo, RegisterHotKey returns false — collect exactly
        // which ones failed so we can name them for the user (a silently dead shortcut with no
        // explanation is worse than none).
        var failed = new List<string>();
        void Reg(int id, uint vk, string label)
        { if (!RegisterHotKey(_hwndHandle, id, mod, vk)) failed.Add(label); }

        Reg(HK_SHOW, 0x50, "Ctrl+Alt+P");       // P
        Reg(HK_TRANSLATE, 0x54, "Ctrl+Alt+T");  // T
        Reg(HK_LIVE, 0x4C, "Ctrl+Alt+L");       // L
        Reg(HK_COMPACT, 0x4D, "Ctrl+Alt+M");    // M
        Reg(HK_READ, 0x52, "Ctrl+Alt+R");       // R

        if (failed.Count > 0)
        {
            // Persistent, specific note in the About tab (a transient toast would scroll away
            // before the user could read which shortcuts are dead).
            HotkeyWarning.Text = "⚠ Already used by another app, so these won't work here: "
                                 + string.Join(", ", failed) + ".";
            HotkeyWarning.Visibility = Visibility.Visible;
        }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HK_SHOW: BringToFront(); handled = true; break;
            case HK_TRANSLATE:
                BringToFront(); MainTabs.SelectedIndex = TabTranslator;
                // The tab's content isn't attached yet the instant we switch to it, so a synchronous
                // Focus() lands nowhere. Defer it until the input has been realised.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => TranslateInput.Focus());
                handled = true; break;
            case HK_LIVE: ToggleLive(); handled = true; break;
            case HK_COMPACT: ToggleCompact(); handled = true; break;
            case HK_READ: ReadLastAreaOnce(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    private void BringToFront()
    {
        // If we're in compact mode, "bring to front" means return to the full window.
        if (_overlay is { IsVisible: true }) { ExitCompactMode(); return; }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        // Nudge topmost to force ourselves above the game, then restore the user's choice.
        Topmost = true;
        Topmost = TopmostCheck.IsChecked == true;
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != null)
        {
            UnregisterHotKey(_hwndHandle, HK_SHOW);
            UnregisterHotKey(_hwndHandle, HK_TRANSLATE);
            UnregisterHotKey(_hwndHandle, HK_LIVE);
            UnregisterHotKey(_hwndHandle, HK_COMPACT);
            UnregisterHotKey(_hwndHandle, HK_READ);
            _hwnd.RemoveHook(HotkeyHook);
        }
        if (_overlay != null) { _overlay.AllowClose = true; _overlay.Close(); }
        base.OnClosed(e);
    }
}

/// <summary>
/// <b>One rendered chip</b> — the whole of what <c>ux-mode-degrade.md</c> §2.3 puts on screen for a
/// state: a glyph, a word, and a <c>Theme.xaml</c> resource KEY for the foreground.
///
/// <para><b>A key and never a <c>Brush</c></b> (UX-DR18): the palette stays in one file, the two
/// existing status writes already use <c>SetResourceReference</c>, and a value type carrying three
/// strings can be produced by a pure function a headless test drives with literals.</para>
///
/// <para><b>NFR11 lives in <see cref="Label"/>.</b> The glyph and the word carry the state; the
/// brush only agrees with them. Strip the palette and <c>○ Google paused 0:58</c> still says
/// everything — which is the assertion the eight-state test makes, on the text alone.</para>
/// </summary>
/// <param name="IsHealthy">§2.1's <b>S1</b> and nothing else. It is what the compact overlay asks
/// before prefixing its one status line ("shown only when not healthy") — and the offline engine
/// answering is deliberately NOT healthy for that purpose: §2.2's table gives S4 an overlay prefix
/// of its own, because "your PC is translating this" is worth eight characters of a 360 px
/// window.</param>
/// <param name="HasClock">Whether <see cref="Text"/> carries a countdown. The overlay asks this too:
/// its single line already steps LIVE's clock while the chain is paused, and E7.S2's AC 4 allows at
/// most one countdown per window.</param>
internal readonly record struct EngineChip(
    string Glyph,
    string Text,
    string BrushKey,
    bool IsHealthy = false,
    bool HasClock = false)
{
    /// <summary>What a <c>TextBlock</c> shows: glyph, one space, word. Assembled here rather than at
    /// the three call sites so the three surfaces cannot come to space it differently.</summary>
    internal string Label => Glyph + " " + Text;
}

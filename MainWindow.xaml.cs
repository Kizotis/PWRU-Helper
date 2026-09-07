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
    /// <para><b>E7.S3, read this before you add the chip.</b> There is exactly ONE stop condition
    /// here — <c>!AllPaused</c> — and the <c>_liveCts</c> guard below returns WITHOUT stopping,
    /// because today the only thing that clears <c>_liveCts</c> is <c>StopLive</c> and it stops the
    /// countdown itself. The moment a second site starts this timer while LIVE is off (the chip is
    /// exactly that site) the guard becomes a 1 Hz poll that paints nothing and cannot stop, for as
    /// long as the pause lasts — which is NFR7's "never runs idle" broken by a caller rather than by
    /// this method. Widen the condition deliberately when you add that caller: the tick must stop
    /// when it has NOTHING left to paint, not when LIVE is off. It is not widened here because the
    /// only headless way to pin AC 1's lifetime is a window with no loop, so a
    /// <c>_liveCts</c>-shaped stop would need a test seam this story has no use for. Flagged for
    /// Winston in the review.</para></summary>
    internal void CountdownTick(ChainPause pause)
    {
        if (!pause.AllPaused) { _countdownTimer.Stop(); return; }
        if (_liveCts == null) return;   // see the E7.S3 note above: returns, does NOT stop

        int? left = LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now);
        SetIfChanged(ScreenReadStatus, LivePausedStatus(left));
        _overlay?.SetStatusIfChanged(LivePausedOverlayStatus(left));
    }

    /// <summary>The repaint guard (AC 3, UX hint 7): assign <c>.Text</c> only when the rendered
    /// string differs from what the surface already shows. Above 90 s the band changes only on a
    /// minute boundary, so that is one assignment a minute rather than sixty.
    ///
    /// <para>It compares the control's OWN text rather than a cached field — one source of truth,
    /// and it stays correct when something else writes the same surface, which
    /// <c>SetScreenStatus</c> does on both windows.</para></summary>
    internal static void SetIfChanged(TextBlock target, string text)
    {
        if (!string.Equals(target.Text, text, StringComparison.Ordinal)) target.Text = text;
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
    /// countdown will be formatted from <see cref="TranslationException.RetryAt"/> when E7.S1 adds
    /// it (I2: <c>Services/</c> never formats a time).
    ///
    /// <c>internal</c>, not <c>private</c>: the suite reaches it through <c>InternalsVisibleTo</c>.
    /// Both feed-row call sites wrap the result in parentheses — do not add them here (I4).
    /// </summary>
    internal static string Friendly(Exception ex) => UserMessages.For(ex);

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

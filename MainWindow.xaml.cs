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
    // SquadUppercase_Changed) fire as a side effect of setting a slider / combo / tick, and would
    // then write that transient UI state back to disk — clobbering the very settings we're loading.
    //
    // It starts TRUE and is only cleared at the end of ApplySettings, because XAML LOADING ITSELF
    // fires these handlers: `<Slider Value="70" ValueChanged="OcrTolerance_Changed"/>` raises
    // ValueChanged during InitializeComponent(), long before ApplySettings runs. That handler then
    // read the still-unselected filter combo (SelectedTag → null → "off") and persisted "off" —
    // which is why a saved "Boost contrast" came back Off on every launch (fixed in v0.13.0).
    private bool _restoringSettings = true;

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
    private ITranslator _writeTranslator;
    private readonly ITranslator _readTranslator;
    private readonly ITranslator _readOnceTranslator;

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
    private readonly ChainTranslator _readChain;

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

    // Tab indices (order must match the TabControl in XAML):
    // Phrasebook(0) · Squad(1) · Translator(2) · Screen OCR(3) · About(4).
    private const int TabTranslator = 2, TabScreenOcr = 3;

    // The Windows OCR language pack we install / show the command for (single source).
    private const string OcrCapability = "Language.OCR~~~ru-RU~0.0.1.0";

    // --- live screen translation ---
    private CancellationTokenSource? _liveCts;
    private bool _selectingRegion;                       // a screen-area drag is in progress
    private bool _readingOnce;                            // a one-shot Ctrl+Alt+R / read-once is mid-flight
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
        //     _readTranslator lost its initializer; it stays readonly so no handler can reassign it.
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
            UpdateDeepLStatus();

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

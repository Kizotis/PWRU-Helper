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

    // Built from settings in the constructor: DeepL (with Google fallback) when an API key is
    // set, otherwise Google — wrapped in a cache either way. Rebuilt when the key changes.
    private ITranslator _translator;
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
        _translator = BuildTranslator();   // depends on _settings, which is already loaded above
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        OcrResults.ItemsSource = _ocrItems;
        OcrCommandBox.Text = $"Add-WindowsCapability -Online -Name \"{OcrCapability}\"";
        ShowAppVersion();
        PopulateLanguageCombos();
        LoadPhrases();
        LoadSlang();
        LoadSquad();
        BuildSquadTab();
        CheckOcrAvailability();
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

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // (No first-run welcome dialog — the app opens straight to the tabs.)

        // Run the update check once the window is up, so the dialog has an owner and
        // appears in front of our always-on-top window instead of behind it.
        await CheckForUpdatesAsync();
    }

    // ============================================================
    //  SETTINGS (persist between runs)
    // ============================================================
    private void ApplySettings()
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
        SetOcrFilterCombo(s.OcrFilterMode ?? "off");   // fires the change handler → sets visibility/label

        ScreenCapture.SetMode(s.CaptureBackend);
        SetCaptureBackendCombo(s.CaptureBackend ?? "gdi");

        UpdateResumeLiveButton();
        ApplyFontScale();
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

    /// <summary>Turn an exception into a short, non-technical message for the user.</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        TranslationException te => te.Message,
        System.Net.Http.HttpRequestException => "no Internet connection",
        TaskCanceledException => "the request timed out",
        _ => ex.Message,
    };

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

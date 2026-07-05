using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private readonly TranslationService _translator = new();
    private readonly UpdateService _updates = new();
    private readonly AppSettings _settings = SettingsService.Load();
    private OcrService _ocr = new("ru");
    private readonly ObservableCollection<OcrResultItem> _ocrItems = new();

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    // Tab indices (order must match the TabControl in XAML).
    private const int TabTranslator = 1, TabScreenOcr = 2;

    // The Windows OCR language pack we install / show the command for (single source).
    private const string OcrCapability = "Language.OCR~~~ru-RU~0.0.1.0";

    // --- live screen translation ---
    private CancellationTokenSource? _liveCts;
    private bool _selectingRegion;                       // a screen-area drag is in progress
    private System.Drawing.Rectangle? _liveRegion;
    private List<string> _prevNorm = new();              // normalised lines from the previous read
    private List<string> _pendingLines = new();          // lines that just appeared, awaiting confirmation
    private int _liveTicks;
    private const int MaxHistory = 50;                   // keep the last 50 translated messages
    // A newly-appeared line must survive into the NEXT read before we translate it. That
    // one-frame confirmation filters OCR flicker from the game moving behind the chat
    // (camera panning), which only ever shows up for a single frame. How strict this
    // confirmation is comes from the "Stability" slider (see StabilityThreshold()); the
    // sensitivity slider only controls how "new" a line must look to count at all.

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        OcrResults.ItemsSource = _ocrItems;
        OcrCommandBox.Text = $"Add-WindowsCapability -Online -Name \"{OcrCapability}\"";
        ShowAppVersion();
        PopulateLanguageCombos();
        LoadPhrases();
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

    private void UpdateResumeLiveButton()
    {
        bool show = _liveCts == null && _settings.LastLiveRegion is { Length: 4 };
        ResumeLiveButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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

    private void CompactButton_Click(object sender, RoutedEventArgs e) => EnterCompactMode();

    private void ToggleCompact()
    {
        if (_overlay is { IsVisible: true }) ExitCompactMode();
        else EnterCompactMode();
    }

    internal void EnterCompactMode()
    {
        if (_overlay == null)
        {
            _overlay = new CompactOverlay(this);
            if (_settings.OverlayWidth is > 200 and { } ow) _overlay.Width = ow;
            if (_settings.OverlayHeight is > 150 and { } oh) _overlay.Height = oh;
            if (_settings.OverlayLeft is { } ol && _settings.OverlayTop is { } ot && IsOnScreen(ol, ot, 240, 180))
            { _overlay.Left = ol; _overlay.Top = ot; }
            else
            {
                // First time: park it near the top-right of the primary work area.
                _overlay.Left = SystemParameters.WorkArea.Right - _overlay.Width - 20;
                _overlay.Top = SystemParameters.WorkArea.Top + 40;
            }
        }
        _overlay.Show();
        _overlay.Activate();
        Hide();
    }

    internal void ExitCompactMode()
    {
        if (_overlay != null) { SaveOverlayBounds(); _overlay.Hide(); }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SaveOverlayBounds()
    {
        if (_overlay == null) return;
        var b = _overlay.RestoreBounds;
        if (b.IsEmpty) return;
        _settings.OverlayLeft = b.Left; _settings.OverlayTop = b.Top;
        _settings.OverlayWidth = b.Width; _settings.OverlayHeight = b.Height;
    }

    /// <summary>Single entry point for the live button / Resume / Ctrl+Alt+L / overlay:
    /// stop if running, else resume the saved area, else surface the picker.</summary>
    internal void ToggleLive()
    {
        if (_selectingRegion) return;
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
            var ru = await _translator.TranslateAsync(text, from, "ru");
            bool copied = ru.Length > 0 && await CopyToClipboardAsync(ru);
            return new ReplyOutcome(true, ru, copied, null);
        }
        catch (Exception ex) { return new ReplyOutcome(false, "", false, Friendly(ex)); }
    }

    /// <summary>Show the real build version in the About tab (never hard-coded).</summary>
    private void ShowAppVersion()
        => VersionText.Text = $"v{UpdateService.CurrentVersion.ToString(3)}";

    // ============================================================
    //  UPDATE CHECK (GitHub Releases)
    // ============================================================
    private async Task CheckForUpdatesAsync()
    {
        var info = await _updates.CheckForUpdateAsync();
        if (info == null) return; // up to date, or the check couldn't run — stay quiet

        var choice = MessageBox.Show(this,
            "A new version of PWRU Helper is available!\n\n" +
            $"You have: {UpdateService.CurrentVersion.ToString(3)}\n" +
            $"Latest: {info.LatestVersion.ToString(3)}\n\n" +
            "Open the download page now?",
            "Update available",
            MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (choice != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
        }
        catch
        {
            // Browser association broken, etc. — try the canonical page, then give up gracefully.
            try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true }); }
            catch { ShowToast("Couldn't open the browser — see github.com/Kizotis/PWRU-Helper/releases"); }
        }
    }

    // ============================================================
    //  PHRASEBOOK
    // ============================================================
    private void LoadPhrases()
    {
        // The embedded copy always works (it's compiled into the exe) — it's our
        // guaranteed source so the phrasebook is NEVER empty, even if writing/reading
        // an editable copy fails (e.g. installed under Program Files with no write access).
        string? json = ReadEmbeddedPhrases();

        try
        {
            var editable = FindOrCreateEditablePhrases(json);
            if (editable != null && File.Exists(editable))
                json = File.ReadAllText(editable);
        }
        catch
        {
            // Couldn't read an editable copy — fall back to the embedded one (already set).
        }

        try
        {
            if (json != null)
            {
                var items = JsonSerializer.Deserialize<List<Phrase>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (items != null)
                {
                    // A user-edited file may have missing/null fields — coalesce so the
                    // search filter (p.En.Contains…) can never hit a NullReferenceException.
                    // (Nullable ref types are compile-time only; JSON can still write null.)
                    static string Safe(string? s) => s ?? "";
                    foreach (var p in items)
                    {
                        p.En = Safe(p.En); p.Ru = Safe(p.Ru); p.Translit = Safe(p.Translit);
                        p.Category = string.IsNullOrEmpty(p.Category) ? "Other" : p.Category;
                    }
                    _allPhrases.AddRange(items.Where(p => p.Ru.Length > 0 || p.En.Length > 0));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read the phrase list:\n{ex.Message}", "PWRU Helper");
        }

        RebuildPhraseView();
    }

    /// <summary>Rebuild the grouped phrase list: a "🕑 Recent" group and a "★ Favourites"
    /// group (both cloned from the real phrases) on top, then every phrase by category.</summary>
    private void RebuildPhraseView()
    {
        // Keep the reader's place: replacing ItemsSource resets the scroll to the top.
        double offset = PhraseScroller?.VerticalOffset ?? 0;

        var fav = new HashSet<string>(_settings.Favourites);
        var byRu = _allPhrases.GroupBy(p => p.Ru).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in _allPhrases) p.IsFavourite = fav.Contains(p.Ru);

        var display = new List<Phrase>();
        foreach (var ru in _settings.Recents)
            if (byRu.TryGetValue(ru, out var src)) display.Add(Clone(src, "🕑 Recent"));
        foreach (var ru in _settings.Favourites)
            if (byRu.TryGetValue(ru, out var src)) display.Add(Clone(src, "★ Favourites"));
        display.AddRange(_allPhrases);

        _phrasesView = new CollectionViewSource { Source = display };
        _phrasesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Phrase.Category)));
        _phrasesView.Filter += PhrasesFilter;
        PhraseList.ItemsSource = _phrasesView.View;

        if (PhraseScroller != null && offset > 0)
            Dispatcher.BeginInvoke(new Action(() => PhraseScroller.ScrollToVerticalOffset(offset)),
                                   DispatcherPriority.Loaded);

        static Phrase Clone(Phrase p, string category) => new()
        { En = p.En, Ru = p.Ru, Translit = p.Translit, Category = category, IsFavourite = p.IsFavourite };
    }

    private void RecordRecent(string ru)
    {
        if (_settings.Recents.Count > 0 && _settings.Recents[0] == ru) return;  // already on top — nothing changes
        _settings.Recents.RemoveAll(x => x == ru);
        _settings.Recents.Insert(0, ru);
        while (_settings.Recents.Count > 8) _settings.Recents.RemoveAt(_settings.Recents.Count - 1);
        SettingsService.Save(_settings);
        RebuildPhraseView();
    }

    private void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        // The ★ button lives inside the phrase-card button; stop the click from bubbling
        // up and ALSO copying/recording the phrase.
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: Phrase p }) return;
        if (!_settings.Favourites.Remove(p.Ru)) _settings.Favourites.Insert(0, p.Ru);
        SettingsService.Save(_settings);
        RebuildPhraseView();
    }

    /// <summary>
    /// Returns the path of an editable phrases.json the user can customise, creating it
    /// from the embedded copy on first run. Prefers next to the exe (portable), but falls
    /// back to %AppData%\PWRUHelper when that folder isn't writable (e.g. an MSI install
    /// under Program Files). Returns null if no editable copy could be provided.
    /// </summary>
    private static string? FindOrCreateEditablePhrases(string? embedded)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "phrases.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "PWRUHelper", "phrases.json"),
        };

        // If an editable copy already exists anywhere, use it.
        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        // Otherwise try to create one (best-effort) so the user has something to edit.
        if (embedded != null)
            foreach (var path in candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, embedded);
                    return path;
                }
                catch { /* not writable here — try the next location */ }
            }

        return null;
    }

    private void PhrasesFilter(object sender, FilterEventArgs e)
    {
        var q = PhraseSearch.Text?.Trim();
        if (string.IsNullOrEmpty(q)) { e.Accepted = true; return; }
        if (e.Item is not Phrase p) { e.Accepted = false; return; }

        e.Accepted =
            p.En.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Ru.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Translit.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void PhraseSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _phrasesView?.View.Refresh();
        if (SearchPlaceholder != null)
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(PhraseSearch.Text)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PhraseCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Phrase p })
        {
            if (await CopyToClipboardAsync(p.Ru))
            {
                ShowToast($"Copied:  {p.Ru}");
                RecordRecent(p.Ru);
            }
        }
    }

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

    private static string? ReadEmbeddedPhrases()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("phrases.json", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
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
        if (region is not { } rect) return;

        MainTabs.SelectedIndex = 1;          // results show on the Translator page
        SelectAreaButton.IsEnabled = false;
        LiveButton.IsEnabled = false;        // don't let live start mid-read (shared OCR engine)
        _ocrItems.Clear();
        ScreenReadStatus.Text = "Reading…";
        try
        {
            using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
            var sentences = TextMatching.ToSentences(await _ocr.ReadLinesAsync(bmp));
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
        }
    }

    /// <summary>Fill the reading list with each Russian sentence and its translation.</summary>
    private async Task TranslateSentencesInto(List<string> sentences, string target)
    {
        _ocrItems.Clear();
        var items = new List<OcrResultItem>();
        foreach (var s in sentences)
        {
            var item = new OcrResultItem { Original = s, Translation = "…" };
            _ocrItems.Add(item);
            items.Add(item);
        }

        List<string> translations;
        try { translations = await _translator.TranslateLinesAsync(sentences, "ru", target); }
        catch (Exception ex)
        {
            foreach (var it in items) it.Translation = $"({Friendly(ex)})";
            return;
        }
        for (int i = 0; i < items.Count && i < translations.Count; i++)
            items[i].Translation = translations[i];
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
        _prevNorm = new();
        _pendingLines = new();
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
                int minLetters = MinFragmentLetters();
                var lines = TextMatching.ToSentences(await _ocr.ReadLinesAsync(bmp))
                    .Where(l => TextMatching.LooksLikeText(l, minLetters)).ToList();
                if (ct.IsCancellationRequested) break;
                var cur = lines.Select(TextMatching.Normalize).ToList();

                // CONFIRM: lines that appeared in the previous read AND are still on screen
                // now are real new messages (they survived a frame, so they aren't flicker
                // from the game moving behind the chat). Those get translated.
                var confirmed = _pendingLines
                    .Where(p => TextMatching.ContainsSimilar(cur, TextMatching.Normalize(p), StabilityThreshold()))
                    .Distinct()
                    .ToList();

                // FRESH: lines on screen now that aren't (fuzzily) in the previous read.
                // The sensitivity slider sets how similar counts as "already seen": higher
                // sensitivity = stricter = smaller changes register as new. Repeats work
                // naturally — a line that scrolled off leaves _prevNorm, so if it's sent
                // again it reads as fresh and gets translated again.
                double freshThr = SensitivityThreshold();
                _pendingLines = lines
                    .Where(l => !TextMatching.ContainsSimilar(_prevNorm, TextMatching.Normalize(l), freshThr))
                    .ToList();
                _prevNorm = cur;

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
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                if (++consecutiveErrors >= 5)
                {
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
        var items = new List<OcrResultItem>();
        foreach (var s in newLines)
        {
            var item = new OcrResultItem { Original = s, Translation = "…" };
            _ocrItems.Add(item);
            items.Add(item);
            while (_ocrItems.Count > MaxHistory) _ocrItems.RemoveAt(0);   // drop the oldest
        }
        ResultsScroller?.ScrollToEnd();

        List<string> translations;
        try
        {
            translations = await _translator.TranslateLinesAsync(newLines, "ru", target, ct);
        }
        catch (Exception ex)
        {
            // Don't leave the placeholders stuck on "…" forever (e.g. Google rate-limit):
            // mark them, then let the loop's error handling show the reason.
            foreach (var it in items) it.Translation = $"({Friendly(ex)})";
            throw;
        }
        if (ct.IsCancellationRequested) return;
        for (int i = 0; i < items.Count && i < translations.Count; i++)
            items[i].Translation = translations[i];
        ResultsScroller?.ScrollToEnd();
    }

    /// <summary>Map the sensitivity slider (0–100%) to a fuzzy-match threshold. Higher
    /// sensitivity → stricter "same line" test → smaller changes count as new text.
    /// (The text-matching helpers themselves live in <see cref="TextMatching"/>.)</summary>
    private double SensitivityThreshold()
    {
        double sens = SensitivitySlider?.Value ?? 10;   // default matches the XAML slider
        return 0.60 + (sens / 100.0) * 0.38;            // 0.60 (calm) … 0.98 (very sensitive)
    }

    /// <summary>Smallest text fragment (in letters) that live mode will bother translating.</summary>
    private int MinFragmentLetters()
        => (int)Math.Round(MinFragmentSlider?.Value ?? 2);

    /// <summary>Map the stability slider (0–100%) to the frame-confirmation threshold. Higher
    /// = a newly-appeared line must match itself more closely across a frame to be accepted
    /// (fewer false positives from OCR noise, but slightly slower to confirm real text).</summary>
    private double StabilityThreshold()
    {
        double v = StabilitySlider?.Value ?? 49;        // default matches the XAML slider
        return 0.50 + (v / 100.0) * 0.45;               // 0.50 (loose) … 0.95 (strict), ≈0.72 at 49%
    }

    /// <summary>Live re-read interval from the speed slider (higher speed = shorter wait).</summary>
    private int CurrentLiveIntervalMs()
    {
        double speed = LiveSpeedSlider?.Value ?? 48;    // 0..100, default matches the XAML slider
        return (int)(3000 - (speed / 100.0) * 2500);    // 3.0s (slow) … 0.5s (fast)
    }

    // ============================================================
    //  TRANSLATOR
    // ============================================================
    private async void Translate_Click(object sender, RoutedEventArgs e) => await RunTranslation();

    private async void TranslateInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            await RunTranslation();
        }
    }

    private async Task RunTranslation()
    {
        if (!TranslateButton.IsEnabled) return;   // a translation is already running

        var text = TranslateInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        var from = SelectedTag(FromCombo) ?? "en";
        var to = SelectedTag(ToCombo) ?? "ru";

        // Auto-detect: if the text is Russian but we're not translating FROM Russian,
        // flip the direction so pasting Russian "just works" (was silently wrong before).
        if (from != "ru" && Regex.IsMatch(text, @"\p{IsCyrillic}"))
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
            var result = await _translator.TranslateAsync(text, from, to);
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
    // ============================================================
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    private const int HK_SHOW = 1, HK_TRANSLATE = 2, HK_LIVE = 3, HK_COMPACT = 4;
    private HwndSource? _hwnd;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _hwnd = HwndSource.FromHwnd(handle);
        _hwnd?.AddHook(HotkeyHook);

        uint mod = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        // If another app already owns a combo, RegisterHotKey returns false — collect those
        // so we can tell the user once, instead of a silently dead shortcut.
        bool ok = RegisterHotKey(handle, HK_SHOW, mod, 0x50)       // P
                & RegisterHotKey(handle, HK_TRANSLATE, mod, 0x54)   // T
                & RegisterHotKey(handle, HK_LIVE, mod, 0x4C)        // L
                & RegisterHotKey(handle, HK_COMPACT, mod, 0x4D);    // M
        if (!ok)
            Dispatcher.BeginInvoke(new Action(() =>
                ShowToast("Some Ctrl+Alt shortcuts are already used by another app.")),
                DispatcherPriority.ApplicationIdle);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HK_SHOW: BringToFront(); handled = true; break;
            case HK_TRANSLATE:
                BringToFront(); MainTabs.SelectedIndex = TabTranslator; TranslateInput.Focus(); handled = true; break;
            case HK_LIVE: ToggleLive(); handled = true; break;
            case HK_COMPACT: ToggleCompact(); handled = true; break;
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
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HK_SHOW);
            UnregisterHotKey(handle, HK_TRANSLATE);
            UnregisterHotKey(handle, HK_LIVE);
            UnregisterHotKey(handle, HK_COMPACT);
            _hwnd.RemoveHook(HotkeyHook);
        }
        if (_overlay != null) { _overlay.AllowClose = true; _overlay.Close(); }
        base.OnClosed(e);
    }
}

/// <summary>One OCR line + its translation (updates the UI when translated).</summary>
public class OcrResultItem : INotifyPropertyChanged
{
    private string _translation = "";
    public string Original { get; set; } = "";
    public string Translation
    {
        get => _translation;
        set { _translation = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translation))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

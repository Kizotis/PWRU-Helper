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

    // --- live screen translation ---
    private CancellationTokenSource? _liveCts;
    private System.Drawing.Rectangle? _liveRegion;
    private List<string> _prevNorm = new();              // normalised lines from the previous read
    private List<string> _pendingLines = new();          // lines that just appeared, awaiting confirmation
    private int _liveTicks;
    private const int MaxHistory = 50;                   // keep the last 50 translated messages
    // A newly-appeared line must survive into the NEXT read before we translate it. That
    // one-frame confirmation filters OCR flicker from the game moving behind the chat
    // (camera panning), which only ever shows up for a single frame. This stability check
    // uses a fixed, tolerant threshold so small OCR noise on a real line doesn't break it —
    // the sensitivity slider only controls how "new" a line must look to count at all.
    private const double StabilityThreshold = 0.72;

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        OcrResults.ItemsSource = _ocrItems;
        ShowAppVersion();
        PopulateLanguageCombos();
        LoadPhrases();
        CheckOcrAvailability();
        ApplySettings();
        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            SettingsService.Save(_settings);   // persist now so it can't reappear after a crash
            MessageBox.Show(this,
                "Welcome to PWRU Helper!\n\n" +
                "• Phrasebook — click a Russian phrase to copy it, then paste in game with Ctrl+V.\n" +
                "• Translator — type in your language, get Russian (auto-copied). Paste Russian and it\n" +
                "   flips direction automatically.\n" +
                "• Screen OCR — read & live-translate Russian text off your screen. Install the Russian\n" +
                "   OCR pack once (one click) for good Cyrillic reading.\n\n" +
                "Tip: run your game windowed or borderless, and keep \"Always on top\" ticked.",
                "Welcome", MessageBoxButton.OK, MessageBoxImage.Information);
        }

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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        try
        {
            var s = _settings;
            s.SensitivityPercent = (int)Math.Round(SensitivitySlider.Value);
            s.LiveSpeedPercent = (int)Math.Round(LiveSpeedSlider.Value);
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

    private void ApplyFontScale()
    {
        double s = _settings.FontScale;
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

    internal void ToggleLiveFromOverlay()
    {
        if (_liveCts != null) { StopLive(); return; }
        if (_settings.LastLiveRegion is { Length: 4 } r)
            StartLive(new System.Drawing.Rectangle(r[0], r[1], r[2], r[3]));
        else
        {
            ExitCompactMode();
            MainTabs.SelectedIndex = 2;   // Screen OCR tab
            ShowToast("Pick a screen area once — then ▶ Live (or Ctrl+Alt+L) resumes it.");
        }
    }

    /// <summary>Translate a short reply into Russian and copy it. Never throws.</summary>
    internal async Task<string> QuickReplyTranslateAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        var from = SelectedTag(FromCombo);
        if (from is null or "ru" or "auto") from = "en";
        try
        {
            var ru = await _translator.TranslateAsync(text, from, "ru");
            if (ru.Length > 0) CopyToClipboard(ru);
            return ru;
        }
        catch (Exception ex) { return $"(couldn't translate: {Friendly(ex)})"; }
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
        var fav = new HashSet<string>(_settings.Favourites);
        foreach (var p in _allPhrases) p.IsFavourite = fav.Contains(p.Ru);

        var display = new List<Phrase>();
        foreach (var ru in _settings.Recents)
            if (_allPhrases.FirstOrDefault(p => p.Ru == ru) is { } src) display.Add(Clone(src, "🕑 Recent"));
        foreach (var ru in _settings.Favourites)
            if (_allPhrases.FirstOrDefault(p => p.Ru == ru) is { } src) display.Add(Clone(src, "★ Favourites"));
        display.AddRange(_allPhrases);

        _phrasesView = new CollectionViewSource { Source = display };
        _phrasesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Phrase.Category)));
        _phrasesView.Filter += PhrasesFilter;
        PhraseList.ItemsSource = _phrasesView.View;

        static Phrase Clone(Phrase p, string category) => new()
        { En = p.En, Ru = p.Ru, Translit = p.Translit, Category = category, IsFavourite = p.IsFavourite };
    }

    private void RecordRecent(string ru)
    {
        _settings.Recents.RemoveAll(x => x == ru);
        _settings.Recents.Insert(0, ru);
        while (_settings.Recents.Count > 8) _settings.Recents.RemoveAt(_settings.Recents.Count - 1);
        RebuildPhraseView();
    }

    private void TogglePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Phrase p }) return;
        if (!_settings.Favourites.Remove(p.Ru)) _settings.Favourites.Insert(0, p.Ru);
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

    private void PhraseCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Phrase p })
        {
            if (CopyToClipboard(p.Ru))
            {
                ShowToast($"Copied:  {p.Ru}");
                RecordRecent(p.Ru);
            }
        }
    }

    // ============================================================
    //  SCREEN OCR + TRANSLATE
    // ============================================================
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
                                "\"Add-WindowsCapability -Online -Name 'Language.OCR~~~ru-RU~0.0.1.0'\"",
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

    private void CopyOcrCommand_Click(object sender, RoutedEventArgs e)
    {
        if (CopyToClipboard(OcrCommandBox.Text)) ShowToast("Command copied");
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
        Hide();
        await Task.Delay(150);
        try
        {
            var overlay = new SelectionOverlay();
            return overlay.ShowDialog() == true ? overlay.SelectedRegion : null;
        }
        finally
        {
            Show();
            Topmost = wasTopmost;
            Activate();
        }
    }

    private async void SelectArea_Click(object sender, RoutedEventArgs e)
    {
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
                ScreenReadStatus.Text = "No text detected there. Try a tighter box around the text.";
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
        if (_liveCts != null) { StopLive(); return; }

        var region = await SelectRegionAsync();
        if (region is { } rect) StartLive(rect);
    }

    private void ResumeLive_Click(object sender, RoutedEventArgs e)
    {
        if (_liveCts != null) return;
        if (_settings.LastLiveRegion is { Length: 4 } r)
            StartLive(new System.Drawing.Rectangle(r[0], r[1], r[2], r[3]));
    }

    private void StartLive(System.Drawing.Rectangle rect)
    {
        // Remember the area so it can be resumed next session without re-selecting.
        _settings.LastLiveRegion = new[] { rect.X, rect.Y, rect.Width, rect.Height };

        _liveRegion = rect;
        _prevNorm = new();
        _pendingLines = new();
        _liveTicks = 0;
        _ocrItems.Clear();
        SetLiveUi(true);
        MainTabs.SelectedIndex = 1;
        ScreenReadStatus.Text = "🔴 Live — watching the selected area. Translations appear when new text shows up.";

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

    private void StopLive()
    {
        if (_liveCts == null) return;
        _liveCts.Cancel();
        _liveCts.Dispose();
        _liveCts = null;
        _liveRegion = null;
        SetLiveUi(false);
        ScreenReadStatus.Text = "Live stopped.";
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
                var lines = TextMatching.ToSentences(await _ocr.ReadLinesAsync(bmp))
                    .Where(TextMatching.LooksLikeText).ToList();
                if (ct.IsCancellationRequested) break;
                var cur = lines.Select(TextMatching.Normalize).ToList();

                // CONFIRM: lines that appeared in the previous read AND are still on screen
                // now are real new messages (they survived a frame, so they aren't flicker
                // from the game moving behind the chat). Those get translated.
                var confirmed = _pendingLines
                    .Where(p => TextMatching.ContainsSimilar(cur, TextMatching.Normalize(p), StabilityThreshold))
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
                    ScreenReadStatus.Text = $"🔴 Live — {confirmed.Count} new line(s), translating…";
                    await AppendLinesToHistory(confirmed, target, ct);
                    if (ct.IsCancellationRequested) break;
                    ScreenReadStatus.Text = $"🔴 Live — {_ocrItems.Count} message(s) in history (check #{_liveTicks}).";
                }
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                if (++consecutiveErrors >= 5)
                {
                    ScreenReadStatus.Text = $"Live stopped after repeated errors ({Friendly(ex)}).";
                    StopLive();
                    break;
                }
                ScreenReadStatus.Text = $"Live hiccup ({Friendly(ex)}) — retrying…";
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

        var translations = await _translator.TranslateLinesAsync(newLines, "ru", target, ct);
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
        double sens = SensitivitySlider?.Value ?? 60;   // default matches the XAML slider
        return 0.60 + (sens / 100.0) * 0.38;            // 0.60 (calm) … 0.98 (very sensitive)
    }

    /// <summary>Live re-read interval from the speed slider (higher speed = shorter wait).</summary>
    private int CurrentLiveIntervalMs()
    {
        double speed = LiveSpeedSlider?.Value ?? 55;    // 0..100, default matches the XAML slider
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

            if (AutoCopyCheck.IsChecked == true && result.Length > 0 && CopyToClipboard(result))
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

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TranslateOutput.Text) && CopyToClipboard(TranslateOutput.Text))
            ShowToast("Result copied");
    }

    // Copy the original Russian of a screen-read message.
    private void CopyOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OcrResultItem item } && CopyToClipboard(item.Original))
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

    private void CopyDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (CopyToClipboard("kizotis")) ShowToast("Discord copied: kizotis");
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

    private bool _copying;   // guards against re-entrancy while we pump messages below

    private bool CopyToClipboard(string text)
    {
        // The clipboard is single-owner: another app (often Windows' clipboard history)
        // — or us, still finishing the previous copy — can hold it for a moment.
        // We retry, but the wait between tries MUST keep our message pump running: if we
        // blocked the UI thread (Thread.Sleep), our own window couldn't process the
        // messages that release the clipboard, so every retry would keep failing — which
        // was exactly the "second copy doesn't work" bug.
        if (_copying) return false;
        _copying = true;
        try
        {
            for (int i = 0; i < 8; i++)
            {
                try { Clipboard.SetDataObject(text, true); return true; }
                catch { PumpWait(70); }   // ~0.5s total, message pump stays alive
            }
            ShowToast("Clipboard busy — another app is using it. Try again in a second.");
            return false;
        }
        finally { _copying = false; }
    }

    /// <summary>Wait roughly <paramref name="ms"/> ms while still processing window
    /// messages (unlike Thread.Sleep), so the clipboard's owner can release it.</summary>
    private static void PumpWait(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { frame.Continue = false; timer.Stop(); };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
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
        // Best-effort: if another app already owns a combo, RegisterHotKey just returns false.
        RegisterHotKey(handle, HK_SHOW, mod, 0x50);       // P
        RegisterHotKey(handle, HK_TRANSLATE, mod, 0x54);  // T
        RegisterHotKey(handle, HK_LIVE, mod, 0x4C);       // L
        RegisterHotKey(handle, HK_COMPACT, mod, 0x4D);    // M
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HK_SHOW: BringToFront(); handled = true; break;
            case HK_TRANSLATE:
                BringToFront(); MainTabs.SelectedIndex = 1; TranslateInput.Focus(); handled = true; break;
            case HK_LIVE: ToggleLiveFromHotkey(); handled = true; break;
            case HK_COMPACT: ToggleCompact(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        // Nudge topmost to force ourselves above the game, then restore the user's choice.
        Topmost = true;
        Topmost = TopmostCheck.IsChecked == true;
        Activate();
    }

    private void ToggleLiveFromHotkey()
    {
        if (_liveCts != null) { StopLive(); return; }
        if (_settings.LastLiveRegion is { Length: 4 } r)
            StartLive(new System.Drawing.Rectangle(r[0], r[1], r[2], r[3]));
        else
        {
            BringToFront();
            MainTabs.SelectedIndex = 2;   // Screen OCR tab
            ShowToast("Pick a screen area once — then Ctrl+Alt+L resumes it.");
        }
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
        _overlay?.Close();
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

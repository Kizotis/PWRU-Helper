using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    private OcrService _ocr = new("ru");
    private readonly ObservableCollection<OcrResultItem> _ocrItems = new();

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    // --- live screen translation ---
    private CancellationTokenSource? _liveCts;
    private System.Drawing.Rectangle? _liveRegion;
    private List<string> _prevNorm = new();              // normalised lines from the previous read
    private readonly List<string> _shownNorm = new();    // normalised lines already translated (dedup)
    private int _liveTicks;
    private const int MaxHistory = 50;                   // keep the last 50 translated messages
    private const int ShownMemory = 80;                  // how many past lines we remember for dedup
    // A line must also survive two consecutive reads before we translate it (stability),
    // which filters out OCR flicker caused by the game moving behind the text (camera
    // panning) — that noise only ever appears for a single frame.

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        OcrResults.ItemsSource = _ocrItems;
        LoadPhrases();
        CheckOcrAvailability();
        _ = CheckForUpdatesAsync();
    }

    // ============================================================
    //  UPDATE CHECK (GitHub Releases)
    // ============================================================
    private async Task CheckForUpdatesAsync()
    {
        var info = await _updates.CheckForUpdateAsync();
        if (info == null) return; // up to date, or the check couldn't run — stay quiet

        var choice = MessageBox.Show(
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
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true });
        }
    }

    // ============================================================
    //  PHRASEBOOK
    // ============================================================
    private void LoadPhrases()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "phrases.json");

            // Portable single-file build ships phrases embedded. On first run, write an
            // editable copy next to the exe so the user can add their own phrases.
            if (!File.Exists(path))
            {
                var embedded = ReadEmbeddedPhrases();
                if (embedded != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, embedded);
                }
            }

            var json = File.Exists(path) ? File.ReadAllText(path) : ReadEmbeddedPhrases();
            if (json != null)
            {
                var items = JsonSerializer.Deserialize<List<Phrase>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (items != null) _allPhrases.AddRange(items);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load phrases.json:\n{ex.Message}", "PWRU Helper");
        }

        _phrasesView = new CollectionViewSource { Source = _allPhrases };
        _phrasesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Phrase.Category)));
        _phrasesView.Filter += PhrasesFilter;
        PhraseList.ItemsSource = _phrasesView.View;
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
                ShowToast($"Copied:  {p.Ru}");
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
        _ocrItems.Clear();
        ScreenReadStatus.Text = "Reading…";
        try
        {
            using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
            var sentences = ToSentences(await _ocr.ReadLinesAsync(bmp));
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
            ScreenReadStatus.Text = $"OCR failed: {ex.Message}";
        }
        finally
        {
            SelectAreaButton.IsEnabled = true;
        }
    }

    /// <summary>Fill the reading list with each Russian sentence and its translation.</summary>
    private async Task TranslateSentencesInto(List<string> sentences, string target)
    {
        _ocrItems.Clear();
        foreach (var s in sentences)
        {
            var item = new OcrResultItem { Original = s, Translation = "…" };
            _ocrItems.Add(item);
            try { item.Translation = await _translator.TranslateAsync(s, "ru", target); }
            catch (Exception ex) { item.Translation = $"(translation failed: {ex.Message})"; }
        }
    }

    // ============================================================
    //  LIVE SCREEN TRANSLATION
    // ============================================================
    private async void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveCts != null) { StopLive(); return; }

        var region = await SelectRegionAsync();
        if (region is not { } rect) return;

        _liveRegion = rect;
        _prevNorm = new();
        _shownNorm.Clear();
        _liveTicks = 0;
        _ocrItems.Clear();
        SetLiveUi(true);
        MainTabs.SelectedIndex = 1;
        ScreenReadStatus.Text = "🔴 Live — watching the selected area. Translations update when the text changes.";

        _liveCts = new CancellationTokenSource();
        _ = LiveLoop(rect, _liveCts.Token);
    }

    private void StopLive_Click(object sender, RoutedEventArgs e) => StopLive();

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SensitivityValue != null)
            SensitivityValue.Text = $"{(int)Math.Round(e.NewValue)}%";
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
        LiveStatus.Text = on
            ? "🔴 Live is running — re-reading the area and re-translating whenever the text changes. Press Stop to end."
            : "Live mode keeps watching the chosen area and re-translates automatically whenever the text changes, until you press Stop.";
    }

    private async Task LiveLoop(System.Drawing.Rectangle rect, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _liveTicks++;
                LiveIndicator.Text = (_liveTicks % 2 == 0) ? "●  LIVE" : "○  LIVE";  // heartbeat

                using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
                var rawLines = ToSentences(await _ocr.ReadLinesAsync(bmp));

                // Keep only lines that actually look like text (drop 1-char specks and
                // pure punctuation/number noise the moving background produces).
                var lines = rawLines.Where(LooksLikeText).ToList();
                var norm = lines.Select(Normalize).ToList();

                // A line only counts as "new to translate" when it is:
                //   (1) STABLE — present in this read AND the previous one. A line that
                //       flickers in for a single frame (camera panning behind the chat)
                //       never clears this, so moving the view no longer spams updates.
                //   (2) NOT ALREADY SHOWN — not a fuzzy match of a line we've already
                //       translated, so re-reading the same chat line with tiny OCR
                //       differences doesn't re-translate it.
                double thr = SensitivityThreshold();
                var newIdx = Enumerable.Range(0, lines.Count)
                    .Where(i => ContainsSimilar(_prevNorm, norm[i], thr) &&
                                !ContainsSimilar(_shownNorm, norm[i], thr))
                    .ToList();
                _prevNorm = norm;

                if (newIdx.Count > 0)
                {
                    var newLines = newIdx.Select(i => lines[i]).ToList();
                    foreach (var i in newIdx)
                    {
                        _shownNorm.Add(norm[i]);
                        if (_shownNorm.Count > ShownMemory) _shownNorm.RemoveAt(0);
                    }

                    var target = SelectedTag(OcrTargetCombo) ?? "en";
                    ScreenReadStatus.Text = $"🔴 Live — {newLines.Count} new line(s), translating…";
                    await AppendLinesToHistory(newLines, target);
                    ScreenReadStatus.Text = $"🔴 Live — {_ocrItems.Count} message(s) in history (check #{_liveTicks}).";
                }
            }
            catch (Exception ex)
            {
                ScreenReadStatus.Text = $"Live error: {ex.Message}";
            }

            try { await Task.Delay(800, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Append new Russian lines + their translations, keep the last MaxHistory, and auto-scroll.</summary>
    private async Task AppendLinesToHistory(List<string> newLines, string target)
    {
        foreach (var s in newLines)
        {
            var item = new OcrResultItem { Original = s, Translation = "…" };
            _ocrItems.Add(item);
            while (_ocrItems.Count > MaxHistory) _ocrItems.RemoveAt(0);   // drop the oldest
            ResultsScroller?.ScrollToEnd();
            try { item.Translation = await _translator.TranslateAsync(s, "ru", target); }
            catch (Exception ex) { item.Translation = $"(translation failed: {ex.Message})"; }
            ResultsScroller?.ScrollToEnd();
        }
    }

    /// <summary>Break OCR lines into individual sentences (split on . ! ? …).</summary>
    private static List<string> ToSentences(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            var parts = Regex.Split(line, @"(?<=[\.\!\?…])\s+");
            foreach (var part in parts)
            {
                var t = part.Trim();
                if (t.Length > 0) result.Add(t);
            }
        }
        return result;
    }

    // --- live-diff helpers: make "same chat line" recognition robust to OCR noise ---

    /// <summary>True if a line is worth translating (has ≥2 letters), not background specks.</summary>
    private static bool LooksLikeText(string s)
        => s.Count(char.IsLetter) >= 2;

    /// <summary>Lower-case, collapse whitespace, drop edge punctuation — so trivial OCR
    /// variations of the same line compare equal.</summary>
    private static string Normalize(string s)
    {
        s = Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
        return s.Trim(' ', '.', ',', '!', '?', ':', ';', '"', '\'', '-', '…', '(', ')');
    }

    /// <summary>True if <paramref name="line"/> fuzzy-matches any entry in the set.</summary>
    private static bool ContainsSimilar(IEnumerable<string> set, string line, double threshold)
        => set.Any(s => Similarity(s, line) >= threshold);

    /// <summary>Map the sensitivity slider (0–100%) to a fuzzy-match threshold. Higher
    /// sensitivity → stricter "same line" test → smaller changes count as new text.</summary>
    private double SensitivityThreshold()
    {
        double sens = SensitivitySlider?.Value ?? 60;   // default matches the XAML slider
        return 0.60 + (sens / 100.0) * 0.38;            // 0.60 (calm) … 0.98 (very sensitive)
    }

    /// <summary>0..1 similarity from Levenshtein edit distance (1 = identical).</summary>
    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
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
        var text = TranslateInput.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        var from = SelectedTag(FromCombo) ?? "en";
        var to = SelectedTag(ToCombo) ?? "ru";

        TranslateButton.IsEnabled = false;
        TranslateStatus.Text = "Translating…";
        try
        {
            TranslateOutput.Text = await _translator.TranslateAsync(text, from, to);
            TranslateStatus.Text = $"{from} → {to}";
        }
        catch (Exception ex)
        {
            TranslateStatus.Text = $"Failed: {ex.Message}";
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

    private bool CopyToClipboard(string text)
    {
        // The clipboard is a shared, single-owner resource: a clipboard-history tool,
        // a game overlay or another app can briefly hold it, making the copy fail
        // ("clipboard busy"). Retry patiently, and use SetDataObject(copy: true) rather
        // than SetText — it's more reliable and flushes so the text survives after the
        // app closes. (WPF has no retry-count overload; that one is WinForms-only.)
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { Thread.Sleep(80); }   // ~0.8s total before we give up
        }
        ShowToast("Clipboard busy — another app is using it. Try again in a second.");
        return false;
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
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

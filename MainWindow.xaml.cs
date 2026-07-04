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
    private OcrService _ocr = new("ru");
    private readonly ObservableCollection<OcrResultItem> _ocrItems = new();

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    // --- live screen translation ---
    private CancellationTokenSource? _liveCts;
    private System.Drawing.Rectangle? _liveRegion;
    private readonly List<string> _recentLines = new();  // lines seen on the previous read
    private int _liveTicks;
    private const int MaxHistory = 50;                   // keep the last 50 translated messages

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        OcrResults.ItemsSource = _ocrItems;
        LoadPhrases();
        CheckOcrAvailability();
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
        OcrLangStatus.Text = "Installing Russian OCR… accept the admin prompt. This can take a minute.";
        try
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
            var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();

            // Rebuild the engine and re-check.
            _ocr = new OcrService("ru");
            if (!CheckOcrAvailability())
                OcrLangStatus.Text = "Install finished, but Russian OCR still isn't detected. " +
                                     "Try restarting the app (or Windows) and check again.";
        }
        catch (Exception ex)
        {
            // Most commonly: the user dismissed the UAC prompt.
            OcrLangStatus.Text = $"Install was cancelled or failed ({ex.Message}). " +
                                 "You can also run the command below manually.";
        }
        finally
        {
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
        _recentLines.Clear();
        _liveTicks = 0;
        _ocrItems.Clear();
        SetLiveUi(true);
        MainTabs.SelectedIndex = 1;
        ScreenReadStatus.Text = "🔴 Live — watching the selected area. Translations update when the text changes.";

        _liveCts = new CancellationTokenSource();
        _ = LiveLoop(rect, _liveCts.Token);
    }

    private void StopLive_Click(object sender, RoutedEventArgs e) => StopLive();

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
                var lines = ToSentences(await _ocr.ReadLinesAsync(bmp));

                // Which lines are new since the previous read? (text diff, so even one
                // extra chat line is caught — a pixel % threshold missed those.)
                var prev = new HashSet<string>(_recentLines);
                var newLines = lines.Where(l => !prev.Contains(l)).ToList();
                _recentLines.Clear();
                _recentLines.AddRange(lines);

                if (newLines.Count > 0)
                {
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
        // Clipboard can be briefly locked by another app; retry a few times.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(40); }
        }
        ShowToast("Clipboard busy — try again");
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

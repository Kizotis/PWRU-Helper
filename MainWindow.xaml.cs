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
    private void CheckOcrAvailability()
    {
        var lang = _ocr.ActiveLanguage;
        bool ready = _ocr.IsAvailable && lang != null &&
                     lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

        OcrInstallPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        OcrStatus.Text = ready
            ? "Drag a box over Russian text in your game; each sentence is read and translated below."
            : "⚠ Russian OCR isn't ready yet — install it below (one click).";
    }

    private async void InstallOcr_Click(object sender, RoutedEventArgs e)
    {
        InstallOcrButton.IsEnabled = false;
        // Untick "Always on top" while installing so the Windows admin (UAC) prompt
        // can't hide behind our window.
        bool wasTopmost = Topmost;
        Topmost = false;
        OcrStatus.Text = "⏳ A Windows admin prompt is opening — click \"Yes\". " +
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
            CheckOcrAvailability();
            if (OcrInstallPanel.Visibility == Visibility.Collapsed)
                OcrStatus.Text = "Russian OCR installed ✓ — you're ready to read the screen.";
            else if (exitCode != 0)
                OcrStatus.Text = $"The install command finished with an error (code {exitCode}). " +
                                 "Make sure you're online, then try again — or run the command below in " +
                                 "an admin PowerShell.";
            else
                OcrStatus.Text = "Install finished, but Russian OCR still isn't detected. " +
                                 "Try restarting the app (or Windows) and check again.";
        }
        catch (Win32Exception w32) when (w32.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED: the user clicked "No" / closed the UAC prompt.
            OcrStatus.Text = "You didn't accept the Windows admin prompt, so nothing was installed. " +
                             "Click the button again and choose \"Yes\".";
        }
        catch (Exception ex)
        {
            OcrStatus.Text = $"Install couldn't run ({ex.Message}). " +
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

    private async void SelectArea_Click(object sender, RoutedEventArgs e)
    {
        // Hide ourselves so we don't sit on top of the region being captured.
        var wasTopmost = Topmost;
        Hide();
        await Task.Delay(150);

        System.Drawing.Rectangle? region = null;
        try
        {
            var overlay = new SelectionOverlay();
            if (overlay.ShowDialog() == true)
                region = overlay.SelectedRegion;
        }
        finally
        {
            Show();
            Topmost = wasTopmost;
            Activate();
        }

        if (region is not { } rect) return;

        _ocrItems.Clear();
        OcrStatus.Text = "Reading…";
        SelectAreaButton.IsEnabled = false;

        try
        {
            using var bmp = ScreenCapture.Capture(rect.X, rect.Y, rect.Width, rect.Height);
            var lines = await _ocr.ReadLinesAsync(bmp);
            var sentences = ToSentences(lines);

            if (sentences.Count == 0)
            {
                OcrStatus.Text = "No text detected in that area. Try a tighter box around the text.";
                return;
            }

            var target = SelectedTag(OcrTargetCombo) ?? "en";
            OcrStatus.Text = $"Read {sentences.Count} line(s). Translating…";

            foreach (var s in sentences)
            {
                var item = new OcrResultItem { Original = s, Translation = "…" };
                _ocrItems.Add(item);
                try
                {
                    item.Translation = await _translator.TranslateAsync(s, "ru", target);
                }
                catch (Exception ex)
                {
                    item.Translation = $"(translation failed: {ex.Message})";
                }
            }
            OcrStatus.Text = $"Done — {sentences.Count} line(s) translated.";
        }
        catch (Exception ex)
        {
            OcrStatus.Text = $"OCR failed: {ex.Message}";
        }
        finally
        {
            SelectAreaButton.IsEnabled = true;
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

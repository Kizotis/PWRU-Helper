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
        // Don't rebuild the list right now: the user just clicked a card, and reshuffling the
        // "Recent" group would slide the cards out from under their cursor. Refresh it the next
        // time they come back to the Phrasebook tab instead.
        _recentsDirty = true;
    }

    /// <summary>Refresh the deferred "Recent" group when the user returns to the Phrasebook.</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl.SelectionChanged also bubbles up from inner ComboBoxes — ignore those.
        if (e.Source is not TabControl) return;
        if (MainTabs.SelectedIndex == 0 && _recentsDirty)
        {
            _recentsDirty = false;
            RebuildPhraseView();
        }
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

    private static string? ReadEmbeddedPhrases() => ReadEmbeddedJson("phrases.json");

    private static string? ReadEmbeddedJson(string endsWith)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Editable-copy locator shared by the phrasebook and the slang glossary:
    /// prefer an existing copy next to the exe or in %AppData%, else create one from the
    /// embedded text so the user has something to edit. Returns null if none is possible.</summary>
    private static string? FindOrCreateEditable(string fileName, string? embedded)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "PWRUHelper", fileName),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        if (embedded != null)
            foreach (var path in candidates)
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, embedded);
                    return path;
                }
                catch { /* not writable here — try the next location */ }
        return null;
    }
}

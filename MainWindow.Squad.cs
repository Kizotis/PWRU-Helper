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
    //  SQUAD BUILDER  (tick dungeons/classes/roles → LFM chat phrase)
    // ============================================================

    /// <summary>The English dungeon name (white) is a gloss on the gold code, so it sits a size
    /// below the rest of the row. The window's default is 12.</summary>
    private const double SquadNameFontSize = 10.5;

    /// <summary>Fill the dungeon/class column grids from <c>squad.json</c> (see
    /// <see cref="SquadCatalog"/>) and wire every checkbox to rebuild the phrase. Once, at start.</summary>
    private void BuildSquadTab()
    {
        PopulateSquadColumns(SquadDungeonGrid, _squad.Dungeons);
        PopulateSquadColumns(SquadClassGrid, _squad.Classes);
        SquadEmptyHint.Visibility = _squad.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        RebuildSquadPhrase();
    }

    /// <summary>Render one section as side-by-side titled columns of tick-boxes. Each box shows
    /// "CODE «ru» name" (code gold, ru grey, name white and a size smaller — it's the supporting
    /// gloss for a cryptic dungeon code, not the label itself) and carries its paste token in Tag.</summary>
    private void PopulateSquadColumns(Grid grid, List<SquadColumn> columns)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        var gold = (System.Windows.Media.Brush)FindResource("GoldBrush");
        var muted = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        var text = (System.Windows.Media.Brush)FindResource("TextBrush");

        for (int c = 0; c < columns.Count; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = columns[c].Title,
                Foreground = gold,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            foreach (var opt in columns[c].Items)
            {
                var content = new TextBlock { TextWrapping = TextWrapping.Wrap };
                content.Inlines.Add(new System.Windows.Documents.Run(opt.Code) { Foreground = gold, FontWeight = FontWeights.SemiBold });
                if (!string.IsNullOrWhiteSpace(opt.Ru))
                    content.Inlines.Add(new System.Windows.Documents.Run($"  “{opt.Ru}”") { Foreground = muted });
                if (!string.IsNullOrWhiteSpace(opt.Name))
                    content.Inlines.Add(new System.Windows.Documents.Run($"  {opt.Name}")
                    { Foreground = text, FontSize = SquadNameFontSize });

                var cb = new CheckBox
                {
                    Content = content,
                    Tag = opt.Token,
                    Margin = new Thickness(0, 3, 0, 3),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    ToolTip = $"Adds “{opt.Token}” to the message",
                };
                cb.Checked += SquadCheck_Changed;
                cb.Unchecked += SquadCheck_Changed;
                panel.Children.Add(cb);
            }
            Grid.SetColumn(panel, c);
            grid.Children.Add(panel);
        }
    }

    private void SquadCheck_Changed(object sender, RoutedEventArgs e) => RebuildSquadPhrase();

    /// <summary>The UPPERCASE tick: remember it and re-cast the phrase already in the box.</summary>
    private void SquadUppercase_Changed(object sender, RoutedEventArgs e)
    {
        RebuildSquadPhrase();
        if (_restoringSettings) return;   // ApplySettings is setting the tick — not a user choice
        _settings.SquadUppercase = SquadUppercaseCheck.IsChecked == true;
        SettingsService.Save(_settings);
    }

    /// <summary>Rebuild the "в &lt;dungeons&gt; &lt;classes&gt;" LFM line from the ticks.</summary>
    private void RebuildSquadPhrase()
    {
        if (SquadPhraseBox == null) return;
        SquadPhraseBox.Text = SquadCatalog.BuildPhrase("в",
            TickedTokens(SquadDungeonGrid), TickedTokens(SquadClassGrid),
            SquadUppercaseCheck?.IsChecked == true);
    }

    private static IEnumerable<string> TickedTokens(Grid grid)
        => grid.Children.OfType<StackPanel>()
               .SelectMany(p => p.Children.OfType<CheckBox>())
               .Where(c => c.IsChecked == true)
               .Select(c => (string)c.Tag);

    private async void SquadCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = SquadPhraseBox.Text?.Trim() ?? "";
        if (text.Length == 0) { ShowToast("Tick at least one dungeon or class first."); return; }
        if (await CopyToClipboardAsync(text)) ShowToast($"Copied:  {text}");
    }

    private void SquadClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var grid in new[] { SquadDungeonGrid, SquadClassGrid })
            foreach (var cb in grid.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<CheckBox>()))
                cb.IsChecked = false;   // fires SquadCheck_Changed, which rebuilds the phrase
    }

    /// <summary>Load the squad-builder catalogue (dungeon/class columns). Same editable-copy
    /// strategy as the glossary, so the user can reorder or extend it in squad.json.</summary>
    private void LoadSquad()
    {
        try
        {
            var embedded = ReadEmbeddedJson("squad.json");
            var path = FindOrCreateEditable("squad.json", embedded, out var backup);
            if (backup != null) NoteDataFileRefreshed("squad.json", backup);
            string? json = embedded;
            if (path != null)
                try { json = File.ReadAllText(path); } catch { /* keep embedded */ }
            _squad = SquadCatalog.FromJson(json ?? embedded);
        }
        catch { _squad = SquadCatalog.FromJson(null); }
    }
}

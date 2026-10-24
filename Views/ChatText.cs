using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PWRUHelper.Services;

namespace PWRUHelper;

/// <summary>
/// Paints a list of <see cref="ChatRun"/>s into a TextBlock — the game's chat colours on a feed row.
///
/// An attached property rather than bound <c>Run</c>s: the number of colours in a line is only
/// known at run time (a system announcement can carry four), and WPF cannot bind
/// <c>TextBlock.Inlines</c>. A run with no colour inherits the TextBlock's own Foreground, so a row
/// whose colours could not be read looks exactly as rows always did; a speaker run with no colour
/// takes the muted nickname brush, as before.
///
/// <see cref="DimProperty"/> fades the coloured runs (the Russian line sits under its translation,
/// which stays the line to read).
/// </summary>
public static class ChatText
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<ChatRun>), typeof(ChatText), new PropertyMetadata(null, (d, _) => Rebuild(d)));

    public static readonly DependencyProperty DimProperty = DependencyProperty.RegisterAttached(
        "Dim", typeof(double), typeof(ChatText), new PropertyMetadata(1.0, (d, _) => Rebuild(d)));

    public static IReadOnlyList<ChatRun>? GetRuns(DependencyObject d) => (IReadOnlyList<ChatRun>?)d.GetValue(RunsProperty);
    public static void SetRuns(DependencyObject d, IReadOnlyList<ChatRun>? value) => d.SetValue(RunsProperty, value);
    public static double GetDim(DependencyObject d) => (double)d.GetValue(DimProperty);
    public static void SetDim(DependencyObject d, double value) => d.SetValue(DimProperty, value);

    // A handful of colours, two opacities: build each brush once and freeze it, rather than
    // allocating one per run on every row the LIVE loop adds.
    private static readonly Dictionary<(uint, double), Brush> Brushes = new();

    private static void Rebuild(DependencyObject d)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var runs = GetRuns(tb);
        if (runs == null) return;

        double dim = GetDim(tb);
        foreach (var r in runs)
        {
            if (r.Text.Length == 0) continue;
            var run = new Run(r.Text);
            if (r.Rgb is uint rgb) run.Foreground = BrushFor(rgb, dim);
            else if (r.IsSpeaker) run.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
            if (r.IsSpeaker) run.FontWeight = FontWeights.SemiBold;
            tb.Inlines.Add(run);
        }
    }

    private static Brush BrushFor(uint rgb, double opacity)
    {
        lock (Brushes)
        {
            if (Brushes.TryGetValue((rgb, opacity), out var b)) return b;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb))
                { Opacity = opacity };
            brush.Freeze();
            Brushes[(rgb, opacity)] = brush;
            return brush;
        }
    }
}

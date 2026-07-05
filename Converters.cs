using System.Globalization;
using System.Windows.Data;

namespace PWRUHelper;

/// <summary>
/// Turns an available width (bound from the phrase grid) into a sensible number of
/// columns, so cards stay a comfortable size instead of being squashed 4-up on a narrow
/// window or stretched too wide on a big one. Falls back to a single column at width 0.
/// </summary>
public class WidthToColumnsConverter : IValueConverter
{
    /// <summary>Roughly the narrowest a card should get before we drop a column.</summary>
    public double ItemMinWidth { get; set; } = 210;
    public int MaxColumns { get; set; } = 6;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double w = value is double d && !double.IsNaN(d) ? d : 0;
        int cols = ItemMinWidth > 0 ? (int)(w / ItemMinWidth) : 1;
        return Math.Clamp(cols, 1, MaxColumns);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

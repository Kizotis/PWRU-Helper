using System.Windows;

namespace PWRUHelper.Services;

/// <summary>
/// Where the guide and the bubble go around a spotlight — pure geometry, so the "always fully inside
/// the window, never on top of the thing it points at" promise is unit-tested at every size.
///
/// Rules (Sally's storyboard): the bubble takes whichever band — above or below the spotlight — has
/// more room, centred on the target and kept <see cref="Edge"/> inside the window; the guide sits
/// beside it on the side nearer the window's centre. With no spotlight (the welcome), the guide is
/// centred at 40% of the height with the bubble under it.
/// </summary>
internal static class TutorialLayout
{
    public const double Edge = 12, Gap = 16, GuideGap = 8;

    internal readonly record struct Placement(Point Bubble, Point Guide);

    public static Placement Place(Size area, Rect? target, Size bubble, Size guide)
    {
        if (target is not { Width: > 0, Height: > 0 } t)
        {
            double total = guide.Height + GuideGap + bubble.Height;
            double top = Clamp(area.Height * 0.4 - guide.Height / 2, Edge, area.Height - Edge - total);
            return new Placement(
                new Point(Clamp((area.Width - bubble.Width) / 2, Edge, area.Width - Edge - bubble.Width),
                          top + guide.Height + GuideGap),
                new Point((area.Width - guide.Width) / 2, top));
        }

        double block = Math.Max(bubble.Height, guide.Height);
        double roomAbove = t.Top - Edge, roomBelow = area.Height - Edge - t.Bottom;
        bool below = roomBelow >= roomAbove;

        double blockTop;
        if (below)
            blockTop = roomBelow >= block + Gap ? t.Bottom + Gap : area.Height - Edge - block;
        else
            blockTop = roomAbove >= block + Gap ? t.Top - Gap - block : Edge;
        blockTop = Clamp(blockTop, Edge, area.Height - Edge - block);

        // Bubble centred on the target, then the guide beside it towards the window's centre; if it
        // does not fit on that side, slide the bubble over to make room.
        double bx = Clamp(t.Left + t.Width / 2 - bubble.Width / 2, Edge, area.Width - Edge - bubble.Width);
        bool guideOnRight = bx + bubble.Width / 2 < area.Width / 2;
        double gx = guideOnRight ? bx + bubble.Width + GuideGap : bx - GuideGap - guide.Width;
        if (guideOnRight && gx + guide.Width > area.Width - Edge)
        {
            bx = Math.Max(Edge, area.Width - Edge - guide.Width - GuideGap - bubble.Width);
            gx = bx + bubble.Width + GuideGap;
        }
        else if (!guideOnRight && gx < Edge)
        {
            bx = Math.Min(area.Width - Edge - bubble.Width, Edge + guide.Width + GuideGap);
            gx = bx - GuideGap - guide.Width;
        }

        // Both sit in the block, aligned on the edge nearer the spotlight.
        double by = below ? blockTop : blockTop + block - bubble.Height;
        double gy = below ? blockTop : blockTop + block - guide.Height;
        return new Placement(new Point(bx, by), new Point(gx, gy));
    }

    private static double Clamp(double v, double min, double max) => max < min ? min : Math.Clamp(v, min, max);
}

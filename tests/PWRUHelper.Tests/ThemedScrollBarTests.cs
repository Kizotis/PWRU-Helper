using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>Every scrollbar wears the theme (Sally's spec): no arrow buttons, 10 px in the main
/// window and 8 px in the compact overlay. A broken ScrollBar template only fails when WPF applies
/// it, so these cases apply it for real on the STA thread.</summary>
[Collection("Gates")]
public class ThemedScrollBarTests
{
    [Fact]
    public void The_main_window_and_the_overlay_render_the_themed_scrollbar()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            // A plain ScrollViewer inside the main window's resource scope, with content that overflows.
            var viewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Content = new Border { Height = 2000 },
            };
            window.Content = new Grid { Children = { viewer } };
            var main = LaidOutBar(viewer, (FrameworkElement)window.Content);
            Assert.Equal(10, main.ActualWidth, 1);
            AssertThemed(main);

            var overlay = new CompactOverlay(window);
            overlay.FeedScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
            overlay.FeedScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
            ((Panel)overlay.FeedScroller.Content).Children.Add(new Border { Width = 2000, Height = 2000 });
            var compact = LaidOutBar(overlay.FeedScroller, (FrameworkElement)overlay.Content);
            Assert.Equal(8, compact.ActualWidth, 1);
            AssertThemed(compact);

            // The overlay's Width=8 must not leak onto a horizontal bar: the Orientation trigger wins.
            var horizontal = (ScrollBar)overlay.FeedScroller.Template.FindName("PART_HorizontalScrollBar", overlay.FeedScroller);
            Assert.Equal(10, horizontal.ActualHeight, 1);
            Assert.True(horizontal.ActualWidth > 100, $"horizontal bar is {horizontal.ActualWidth}px wide");
            AssertThemed(horizontal);
        });
    }

    private static ScrollBar LaidOutBar(ScrollViewer viewer, FrameworkElement root)
    {
        root.Measure(new Size(360, 400));
        root.Arrange(new Rect(0, 0, 360, 400));
        root.UpdateLayout();
        var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        Assert.NotNull(bar.Template);
        return bar;
    }

    private static void AssertThemed(ScrollBar bar)
    {
        var parts = Descendants(bar).ToList();
        // Exactly the two track halves, and they page — no arrow buttons.
        var commands = parts.OfType<RepeatButton>().Select(b => b.Command).ToList();
        Assert.Equal(2, commands.Count);
        Assert.Contains(bar.Orientation == Orientation.Vertical ? ScrollBar.PageUpCommand : ScrollBar.PageLeftCommand, commands);
        Assert.Contains(bar.Orientation == Orientation.Vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand, commands);
        var thumb = Assert.Single(parts.OfType<Thumb>());
        Assert.Same(Application.Current.FindResource("ScrollThumb"), thumb.Style);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    [Fact]
    public void The_theme_adds_no_animation_or_timer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj"))) dir = dir.Parent;
        var theme = File.ReadAllText(Path.Combine(dir!.FullName, "Views", "Theme.xaml"));
        Assert.DoesNotContain("Storyboard", theme, StringComparison.Ordinal);
    }
}

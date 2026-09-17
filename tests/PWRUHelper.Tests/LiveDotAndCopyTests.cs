using System.IO;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>Two owner reports from a local build: the LIVE heartbeat shoved the buttons beside it
/// on every beat (● and ○ are not the same width), and the feed rows needed a "copy translation"
/// button next to "copy Russian", in both windows.</summary>
[Collection("Gates")]
public class LiveDotAndCopyTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("(not translated — the engines did not come back)", false)]
    [InlineData("Let's go to the dungeon", true)]
    public void HasTranslation_only_for_a_real_translation(string body, bool expected)
        => Assert.Equal(expected, new OcrResultItem { OriginalBody = "го в данж", TranslationBody = body }.HasTranslation);

    [Fact]
    public void The_heartbeat_keeps_the_same_width_on_both_beats_in_both_windows()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var overlay = new CompactOverlay(window);

            AssertSameWidth(window.LiveIndicator, MainWindow.LiveIndicatorRunning, MainWindow.LiveIndicatorOff);
            AssertSameWidth(overlay.LiveDot, CompactOverlay.DotOn, CompactOverlay.DotOff);
        });
    }

    private static void AssertSameWidth(TextBlock label, string on, string off)
    {
        label.Visibility = Visibility.Visible;
        double Width(string text)
        {
            label.Text = text;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return label.DesiredSize.Width;
        }
        double a = Width(on), b = Width(off);
        Assert.True(Math.Abs(a - b) < 0.01, $"{label.Name}: \"{on}\" is {a:0.00}px but \"{off}\" is {b:0.00}px");
    }

    [Theory]
    [InlineData("Views/MainWindow.xaml")]
    [InlineData("Views/CompactOverlay.xaml")]
    public void Both_feeds_offer_copy_Russian_and_copy_translation(string file)
    {
        var xaml = File.ReadAllText(RepoFile(file));
        Assert.Contains("Click=\"CopyOriginal_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CopyTranslation_Click\"", xaml, StringComparison.Ordinal);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }
}

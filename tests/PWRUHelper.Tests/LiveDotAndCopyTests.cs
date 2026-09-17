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

    [Fact]
    public void The_Translator_tab_has_the_overlays_live_toggle_and_it_follows_live()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var setLiveUi = typeof(MainWindow).GetMethod("SetLiveUi",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            Assert.Equal(CompactOverlay.LiveToggleStart, window.TranslatorLiveButton.Content);
            setLiveUi.Invoke(window, new object[] { true });
            Assert.Equal(CompactOverlay.LiveToggleStop, window.TranslatorLiveButton.Content);
            setLiveUi.Invoke(window, new object[] { false });
            Assert.Equal(CompactOverlay.LiveToggleStart, window.TranslatorLiveButton.Content);
        });
    }

    [Fact]
    public void The_Translator_row_matches_the_overlay_header_order_and_style()
    {
        var xaml = File.ReadAllText(RepoFile("Views/MainWindow.xaml"));
        int dot = xaml.IndexOf("x:Name=\"LiveIndicator\"", StringComparison.Ordinal);
        int read = xaml.IndexOf("x:Name=\"SelectAreaButton\"", StringComparison.Ordinal);
        int live = xaml.IndexOf("x:Name=\"TranslatorLiveButton\"", StringComparison.Ordinal);
        Assert.True(dot > 0 && dot < read && read < live, "● LIVE · 👁 Read once · ▶ Live, like the overlay");
        foreach (int at in new[] { read, live })
            Assert.Contains("GhostButton", xaml[at..xaml.IndexOf("/>", at, StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void The_Screen_OCR_tab_is_about_choosing_the_area_and_has_no_resume_button()
    {
        var xaml = File.ReadAllText(RepoFile("Views/MainWindow.xaml"));
        Assert.DoesNotContain("ResumeLiveButton", xaml, StringComparison.Ordinal);
        int at = xaml.IndexOf("x:Name=\"LiveButton\"", StringComparison.Ordinal);
        Assert.Contains("Select the chat area", xaml[at..xaml.IndexOf("/>", at, StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }
}

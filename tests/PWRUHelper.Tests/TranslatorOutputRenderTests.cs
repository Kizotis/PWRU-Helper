using System.Text;
using System.Windows.Documents;
using System.Windows.Media;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Renders a real translation into the real Translator output box. The box is a RichTextBox so the
/// 78-character chat blocks can be tinted — which introduces the one way this feature could hurt:
/// if the highlighting ever added or dropped a character, the user would paste something other than
/// what was translated into the game. So the load-bearing assertion is that the rendered document
/// reads back character-for-character identical to the translation.
/// </summary>
[Collection("WPF")]
public class TranslatorOutputRenderTests
{
    [Fact]
    public void A_long_translation_is_marked_up_without_altering_a_single_character()
    {
        using var _ = new TempSettings("{}");
        var text = string.Join(" ", Enumerable.Repeat("длинное сообщение", 12));   // needs several messages

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            int blocks = window.ShowTranslation(text);

            Assert.True(blocks > 1, "this text should not fit in one chat message");
            Assert.Equal(text, RenderedText(window.TranslateOutput.Document));

            // The user has to SEE where to stop selecting: at least one block is tinted, and the
            // cut between two blocks is painted red.
            var runs = Runs(window.TranslateOutput.Document).ToList();
            Assert.Contains(runs, r => Same(r.Background, window.FindResource("ChatBlockBrush")));
            Assert.Contains(runs, r => Same(r.Background, window.FindResource("ChatCutBrush")));
        });
    }

    [Fact]
    public void A_short_translation_is_shown_plain_with_no_highlighting()
    {
        using var _ = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            int blocks = window.ShowTranslation("привет всем");

            Assert.Equal(1, blocks);
            Assert.Equal("привет всем", RenderedText(window.TranslateOutput.Document));
            Assert.All(Runs(window.TranslateOutput.Document), r => Assert.Null(r.Background));
        });
    }

    [Fact]
    public void Re_rendering_replaces_the_previous_translation_instead_of_appending_to_it()
    {
        using var _ = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            window.ShowTranslation(string.Join(" ", Enumerable.Repeat("слово", 40)));
            window.ShowTranslation("коротко");

            Assert.Equal("коротко", RenderedText(window.TranslateOutput.Document));
        });
    }

    // ----- helpers -----

    private static IEnumerable<Run> Runs(FlowDocument doc)
        => doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>());

    private static string RenderedText(FlowDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var run in Runs(doc)) sb.Append(run.Text);
        return sb.ToString();
    }

    private static bool Same(Brush? brush, object resource)
        => brush is SolidColorBrush a && resource is SolidColorBrush b && a.Color == b.Color;
}

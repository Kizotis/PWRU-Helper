using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PWRUHelper;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Renders the real OcrResultItem DataTemplate with a real, fully-populated item and fails on
/// ANY WPF data-binding error. This is the regression guard for the v0.11.2 crash, where a
/// <c>Run.Text</c> binding (TwoWay by default) to a get-only property threw on every rendered
/// row — invisible to an empty-list smoke launch. The lesson baked in here: verify list
/// rendering with a genuinely injected item, not an empty collection.
///
/// WPF needs an STA thread with an Application that has the Theme resources loaded — see
/// <see cref="StaTestHost"/>, shared by every WPF test. Settings go to a temp file: constructing
/// a MainWindow saves them, and a test must never rewrite the developer's real %AppData% copy.
/// </summary>
[Collection("WPF")]
public class TemplateRenderTests
{
    [Fact]
    public void OcrResult_template_renders_a_real_item_with_no_binding_errors()
    {
        using var _ = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            // A fully-populated item: speaker + both bodies + a glossary line, so every binding
            // in the template (all four Runs + the Glossary TextBlock) is actually exercised.
            var item = new OcrResultItem
            {
                Speaker = "Игрок",
                OriginalBody = "привет мир",
                TranslationBody = "hello world",
                Glossary = "🔑 в = LFM",
            };

            // Both the main Translator feed and the compact overlay feed render the same
            // OcrResultItem with two-tone (grey nick + body) Runs — the exact shape that crashed
            // in v0.11.2 when a Run.Text bound TwoWay to a get-only property. Guard BOTH templates.
            var window = new MainWindow();
            var compact = new CompactOverlay(window);

            AssertRendersCleanly(window.OcrResults.ItemTemplate, item);
            AssertRendersCleanly(compact.FeedItems.ItemTemplate, item);
        });
    }

    // ----- helpers -----

    // Render one DataTemplate against a real item via a ContentControl (which applies its template
    // synchronously during Measure — unlike an ItemsControl, whose container generation is deferred
    // to the dispatcher and would leave the tree empty headless, a silent pass) and fail on any
    // WPF binding error OR if the bound text never reached the visual tree.
    private static void AssertRendersCleanly(DataTemplate template, OcrResultItem item)
    {
        var host = new ContentControl { ContentTemplate = template, Content = item };

        var errors = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            host.ApplyTemplate();
            host.Measure(new Size(1000, 1000));
            host.Arrange(new Rect(0, 0, 1000, 1000));
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        }

        Assert.True(errors.Messages.Count == 0,
            "WPF reported binding errors while rendering the item:\n" + errors.Dump());

        var texts = CollectTextBlockText(host);
        Assert.Contains(texts, t => t.Contains("hello world"));
        Assert.Contains(texts, t => t.Contains("привет мир"));
        Assert.Contains(texts, t => t.Contains("Игрок:"));   // grey speaker prefix rendered
    }

    private static List<string> CollectTextBlockText(DependencyObject root)
    {
        var result = new List<string>();
        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is TextBlock tb)
                {
                    var text = TextOf(tb);
                    if (!string.IsNullOrEmpty(text)) result.Add(text);
                }
                Walk(child);
            }
        }
        Walk(root);
        return result;
    }

    // A TextBlock whose content is a set of bound <Run>s (as in the two-tone speaker rows)
    // reports an empty Text property — the text lives on the inline Runs. Pull both so the
    // assertions see the actual rendered characters.
    private static string TextOf(TextBlock tb)
    {
        if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
        var sb = new StringBuilder();
        foreach (var inline in tb.Inlines)
            if (inline is Run run) sb.Append(run.Text);
        return sb.ToString();
    }

    /// <summary>Captures anything WPF writes to the data-binding trace source.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = new();
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message!); }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message!); }
        public string Dump() => string.Join(Environment.NewLine, Messages);
    }
}

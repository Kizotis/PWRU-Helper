using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PWRUHelper;
using PWRUHelper.Services;
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

            // The same item as a "read once" result: both templates then take a DataTrigger branch
            // that repaints the card's frame. A trigger is a binding too — an IsReadOnce that got
            // renamed or dropped would fail silently here rather than at the user.
            var framed = new OcrResultItem
            {
                Speaker = item.Speaker,
                OriginalBody = item.OriginalBody,
                TranslationBody = item.TranslationBody,
                Glossary = item.Glossary,
                IsReadOnce = true,
            };

            AssertRendersCleanly(window.OcrResults.ItemTemplate, framed);
            AssertRendersCleanly(compact.FeedItems.ItemTemplate, framed);
        });
    }

    [Fact]
    public void A_read_once_card_is_framed_and_a_live_card_is_not()
    {
        using var _ = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var compact = new CompactOverlay(window);

            // The whole point of the flag: a read-once result now lands INSIDE the live history
            // instead of wiping it, so it has to be visually findable among the live lines. If the
            // frame stops rendering, the feature is gone while every other test still passes.
            foreach (var template in new[] { window.OcrResults.ItemTemplate, compact.FeedItems.ItemTemplate })
            {
                var live = FrameThicknessOf(template, new OcrResultItem { OriginalBody = "привет" });
                var once = FrameThicknessOf(template, new OcrResultItem { OriginalBody = "привет", IsReadOnce = true });

                Assert.True(once > live, $"a read-once card must be framed more heavily than a live one ({once} vs {live})");
            }
        });
    }

    /// <summary>Render one item and report the top border thickness the card actually ended up with.</summary>
    private static double FrameThicknessOf(DataTemplate template, OcrResultItem item)
    {
        var host = new ContentControl { ContentTemplate = template, Content = item };
        host.ApplyTemplate();
        host.Measure(new Size(1000, 1000));
        host.Arrange(new Rect(0, 0, 1000, 1000));
        host.UpdateLayout();

        var border = FindBorder(host) ?? throw new Xunit.Sdk.XunitException("the card template has no Border to frame");
        return border.BorderThickness.Top;

        static Border? FindBorder(DependencyObject root)
        {
            if (root is Border b) return b;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                if (FindBorder(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
    }

    // ----- helpers -----

    /// <summary>
    /// <b>TP-RENDER-03</b> — the provider chip, in each of its main-window placements, in every one
    /// of §2.1's eight states, with no WPF binding error and no missing text.
    ///
    /// <para><b>Three renders with a state loop, not twenty-four windows</b> (CI-4: this collection
    /// is one STA thread with a &lt; 15 s budget). One <c>MainWindow</c>, one layout pass per state,
    /// and the surfaces are the REAL <c>WriteChip</c> and <c>ReadChip</c> from the XAML — a chip
    /// renamed or dropped fails here rather than at the user.</para>
    ///
    /// <para><b>I15, satisfied by construction and asserted anyway.</b> The chip is a plain
    /// <c>TextBlock.Text</c> assignment with no binding at all — that is the deliberate choice, and
    /// the v0.11.2 lesson is why: a <c>Run.Text</c> binding is TwoWay by default and throws once per
    /// render against a get-only property. The binding-error listener is here to prove nothing was
    /// quietly turned into one.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_03_the_chip_renders_in_every_state_on_both_main_window_placements()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            var errors = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            try
            {
                foreach (var chip in EveryState())
                {
                    window.PaintEngineChip(chip, "Google  ● ready");

                    foreach (var surface in new[] { window.WriteChip, window.ReadChip })
                    {
                        surface.Measure(new Size(1000, 1000));
                        surface.Arrange(new Rect(0, 0, 1000, 1000));
                        surface.UpdateLayout();

                        Assert.Equal(chip.Label, surface.Text);
                        // AC 4: a STRING, so the dark ToolTip style in Theme.xaml applies untouched.
                        Assert.IsType<string>(surface.ToolTip);
                        // AC 5, matching the icon-only buttons beside them.
                        Assert.Equal("Translation engine status",
                                     AutomationProperties.GetName(surface));
                        // The foreground follows the state and comes from the theme, never from a
                        // literal colour (UX-DR18) — a resource reference, resolved.
                        Assert.Equal(Application.Current.Resources[chip.BrushKey], surface.Foreground);
                    }
                }
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            }

            Assert.True(errors.Messages.Count == 0,
                "WPF reported binding errors while rendering the chip:\n" + errors.Dump());
        });
    }

    /// <summary>
    /// The third placement (AC 1): the compact overlay has no chip control — the window is 360 px
    /// wide — so <c>MainWindow</c> composes <c>"{chip}  {status}"</c> and the overlay renders it.
    /// Shown <b>only when not healthy</b>, and not at all while the chip is carrying a countdown,
    /// because the overlay's ONE line is already stepping LIVE's clock then (E7.S2's AC 4).
    /// </summary>
    [Fact]
    public void The_overlay_prefixes_its_one_status_line_only_when_the_chain_is_not_healthy()
    {
        var states = EveryState().ToList();      // S1..S8, in §2.1's order
        var healthy = states[0];
        var backup = states[1];
        var allPaused = states[4];
        Assert.True(healthy.IsHealthy && backup.Text.Contains("(backup)") && allPaused.HasClock,
                    "EveryState() no longer yields S1, S2 and S5 where this case expects them");

        Assert.Equal("Reading…", MainWindow.OverlayLine(healthy, "Reading…"));
        Assert.Equal("● Google (backup)  Reading…", MainWindow.OverlayLine(backup, "Reading…"));
        // One clock per window: the status line already has one.
        Assert.Equal("○ Live paused — back in 0:58",
                     MainWindow.OverlayLine(allPaused, "○ Live paused — back in 0:58"));
        // An empty status stays empty — SetStatus collapses the line on it, and a chip prefix must
        // not resurrect a line the overlay had deliberately hidden.
        Assert.Equal("", MainWindow.OverlayLine(backup, ""));
    }

    /// <summary>
    /// The overlay end of the same rule, through the real <c>SetStatus</c>: a prefixed line is
    /// visible and carries both halves, and the visibility contract is untouched.
    /// </summary>
    [Fact]
    public void The_overlay_renders_the_composed_line_it_is_handed()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());
            var backup = EveryState().ToList()[1];      // S2 — a fallback is not healthy

            overlay.SetStatus(MainWindow.OverlayLine(backup, "🔴 Live — watching…"));
            Assert.Equal("● Google (backup)  🔴 Live — watching…", overlay.OverlayStatus.Text);
            Assert.Equal(Visibility.Visible, overlay.OverlayStatus.Visibility);

            overlay.SetStatus(MainWindow.OverlayLine(backup, ""));
            Assert.Equal(Visibility.Collapsed, overlay.OverlayStatus.Visibility);
        });
    }

    /// <summary>
    /// <b>Ruling E6-a, at the surface it is about.</b> A freshly constructed window has not read
    /// <c>provider-state.json</c> — I10 forbids it before first paint — so both chips say
    /// "checking…" rather than claiming a health nothing has verified. The warm-up runs from
    /// <c>OnWindowLoaded</c>, which has not fired here.
    ///
    /// <para>The second assertion is I10's own: <b>constructing the window starts no countdown</b>,
    /// and it cannot, because "checking…" asks for no tick (there is nothing to count down to yet).
    /// That is the invariant E7.S2 pinned and this story had to keep while adding a second start
    /// site.</para>
    /// </summary>
    [Fact]
    public void A_window_that_has_not_warmed_the_gate_state_says_it_is_checking()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            Assert.Equal("○ checking…", window.WriteChip.Text);
            Assert.Equal("○ checking…", window.ReadChip.Text);
            Assert.False(window.CountdownRunning,
                         "a window that has not painted yet must not be running a countdown (I10)");
        });
    }

    /// <summary>
    /// <b>I10, as a behaviour and not as a source scan.</b> The case above proves the chip SAYS
    /// "checking…"; this one proves the reason is true — a real window, built with a real pause
    /// standing in the redirected <c>provider-state.json</c>, has not read that file when its
    /// constructor paints the chip.
    ///
    /// <para>The discriminating assertion is the TOOLTIP, because it is built from the same
    /// snapshots the chip is and it names every tier's state whether or not the warm-up has run: a
    /// constructor that loaded the file would render Google as paused there, while "checking…" on
    /// the chip would go on looking exactly the same. This is the regression a well-meant
    /// "make the tooltip honest" change would introduce — a file read in front of the first paint,
    /// which is what I10 and P1 exist to stop and what ruling <b>E6-a</b> settled by moving the read
    /// to <c>OnWindowLoaded</c>, on the pool, behind every await.</para>
    ///
    /// <para>It writes the run-wide redirect's own file (never the developer's <c>%AppData%</c>) and
    /// deletes it again: the <c>Gates</c> collection is serialised against every other one, so no
    /// case can see it, and a leftover file would seed the next gate case that opens no
    /// <c>TempGateState</c> of its own.</para>
    /// </summary>
    [Fact]
    public void A_new_window_has_not_read_the_gate_state_file_when_it_paints_the_chip()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        // Far enough out to be a live window under either clock a previous case can have left
        // behind — the wall clock, or the run-wide virtual one.
        var path = TestGateStateRedirect.Path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "version": 1, "providers": { "google-dict": {
                "blockedUntil": "2099-01-01T00:00:00+00:00",
                "keyBlockedUntil": null,
                "strikes": 1,
                "lastKind": "RateLimited",
                "lastAt": null,
                "cleanSince": null } } }
            """);

        try
        {
            StaTestHost.Run(() =>
            {
                var window = new MainWindow();

                Assert.Equal("○ checking…", window.WriteChip.Text);
                var tooltip = Assert.IsType<string>(window.WriteChip.ToolTip);

                var google = tooltip.Split('\n')[0];
                Assert.StartsWith("Google ", google, StringComparison.Ordinal);
                Assert.EndsWith("● ready", google, StringComparison.Ordinal);
                Assert.DoesNotContain("paused", tooltip, StringComparison.Ordinal);

                // …and nothing started counting down to a window the app has not read (I10).
                Assert.False(window.CountdownRunning);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <b>The repaint guard, on the chip</b> (AC 3 of E7.S2, hint 7). At 1 Hz above the ninety-second
    /// band the rendered string changes once a minute, and on the other fifty-nine ticks this method
    /// must assign NOTHING — not the text, and not the foreground either, because a resource
    /// reference re-applied every second is the same cost wearing a different name.
    ///
    /// <para>Asserted through <c>ReadLocalValue</c> and not by counting changes: WPF drops a
    /// dependency-property change whose value is equal, so a guardless implementation would raise
    /// nothing either and the two would be indistinguishable. A local value is set by an assignment
    /// and by nothing else — which makes <c>ClearValue</c> a tripwire only a real write can trip.
    /// (Mutation-verified: deleting the <c>if</c> around <c>SetResourceReference</c> fails this.)</para>
    /// </summary>
    [Fact]
    public void The_chip_is_not_repainted_when_the_rendered_string_has_not_changed()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var states = EveryState().ToList();

            window.PaintEngineChip(states[0], "tooltip");
            Assert.NotEqual(DependencyProperty.UnsetValue,
                            window.WriteChip.ReadLocalValue(TextBlock.ForegroundProperty));

            // The tripwire: only PaintEngineChip writes this back.
            window.WriteChip.ClearValue(TextBlock.ForegroundProperty);
            window.ReadChip.ClearValue(TextBlock.ForegroundProperty);

            // The same second, re-rendered — fifty-nine of every sixty ticks above 90 s.
            window.PaintEngineChip(states[0], "tooltip");
            Assert.Equal(DependencyProperty.UnsetValue,
                         window.WriteChip.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.Equal(DependencyProperty.UnsetValue,
                         window.ReadChip.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.Equal(states[0].Label, window.WriteChip.Text);

            // …and a state that really did change goes through, on both surfaces, in full.
            window.PaintEngineChip(states[5], "tooltip");
            Assert.Equal(states[5].Label, window.WriteChip.Text);
            Assert.Equal(states[5].Label, window.ReadChip.Text);
            Assert.NotEqual(DependencyProperty.UnsetValue,
                            window.WriteChip.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.NotEqual(DependencyProperty.UnsetValue,
                            window.ReadChip.ReadLocalValue(TextBlock.ForegroundProperty));
        });
    }

    /// <summary>§2.1's eight states as eight chips, built through the real pure function so this
    /// file cannot drift from <c>ProviderChipTests</c>' expectations — that file owns WHAT each
    /// state says; this one owns whether it renders.</summary>
    private static IEnumerable<EngineChip> EveryState()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var free = new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx };

        EngineStatus Of(Dictionary<string, GateSnapshot> gates, string[] configured,
                        ChainTranslator.Outcome? outcome)
            => EngineStatus.Of(free, gates, configured, outcome, stateKnown: true, now);

        GateSnapshot Blocked(int seconds, TranslationErrorKind kind)
            => new(GateState.Open, seconds < 0 ? DateTimeOffset.MaxValue
                                               : now + TimeSpan.FromSeconds(seconds), 1, kind);

        ChainTranslator.Outcome Answered(string id, params string[] skipped)
            => new(id, skipped.Select(s => (ProviderId: s, Reason: "Paused")).ToList(), null, null);

        var none = new Dictionary<string, GateSnapshot>(StringComparer.Ordinal);
        var keys = new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx,
                           ProviderIds.DeepL, ProviderIds.Azure };

        yield return MainWindow.ChipFor(Of(none, free, Answered(ProviderIds.GoogleDict)));   // S1
        yield return MainWindow.ChipFor(Of(none, free,
            Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict)));                        // S2
        yield return MainWindow.ChipFor(Of(
            new(StringComparer.Ordinal)
            { [ProviderIds.GoogleDict] = Blocked(58, TranslationErrorKind.RateLimited) },
            free, Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict)));                  // S3
        yield return MainWindow.ChipFor(EngineStatus.Of(
            new[] { ProviderIds.GoogleDict, ProviderIds.Bergamot }, none,
            new[] { ProviderIds.GoogleDict, ProviderIds.Bergamot },
            Answered(ProviderIds.Bergamot, ProviderIds.GoogleDict), true, now));              // S4
        yield return MainWindow.ChipFor(Of(
            new(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = Blocked(200, TranslationErrorKind.RateLimited),
                [ProviderIds.GoogleGtx] = Blocked(260, TranslationErrorKind.Blocked),
            }, free, null));                                                                   // S5
        yield return MainWindow.ChipFor(Of(
            new(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = Blocked(30, TranslationErrorKind.Network),
                [ProviderIds.GoogleGtx] = Blocked(30, TranslationErrorKind.Network),
            }, free, null));                                                                   // S6
        yield return MainWindow.ChipFor(Of(
            new(StringComparer.Ordinal)
            { [ProviderIds.DeepL] = Blocked(-1, TranslationErrorKind.AuthFailed) },
            keys, Answered(ProviderIds.GoogleDict)));                                          // S7
        yield return MainWindow.ChipFor(Of(
            new(StringComparer.Ordinal)
            { [ProviderIds.Azure] = Blocked(3600, TranslationErrorKind.QuotaExhausted) },
            keys, Answered(ProviderIds.GoogleDict, ProviderIds.Azure)));                       // S8
    }

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

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
[Collection("Gates")]
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

    /// <summary>
    /// <b>TP-RENDER-01 and TP-RENDER-02 — every row state, on both templates, with zero binding
    /// errors and the text in the visual tree.</b> §3.3a (amendment <b>A5</b>) says three row texts
    /// exist and a row may carry nothing else; this renders all three plus a real translation, live
    /// and read-once, through <c>window.OcrResults.ItemTemplate</c> and
    /// <c>compact.FeedItems.ItemTemplate</c>.
    ///
    /// <para><b>Why a <c>ContentControl</c> and not an <c>ItemsControl</c>:</b> container generation
    /// is deferred to the dispatcher, so an <c>ItemsControl</c> renders NOTHING headless and every
    /// assertion below would pass over an empty tree. That is written down in four places in this
    /// repo and it is the vacuous-green failure this story exists to avoid.</para>
    ///
    /// <para><b>The strings come from the deck, never as literals.</b> A row text is
    /// <c>UserMessages</c>' to spell (GAP-4 / UX-DR19), and the "(" is I4's failure marker added by
    /// the call site — which is exactly the distinction AC 1 and AC 2 draw: the two FINISHED forms
    /// are parenthesised, the pending one deliberately is not, because it has not failed yet.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_01_02_every_row_state_renders_on_both_feed_templates()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var compact = new CompactOverlay(window);

            var states = new (string Name, string Body)[]
            {
                ("translated", "hello world"),
                ("pending",    UserMessages.PendingRetryRow()),
                ("given-up",   $"({UserMessages.RetryGaveUpRow()})"),
                ("cancelled",  $"({UserMessages.ReadCancelledRow()})"),
            };

            foreach (var template in new[] { window.OcrResults.ItemTemplate, compact.FeedItems.ItemTemplate })
                foreach (var (name, body) in states)
                    foreach (var readOnce in new[] { false, true })
                    {
                        var texts = RenderAndCollectText(template, new OcrResultItem
                        {
                            Speaker = "Игрок",
                            OriginalBody = "привет мир",
                            TranslationBody = body,
                            IsReadOnce = readOnce,       // the DataTrigger is a binding too
                        });

                        Assert.True(texts.Any(t => t.Contains(body, StringComparison.Ordinal)),
                            $"the {name} row never reached the visual tree (read-once: {readOnce}); "
                            + "rendered: " + string.Join(" | ", texts));

                        // The Russian is never replaced by the row text: a message that could not be
                        // translated still leaves the player the line they can copy and ask about.
                        Assert.Contains(texts, t => t.Contains("привет мир", StringComparison.Ordinal));

                        // AC 1, at the surface rather than at the deck: a pending row must not read
                        // as terminal, and "(" is the only thing that would make it.
                        if (name == "pending")
                            Assert.DoesNotContain(texts, t => t.Contains('('));
                    }
        });
    }

    /// <summary>
    /// <b>E5.S3's whole point, as a render case.</b> A pending row is filled in <i>in place</i> —
    /// <c>TranslationBody</c> raises <c>PropertyChanged</c>, the <c>Mode=OneWay</c> <c>Run.Text</c>
    /// binding repaints, and the row is NOT appended a second time (a duplicated feed would be worse
    /// than the failure it came from).
    ///
    /// <para>Asserted as "same visual root, different text": the template child the first layout
    /// produced is still the one carrying the new string. A binding that had been dropped, or a
    /// property that stopped notifying, would leave "…" on screen for ever with the drain reporting
    /// success — the failure no other test in this file can see.</para>
    /// </summary>
    [Fact]
    public void A_pending_row_that_is_filled_in_repaints_the_same_visual_tree()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var compact = new CompactOverlay(window);

            foreach (var template in new[] { window.OcrResults.ItemTemplate, compact.FeedItems.ItemTemplate })
            {
                var item = new OcrResultItem
                {
                    Speaker = "Игрок",
                    OriginalBody = "привет мир",
                    TranslationBody = UserMessages.PendingRetryRow(),
                };
                var host = new ContentControl { ContentTemplate = template, Content = item };

                Assert.Empty(Render(host).Messages);
                Assert.Contains(CollectTextBlockText(host),
                                t => t.Contains(UserMessages.PendingRetryRow(), StringComparison.Ordinal));
                var root = VisualTreeHelper.GetChild(host, 0);

                // The drain, exactly as MainWindow.Live.cs writes it: one assignment, no re-add.
                item.TranslationBody = "hello world";
                var errors = Render(host);

                Assert.True(errors.Messages.Count == 0,
                    "WPF reported binding errors while filling a pending row in:\n" + errors.Dump());

                var after = CollectTextBlockText(host);
                Assert.Contains(after, t => t.Contains("hello world", StringComparison.Ordinal));
                Assert.DoesNotContain(after, t => t.Contains(UserMessages.PendingRetryRow(), StringComparison.Ordinal));
                Assert.Same(root, VisualTreeHelper.GetChild(host, 0));
            }
        });
    }

    /// <summary>
    /// <b>TP-RENDER-06 — no row ever carries a countdown</b> (UX principle 5, hint 2). Fifty rows in
    /// the state a full pause leaves them — mostly pending, some given up, one read cancelled — laid
    /// out through both real templates, and the RENDERED text of every one of them is scanned for a
    /// clock.
    ///
    /// <para>A render-time scan rather than a source one on purpose: the regression this catches is
    /// a future story helpfully appending "— back in 0:58" to a row, which no assertion about
    /// <c>UserMessages</c> would see. There is exactly one clock per window and it lives in the chip
    /// and the status line (E7.S2); fifty of them, repainting at 1 Hz, is also NFR7's repaint budget
    /// spent on saying the same thing fifty times.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_06_no_row_carries_a_countdown_with_fifty_rows_paused()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var compact = new CompactOverlay(window);

            var rows = Enumerable.Range(0, 50).Select(i => new OcrResultItem
            {
                Speaker = i % 3 == 0 ? "" : "Игрок" + i,       // both shapes of the two-tone line
                OriginalBody = "привет мир " + i,
                TranslationBody = (i % 10) switch
                {
                    7 => $"({UserMessages.RetryGaveUpRow()})",     // attempts spent
                    9 => $"({UserMessages.ReadCancelledRow()})",   // E5-g's third state
                    _ => UserMessages.PendingRetryRow(),           // still waiting for the drain
                },
            }).ToList();

            int renderedRows = 0;
            foreach (var template in new[] { window.OcrResults.ItemTemplate, compact.FeedItems.ItemTemplate })
                foreach (var row in rows)
                {
                    var texts = RenderAndCollectText(template, row);
                    Assert.NotEmpty(texts);                        // non-vacuous: it really rendered
                    renderedRows++;

                    foreach (var text in texts)
                    {
                        Assert.False(Regex.IsMatch(text, @"\d+:\d{2}"),
                            $"a feed row rendered an m:ss countdown: {text}");
                        Assert.False(Regex.IsMatch(text, @"\babout\b", RegexOptions.IgnoreCase),
                            $"a feed row rendered \"about N min\" / \"about to retry\": {text}");
                        Assert.False(Regex.IsMatch(text, @"\bmins?\b|\bseconds?\b|\bsecs?\b",
                                                   RegexOptions.IgnoreCase),
                            $"a feed row rendered a duration: {text}");
                        Assert.False(Regex.IsMatch(text, @"retry|retrying|paused", RegexOptions.IgnoreCase),
                            $"a feed row rendered a status the status line already carries: {text}");
                    }
                }

            Assert.Equal(rows.Count * 2, renderedRows);
        });
    }

    /// <summary>
    /// <b>TP-RENDER-04 / I15 — the source scan.</b> Every <c>Run.Text</c> BINDING in the two windows
    /// carries <c>Mode=OneWay</c>. <c>Run.Text</c> binds TwoWay by default and a get-only property
    /// (<c>SpeakerPrefix</c>, <c>Original</c>, <c>Translation</c>) bound TwoWay throws once per
    /// rendered row — the v0.11.2 crash, invisible to an empty-list smoke launch.
    ///
    /// <para><b>Scoped to bindings, and proved non-vacuous twice.</b> The About tab is full of
    /// <c>&lt;Run Text="literal"/&gt;</c>, which a naive scan would flag; this one requires
    /// <c>{Binding</c>. The standard failure of a source-scan test is a regex that matches nothing
    /// and is green for ever, so the scan asserts a floor (the four bindings each feed template ships
    /// today) AND is run against a synthetic TwoWay Run that it must catch.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_04_every_Run_Text_binding_is_Mode_OneWay()
    {
        const string pattern = @"<Run\s[^>]*?Text\s*=\s*""\{\s*Binding[^""]*""";

        // The tripwire first: a pattern that matched nothing would pass the loop below silently.
        var trap = Regex.Matches("""<Run Text="{Binding TranslationBody}" Foreground="x"/>""", pattern);
        Assert.Single(trap);
        Assert.DoesNotContain("Mode=OneWay", trap[0].Value, StringComparison.Ordinal);

        var total = 0;
        foreach (var file in new[] { "Views/MainWindow.xaml", "Views/CompactOverlay.xaml" })
        {
            var runs = Regex.Matches(File.ReadAllText(RepoFile(file)), pattern)
                            .Select(m => m.Value).ToList();

            Assert.True(runs.Count >= 4,
                $"{file}: the scan found {runs.Count} Run.Text bindings and the feed template ships "
                + "four — the regex has drifted from the XAML and would pass over anything");

            foreach (var run in runs)
                Assert.True(run.Contains("Mode=OneWay", StringComparison.Ordinal),
                    $"{file}: a Run.Text binding without Mode=OneWay (I15 — it binds TwoWay by "
                    + $"default and throws once per rendered row): {run}");

            total += runs.Count;
        }

        Assert.True(total >= 8, $"only {total} Run.Text bindings found across both feed templates");
    }

    /// <summary>
    /// <b>AC 4 — <c>OcrResultItem</c> does not move.</b> It lives at the repository ROOT with
    /// namespace <c>PWRUHelper</c> (not <c>.Models</c>), <c>project-context.md</c> says so, and every
    /// render case in this file binds against it. A rule stated in four documents and enforced by
    /// none is a rule that gets tidied away by the next refactor.
    /// </summary>
    [Fact]
    public void OcrResultItem_stays_at_the_repository_root_in_the_PWRUHelper_namespace()
    {
        Assert.Equal("PWRUHelper", typeof(OcrResultItem).Namespace);

        var path = RepoFile("OcrResultItem.cs");     // asserts it is AT the root
        Assert.Contains("namespace PWRUHelper;", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>An overlay attached to the window the way <c>EnterCompactMode</c> attaches one, so
    /// <c>PaintEngineChip</c> really reaches it — without <c>Show()</c>-ing a 360 px always-on-top
    /// window on a build agent (CI-3). The field is the seam because the wiring under test is
    /// "MainWindow paints every surface it has", and a case that called the overlay's own renderer
    /// would pass with that wiring cut.</summary>
    private static CompactOverlay AttachOverlay(MainWindow window)
    {
        var overlay = new CompactOverlay(window);
        typeof(MainWindow).GetField("_overlay", BindingFlags.Instance | BindingFlags.NonPublic)!
                          .SetValue(window, overlay);
        return overlay;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        var path = Path.Combine(dir!.FullName, relative);
        Assert.True(File.Exists(path), $"expected {relative} at the repo root");
        return path;
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
    /// and the surfaces are the REAL <c>WriteChip</c> and the overlay's REAL <c>EngineChipText</c>
    /// from the XAML — a chip renamed or dropped fails here rather than at the user.</para>
    ///
    /// <para>The read path's own chip is gone (it said what <c>WriteChip</c> said, a hand away on
    /// the same tab); the second placement is the compact overlay's header, and it is driven here
    /// through the REAL wiring — <c>PaintEngineChip</c> reaching the overlay <c>MainWindow</c>
    /// holds — rather than by calling the overlay's renderer, so a chip that stops being pushed
    /// fails here too. The field is set rather than <c>EnterCompactMode</c> called: showing a 360 px
    /// always-on-top window on a build agent is what CI-3 forbids.</para>
    ///
    /// <para><b>I15, satisfied by construction and asserted anyway.</b> The chip is a plain
    /// <c>TextBlock.Text</c> assignment with no binding at all — that is the deliberate choice, and
    /// the v0.11.2 lesson is why: a <c>Run.Text</c> binding is TwoWay by default and throws once per
    /// render against a get-only property. The binding-error listener is here to prove nothing was
    /// quietly turned into one.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_03_the_chip_renders_in_every_state_on_both_placements()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var overlay = AttachOverlay(window);

            var errors = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            try
            {
                foreach (var chip in EveryState())
                {
                    window.PaintEngineChip(chip, "Google  ● ready");

                    foreach (var surface in new[] { window.WriteChip, overlay.EngineChipText })
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
    /// The third placement (AC 1) is now a REAL chip, in the overlay's header — so the status line
    /// below it carries the status and nothing else.
    ///
    /// <para>It used to be a composed prefix (<c>MainWindow.OverlayLine</c>, <c>"{chip}  {status}"</c>)
    /// because that window had no chip control to put the state on. It has one now, painted from the
    /// same <see cref="EngineChip"/> by <c>PaintEngineChip</c>, a couple of centimetres above: the
    /// prefix had become the same label twice on one 360 px window — the duplication UX-DR19 names,
    /// and the same one the Translator tab's second chip was removed for. Both halves are pinned
    /// here so a revert is loud: the composer is gone, and the loop hands the overlay <c>msg</c>.</para>
    /// </summary>
    [Fact]
    public void The_overlay_status_line_carries_no_chip_prefix_because_the_header_has_a_chip()
    {
        var main = File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs"));
        var live = File.ReadAllText(RepoFile("Views/MainWindow.Live.cs"));

        Assert.DoesNotContain("string OverlayLine(", main);
        Assert.Contains("_overlay?.SetStatus(msg);", live, StringComparison.Ordinal);
        // The header chip is the surface that carries the state now, and it is TOLD (I2).
        Assert.Contains("_overlay?.SetEngineChip(chip, tooltip);", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlay end of the same rule, through the real <c>SetStatus</c>: the line it is handed is
    /// the line it shows, and the visibility contract is untouched.
    /// </summary>
    [Fact]
    public void The_overlay_renders_the_status_it_is_handed()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());
            var backup = EveryState().ToList()[1];      // S2 — a fallback is not healthy

            // Degraded chain, and the status line STILL says only what the status says: the chip
            // beside it is where "Google (backup)" is read, once.
            overlay.SetEngineChip(backup, "why");
            overlay.SetStatus(UserMessages.LiveWatching());
            Assert.Equal(UserMessages.LiveWatching(), overlay.OverlayStatus.Text);
            Assert.Equal(Visibility.Visible, overlay.OverlayStatus.Visibility);
            Assert.Equal(backup.Label, overlay.EngineChipText.Text);

            overlay.SetStatus("");
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
    /// deletes it again — a leftover would seed the next gate case that opens no
    /// <c>TempGateState</c> of its own. <b>E7.S8 corrected what that used to rest on:</b> this class
    /// said the <c>Gates</c> collection was serialised against every other one, and it was not —
    /// the gate cases ran beside this file's and read this very <c>blockedUntil: 2099</c> while it
    /// existed. This class is now IN that collection, which is the serialisation.</para>
    ///
    /// <para><b>And it is attempted more than once, which is not a shrug</b> (E7.S8; this case and
    /// <c>PerLineFallbackTests</c>' were the two E7.S7's review saw fail in 2 of 7 full runs).
    /// Laying any part of a real window out makes WPF queue that window's <c>Loaded</c>, and
    /// <c>Loaded</c> is raised by a <b>dispatcher operation</b> — so a window an earlier case
    /// measured fires <c>OnWindowLoaded</c> whenever a later layout pass happens to complete, which
    /// on this STA thread is a leftover 1 Hz countdown tick, seconds later and inside somebody
    /// else's case. Its answer is <c>Task.Run(…EnsureGateStateLoaded…)</c>, which reads
    /// <c>provider-state.json</c> — <b>this file</b> — and seeds the registry the assertion below
    /// reads. Nothing in a test can stop that: it is the app's own behaviour, correctly, and the
    /// only handle on it would be a production change to hold the task. So each attempt puts the
    /// registry back first: <b>a constructor that really reads the file fails every attempt</b>,
    /// while a stray warm-up can only ever spoil one.</para>
    /// </summary>
    [Fact]
    public void A_new_window_has_not_read_the_gate_state_file_when_it_paints_the_chip()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        var path = TestGateStateRedirect.Path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Exception? failure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // "Nothing read yet" is this case's premise, and an earlier case in this collection may
            // have loaded the registry — which would make the assertions below VACUOUS, since a
            // window cannot read a file the process has already read.
            ProviderGates.ResetForTests();                            // …which nulls the override
            ProviderGates.PathOverride = TestGateStateRedirect.Path;  // never the real %AppData%

            // Far enough out to be a live window under either clock a previous case can have left
            // behind — the wall clock, or the run-wide virtual one.
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

                return;
            }
            catch (Exception ex)
            {
                failure = ex;   // a stray warm-up, or the regression — the next attempts say which
            }
            finally
            {
                File.Delete(path);
            }
        }

        throw failure!;
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
            var overlay = AttachOverlay(window);
            var states = EveryState().ToList();

            window.PaintEngineChip(states[0], "tooltip");
            Assert.NotEqual(DependencyProperty.UnsetValue,
                            window.WriteChip.ReadLocalValue(TextBlock.ForegroundProperty));

            // The tripwire: only PaintEngineChip writes this back.
            window.WriteChip.ClearValue(TextBlock.ForegroundProperty);
            overlay.EngineChipText.ClearValue(TextBlock.ForegroundProperty);

            // The same second, re-rendered — fifty-nine of every sixty ticks above 90 s.
            window.PaintEngineChip(states[0], "tooltip");
            Assert.Equal(DependencyProperty.UnsetValue,
                         window.WriteChip.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.Equal(DependencyProperty.UnsetValue,
                         overlay.EngineChipText.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.Equal(states[0].Label, window.WriteChip.Text);

            // …and a state that really did change goes through, on both surfaces, in full.
            window.PaintEngineChip(states[5], "tooltip");
            Assert.Equal(states[5].Label, window.WriteChip.Text);
            Assert.Equal(states[5].Label, overlay.EngineChipText.Text);
            Assert.NotEqual(DependencyProperty.UnsetValue,
                            window.WriteChip.ReadLocalValue(TextBlock.ForegroundProperty));
            Assert.NotEqual(DependencyProperty.UnsetValue,
                            overlay.EngineChipText.ReadLocalValue(TextBlock.ForegroundProperty));
        });
    }

    /// <summary>
    /// <b>The overlay's header chip is the SAME chip</b> — one composer, two surfaces. The main
    /// window builds one <see cref="EngineChip"/> and writes both, so the tab the player left and
    /// the 360 px window in front of the game cannot come to say two different things about one
    /// state (principle 1).
    ///
    /// <para>The three properties are asserted together because they are written together: the
    /// label, the foreground as a THEME resource reference (never a literal colour — UX-DR18), and
    /// a tooltip that is a <c>string</c>, which is what keeps <c>Theme.xaml</c>'s dark
    /// <c>ToolTip</c> style applying to it (AC 4).</para>
    /// </summary>
    [Fact]
    public void The_overlay_header_chip_says_what_the_main_chip_says()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var overlay = AttachOverlay(window);

            foreach (var chip in EveryState())
            {
                window.PaintEngineChip(chip, "Google  ● ready");

                Assert.Equal(window.WriteChip.Text, overlay.EngineChipText.Text);
                Assert.Equal(chip.Label, overlay.EngineChipText.Text);
                Assert.Equal(Application.Current.Resources[chip.BrushKey],
                             overlay.EngineChipText.Foreground);
                Assert.Equal(window.WriteChip.ToolTip,
                             Assert.IsType<string>(overlay.EngineChipText.ToolTip));
            }
        });
    }

    /// <summary>
    /// <b>The same words in a different colour still repaint.</b> The repaint guard compared the
    /// LABEL and let the foreground ride on it, which held only while a label implied a state — and
    /// it does not: §3.0 rule 2 drops the <c>· backup</c> suffix from a name that already carries
    /// one, so a chain SERVING from <c>google-gtx</c> (healthy, teal) and one that FELL BACK to it
    /// (degraded, gold) both render <c>● Google (backup)</c> with the same glyph. Painting the
    /// second after the first left the first's colour on screen: the app saying "degraded" in the
    /// healthy colour until some later state happened to change the words.
    ///
    /// <para>Both directions, on both surfaces, and the premise is asserted first — if the two
    /// labels ever stop being identical this case is proving nothing and says so.</para>
    /// </summary>
    [Fact]
    public void Two_states_with_one_label_and_two_brushes_do_not_share_a_stale_colour()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var overlay = AttachOverlay(window);
            var states = EveryState().ToList();

            var fellBack = states[1];       // S2  — degraded: google-dict was skipped
            var serving = states[^1];       // S1′ — healthy: google-gtx answered on its own
            Assert.Equal(fellBack.Label, serving.Label);
            Assert.NotEqual(fellBack.BrushKey, serving.BrushKey);
            Assert.True(serving.IsHealthy && !fellBack.IsHealthy);

            foreach (var (first, second) in new[] { (fellBack, serving), (serving, fellBack) })
            {
                window.PaintEngineChip(first, "tooltip");
                window.PaintEngineChip(second, "tooltip");

                foreach (var surface in new[] { window.WriteChip, overlay.EngineChipText })
                    Assert.Equal(Application.Current.Resources[second.BrushKey], surface.Foreground);
            }
        });
    }

    /// <summary>
    /// <b>TP-RENDER-10 — the overlay header never paints two things on top of each other.</b>
    ///
    /// <para>It did. The header was a single-cell <c>Grid</c> holding a left group and a right group
    /// that overlapped whenever their widths stopped adding up, and adding the chip is what made
    /// them stop: at the shipped 360 px with LIVE running, <c>● Google</c> (x 91.1→136.1) painted
    /// over 40 of the 44.8 px of <c>● LIVE</c> (x 86.3→131.1) — in the one state the overlay exists
    /// for. At <c>MinWidth</c> 240 the buttons already sat on the title before the chip existed.</para>
    ///
    /// <para><b>Docked, in a priority order</b> (see the XAML): the buttons keep their full width,
    /// then <c>● LIVE</c>, then the chip, and the title takes what is left and trims. A column
    /// layout was measured too and rejected: with <c>Auto</c> columns the over-constrained header
    /// pushes the buttons off the window instead of over the title — the <c>⤢</c> button ended at
    /// x=479 on a 240 px window, unreachable. Docking is the only WPF panel that expresses "who
    /// gives way first".</para>
    ///
    /// <para><b>What is asserted, in two strengths.</b> The layout SLOTS are disjoint at both widths
    /// and in every state — that is the by-construction claim, and it is exact. The PAINTED boxes
    /// are disjoint too, up to one allowance that WPF makes unavoidable: a <c>TextBlock</c> trimmed
    /// past its own <c>…</c> still renders that <c>…</c>, so two labels squeezed into a few pixels
    /// bleed by up to their two ellipses (19.0 px for the chip against the LIVE dot, measured from
    /// the real typefaces). Anything that is not a trimmed label — the buttons — is allowed nothing,
    /// and the case asserts up front that the allowance is smaller than the 40 px overdraw it exists
    /// to catch. What it replaces at 240 is a 107 px overdraw of the title by the buttons.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_10_the_overlay_header_lays_out_without_overlap_at_both_widths()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var overlay = AttachOverlay(window);
            var root = (FrameworkElement)overlay.Content;

            // Every state §2.1 has, including the longest label the app can produce — found rather
            // than guessed, so a copy change that grows a chip is measured here rather than sailing
            // past this case.
            var states = EveryState().ToList();

            // …and the allowance below is not what makes this pass. The review measured the shipped
            // Grid at 360 with LIVE on: "● Google" at 91.1→136.1 over "● LIVE" at 86.3→131.1, a
            // 40.0 px overdraw. Two ellipses are 19.0 px, so that geometry fails this case by more
            // than double — the case is not green by construction.
            Assert.True(131.1 - 91.1 > EllipsisWidth(overlay.LiveDot) + EllipsisWidth(overlay.EngineChipText),
                        "the ellipsis allowance has grown large enough to cover the overdraw this "
                        + "case exists for");

            foreach (var width in new[] { 240.0, 360.0 })
            foreach (var live in new[] { true, false })
            foreach (var chip in states)
            {
                overlay.LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                window.PaintEngineChip(chip, "tooltip");

                root.Measure(new Size(width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
                root.UpdateLayout();

                var where = $"at {width}px, LIVE {(live ? "on" : "off")}, chip \"{chip.Label}\"";
                // Left to right, which on this DockPanel is the REVERSE of the docking order: the
                // first child docked Right is the rightmost, so the priority list (buttons, dot,
                // chip, title) reads back as title, chip, dot, buttons on screen.
                var groups = new List<(string Name, FrameworkElement El)>
                {
                    ("title", overlay.TitleText),
                    ("chip", overlay.EngineChipText),
                    ("buttons", overlay.HeaderButtons),
                };
                if (live) groups.Insert(2, ("LIVE dot", overlay.LiveDot));

                // 1. The slots — the panel's own rectangles. Disjoint by construction, exactly.
                for (int i = 1; i < groups.Count; i++)
                {
                    var (leftName, left) = groups[i - 1];
                    var (rightName, right) = groups[i];
                    var a = LayoutInformation.GetLayoutSlot(left);
                    var b = LayoutInformation.GetLayoutSlot(right);
                    Assert.True(a.Right <= b.Left + 0.01,
                        $"{where}: the {leftName} slot ({a.Left:0.0}→{a.Right:0.0}) runs into the "
                        + $"{rightName} slot ({b.Left:0.0}→{b.Right:0.0})");
                }

                // 2. What is actually painted, in window coordinates. A label squeezed past its own
                // "…" still renders that — a trimmed TextBlock has no narrower form — so two
                // neighbours may bleed into each other by their two ellipses and by nothing else.
                // That is a two-character allowance derived from the real typefaces (19.0 px for
                // the chip against the LIVE dot), not a fudge: the overdraw this case exists for
                // was 40.0 px, and anything that is not a trimmed label is allowed nothing at all.
                var boxes = groups.Select(g => (g.Name, g.El, Box: PaintedX(g.El, root))).ToList();
                for (int i = 1; i < boxes.Count; i++)
                {
                    var (leftName, leftEl, a) = boxes[i - 1];
                    var (rightName, rightEl, b) = boxes[i];
                    double overlap = a.Right - b.Left;
                    double allowed = EllipsisWidth(leftEl) + EllipsisWidth(rightEl) + 0.01;
                    Assert.True(overlap <= allowed,
                        $"{where}: {leftName} ({a.Left:0.0}→{a.Right:0.0}) paints {overlap:0.0}px "
                        + $"over {rightName} ({b.Left:0.0}→{b.Right:0.0}); allowed {allowed:0.0}");
                }

                // 3. …and nothing is pushed off the window, which is how the column layout failed:
                // the buttons are the one group here the player has to be able to hit.
                var buttons = PaintedX(overlay.HeaderButtons, root);
                Assert.True(buttons.Right <= width - 8.5,
                    $"{where}: the buttons end at {buttons.Right:0.0} on a {width}px window");
                Assert.True(buttons.Right - buttons.Left >= 200,
                    $"{where}: the buttons were squeezed to {buttons.Right - buttons.Left:0.0}px — "
                    + "they are interactive and may not be the thing that gives way");
            }
        });
    }

    /// <summary>The painted box of one header element, in window coordinates.</summary>
    private static (double Left, double Right) PaintedX(FrameworkElement el, FrameworkElement root)
    {
        var x = el.TransformToAncestor(root).Transform(new Point(0, 0)).X;
        return (x, x + el.ActualWidth);
    }

    /// <summary>How wide a <c>…</c> is in this element's own typeface — the floor a trimmed
    /// <c>TextBlock</c> cannot render below, and therefore the whole of the bleed a crushed header
    /// label can have. Zero for anything that is not a label: the button group is never trimmed and
    /// is never allowed to overlap anything. Measured from the real control, so a font change moves
    /// the allowance rather than invalidating it.</summary>
    private static double EllipsisWidth(FrameworkElement element)
    {
        if (element is not TextBlock source) return 0;

        var probe = new TextBlock
        {
            Text = "…",
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// <summary>
    /// <b>The chip is right the moment the overlay opens</b>, not at the next repaint. The 1 Hz tick
    /// assigns nothing while the rendered string is unchanged (that is the guard above), so a window
    /// opened between two repaints would sit there blank for up to a minute. <c>EnterCompactMode</c>
    /// therefore pushes the state through the one composer after <c>Show()</c> — the same door
    /// <c>SetPaused</c> and <c>SetReadOnceCancelMode</c> already go through, and for the same reason.
    ///
    /// <para>Source-level for the door itself, because the alternative is <c>Show()</c>-ing a 360 px
    /// always-on-top window on a build agent (CI-3); what it renders is pinned by the STA cases
    /// above. The call is also one-way: <c>MainWindow</c> tells, the overlay does not ask (I2).</para>
    /// </summary>
    [Fact]
    public void The_chip_is_pushed_to_the_overlay_when_it_opens_and_is_never_asked_for()
    {
        var compact = File.ReadAllText(RepoFile("Views/MainWindow.Compact.cs"));
        int show = compact.IndexOf("_overlay.Show();", StringComparison.Ordinal);
        int push = compact.IndexOf("UpdateEngineChip();", StringComparison.Ordinal);
        Assert.True(show > 0, "EnterCompactMode no longer shows the overlay");
        Assert.True(push > show,
                    "the overlay is not handed the current chip after it is shown — its header "
                    + "would stay blank until the rendered string next changes");

        // One composer, one direction: PaintEngineChip hands the record it already built to an
        // overlay that may not exist yet, and CompactOverlay renders it without asking anything.
        var main = File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs"));
        Assert.Contains("_overlay?.SetEngineChip(chip, tooltip);", main, StringComparison.Ordinal);

        var overlay = File.ReadAllText(RepoFile("Views/CompactOverlay.xaml.cs"));
        Assert.Contains("MainWindow.PaintChipSurface(EngineChipText, chip, tooltip);", overlay,
                        StringComparison.Ordinal);
        Assert.DoesNotContain("ChipFor(", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("EngineStatus(", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The placements themselves</b> — the Translator tab shows the chip ONCE. It used to declare
    /// a second <c>TextBlock</c> beside the read-once button, and since one method paints every
    /// surface from one record the two were always identical: the same words, a hand apart on one
    /// tab. The second placement that earns its keep is the overlay's header, on the line with the
    /// title and the buttons — which is where a player mid-fight is looking.
    ///
    /// <para>The overlay's header is a <c>DockPanel</c>, and <b>declaration order there is the
    /// priority order</b> — what gives way first when 360 px is not enough (TP-RENDER-10 measures
    /// what that produces). Pinned as source because it is a decision, not an accident: buttons,
    /// then <c>● LIVE</c>, then the chip, then the title.</para>
    /// </summary>
    [Fact]
    public void The_translator_tab_declares_one_chip_and_the_overlay_header_declares_the_other()
    {
        var main = File.ReadAllText(RepoFile("Views/MainWindow.xaml"));
        Assert.Contains("x:Name=\"WriteChip\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadChip", main, StringComparison.Ordinal);

        var overlay = File.ReadAllText(RepoFile("Views/CompactOverlay.xaml"));
        int chip = overlay.IndexOf("x:Name=\"EngineChipText\"", StringComparison.Ordinal);
        int header = overlay.IndexOf("x:Name=\"HeaderBar\"", StringComparison.Ordinal);
        int buttons = overlay.IndexOf("x:Name=\"HeaderButtons\"", StringComparison.Ordinal);
        int dot = overlay.IndexOf("x:Name=\"LiveDot\"", StringComparison.Ordinal);
        int title = overlay.IndexOf("x:Name=\"TitleText\"", StringComparison.Ordinal);
        Assert.True(header > 0 && header < buttons && buttons < dot && dot < chip && chip < title,
                    "the header docks in priority order — buttons, LIVE dot, chip, title — and the "
                    + "chip sits inside it");
        Assert.Contains("<DockPanel Grid.Row=\"0\" x:Name=\"HeaderBar\"", overlay, StringComparison.Ordinal);

        var element = overlay[chip..overlay.IndexOf("/>", chip, StringComparison.Ordinal)];
        // Written from code like the main window's (RefreshEngineChip): no Text literal here and no
        // {Binding} either — a Run.Text binding would be TwoWay by default and would need a render
        // case of its own (I15). The only colour it names is a theme brush, for the moment before
        // the first paint.
        Assert.DoesNotContain("Text=\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding", element, StringComparison.Ordinal);
        Assert.Contains("{StaticResource ", element, StringComparison.Ordinal);
        Assert.Contains("Translation engine status", element, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>TP-RENDER-08 (E7.S7) — the About tab's "Translation engines" block, rendered.</b> One
    /// window, one layout pass, a loop over §2.1's states: the block is measured and arranged once
    /// per state and WPF may report no binding error at any point (CI-4 — the whole <c>WPF</c>
    /// collection stays under 15 s, so this renders the block ONCE and drives the states through
    /// the same tree rather than building eight windows).
    ///
    /// <para>What it pins beyond "it renders": the four read-only lines really carry text (a block
    /// whose composer was never called would render four empty <c>TextBlock</c>s and pass every
    /// assertion that only counted binding errors), the <c>In use now</c> row says exactly what the
    /// chip says, the Chain lines name no tier the app does not ship, and §4.2's static copy is on
    /// screen from the deck rather than from a XAML attribute.</para>
    /// </summary>
    [Fact]
    public void TP_RENDER_08_the_about_engines_block_renders_in_every_state()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var refresh = typeof(MainWindow).GetMethod("RefreshAboutEngineLines",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var errors = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            try
            {
                foreach (var status in EveryEngineStatus())
                {
                    refresh.Invoke(window, new object?[] { status });

                    foreach (var line in new[]
                             {
                                 window.EngineInUseText, window.EngineChainWriteText,
                                 window.EngineChainReadText, window.EnginesIntroText,
                                 window.CachePrivacyText, window.OfflineEngineText,
                                 // E7.S8's two rows: the pauses line under the intro, and the P1
                                 // expectation line beside "Check for updates".
                                 window.EnginesPausesText, window.FirstLaunchExpectationText,
                             })
                    {
                        line.Measure(new Size(600, 1000));
                        line.Arrange(new Rect(0, 0, 600, 1000));
                        line.UpdateLayout();
                        Assert.False(string.IsNullOrWhiteSpace(line.Text),
                                     "an About-block line rendered empty");
                    }

                    // The chip's fourth placement, agreeing with the other three word for word. The
                    // About tab is not the selected one here (LastTab defaults to 0), so the line
                    // deliberately carries no clock — §2.4's one countdown per window.
                    Assert.Equal(MainWindow.AboutChipFor(status, showTheClock: false).Label,
                                 window.EngineInUseText.Text);

                    foreach (var chain in new[] { window.EngineChainWriteText, window.EngineChainReadText })
                    {
                        Assert.DoesNotContain("Edge", chain.Text);        // ruling E3-d
                        Assert.DoesNotContain("Offline", chain.Text);     // E8 has not shipped
                    }
                }
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                // §4.2's specified copy is on screen, from the deck (GAP-4 / UX-DR19) — a XAML
                // attribute would put a second spelling of it one file away from the scan.
                Assert.Equal(UserMessages.AboutEnginesIntro(), window.EnginesIntroText.Text);
                Assert.Equal(UserMessages.AboutEnginesPauses(), window.EnginesPausesText.Text);
                Assert.Equal(UserMessages.FirstLaunchExpectation(),
                             window.FirstLaunchExpectationText.Text);
                Assert.Equal(UserMessages.AboutKeysIntro(), window.KeysIntroText.Text);
                Assert.Equal(UserMessages.AboutOfflineNotInstalled(), window.OfflineEngineText.Text);
                Assert.Equal(UserMessages.CachePrivacyLine(), window.CachePrivacyText.Text);
                Assert.Equal(UserMessages.ClearCacheLabel(), window.ClearCacheButton.Content);

                // The offline block is the PLACEHOLDER T6 recommends: E8.S3 owns the download, so
                // there is no button here to press. A visible one that did nothing would be worse.
                Assert.DoesNotContain("Download", window.OfflineEngineText.Text);
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            }

            Assert.True(errors.Messages.Count == 0,
                "WPF reported binding errors while rendering the About block:\n" + errors.Dump());
        });
    }

    /// <summary>The same states <see cref="EveryState"/> builds chips from, as the records
    /// themselves — the About block is drawn from an <c>EngineStatus</c> and not from a chip.</summary>
    private static IEnumerable<EngineStatus> EveryEngineStatus()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var free = new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx };
        var none = new Dictionary<string, GateSnapshot>(StringComparer.Ordinal);

        ChainTranslator.Outcome Answered(string id, params string[] skipped)
            => new(id, skipped.Select(s => (ProviderId: s, Reason: "Paused")).ToList(), null, null);

        yield return EngineStatus.Of(free, none, free, null, stateKnown: false, now);        // checking…
        yield return EngineStatus.Of(free, none, free, Answered(ProviderIds.GoogleDict), true, now);
        yield return EngineStatus.Of(free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = new(GateState.Open, now.AddSeconds(58), 1,
                                               TranslationErrorKind.RateLimited),
            },
            free, Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict), true, now);
        yield return EngineStatus.Of(free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = new(GateState.Open, now.AddSeconds(200), 1,
                                               TranslationErrorKind.Network),
                [ProviderIds.GoogleGtx] = new(GateState.Open, now.AddSeconds(260), 1,
                                              TranslationErrorKind.Network),
            },
            free, null, true, now);

        // S7 / S8 — the two a user's own key can be in, and the WIDEST rows the block can render:
        // the chip names a provider AND a state, and S8's fallback additionally puts §4.2's reason
        // clause beside it. TP-RENDER-09 measures against exactly this pair.
        var keys = new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx,
                           ProviderIds.DeepL, ProviderIds.Azure };
        yield return EngineStatus.Of(free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.DeepL] = new(GateState.Open, DateTimeOffset.MaxValue, 1,
                                          TranslationErrorKind.AuthFailed),
            },
            keys, Answered(ProviderIds.GoogleDict), true, now);
        yield return EngineStatus.Of(free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.Azure] = new(GateState.Open, now.AddSeconds(58), 1,
                                          TranslationErrorKind.QuotaExhausted),
            },
            keys, Answered(ProviderIds.GoogleDict, ProviderIds.Azure), true, now);
    }

    /// <summary>
    /// <b>TP-RENDER-09 (E7.S7 review) — the About tab laid out at the size the app really opens
    /// at.</b> E7.S7 shipped with the story's "check at 800×600" unverified, and E6.S5's off-screen
    /// Test button is the failure class it was pointing at: the About tab is one <c>StackPanel</c>
    /// in a <c>ScrollViewer</c> that scrolls <b>vertically only</b>, so a row wider than the
    /// viewport is not scrolled to — it is clipped, with no way to reach it at any window size.
    ///
    /// <para>Both sizes are READ from the window and never hardcoded (<c>Width="640" Height="720"</c>,
    /// <c>MinWidth="460" MinHeight="480"</c> in the XAML; <c>AppSettings.WindowWidth/Height</c> are
    /// null until a user drags the frame). The minimum is included because that is the width the
    /// user can actually drag to, and it is the one E6.S5's WrapPanels were introduced for.</para>
    ///
    /// <list type="number">
    /// <item><b>Nothing is clipped horizontally</b> — every laid-out element of the region ends
    ///       inside the viewport, in every one of §2.1's states. The states matter: the "In use
    ///       now" row is the widest thing on the tab and it exists only while a chip has something
    ///       to say, so one static pass would prove nothing.</item>
    /// <item><b>Every button is reachable</b> — the region really scrolls, and scrolling to each
    ///       button's own offset brings it fully inside the viewport. "Copy error report" is the
    ///       one the story flagged (a support path, now below two more blocks), so it is named
    ///       explicitly alongside the two Saves, the two Test keys and Clear cache.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void TP_RENDER_09_the_about_tab_fits_and_every_button_is_reachable_at_the_shipped_sizes()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;
            var scroller = (ScrollViewer)window.AboutTab.Content;
            var page = (FrameworkElement)scroller.Content;

            // The window's own sizes minus the chrome the client area never gets. Subtracting it
            // makes the test STRICTER than the real window, which is the safe direction to be
            // wrong in.
            Size Client(double width, double height) => new(
                width - 2 * SystemParameters.ResizeFrameVerticalBorderWidth,
                height - 2 * SystemParameters.ResizeFrameHorizontalBorderHeight
                       - SystemParameters.WindowCaptionHeight);

            // The About tab has to BE the selected one: a TabControl builds one content presenter,
            // and this is also the state in which the "In use now" line carries its clock (E7.S7's
            // countdown rule), i.e. its widest string.
            window.MainTabs.SelectedItem = window.AboutTab;

            var refresh = typeof(MainWindow).GetMethod("RefreshAboutEngineLines",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            foreach (var (what, client) in new[]
                     {
                         ("the default size", Client(window.Width, window.Height)),
                         ("MinWidth/MinHeight", Client(window.MinWidth, window.MinHeight)),
                     })
            {
                foreach (var status in EveryEngineStatus())
                {
                    refresh.Invoke(window, new object?[] { status });
                    root.Measure(client);
                    root.Arrange(new Rect(new Point(0, 0), client));
                    root.UpdateLayout();

                    Assert.True(scroller.ViewportWidth > 0, "the About tab never laid out");

                    foreach (var element in Descendants(page))
                    {
                        if (element.ActualWidth <= 0 || element.Visibility != Visibility.Visible) continue;
                        var right = element.TransformToAncestor(page).Transform(new Point(0, 0)).X
                                  + element.ActualWidth;
                        Assert.True(right <= scroller.ViewportWidth + 0.5,
                            $"at {what}, {Describe(element)} ends "
                            + $"{right - scroller.ViewportWidth:0.#} px past the "
                            + $"{scroller.ViewportWidth:0.#} px viewport. The About tab scrolls "
                            + "vertically only, so that content is CLIPPED and unreachable "
                            + "(E6.S5's failure class — use a WrapPanel, or let the text wrap).");
                    }
                }

                // ---- (2) every button is reachable ---------------------------------------------
                var buttons = Descendants(page).OfType<Button>()
                    .Where(b => b.Visibility == Visibility.Visible && b.ActualWidth > 0).ToList();

                // Non-vacuity, and the named ones: a walk that found no buttons would pass
                // everything below it.
                var labels = buttons.Select(b => b.Content as string ?? "").ToList();
                Assert.Contains("📋 Copy error report", labels);
                Assert.Equal(2, labels.Count(l => l == "Save"));
                Assert.Contains(window.DeepLTestButton, buttons);
                Assert.Contains(window.AzureTestButton, buttons);
                Assert.Contains(window.ClearCacheButton, buttons);
                // E8.S3's Download/Remove button. Named for the same reason the others are: this
                // region grew a WrapPanel and a row whose longest sentence is AC 3's failure line,
                // and E6.S5's clipped Test button is the failure class both are shaped against. Its
                // Cancel twin is Collapsed at rest and is correctly not in this list.
                Assert.Contains(window.OfflineEngineButton, buttons);

                Assert.True(scroller.ScrollableHeight > 0,
                    $"the About region does not scroll at {what}, so anything below the fold would "
                    + "be unreachable");

                foreach (var button in buttons)
                {
                    var top = button.TransformToAncestor(page).Transform(new Point(0, 0)).Y;
                    scroller.ScrollToVerticalOffset(Math.Min(top, scroller.ScrollableHeight));
                    root.UpdateLayout();

                    var y = button.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
                    Assert.True(y >= -0.5 && y + button.ActualHeight <= scroller.ViewportHeight + 0.5,
                        $"at {what}, {Describe(button)} cannot be scrolled into view "
                        + $"(it lands at y={y:0.#} in a {scroller.ViewportHeight:0.#} px viewport)");
                }

                scroller.ScrollToTop();
                root.UpdateLayout();
            }
        });
    }

    /// <summary>Every <see cref="FrameworkElement"/> under a visual root, depth-first.</summary>
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Enough of an element to find it in the XAML from a failure message.</summary>
    private static string Describe(FrameworkElement element)
    {
        var what = string.IsNullOrEmpty(element.Name)
            ? element.GetType().Name : element.Name + " (" + element.GetType().Name + ")";
        var text = element switch
        {
            TextBlock block => block.Text.Length > 0 ? block.Text
                               : string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)),
            ContentControl { Content: string content } => content,
            _ => "",
        };
        return text.Length == 0 ? what
             : what + " \"" + (text.Length <= 48 ? text : text[..48] + "…") + "\"";
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

        // S1 again, with the BACKUP tier answering on its own — healthy, because nothing was
        // skipped: google-dict's window expired, the chain tried it, it answered nothing this time
        // and google-gtx served. §3.0 rule 2 then drops the "· backup" suffix from a name that
        // already carries it, so this state's LABEL is S2's to the byte and its BRUSH is not.
        // It is appended, not inserted: cases in this file index S1, S2 and S6 by position.
        yield return MainWindow.ChipFor(Of(none, free, Answered(ProviderIds.GoogleGtx)));      // S1′
    }

    // Render one DataTemplate against a real item via a ContentControl (which applies its template
    // synchronously during Measure — unlike an ItemsControl, whose container generation is deferred
    // to the dispatcher and would leave the tree empty headless, a silent pass) and fail on any
    // WPF binding error OR if the bound text never reached the visual tree.
    private static void AssertRendersCleanly(DataTemplate template, OcrResultItem item)
    {
        var texts = RenderAndCollectText(template, item);
        Assert.Contains(texts, t => t.Contains("hello world"));
        Assert.Contains(texts, t => t.Contains("привет мир"));
        Assert.Contains(texts, t => t.Contains("Игрок:"));   // grey speaker prefix rendered
    }

    /// <summary>
    /// The render half of the helper above, reusable by the row-state cases: apply the real
    /// template to a real item through a <c>ContentControl</c>, fail on ANY WPF binding error, and
    /// hand back the text that actually reached the visual tree.
    ///
    /// <para>Renders into a fresh host every call on purpose — a row state is a different item, not
    /// a mutated one, and reusing a host would let a stale tree answer for a template that never
    /// re-applied. (The one case that DOES mutate an item in place asks for that explicitly.)</para>
    /// </summary>
    private static List<string> RenderAndCollectText(DataTemplate template, OcrResultItem item)
    {
        var host = new ContentControl { ContentTemplate = template, Content = item };
        var errors = Render(host);

        Assert.True(errors.Messages.Count == 0,
            "WPF reported binding errors while rendering the item:\n" + errors.Dump());

        return CollectTextBlockText(host);
    }

    /// <summary>One layout pass with the data-binding trace source captured for its duration.</summary>
    private static BindingErrorListener Render(ContentControl host)
    {
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
        return errors;
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

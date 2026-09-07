using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E7.S2 — <b>the countdown is quiet.</b> Three things are pinned here, and they are the three ways
/// a status line can cost a player frames:
///
/// <list type="number">
/// <item><b>The bands</b> (§2.4 / amendment A9, TP-RENDER-07): <c>about to retry</c> under five
///       seconds, <c>m:ss</c> to ninety, <c>about N min</c> above it, <c>about 30 min</c> at the
///       display cap — culture-invariant, because the <c>:</c> of a locale-aware format is a real
///       defect on a Russian-language Windows (the reason E2.S6's gate log is invariant).</item>
/// <item><b>The repaint guard</b> (AC 3, hint 7): a descending second is usually the SAME string,
///       and the same string is not an assignment. Above ninety seconds that is one write a
///       minute.</item>
/// <item><b>The timer's lifetime</b> (AC 1): not running on a healthy app, started when a pause
///       begins, and <b>stopped by its own tick</b> the moment nothing is paused.</item>
/// </list>
///
/// <para><b>Nothing here sleeps</b> (CI-3). The tick handler is driven directly with a
/// <see cref="ChainPause"/> this file builds, so a countdown test never waits for a real second —
/// which would make it the first flaky test in the suite — and never touches the process-global
/// gates, so it needs no <c>Gates</c> collection either. The instants are this file's own
/// (IS-6: an injected clock, never <c>DateTimeOffset.UtcNow</c>).</para>
/// </summary>
[Collection("WPF")]
public class CountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A pause that has <paramref name="seconds"/> left on the gates' own clock — the pair
    /// <c>ChainTranslator.PauseNow()</c> hands the UI, built here so the tick can be driven without
    /// a gate, a registry or a wait.</summary>
    private static ChainPause Paused(int seconds)
        => new(true, Now + TimeSpan.FromSeconds(seconds), Now);

    private static ChainPause Healthy() => new(false, null, Now);

    // ---- TP-RENDER-07: the bands ----------------------------------------------------------------

    /// <summary>
    /// One case per band plus every boundary a band rule is actually written down on. The
    /// boundaries are the test: a <c>&lt;</c> that should have been a <c>&lt;=</c> moves exactly one
    /// of these and nothing else.
    /// </summary>
    [Theory]
    // Nothing honest to count down to → nothing invented (the contract E5.S4 extracted).
    [InlineData(null, null)]
    // §2.4's floor: under five seconds a countdown stops counting and says what happens next.
    // Zero and a negative land here too — a window that has just elapsed is "about to retry", never
    // "0:00" and never "-0:01".
    [InlineData(-1, "about to retry")]
    [InlineData(0, "about to retry")]
    [InlineData(4, "about to retry")]
    // …and five seconds is the first one that counts.
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(61, "1:01")]
    [InlineData(89, "1:29")]
    // 90 is the last second of the m:ss band, inclusive — "≤ 90 s → m:ss".
    [InlineData(90, "1:30")]
    // …and 91 is the first of the minutes band, rounded UP, so the number never promises the gate
    // will reopen sooner than it will.
    [InlineData(91, "about 2 min")]
    [InlineData(120, "about 2 min")]
    [InlineData(121, "about 3 min")]
    // The longest window the breaker can open for, exactly on the display cap.
    [InlineData(1800, "about 30 min")]
    // Past it: the cap holds rather than counting on, and it does so without overflowing on the
    // ceiling arithmetic — int.MaxValue is what a caller that skipped CountdownSeconds would hand it.
    [InlineData(1801, "about 30 min")]
    [InlineData(3599, "about 30 min")]
    // 3600 is LiveTickPolicy.MaxCountdownSeconds — the LAST second CountdownSeconds still gives a
    // number for, and therefore the largest value production can actually hand this. 3601 is the
    // first it cannot (the fact below pins that guard); both are here because the story's Testing
    // section names them, and because the display cap must not develop an opinion at either.
    [InlineData(3600, "about 30 min")]
    [InlineData(3601, "about 30 min")]
    [InlineData(int.MaxValue, "about 30 min")]
    public void TP_RENDER_07_the_four_bands(int? seconds, string? expected)
        => Assert.Equal(expected, MainWindow.CountdownText(seconds));

    /// <summary>
    /// The <c>MaxValue</c> sentinel a gate stores for <c>AuthFailed</c> never reaches the formatter
    /// as a number: <c>LiveTickPolicy.CountdownSeconds</c> answers <b>null</b> beyond an hour, and
    /// that guard is a DIFFERENT rule from §2.4's 30-minute display cap. Both stay — the story says
    /// so in as many words, and this is the pin that stops the next reader collapsing them.
    /// </summary>
    [Fact]
    public void The_honesty_guard_and_the_display_cap_are_two_different_rules()
    {
        // The honesty guard: an hour out, there is nothing to count down to at all.
        Assert.Null(LiveTickPolicy.CountdownSeconds(Now + TimeSpan.FromHours(2), Now));
        Assert.Null(MainWindow.CountdownText(LiveTickPolicy.CountdownSeconds(DateTimeOffset.MaxValue, Now)));

        // The display cap: a real 45-minute wait would still be shown, and shown as "about 30 min".
        // Nothing in the app produces one today (the honesty guard answers null first), which is
        // exactly why the cap needs its own case rather than being read off a caller.
        Assert.Equal("about 30 min", MainWindow.CountdownText(45 * 60));
        Assert.Equal(3600, LiveTickPolicy.MaxCountdownSeconds);
    }

    /// <summary>
    /// I11 / §10.2, the same pin <c>GateLog</c> earned in E2.S6's review: the <c>:</c> of a custom
    /// time format is the culture's <c>TimeSeparator</c>, so a machine whose locale spells a time
    /// differently would render a countdown the player reads as something else. The culture here is
    /// deliberately hostile — one that happened to agree with the invariant one would make this test
    /// pass while proving nothing.
    ///
    /// <para><b>What it does NOT prove</b> (review): .NET ignores <c>NativeDigits</c> when
    /// formatting an integer, so no culture can substitute the digits and this test cannot fail on
    /// that. What it pins is the SHAPE — a literal <c>:</c> rather than a time format, and an
    /// explicit provider on both halves — which is what stops the next edit reaching for
    /// <c>TimeSpan.ToString</c> or a bare <c>{s:00}</c> under a locale-aware provider.</para>
    /// </summary>
    [Fact]
    public void The_bands_render_the_same_digits_on_a_hostile_locale()
    {
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.DateTimeFormat.TimeSeparator = "#";
        hostile.NumberFormat.NegativeSign = "MINUS";
        hostile.NumberFormat.NumberDecimalSeparator = "#";

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = hostile;

            Assert.Equal("0:58", MainWindow.CountdownText(58));
            Assert.Equal("1:05", MainWindow.CountdownText(65));
            Assert.Equal("about 4 min", MainWindow.CountdownText(200));
            Assert.Matches(@"^\d:\d\d$", MainWindow.CountdownText(58)!);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ---- AC 3 / hint 7: the repaint guard --------------------------------------------------------

    /// <summary>
    /// <b>TP-RENDER-07's second half, and it needs no timer and no window.</b> Above ninety seconds
    /// the band only changes on a minute boundary, so a full minute of ticks is ONE distinct string:
    /// 300 → 240 yields one change, not sixty. That is the whole of hint 7 — the guard below turns
    /// "distinct string" into "assignment", and the arithmetic here is what makes there be only one.
    /// </summary>
    [Fact]
    public void A_minute_above_the_clock_band_is_one_string_and_a_minute_below_it_is_sixty()
    {
        var aboveTheBand = Enumerable.Range(0, 61).Select(i => MainWindow.CountdownText(300 - i)).ToList();
        Assert.Equal(new[] { "about 5 min", "about 4 min" }, aboveTheBand.Distinct());

        // …and inside the clock band every second really is its own string, because that is the
        // band's whole point: a stopwatch that does not move is not a stopwatch.
        var insideTheBand = Enumerable.Range(0, 30).Select(i => MainWindow.CountdownText(90 - i)).ToList();
        Assert.Equal(30, insideTheBand.Distinct().Count());
    }

    /// <summary>
    /// The guard itself, on a real <c>TextBlock</c>. It is asserted through
    /// <c>ReadLocalValue</c> rather than by counting change notifications, and that is the only way
    /// it CAN be asserted: WPF drops a dependency-property change whose value is equal, so a bare
    /// <c>Text = same</c> raises nothing either and the two implementations are indistinguishable
    /// from the outside. A local value, though, is set by an assignment and by nothing else — so a
    /// <c>TextBlock</c> that still reports <c>UnsetValue</c> is a <c>TextBlock</c> that was not
    /// written to.
    /// </summary>
    [Fact]
    public void The_repaint_guard_does_not_write_a_string_the_surface_already_shows()
    {
        StaTestHost.Run(() =>
        {
            // Text reads "" (the property's default) and has no local value yet.
            var untouched = new TextBlock();
            Assert.Equal("", untouched.Text);
            MainWindow.SetIfChanged(untouched, "");
            Assert.Equal(DependencyProperty.UnsetValue, untouched.ReadLocalValue(TextBlock.TextProperty));

            // …and a string it does NOT already show is written, once.
            var written = new TextBlock();
            MainWindow.SetIfChanged(written, "0:58");
            Assert.Equal("0:58", written.ReadLocalValue(TextBlock.TextProperty));
            Assert.Equal("0:58", written.Text);
        });
    }

    /// <summary>
    /// <b>The other half of the guard, on the other window</b> (review). The countdown writes TWO
    /// surfaces a second and only one of them was pinned: <c>CompactOverlay.SetStatusIfChanged</c>
    /// could have been a plain forward to <c>SetStatus</c> and the whole suite would have stayed
    /// green, which is exactly the shape of an untested guard.
    ///
    /// <para>It is asserted through the visibility flip rather than through <c>ReadLocalValue</c>,
    /// because on this window the guard is not only about the text: <c>SetStatus</c> also
    /// recomputes <c>OverlayStatus.Visibility</c> and refreshes the empty hint on EVERY call, and
    /// skipping that work is most of what the guard buys here. Nothing else touches that visibility,
    /// so collapsing it by hand makes a tripwire only a real <c>SetStatus</c> call can trip.</para>
    /// </summary>
    [Fact]
    public void The_overlays_half_of_the_guard_does_not_rewrite_the_line_it_already_shows()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());

            overlay.SetStatus("○ Live paused — back in 0:58");
            Assert.Equal(Visibility.Visible, overlay.OverlayStatus.Visibility);

            // The tripwire: only SetStatus writes this back to Visible.
            overlay.OverlayStatus.Visibility = Visibility.Collapsed;

            // The same second, re-rendered: above 90 s this is fifty-nine of every sixty ticks.
            overlay.SetStatusIfChanged("○ Live paused — back in 0:58");
            Assert.Equal(Visibility.Collapsed, overlay.OverlayStatus.Visibility);
            Assert.Equal("○ Live paused — back in 0:58", overlay.OverlayStatus.Text);

            // …and a string the line does NOT already show goes through, once, in full.
            overlay.SetStatusIfChanged("○ Live paused — back in 0:57");
            Assert.Equal(Visibility.Visible, overlay.OverlayStatus.Visibility);
            Assert.Equal("○ Live paused — back in 0:57", overlay.OverlayStatus.Text);
        });
    }

    // ---- AC 1: the timer's lifetime --------------------------------------------------------------

    /// <summary>
    /// <b>AC 1, end to end, without a single wait.</b> The timer is not running on a healthy app, it
    /// starts when a pause is signalled, it keeps running while the pause lasts, and its own tick
    /// stops it the moment nothing is paused.
    ///
    /// <para>The first assertion is also <b>I10</b>: a freshly constructed window has started
    /// nothing, so no timer is running before the first paint. Today nothing could start one either,
    /// because nothing issues a request before it — but that is a consequence, not the rule, and
    /// <b>E7.S3</b> will legitimately warm the gate state from <c>OnWindowLoaded</c> (ruling E6-a,
    /// Services-routed, pool thread, AFTER first paint). What this line pins is the invariant that
    /// survives that: <b>constructing the window starts no countdown.</b></para>
    /// </summary>
    [Fact]
    public void The_timer_runs_only_while_something_is_paused()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.False(window.CountdownRunning, "a healthy app must not be running a countdown");

            window.EnsureCountdownRunning(Paused(120));
            Assert.True(window.CountdownRunning, "a pause starts it (AC 1)");

            // A second pause site saying the same thing is a no-op, not a second timer: there is one
            // countdown for the whole app and every site may just ask for it.
            window.EnsureCountdownRunning(Paused(119));
            Assert.True(window.CountdownRunning);

            window.CountdownTick(Paused(118));
            Assert.True(window.CountdownRunning, "still paused, still ticking");

            // The window elapses. The tick is what stops it, and the question it asks is
            // PauseNow()'s BlockedUntil > Now() (ruling E3-a) — a State == Open test would keep it
            // running for ever, because that state deliberately outlives its window.
            window.CountdownTick(Healthy());
            Assert.False(window.CountdownRunning, "nothing paused ⇒ the tick stops itself (AC 1)");

            // …and it can be started again by the next pause, so stopping is not a one-way door.
            window.EnsureCountdownRunning(Paused(30));
            Assert.True(window.CountdownRunning);
            window.CountdownTick(Healthy());
            Assert.False(window.CountdownRunning);
        });
    }

    /// <summary>
    /// The timer must not survive the window: a <c>DispatcherTimer</c> roots its handler, and the
    /// handler here closes over the window it would keep alive. <c>OnClosing</c> stops it beside
    /// <c>CancelReadOnce()</c>, and <c>StopLive</c> stops it too, because a countdown with no
    /// surface left to paint is exactly the "never runs idle" NFR7 forbids.
    ///
    /// <para><b>Both halves are bounded to the method they are about</b> (review): a
    /// <c>Contains</c> over everything AFTER a signature passes on a <c>StopCountdown()</c> that
    /// landed in some later method entirely, which is the failure mode a source scan exists to
    /// catch. And in <c>StopLive</c> the ORDER is the assertion: the countdown goes off BEFORE
    /// "Live stopped." is written, or the next tick repaints a promise over the sentence that just
    /// said nothing is coming (the R-02 zombie shape).</para>
    /// </summary>
    [Fact]
    public void The_timer_is_stopped_by_the_window_and_by_stopping_live()
    {
        var main = Code(File.ReadAllText(RepoFile("MainWindow.xaml.cs")));
        var live = Code(File.ReadAllText(RepoFile("MainWindow.Live.cs")));

        // OnClosing's body is not brace-matchable (string literals with braces further down), so it
        // is bounded by two calls that unambiguously bracket this one inside it. Both are unique in
        // the file, and NEITHER is the registry flush that also sits in there: writing the literal
        // "ProviderGates." in this file's code — even inside a scan's needle — makes
        // ProviderGatesTests' own guard read this class as a registry toucher and demand it join the
        // "Gates" collection, which it cannot (it is already in "WPF") and does not need to.
        int cancelKeyTests = main.IndexOf("CancelKeyTests();", StringComparison.Ordinal);
        int flushCache = main.IndexOf("TranslationChains.FlushCache();", StringComparison.Ordinal);
        Assert.True(cancelKeyTests >= 0 && flushCache > cancelKeyTests,
                    "OnClosing's shutdown sequence is not where this scan expects it");
        Assert.Contains("StopCountdown();", main[cancelKeyTests..flushCache], StringComparison.Ordinal);

        var stopLive = BracedBlock(live, live.IndexOf("private void StopLive()", StringComparison.Ordinal));
        int off = stopLive.IndexOf("StopCountdown();", StringComparison.Ordinal);
        int stopped = stopLive.IndexOf("SetScreenStatus(\"Live stopped.\")", StringComparison.Ordinal);
        Assert.True(off >= 0, "StopLive must still be where the countdown is turned off");
        Assert.True(stopped > off,
                    "StopLive must stop the countdown BEFORE it writes \"Live stopped.\"");
    }

    /// <summary>
    /// <b>The guard on the other side of the tick</b> (review, and the story's deviation 3). The
    /// status line the countdown paints is not always LIVE's: read-once writes its own past-tense
    /// summary there, and <c>StopLive</c> writes "Live stopped." on it. A countdown stepping over
    /// either would be a second clock on one window (AC 4) and would overwrite a sentence about
    /// something else — so the tick repaints only while <c>_liveCts</c> is set.
    ///
    /// <para>Nothing pinned that, and it is one deleted line away from being wrong. The window here
    /// has no LIVE loop, so every tick it is given must leave the line exactly as it found it.</para>
    /// </summary>
    [Fact]
    public void The_tick_never_repaints_a_status_line_live_does_not_own()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();          // no loop: _liveCts is null
            window.ScreenReadStatus.Text = "Live stopped.";

            window.EnsureCountdownRunning(Paused(120));
            Assert.Equal("Live stopped.", window.ScreenReadStatus.Text);

            window.CountdownTick(Paused(119));
            Assert.Equal("Live stopped.", window.ScreenReadStatus.Text);

            // …and read-once's own paused summary, which lives on the very same TextBlock.
            window.ScreenReadStatus.Text = "Read 3 line(s) — all engines are paused. Try again in 0:30.";
            window.CountdownTick(Paused(30));
            Assert.Equal("Read 3 line(s) — all engines are paused. Try again in 0:30.",
                         window.ScreenReadStatus.Text);

            // The tick still answers AC 1 while it is declining to paint: nothing paused stops it.
            window.CountdownTick(Healthy());
            Assert.False(window.CountdownRunning);
        });
    }

    // ---- §3.2: the two paused lines the countdown feeds ------------------------------------------

    /// <summary>
    /// §3.2's paused rows, main window column. Three forms and not one with a hole in it: the
    /// under-five-seconds row exists because §2.4's floor renders a <b>clause</b> and not a
    /// duration — "next try in about to retry" is not a sentence, so the deck replaces the whole
    /// line instead of substituting into it.
    /// </summary>
    [Fact]
    public void The_main_windows_paused_line_is_the_decks_three_rows()
    {
        Assert.Equal("○ Live — paused, next try in 0:58. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(58));
        Assert.Equal("○ Live — paused, next try in about 4 min. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(200));
        Assert.Equal("○ Live — paused, about to retry.", MainWindow.LivePausedStatus(4));
        Assert.Equal("○ Live — paused. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(null));
    }

    /// <summary>
    /// §3.2's overlay column, and the budget it is written to: the compact window has ONE status
    /// line, so the long form would wrap the whole window. Forty characters is the deck's number.
    /// </summary>
    [Fact]
    public void The_overlays_paused_line_is_the_short_form_and_fits_in_forty_characters()
    {
        Assert.Equal("○ Live paused — back in 0:58", MainWindow.LivePausedOverlayStatus(58));
        Assert.Equal("○ Live paused — back in about 4 min", MainWindow.LivePausedOverlayStatus(200));
        Assert.Equal("○ Live paused — about to retry", MainWindow.LivePausedOverlayStatus(4));
        Assert.Equal("○ Live paused — it resumes on its own", MainWindow.LivePausedOverlayStatus(null));

        foreach (int? s in new int?[] { null, 0, 58, 200, 1800, int.MaxValue })
            Assert.True(MainWindow.LivePausedOverlayStatus(s).Length <= 40,
                        $"the overlay budget is 40 characters — {MainWindow.LivePausedOverlayStatus(s)}");
    }

    /// <summary>
    /// The <c>{t}</c> a sentence JOINS with "in {t}" is not always the one a surface SHOWS on its
    /// own, and that is grammar rather than policy. Under §2.4's floor there is no duration left to
    /// join, so the join answers null and amendment A12's substitution ("in {t}" → "shortly") does
    /// the talking — rather than "Try again in about to retry.", which is not English.
    /// </summary>
    [Fact]
    public void A_sentence_that_joins_in_t_gets_no_number_under_the_floor()
    {
        Assert.Equal("0:30", MainWindow.CountdownJoinText(30));
        Assert.Equal("about 4 min", MainWindow.CountdownJoinText(200));
        Assert.Null(MainWindow.CountdownJoinText(4));
        Assert.Null(MainWindow.CountdownJoinText(null));

        // The two sentences that take it, at the floor: each already has an honest form for "no
        // number", and this is what selects it. Neither sentence is changed by this story.
        Assert.Equal("Read 3 line(s) — all engines are paused. Try again shortly.",
                     UserMessages.ReadOncePaused(3, MainWindow.CountdownJoinText(4)));
        Assert.Equal("⚠ Not checked — Azure is paused right now.",
                     UserMessages.KeyTestSentence(ProviderIds.Azure,
                         KeyTestResult.PausedFor(TranslationErrorKind.RateLimited, 4), "westeurope",
                         MainWindow.CountdownJoinText(4)));
    }

    // ---- AC 4 / TP-RENDER-06: one countdown per window, and never on a row -----------------------

    /// <summary>
    /// <b>TP-RENDER-06, the source half</b> (E7.S6 lands the render half). Principle 5 and hint 2:
    /// no feed row may ever carry a countdown. The three files that write a row are scanned for a
    /// line that does both — the countdown lives on a status line, and a status line is the one
    /// surface there is exactly one of per window.
    /// </summary>
    [Fact]
    public void TP_RENDER_06_no_row_ever_carries_a_countdown()
    {
        foreach (var file in new[] { "MainWindow.Live.cs", "MainWindow.Ocr.cs", "MainWindow.xaml.cs" })
        {
            var code = Code(File.ReadAllText(RepoFile(file)));
            foreach (var line in code.Split('\n').Where(l => l.Contains("TranslationBody")))
                Assert.False(line.Contains("Countdown") || line.Contains("PausedStatus"),
                             $"{file} writes a countdown onto a feed row: {line.Trim()}");
        }
    }

    /// <summary>
    /// <b>AC 4, at the tick.</b> The repaint writes exactly two surfaces — the main window's status
    /// line and the overlay's single status line — which is one clock per window, and it writes
    /// both through the guard. It names no row collection at all.
    /// </summary>
    [Fact]
    public void The_tick_paints_one_status_line_per_window_and_no_row()
    {
        var main = Code(File.ReadAllText(RepoFile("MainWindow.xaml.cs")));
        var body = BracedBlock(main, main.IndexOf("internal void CountdownTick(ChainPause pause)",
                                                 StringComparison.Ordinal));

        Assert.Contains("SetIfChanged(ScreenReadStatus, LivePausedStatus(", body, StringComparison.Ordinal);
        Assert.Contains("SetStatusIfChanged(LivePausedOverlayStatus(", body, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "_ocrItems", "OcrResults", "TranslationBody", "DateTimeOffset.UtcNow" })
            Assert.False(body.Contains(forbidden, StringComparison.Ordinal),
                         $"the countdown tick must not name {forbidden}");

        // One assignment per surface: the guard is the only way either is written from here.
        Assert.Equal(1, Occurrences(body, "SetIfChanged(ScreenReadStatus"));
        Assert.Equal(1, Occurrences(body, "SetStatusIfChanged("));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string BracedBlock(string source, int from)
    {
        Assert.True(from >= 0, "the method this scan is about was not found");
        int open = source.IndexOf('{', from);
        Assert.True(open >= 0, "no block after the signature");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail("the block is not closed");
        return "";
    }

    /// <summary>Code only — a <c>//</c> mention is prose, and these blocks are heavily commented
    /// about exactly the things they must not do.</summary>
    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

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
}

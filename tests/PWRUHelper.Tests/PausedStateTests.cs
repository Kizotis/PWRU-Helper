using System.Collections.Generic;
using System.IO;
using System.Linq;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E7.S4 — <b>a blinking dot over a stopped pipe is a lie, and this is where it stops.</b> Risk
/// R-02 is the owner's original complaint wearing its technical name ("the heartbeat blinks, the
/// loop is paused or dead, the user waits forever"), and E5.S1 closed exactly half of it: the main
/// window stopped ALTERNATING because <c>_liveTicks</c> is not advanced, while the overlay's own
/// 600 ms <c>DispatcherTimer</c> kept blinking because it knew nothing about the pause.
///
/// <para>Four things are pinned here:</para>
/// <list type="number">
/// <item><b>TP-LIVE-17</b> — the heartbeat is frozen on BOTH surfaces across N ticks, and blinks
///       again on resume. The main window takes AC 1's fuller <c>○  LIVE (paused)</c> form; the
///       overlay freezes on <c>○</c> and <b>stops its timer</b> (AC 2 — the degraded state costs
///       <i>less</i> CPU than the healthy one, UX-DR18).</item>
/// <item><b>§2.1's S5 and S6 on both surfaces</b>, from ONE decision — <c>ChainPause</c>, the same
///       answer the LIVE loop skips its tick on — with the overlay's forty-character budget.</item>
/// <item><b>The toast/status arbitration</b> E7.S2's review left open: on a 360 px window the toast
///       and the 1 Hz countdown write the same <c>TextBlock</c>, and the toast now owns it for its
///       lifetime, after which the state comes back.</item>
/// <item><b>§3.5's one-time notices</b> — <c>Back on {P}.</c> and, as amendment A4's settlement of
///       deviation D2, <c>Translated by {P} — {P2} is paused.</c> from a non-empty
///       <c>LastOutcome.Skipped</c>.</item>
/// </list>
///
/// <para><b>Nothing sleeps</b> (CI-3): the 600 ms heartbeat is asserted through
/// <c>BeatRunning</c> and its pure rule, never by waiting for a glyph — a test that waits 600 ms is
/// the exact test this suite must not grow. The gates are LOCAL (<c>new ProviderGate(clock)</c>),
/// so nothing joins the registry and nothing reads <c>provider-state.json</c>; the windows use
/// <c>TempSettings</c>, so no test writes the developer's own <c>%AppData%</c>.</para>
/// </summary>
[Collection("Gates")]
public class PausedStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static ChainPause Paused(int seconds, bool noNetwork = false)
        => new(true, Now + TimeSpan.FromSeconds(seconds), Now, noNetwork);

    private static ChainPause Healthy() => new(false, null, Now);

    private const string NoSettings = """{ "SettingsVersion": 3 }""";

    // =============================================================================================
    //  TP-LIVE-17 — the heartbeat is frozen while paused, on both surfaces
    // =============================================================================================

    /// <summary>
    /// <b>The main window's half, across ten ticks.</b> AC 1 asks for a different string and not
    /// merely a frozen one: <c>●  LIVE</c> and <c>○  LIVE</c> are the two halves of a blink, so
    /// stopping on either still reads as "a heartbeat that happens to be between beats". The word
    /// is what carries the state with the palette stripped out (NFR11).
    ///
    /// <para>The tick is driven directly with a <c>ChainPause</c> this file builds (CI-3), which is
    /// also the assertion that the tick itself cannot un-freeze it: a repaint that ran before the
    /// stop rule and wrote the running form is precisely how a partially frozen heartbeat gets
    /// shipped.</para>
    /// </summary>
    [Fact]
    public void TP_LIVE_17_the_main_windows_heartbeat_freezes_and_comes_back()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            window.CountdownTick(Paused(58), chipNeedsIt: false);
            Assert.Equal("○  LIVE (paused)", window.LiveIndicator.Text);
            Assert.Equal(MainWindow.LiveIndicatorPaused, window.LiveIndicator.Text);

            // N ticks, the countdown stepping inside the status line all the while: the indicator
            // does not alternate, because nothing is being sent to alternate about.
            for (int s = 57; s > 47; s--)
            {
                window.CountdownTick(Paused(s), chipNeedsIt: false);
                Assert.Equal(MainWindow.LiveIndicatorPaused, window.LiveIndicator.Text);
            }

            // …and the gate reopens. The tick that finds nothing paused is the tick that un-freezes,
            // which is why the write is above the stop rule and not below it.
            window.CountdownTick(Healthy(), chipNeedsIt: false);
            Assert.Equal(MainWindow.LiveIndicatorRunning, window.LiveIndicator.Text);
            Assert.False(window.CountdownRunning, "nothing paused ⇒ the tick still stops itself");
        });
    }

    /// <summary>
    /// <b>AC 4 in its cheapest form</b>, and the same shape as E7.S2's repaint guard: at 1 Hz the
    /// pause answers the same thing sixty times a minute, and the transition guard means the
    /// indicator is written ONCE per state. The tripwire is what makes "assigned" observable —
    /// only <c>SetLivePaused</c> writes this <c>TextBlock</c> from here.
    /// </summary>
    [Fact]
    public void The_paused_indicator_is_written_once_per_transition_and_not_once_a_second()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            window.SetLivePaused(true);

            window.LiveIndicator.Text = "tripwire";
            for (int i = 0; i < 10; i++) window.SetLivePaused(true);
            Assert.Equal("tripwire", window.LiveIndicator.Text);

            // …and a real transition still writes.
            window.SetLivePaused(false);
            Assert.Equal(MainWindow.LiveIndicatorRunning, window.LiveIndicator.Text);
        });
    }

    /// <summary>
    /// <b>The overlay's half — the one E5.S1 explicitly left to this story.</b> The dot freezes on
    /// <c>○</c> and stays there however the indicator is refreshed: <c>UpdateLiveIndicator</c> has
    /// four entry points (the 600 ms tick, <c>IsVisibleChanged</c>, <c>SetPaused</c> and the ▶/■
    /// button), so stopping the timer alone would leave three ways to re-blink a paused dot.
    /// </summary>
    [Fact]
    public void TP_LIVE_17_the_overlays_dot_freezes_on_the_quiet_glyph()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());

            overlay.SetPaused(true);
            Assert.Equal(CompactOverlay.DotOff, overlay.LiveDot.Text);

            // Ten heartbeats' worth of refreshes through the method the timer calls.
            for (int i = 0; i < 10; i++)
            {
                overlay.UpdateLiveIndicator();
                Assert.Equal(CompactOverlay.DotOff, overlay.LiveDot.Text);
            }

            // AC 4 again: a second SetPaused(true) writes nothing at all.
            overlay.LiveDot.Text = "tripwire";
            overlay.SetPaused(true);
            Assert.Equal("tripwire", overlay.LiveDot.Text);
        });
    }

    /// <summary>
    /// The blink itself, as the pure alternation the frozen state replaces: with the path paused the
    /// glyph is the same on both parities, and with it running the two parities differ. This is what
    /// "does not alternate across N ticks" MEANS, and it is asserted without a running LIVE loop.
    /// </summary>
    [Theory]
    [InlineData(true, true, CompactOverlay.DotOff)]
    [InlineData(true, false, CompactOverlay.DotOff)]
    [InlineData(false, true, CompactOverlay.DotOn)]
    [InlineData(false, false, CompactOverlay.DotOff)]
    public void The_dot_alternates_only_while_requests_are_flowing(bool paused, bool blink, string expected)
        => Assert.Equal(expected, CompactOverlay.Heartbeat(paused, blink));

    // =============================================================================================
    //  AC 2 — the 600 ms timer STOPS, and no second call site restarts it
    // =============================================================================================

    /// <summary>
    /// <b>AC 2 is about the timer, not the text</b> (UX-DR18: "the degraded state costs <i>less</i>
    /// CPU than the healthy one"). Asserted through the flag rather than by waiting 600 ms for a
    /// glyph, which would be the first flaky test in the suite (CI-3).
    ///
    /// <para><b>And the <c>IsVisibleChanged</c> trap end to end</b>: <c>_beat</c> is started by the
    /// visibility change, so toggling to compact mode and back mid-pause (Ctrl+Alt+M ×2) is a real
    /// way to bring the blink back over a stopped pipe. The visibility is handed in rather than
    /// read, so the whole rule is exercised without showing an always-on-top window on a build
    /// agent.</para>
    /// </summary>
    [Fact]
    public void The_600ms_heartbeat_stops_while_paused_and_a_visibility_toggle_does_not_restart_it()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());

            overlay.SyncBeat(visible: true);
            Assert.True(overlay.BeatRunning, "a visible overlay blinks — that is the healthy state");

            overlay.SetPaused(true);
            Assert.False(overlay.BeatRunning, "the 600 ms blink must STOP while paused (AC 2)");

            // The trap: the overlay is shown again, still paused.
            overlay.SyncBeat(visible: true);
            Assert.False(overlay.BeatRunning, "a visibility change while paused must not restart it");

            // …and the pause ends, so the cheapest animation in the app comes back.
            overlay.SetPaused(false);
            overlay.SyncBeat(visible: true);
            Assert.True(overlay.BeatRunning, "requests are flowing again ⇒ the heartbeat blinks again");

            // Hidden costs nothing either way, as before — and it leaves no timer running behind
            // this test on the shared STA dispatcher.
            overlay.SyncBeat(visible: false);
            Assert.False(overlay.BeatRunning);
        });
    }

    /// <summary>
    /// <b>The <c>IsVisibleChanged</c> trap, as the rule both call sites share.</b> <c>_beat</c> is
    /// started on the visibility change, so toggling to compact mode and back during a pause
    /// (Ctrl+Alt+M ×2) would restart the blink unless the pause is REMEMBERED on the window rather
    /// than applied and forgotten. Pure, so the four corners are asserted without showing a window.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]    // visible, running: the heartbeat is the only animation
    [InlineData(true, true, false)]    // visible, paused:  AC 2
    [InlineData(false, false, false)]  // hidden: it costs nothing either way (as before)
    [InlineData(false, true, false)]
    public void The_beat_runs_only_when_visible_and_not_paused(bool visible, bool paused, bool expected)
        => Assert.Equal(expected, CompactOverlay.BeatShouldRun(visible, paused));

    /// <summary>
    /// …and the rule has ONE enforcement point. The trap this story exists to close is a <i>second</i>
    /// call site re-enabling a blink that was correctly stopped, so <c>_beat.Start()</c> appears
    /// exactly once in the file and it is inside <c>SyncBeat</c>.
    /// </summary>
    [Fact]
    public void There_is_exactly_one_place_that_starts_the_heartbeat()
    {
        var overlay = Code(File.ReadAllText(RepoFile("Views/CompactOverlay.xaml.cs")));

        Assert.Equal(1, Occurrences(overlay, "_beat.Start()"));
        Assert.Contains("if (BeatShouldRun(visible, _paused)) _beat.Start();", overlay,
                        StringComparison.Ordinal);
        Assert.Contains("private void SyncBeat() => SyncBeat(IsVisible);", overlay,
                        StringComparison.Ordinal);
        // …and both doors go through it: the visibility change and the pause itself.
        var visibility = BracedBlock(overlay, overlay.IndexOf("IsVisibleChanged +=", StringComparison.Ordinal));
        Assert.Contains("SyncBeat();", visibility, StringComparison.Ordinal);
        var setPaused = BracedBlock(overlay, overlay.IndexOf("internal void SetPaused(bool paused)",
                                                             StringComparison.Ordinal));
        Assert.Contains("SyncBeat();", setPaused, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The overlay never asks anything</b> (the boundary E5.S1's comment drew: "this loop does not
    /// reach into <c>CompactOverlay</c>"). One decision, in <c>MainWindow</c>, told to the overlay —
    /// and told again when the overlay is opened DURING a pause, which is the one door that does not
    /// go through the countdown tick.
    /// </summary>
    [Fact]
    public void The_pause_reaches_the_overlay_as_a_one_way_call()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var compact = Code(File.ReadAllText(RepoFile("Views/MainWindow.Compact.cs")));

        var body = BracedBlock(live, live.IndexOf("internal void SetLivePaused(bool paused)",
                                                  StringComparison.Ordinal));
        Assert.Contains("_overlay?.SetPaused(paused);", body, StringComparison.Ordinal);
        Assert.Contains("_overlay.SetPaused(_livePaused);", compact, StringComparison.Ordinal);

        // SetLiveUi's unconditional "●  LIVE" is what silently un-froze the indicator: the paused
        // write is authoritative now, which is the third of the three "a second call site re-enabled
        // it" traps this story names.
        Assert.Contains("LiveIndicator.Text = _livePaused ? LiveIndicatorPaused : LiveIndicatorRunning;",
                        live, StringComparison.Ordinal);
    }

    /// <summary>
    /// E5.S1's boundary from the other side: the loop still does not name the overlay, and the
    /// heartbeat still does not move inside a skipped tick. The one thing that changed is that the
    /// tick which is NOT skipped un-freezes both surfaces on the spot rather than up to a second
    /// later — <c>SetLivePaused</c> is in the running branch and not in the skipped one.
    /// </summary>
    [Fact]
    public void The_skipped_branch_is_untouched_and_the_running_branch_unfreezes()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var skipped = BracedBlock(live, live.IndexOf("if (pause.AllPaused)", StringComparison.Ordinal));

        Assert.DoesNotContain("SetLivePaused", skipped);
        Assert.DoesNotContain("_overlay", skipped);
        Assert.Contains("SetScreenStatus(LivePausedStatus(", skipped, StringComparison.Ordinal);

        Assert.Contains("SetLivePaused(false);", live, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  §2.1 S5 / S6 — the same pause, two causes, both surfaces
    // =============================================================================================

    /// <summary>
    /// <b>§3.2's paused rows, table-driven over the two surfaces</b> (AC 3). One decision —
    /// <c>ChainPause</c> — chooses the row, so the main window and the overlay cannot come to
    /// describe one state two ways (principle 1). S6 is <b>not</b> a countdown row: ruling GAP-3
    /// made it a full pause like S5, and a connection comes back when it comes back.
    /// </summary>
    [Theory]
    // S5, the three countdown bands the deck writes rows for.
    [InlineData(58, false, "○ Live — paused, next try in 0:58. It resumes on its own; nothing is lost.",
                           "○ Live paused — back in 0:58")]
    [InlineData(200, false, "○ Live — paused, next try in about 4 min. It resumes on its own; nothing is lost.",
                            "○ Live paused — back in about 4 min")]
    [InlineData(4, false, "○ Live — paused, about to retry.", "○ Live paused — about to retry")]
    [InlineData(null, false, "○ Live — paused. It resumes on its own; nothing is lost.",
                             "○ Live paused — it resumes on its own")]
    // S6 — the cause replaces the whole line on both surfaces, countdown or no countdown.
    [InlineData(58, true, "○ Live — paused, no internet connection. It resumes on its own; nothing is lost.",
                          "○ Live paused — no internet")]
    [InlineData(null, true, "○ Live — paused, no internet connection. It resumes on its own; nothing is lost.",
                            "○ Live paused — no internet")]
    public void The_deck_rows_render_on_both_surfaces(int? seconds, bool noNetwork,
                                                      string mainWindow, string overlay)
    {
        Assert.Equal(mainWindow, MainWindow.LivePausedStatus(seconds, noNetwork));
        Assert.Equal(overlay, MainWindow.LivePausedOverlayStatus(seconds, noNetwork));
        Assert.True(overlay.Length <= 40, $"the overlay budget is 40 characters — {overlay}");
    }

    /// <summary>
    /// <b>S6 is a fact about the gates, not a guess.</b> Every rung inside a window the gate recorded
    /// for <c>Network</c> is "nothing resolves"; one rung blocked for anything else is S5, and the
    /// app says so rather than blaming the player's connection for a rate limit.
    ///
    /// <para>The pause PREDICATE is unchanged (E5.S1's <c>A_dead_network_pauses_…_no_special_case</c>
    /// still holds): this flag decides which sentence, never whether.</para>
    /// </summary>
    [Fact]
    public void The_chain_says_whether_a_pause_is_a_dead_network()
    {
        var clock = new FakeClock();
        var dict = new ProviderGate(clock.Read);
        var gtx = new ProviderGate(clock.Read);
        dict.ReportFailure(TranslationErrorKind.Network);
        gtx.ReportFailure(TranslationErrorKind.Network);

        var s6 = Chain(dict, gtx).PauseNow();
        Assert.True(s6.AllPaused);
        Assert.True(s6.NoNetwork, "every tier blocked on Network is S6");

        // One tier blocked for something else and it is S5 again — the ordinary "all paused".
        var throttled = new ProviderGate(clock.Read);
        throttled.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromMinutes(5));
        var s5 = Chain(dict, throttled).PauseNow();
        Assert.True(s5.AllPaused);
        Assert.False(s5.NoNetwork);

        // …and a chain that is not paused at all says nothing about the network.
        var s1 = Chain(dict, new ProviderGate(clock.Read)).PauseNow();
        Assert.False(s1.AllPaused);
        Assert.False(s1.NoNetwork);
    }

    // =============================================================================================
    //  The toast / status arbitration (E7.S2's review left this open)
    // =============================================================================================

    /// <summary>
    /// <b>A toast owns the overlay's one line for its lifetime, and the state comes back after.</b>
    /// The window is 360 px wide and has a single status line, so a toast routed there (the main
    /// window is hidden in compact mode) and the 1 Hz countdown are writing the same
    /// <c>TextBlock</c> — and before this the countdown won within a second, so the toast was never
    /// read.
    ///
    /// <para>Nothing is lost either way: the status that would have been painted meanwhile is
    /// remembered and lands the moment the toast ends, and it is the LAST one, because a state is a
    /// fact about now and not a queue.</para>
    /// </summary>
    [Fact]
    public void A_toast_holds_the_overlay_line_and_the_state_lands_when_it_ends()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());
            overlay.SetStatus("🔴 Live — watching…");

            overlay.ShowToast("Discord copied: kizotis");
            Assert.Equal("Discord copied: kizotis", overlay.OverlayStatus.Text);

            // The countdown, ≤ 1 s later, and then a second later again. Neither reaches the line.
            overlay.SetStatusIfChanged("○ Live paused — back in 0:58");
            overlay.SetStatus("○ Live paused — back in 0:57");
            Assert.Equal("Discord copied: kizotis", overlay.OverlayStatus.Text);

            // The toast's own timer ends the hold (MainWindow owns it — no timer was added here),
            // and the line goes back to describing the state, at the freshest second it was given.
            overlay.EndToast();
            Assert.Equal("○ Live paused — back in 0:57", overlay.OverlayStatus.Text);

            // Idempotent, and the ordinary path is unaffected afterwards.
            overlay.EndToast();
            overlay.SetStatusIfChanged("○ Live paused — back in 0:56");
            Assert.Equal("○ Live paused — back in 0:56", overlay.OverlayStatus.Text);
        });
    }

    /// <summary>
    /// A toast with nothing behind it leaves the line exactly as the toast left it: there is no
    /// state to restore, and inventing one (an empty string, say) would collapse a status line the
    /// player was reading.
    /// </summary>
    [Fact]
    public void A_toast_with_no_status_behind_it_leaves_the_line_alone()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());
            overlay.ShowToast("Text size 110%");
            overlay.EndToast();
            Assert.Equal("Text size 110%", overlay.OverlayStatus.Text);
        });
    }

    /// <summary>
    /// The wiring, which is the half a behavioural test cannot see without a shown window: the
    /// compact route goes through <c>ShowToast</c> (so the hold begins) and the hold ends on the
    /// toast timer <c>MainWindow</c> already runs — <b>no new timer</b> (§2.4: one countdown for the
    /// whole app, and the 600 ms heartbeat is the only other thing that ticks).
    /// </summary>
    [Fact]
    public void The_toast_route_and_its_lifetime_are_the_main_windows()
    {
        var main = Code(File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")));

        Assert.Contains("if (_overlay is { IsVisible: true }) _overlay.ShowToast(message);", main,
                        StringComparison.Ordinal);
        Assert.Contains("_overlay?.EndToast();", main, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(main, "_overlay?.EndToast()"));

        // The main window's timers, counted exactly so a fourth has to be a decision. THIS story
        // added none of them: the toast timer and the 1 Hz countdown are E7's, and the third is
        // E8.S4's one-shot idle unload — armed by StopLive, fired once, stopped by its own handler,
        // and deliberately NOT a third question on the countdown's stop rule (which is the bug
        // E7.S2's review flagged). §2.4's "one DispatcherTimer for the whole app" is about one
        // COUNTDOWN, as `_countdownTimer`'s own remark says, not one timer in the process.
        Assert.Equal(3, Occurrences(main, "new() { Interval ="));
        Assert.Equal(1, Occurrences(main, "private readonly DispatcherTimer _offlineIdleTimer ="));
        Assert.Equal(1, Occurrences(Code(File.ReadAllText(RepoFile("Views/CompactOverlay.xaml.cs"))),
                                    "DispatcherTimer"));
    }

    // =============================================================================================
    //  §3.5 — the one-time notices, and D2's evidence
    // =============================================================================================

    /// <summary>
    /// <b>Deviation D2, settled the way amendment A4 settles it.</b> "— another engine is being
    /// tried" is not restored as a tail of §3.1's sentences (those are rendered once the whole
    /// attempt has failed, so the promise would be false at the one moment it is read). What renders
    /// instead is §3.5's one-time notice, and its evidence is <c>LastOutcome.Skipped</c> being
    /// non-empty behind a tier that ANSWERED — ruling E3-b, <c>EngineStatus.FellBack</c>.
    /// </summary>
    [Fact]
    public void The_fallback_notice_renders_only_with_a_skipped_tier_and_a_pause_to_name()
    {
        // Google skipped because it is inside a window, Edge answering: the deck's own example.
        Assert.Equal("Translated by Google (backup) — Google is paused.",
                     MainWindow.FallbackNotice(FellBackFrom(pausedPreferred: true)));

        // Nothing was skipped — the first tier answered. There is no fallback to report, and the
        // app does not announce one (this is the D2 clause's whole gate).
        Assert.Null(MainWindow.FallbackNotice(Status(outcome: Answered(ProviderIds.GoogleDict))));

        // A tier WAS skipped, but no tier at or above the one serving is inside a window any more:
        // there is nothing honest to name, so nothing is written (§3.0 rule 1).
        Assert.Null(MainWindow.FallbackNotice(FellBackFrom(pausedPreferred: false)));

        // …and the sentence itself, from the names table and never a guess.
        Assert.Equal("Translated by Edge — Google is paused.",
                     UserMessages.TranslatedBy(ProviderIds.Edge, ProviderIds.GoogleDict));
        Assert.Null(UserMessages.TranslatedBy(ProviderIds.Edge, null));
        Assert.Null(UserMessages.TranslatedBy("something-new", ProviderIds.GoogleDict));
    }

    /// <summary>
    /// <b>§3.5 is "once per switch", and the notice goes on the surface that owns the state.</b>
    /// With LIVE off that is the Translator tab's line (where E7.S3 put it, beside the write path's
    /// own chip); with LIVE running it is the read status line, and §3.2's "resumed" row says how —
    /// the normal running line PLUS the notice, so it neither replaces a fresh status nor is
    /// replaced by the next tick 700 ms later.
    /// </summary>
    [Fact]
    public void The_recovery_notice_rides_on_the_next_running_line_and_replaces_nothing()
    {
        Assert.Equal("🔴 Live — watching for new text…  Back on Google.",
                     MainWindow.WithNotice("🔴 Live — watching for new text…", "Back on Google."));
        Assert.Equal("🔴 Live — watching for new text…",
                     MainWindow.WithNotice("🔴 Live — watching for new text…", null));

        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();   // no LIVE loop: the Translator tab owns the state
            window.ShowStateNotice("Back on Google.");
            Assert.Equal("Back on Google.", window.TranslateStatus.Text);
        });
    }

    /// <summary>
    /// The "once per switch" memory itself: the same sentence is not re-announced sixty times a
    /// minute while the state it describes lasts (principle 1 — a state is announced once and left
    /// there), and a state with nothing to say clears the memory so the next switch speaks again.
    /// </summary>
    [Fact]
    public void The_notice_is_compared_before_it_is_written()
    {
        var main = Code(File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")));
        var body = BracedBlock(main, main.IndexOf("internal bool RefreshEngineChip()", StringComparison.Ordinal));

        Assert.Contains("if (notice is not null && !string.Equals(notice, _lastStateNotice, StringComparison.Ordinal))",
                        body, StringComparison.Ordinal);
        Assert.Contains("_lastStateNotice = notice;", body, StringComparison.Ordinal);
        // E8.S3 added AC 2's nudge as a fourth argument, and it rides this same comparison — which
        // is the whole reason the nudge is safe: it is a sentence a pure function chooses, so it can
        // never become a dialog or a per-row annotation, and "once, when the state is entered" is
        // the memory below rather than a new mechanism.
        // …and E8.S5 added a fifth: which SURFACE is going to render it. S4 is the one state whose
        // sentence differs between the Translator tab and the LIVE status line (§2.2's table), and
        // the choice is made where the state is rather than by the writer downstream.
        Assert.Contains(
            "var notice = StateNotice(status, chip, _chipWasDegraded, _offlineInstalled, _liveCts != null);",
            body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The join is bounded</b> (review, Winston). Two sentences that are each budgeted on their
    /// own do not make a budgeted line: the worst real pair — §3.2's paused row plus §3.5's
    /// fallback notice — already passes §3.2's ~120, and "the main window wraps" is a reason to
    /// bound it rather than a reason not to.
    ///
    /// <para>The half that gives way is the STATUS: it is re-derived on the next tick, while the
    /// notice is said once per switch and never repeated. The cut is an ellipsis and it never
    /// leaves half a surrogate pair behind — every LIVE line in the deck opens on <c>🔴</c>.</para>
    /// </summary>
    [Fact]
    public void The_notice_rides_within_the_main_lines_budget_and_the_status_gives_way()
    {
        // The worst pair the app can actually render today: the paused row plus the fallback line.
        var paused = MainWindow.LivePausedStatus(58);
        var notice = UserMessages.TranslatedBy(ProviderIds.GoogleGtx, ProviderIds.GoogleDict)!;
        var line = MainWindow.WithNotice(paused, notice);

        Assert.True(line.Length <= MainWindow.MainStatusBudget,
                    $"the joined line is {line.Length} chars, over §3.2's {MainWindow.MainStatusBudget}: {line}");
        Assert.EndsWith(notice, line, StringComparison.Ordinal);
        Assert.Contains("…  ", line, StringComparison.Ordinal);

        // A pair that fits is joined untouched — the bound is a ceiling, not a formatter.
        Assert.Equal("🔴 Live — watching for new text…  Back on Google.",
                     MainWindow.WithNotice("🔴 Live — watching for new text…", "Back on Google."));

        // Nothing to ride on: the notice is the line, rather than two spaces and a sentence.
        Assert.Equal("Back on Google.", MainWindow.WithNotice("", "Back on Google."));

        // …and the cut never splits a surrogate pair, with the pair placed exactly ON the cut.
        int room = MainWindow.MainStatusBudget - notice.Length - 3;
        var cut = MainWindow.WithNotice(new string('x', room - 1) + "🔴 tail", notice);
        Assert.True(cut.Length <= MainWindow.MainStatusBudget);
        Assert.DoesNotContain("🔴", cut, StringComparison.Ordinal);
        for (int i = 0; i < cut.Length; i++)
            Assert.False(char.IsHighSurrogate(cut[i]) && (i + 1 == cut.Length || !char.IsLowSurrogate(cut[i + 1])),
                         "a cut line must not end on half a surrogate pair");
    }

    /// <summary>
    /// <b>The fallback notice never names the engine that is serving.</b> <c>PreferredPause</c>
    /// counts the serving tier's OWN window on purpose — for the chip, which is right: the player
    /// is about to feel it — but "Translated by Google (backup) — Google (backup) is paused." is a
    /// sentence that contradicts itself in six words. It is reachable the ordinary way round: the
    /// tier that answered a moment ago closes behind the answer while the tier above it reopens.
    /// </summary>
    [Fact]
    public void The_fallback_notice_never_names_the_engine_that_is_serving()
    {
        var servingIsPaused = Status(
            gates: new(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleGtx] =
                    new(GateState.Open, Now + TimeSpan.FromSeconds(58), 1, TranslationErrorKind.RateLimited),
            },
            outcome: Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict));

        Assert.Null(MainWindow.FallbackNotice(servingIsPaused));
    }

    /// <summary>
    /// <b>§3.5's two directions, as the one decision that chooses between them</b> (review): the
    /// recovery line fires once per resume and <b>never on the first start</b> — every session
    /// begins undegraded, so counting that would announce a recovery from nothing at the first
    /// successful translation of every launch.
    /// </summary>
    [Fact]
    public void The_recovery_notice_fires_once_per_resume_and_never_on_the_first_start()
    {
        var healthy = Status(outcome: Answered(ProviderIds.GoogleDict));
        var healthyChip = MainWindow.ChipFor(healthy);
        Assert.True(healthyChip.IsHealthy);

        // The first start: nothing degraded was ever observed, so there is nothing to recover from.
        Assert.Null(MainWindow.StateNotice(healthy, healthyChip, wasDegraded: false));

        // Degraded, and the notice is the OTHER direction's — D2's evidence, not a recovery.
        var degraded = FellBackFrom(pausedPreferred: true);
        var degradedChip = MainWindow.ChipFor(degraded);
        Assert.False(degradedChip.IsHealthy);
        Assert.Equal("Translated by Google (backup) — Google is paused.",
                     MainWindow.StateNotice(degraded, degradedChip, wasDegraded: false));

        // …and back up: once, on the tick that recovers.
        Assert.Equal("Back on Google.", MainWindow.StateNotice(healthy, healthyChip, wasDegraded: true));
        // The caller's memory has moved on by the next tick (_chipWasDegraded is false while the
        // chip is healthy), so the same recovery is not announced sixty times a minute.
        Assert.Null(MainWindow.StateNotice(healthy, healthyChip, wasDegraded: false));
    }

    /// <summary>
    /// <b>§3.2's S3 row — the decision E7.S5 had to make, pinned as behaviour.</b> The deck writes
    /// it as a status line: <c>🔴 Live — {P} paused ({t}), using {P2}.</c> It is <b>not</b> written,
    /// and the three reasons are all invariants this epic already holds:
    ///
    /// <list type="number">
    /// <item><b>One clock per window</b> (E7.S2 AC 4). In S3 nothing is fully paused, so the LIVE
    ///       status line does not own the clock and the CHIP does — <c>Google paused 0:58</c>,
    ///       stepping at 1 Hz. A second <c>({t})</c> on the status line is a second clock; a coarse
    ///       one (E7-a) is worse — two renderings of one countdown, disagreeing.</item>
    /// <item><b>One message per state</b> (§1 principle 1). §3.5's notice already carries S3's two
    ///       names on this very line, once per switch, with the cause: they are the same fact.</item>
    /// <item>In S3 requests really are flowing — E7.S4 keeps the heartbeat blinking for exactly that
    ///       reason — so replacing the progress row with a pause-shaped sentence would read as a
    ///       loop that has stopped.</item>
    /// </list>
    ///
    /// <para>And the copy is not written EITHER: a sentence in the deck that nothing renders is
    /// UX-DR19's failure the other way round, which is the argument E7.S4 made when it declined
    /// §3.2's "⚠ one read is retrying" overlay column.</para>
    /// </summary>
    [Fact]
    public void S3_is_said_by_the_chip_and_the_one_time_notice_and_never_by_a_second_clock()
    {
        var s3 = FellBackFrom(pausedPreferred: true);
        var chip = MainWindow.ChipFor(s3);

        // The persistent half: the paused engine's name AND the countdown, on the chip.
        Assert.Equal("Google paused 0:58", chip.Text);
        Assert.True(chip.HasClock);
        Assert.False(chip.IsHealthy);

        // The explaining half: both names, once per switch, on the status line the loop writes.
        Assert.Equal("Translated by Google (backup) — Google is paused.",
                     MainWindow.StateNotice(s3, chip, wasDegraded: false));

        // …so there is no second S3 sentence anywhere, and nothing to render one from.
        foreach (var file in new[] { Path.Combine("Views", "MainWindow.Live.cs"), Path.Combine("Services", "UserMessages.cs") })
            Assert.Equal(0, Occurrences(Code(File.ReadAllText(RepoFile(file))), ", using "));
    }

    /// <summary>
    /// <b>D2's free-chain pair, and it is kept exactly as the names table writes it</b> (E7.S5).
    /// <c>Translated by Google (backup) — Google is paused.</c> reads oddly at a glance — the same
    /// vendor twice — and it is still the right sentence: §3.0 rule 1 forbids inventing a name,
    /// rule 2's "drop the suffix the name already carries" is about the chip's <c>· backup</c> and
    /// not about the vendor, and a same-vendor special case would be a fourth spelling of a provider
    /// name (UX-DR19) that hid the one fact making the notice useful — the fallback is the same
    /// vendor's second door, so the player should expect the same quality and no action of theirs.
    ///
    /// <para>It is also the only pair reachable today (E3-d: Edge is not shipped), which is why it
    /// is pinned as the rendered sentence rather than as a template.</para>
    /// </summary>
    [Fact]
    public void The_free_chains_own_fallback_pair_names_both_engines_of_the_one_vendor()
    {
        var notice = UserMessages.TranslatedBy(ProviderIds.GoogleGtx, ProviderIds.GoogleDict);

        Assert.Equal("Translated by Google (backup) — Google is paused.", notice);
        // Both halves come from the table and neither is edited at the join.
        Assert.Contains(ProviderNames.Display(ProviderIds.GoogleGtx)!, notice, StringComparison.Ordinal);
        Assert.Contains(ProviderNames.Display(ProviderIds.GoogleDict)!, notice, StringComparison.Ordinal);
        Assert.True(notice!.Length <= MainWindow.MainStatusBudget);
    }

    /// <summary>
    /// <b>Two toasts that overlap keep ONE hold, and the last one wins</b> (review): the hold is a
    /// flag rather than a counter, and its lifetime is the single <c>_toastTimer</c> restart the
    /// main window does on every toast — so the second toast does not leave a hold behind that the
    /// first toast's timer already ended.
    /// </summary>
    [Fact]
    public void Two_overlapping_toasts_keep_one_hold_and_the_state_still_lands()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var overlay = new CompactOverlay(new MainWindow());

            overlay.ShowToast("Copied.");
            overlay.SetStatusIfChanged("○ Live paused — back in 0:58");
            overlay.ShowToast("Discord copied: kizotis");      // the second toast, mid-hold
            Assert.Equal("Discord copied: kizotis", overlay.OverlayStatus.Text);

            overlay.SetStatusIfChanged("○ Live paused — back in 0:57");
            Assert.Equal("Discord copied: kizotis", overlay.OverlayStatus.Text);

            // ONE EndToast (the one timer restarted by the second toast) releases the one hold, and
            // the freshest state lands — not the one the first toast interrupted.
            overlay.EndToast();
            Assert.Equal("○ Live paused — back in 0:57", overlay.OverlayStatus.Text);

            // …and a toast shown on the MAIN window leaves this line alone: EndToast is a no-op
            // when this window holds nothing, so it can never invent a state or replay an old one.
            overlay.EndToast();
            Assert.Equal("○ Live paused — back in 0:57", overlay.OverlayStatus.Text);
        });
    }

    /// <summary>
    /// <b>Nothing stale is SHOWN once LIVE is off</b> (review). The paused state is remembered
    /// across a Stop on purpose — it is what lets a restart into a standing pause render the paused
    /// form at once, and what keeps the overlay's 600 ms timer stopped meanwhile (UX-DR18) — so the
    /// thing that must hold is that neither surface can show a paused heartbeat while there is no
    /// loop: both are <c>Collapsed</c>, and the overlay's ▶/■ button still follows the loop rather
    /// than being stranded behind the pause guard's early return.
    /// </summary>
    [Fact]
    public void A_pause_with_LIVE_off_shows_no_heartbeat_on_either_surface()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            window.SetLivePaused(true);
            Assert.Equal(MainWindow.LiveIndicatorPaused, window.LiveIndicator.Text);
            Assert.Equal(System.Windows.Visibility.Collapsed, window.LiveIndicator.Visibility);

            var overlay = new CompactOverlay(window);
            overlay.SetPaused(true);
            Assert.Equal(System.Windows.Visibility.Collapsed, overlay.LiveDot.Visibility);
            Assert.Equal("▶ Live", overlay.LiveToggleButton.Content);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>A clock this file drives, so "the window has (not) elapsed" is a fact and not a
    /// race (IS-6) — the same shape <c>LivePauseTests</c> uses.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    /// <summary>A translator that would notice if it were ever called. Nothing here calls one.</summary>
    private sealed class NeverCalled : ITranslator
    {
        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
            => throw new InvalidOperationException("no case in this file sends anything");

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
                                                      CancellationToken ct = default)
            => throw new InvalidOperationException("no case in this file sends anything");
    }

    private static ChainTranslator Chain(params ProviderGate[] gates) =>
        new(gates.Select((g, i) => new ChainTier($"p{i}", g, new NeverCalled())).ToList());

    private static readonly string[] FreeChain = { ProviderIds.GoogleDict, ProviderIds.GoogleGtx };

    private static ChainTranslator.Outcome Answered(string providerId, params string[] skipped)
        => new(providerId, skipped.Select(s => (ProviderId: s, Reason: "Paused")).ToList(),
               skipped.Length == 0 ? null : Now + TimeSpan.FromSeconds(58), null);

    private static EngineStatus Status(Dictionary<string, GateSnapshot>? gates = null,
                                       ChainTranslator.Outcome? outcome = null)
        => EngineStatus.Of(FreeChain,
                           gates ?? new Dictionary<string, GateSnapshot>(StringComparer.Ordinal),
                           FreeChain, outcome, stateKnown: true, Now);

    /// <summary>Google skipped, the backup answering — with or without Google's window still
    /// standing, which is the difference between "there is a pause to name" and "there is not".</summary>
    private static EngineStatus FellBackFrom(bool pausedPreferred)
        => Status(gates: pausedPreferred
                      ? new(StringComparer.Ordinal)
                        {
                            [ProviderIds.GoogleDict] =
                                new(GateState.Open, Now + TimeSpan.FromSeconds(58), 1,
                                    TranslationErrorKind.RateLimited),
                        }
                      : null,
                  outcome: Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict));

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

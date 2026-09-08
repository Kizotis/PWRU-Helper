using System.IO;
using System.Linq;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E5.S1 — <b>a paused app costs a player nothing.</b> Three things are pinned here and they are the
/// three ways this story can be got wrong:
///
/// <list type="number">
/// <item><see cref="ChainTranslator.PauseNow"/> answers the right question (ruling E3-a:
///       <c>BlockedUntil &gt; Now()</c>, never <c>State</c>) and answers it without touching
///       anything (ruling R-2).</item>
/// <item>A rate-ceiling <c>Wait</c> is <b>not</b> a pause (ruling E5-a's negative half) — the fix
///       that would pause LIVE for a condition clearing in 500 ms.</item>
/// <item>The skipped tick really is empty: no capture, no OCR, no <c>_dedup.Next</c>, no request
///       (TP-LIVE-01, OQ-B), and the dedup clock is therefore frozen (TP-LIVE-15, AC 3).</item>
/// </list>
///
/// <para>The gates here are LOCAL — <c>new ProviderGate(clock)</c> with this file's own fake clock —
/// so nothing joins the registry, nothing reads <c>provider-state.json</c>, and the file needs
/// neither the non-parallel <c>Gates</c> collection nor an STA host. Nothing sleeps (CI-3).</para>
/// </summary>
public class LivePauseTests
{
    /// <summary>A clock this file drives, so "the window has (not) elapsed" is a fact and not a
    /// race — the shape <c>ChainTranslatorTests</c> and <c>HttpProviderCoreTests</c> both use.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
    }

    /// <summary>A translator that would notice if it were ever called. The point of every case in
    /// this file is that it never is.</summary>
    private sealed class CountingTranslator : ITranslator
    {
        public int Calls;

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
        { Calls++; return Task.FromResult(text); }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
                                                      CancellationToken ct = default)
        { Calls++; return Task.FromResult(lines.ToList()); }
    }

    private static ChainTranslator Chain(params ProviderGate[] gates) =>
        new(gates.Select((g, i) => new ChainTier($"p{i}", g, new CountingTranslator())).ToList());

    // ---- PauseNow: the predicate ----------------------------------------------------------------

    /// <summary>
    /// The whole answer, in one case: two tiers, both blocked, and the chain is paused until the
    /// EARLIEST of the two windows — because the chain gets a rung back when the first of them
    /// reopens, and telling a player to wait for the last one would leave a working provider unused
    /// for the difference.
    /// </summary>
    [Fact]
    public void Every_tier_blocked_is_a_pause_that_ends_at_the_earliest_window()
    {
        var clock = new FakeClock();
        var soon = new ProviderGate(clock.Read);
        var late = new ProviderGate(clock.Read);
        soon.ReportFailure(TranslationErrorKind.Unavailable, clock.Now + TimeSpan.FromSeconds(5));
        late.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromMinutes(30));

        var pause = Chain(soon, late).PauseNow();

        Assert.True(pause.AllPaused);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(5), pause.RetryAt);
    }

    /// <summary>One open rung is enough — a partially paused chain still translates, so the LIVE
    /// tick must run and let the chain skip the blocked tier itself.</summary>
    [Fact]
    public void One_tier_still_available_is_not_a_pause()
    {
        var clock = new FakeClock();
        var blocked = new ProviderGate(clock.Read);
        blocked.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromMinutes(30));

        var pause = Chain(blocked, new ProviderGate(clock.Read)).PauseNow();

        Assert.False(pause.AllPaused);
        Assert.Null(pause.RetryAt);
    }

    /// <summary>
    /// <b>Ruling E3-a, and the reason R-01 is not in this app.</b> Once the window has elapsed the
    /// gate is still <see cref="GateState.Open"/> — deliberately, because it becomes half-open only
    /// when a caller actually takes the probe. A pause check written on the STATE would skip for
    /// ever: nobody would call <c>TryEnter</c>, so nobody would take the probe, so the gate would
    /// never close. The assertion on <c>State</c> is what makes the case a regression test rather
    /// than a tautology.
    /// </summary>
    [Fact]
    public void An_elapsed_window_is_no_longer_a_pause_even_though_the_state_still_says_open()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        gate.ReportFailure(TranslationErrorKind.Unavailable, clock.Now + TimeSpan.FromSeconds(5));
        var chain = Chain(gate);
        Assert.True(chain.PauseNow().AllPaused);

        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(GateState.Open, gate.Snapshot().State);      // the state outlives the window…
        Assert.False(chain.PauseNow().AllPaused);                  // …and the pause does not
    }

    /// <summary>
    /// <b>Ruling E5-a's negative half — the test that stops the wrong fix.</b> The §5.4 token bucket
    /// is empty, so the next request would be told to <c>Wait</c>; no <c>BlockedUntil</c> is set,
    /// because a rate ceiling is not an outage. The tick must therefore RUN, and
    /// <c>HttpProviderCore</c> does the ≤ 2 bounded waits inside it. A pre-tick check that treated
    /// this as a pause would freeze LIVE for something that clears in 500 ms.
    /// </summary>
    [Fact]
    public void An_exhausted_rate_ceiling_is_not_a_pause()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);

        // Drain the bucket (capacity 2) and confirm the gate really is at the ceiling.
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        var refused = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Wait, refused.Outcome);
        Assert.True(GateDecision.WorthWaiting(refused.Delay), "a ceiling wait is short by definition");

        Assert.Null(gate.Snapshot().BlockedUntil);
        Assert.False(Chain(gate).PauseNow().AllPaused);
    }

    /// <summary>
    /// <b>Ruling GAP-3 / R-6 — "full pause is universal", and the end-to-end half of the escalation
    /// this story retires.</b> The gates are told only the KIND here, never a window: <c>Network</c>
    /// classifies to <c>GateReaction.SoftCooldown</c>, so a machine with no connection has every read
    /// tier blocked for <see cref="TranslationPolicy.SoftCooldownSecs"/> at a time and
    /// <c>AllPaused</c> is true with no special case for UX state <b>S6</b>. That is why the loop
    /// stops capturing when the cable is out, and it is the chain half of E2.S5's accepted
    /// "no network ⇒ LIVE auto-stops after ~3 s (5 <i>refused</i> ticks)" — those ticks are now
    /// skipped, so they reach no counter at all.
    ///
    /// <para>Asserting the classification rather than a hand-written <c>BlockedUntil</c> is the
    /// point: a future kind table that stopped giving <c>Network</c> a window would leave the loop
    /// capturing and OCR-ing through an outage, and every other case in this file would stay green.
    /// It also pins that what the player is counted down to is the gate's window and not a number
    /// the loop invented.</para>
    /// </summary>
    [Fact]
    public void A_dead_network_pauses_the_whole_read_chain_with_no_special_case_for_S6()
    {
        var clock = new FakeClock();
        var dict = new ProviderGate(clock.Read);
        var gtx = new ProviderGate(clock.Read);
        dict.ReportFailure(TranslationErrorKind.Network);
        gtx.ReportFailure(TranslationErrorKind.Network);

        var pause = Chain(dict, gtx).PauseNow();

        Assert.True(pause.AllPaused);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(TranslationPolicy.SoftCooldownSecs),
                     pause.RetryAt);
        Assert.Equal(TranslationPolicy.SoftCooldownSecs,
                     LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now));
    }

    /// <summary>
    /// <b>IS-6, as the countdown a player actually reads.</b> <c>RetryAt</c> is produced against the
    /// gates' clock, so the answer carries the instant it was compared against and the caller
    /// subtracts THAT — never its own <c>UtcNow</c>. The two agree in production and diverge under
    /// every injected clock, which is the whole reason <c>ProviderGate.Now()</c> exists; before this
    /// was fixed (review, E5.S1) the loop mixed them and the countdown could only ever be asserted
    /// through the formatting helper, never end to end.
    /// </summary>
    [Fact]
    public void The_pause_answer_carries_the_gate_clock_it_was_computed_against()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        gate.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromSeconds(30));
        var chain = Chain(gate);

        var pause = chain.PauseNow();
        Assert.Equal(clock.Now, pause.Now);
        // The window is the gate's to choose (it extends a hint it thinks too short), so the whole
        // countdown is read back rather than asserted — what matters is that it then runs down.
        int whole = LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now)!.Value;
        Assert.True(whole > 5, "the window has to be long enough to run down");

        clock.Advance(TimeSpan.FromSeconds(whole - 5));

        // The wall clock did not move; the gates' did, and the countdown followed it.
        var later = chain.PauseNow();
        Assert.Equal(clock.Now, later.Now);
        Assert.Equal(5, LiveTickPolicy.CountdownSeconds(later.RetryAt, later.Now));
    }

    /// <summary>
    /// <b>A gate that has not read <c>provider-state.json</c> yet must not answer "nothing is
    /// blocked".</b> E2.S4 hangs the lazy load off <c>TryEnter</c> — the right trigger for every path
    /// that sends something, and the wrong one for the single caller whose entire purpose is to
    /// decide NOT to send. Without the seeding call inside <c>PauseNow</c> (review, E5.S1) the first
    /// tick of a session resumed INTO a standing window captures, OCRs, advances the dedup clock and
    /// spends a request before the gate has looked at the file — which is exactly the story's own
    /// manual verification (hand-edit the file, restart, start LIVE, expect a frozen feed).
    ///
    /// <para>The seam is the gate's, so this test needs no registry and no temp file: the
    /// <c>ensureLoaded</c> delegate stands in for the load and reports whether it ran.</para>
    /// </summary>
    [Fact]
    public void A_status_read_seeds_the_persisted_window_before_it_answers()
    {
        var clock = new FakeClock();
        int loads = 0;
        ProviderGate? gate = null;
        gate = new ProviderGate(clock.Read, ensureLoaded: () =>
        {
            loads++;
            // What ProviderGates.EnsureLoaded does: seed the gate from the file, once.
            gate!.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromMinutes(30));
        });

        var pause = Chain(gate).PauseNow();

        Assert.Equal(1, loads);
        Assert.True(pause.AllPaused, "a window standing on disk is a pause on the FIRST tick, not the second");
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(30), pause.RetryAt);
    }

    /// <summary>
    /// Ruling <b>R-2</b>, as an assertion rather than a promise: asking a hundred times changes
    /// nothing. No token is spent (the bucket still admits its two), no probe is taken (the state is
    /// exactly what it was), no clock moves. A status read that took the half-open probe would leave
    /// the gate half-open for a whole window with nobody able to report the result (I1) — R-01
    /// introduced by the very code that exists to prevent it.
    /// </summary>
    [Fact]
    public void PauseNow_is_side_effect_free()
    {
        var clock = new FakeClock();
        var blocked = new ProviderGate(clock.Read);
        blocked.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromMinutes(30));
        var healthy = new ProviderGate(clock.Read);
        var chain = Chain(blocked, healthy);

        var before = (blocked.Snapshot(), healthy.Snapshot());
        for (int i = 0; i < 100; i++) chain.PauseNow();

        Assert.Equal(before.Item1, blocked.Snapshot());
        Assert.Equal(before.Item2, healthy.Snapshot());
        Assert.Equal(clock.Now, new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        // The bucket is untouched: both of its tokens are still there to be taken.
        Assert.Equal(GateOutcome.Allow, healthy.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Allow, healthy.TryEnter(RequestPriority.Interactive).Outcome);
    }

    /// <summary>…and it sends nothing, which is the reading that matters to a metered provider: the
    /// tier's translator is never entered, however many times the loop asks.</summary>
    [Fact]
    public void Asking_whether_the_chain_is_paused_costs_no_request()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        gate.ReportFailure(TranslationErrorKind.Network, clock.Now + TimeSpan.FromSeconds(5));
        var translator = new CountingTranslator();
        var chain = new ChainTranslator(new[] { new ChainTier("p0", gate, translator) });

        for (int i = 0; i < 100; i++) Assert.True(chain.PauseNow().AllPaused);

        Assert.Equal(0, translator.Calls);
    }

    // ---- TP-LIVE-01: the skipped tick is empty --------------------------------------------------

    /// <summary>
    /// <b>TP-LIVE-01 and TP-LIVE-15, behaviourally — the four counters, actually counted.</b> The
    /// loop is code-behind and needs a window; its DECISION is not, so this drives the real
    /// <see cref="ChainTranslator.PauseNow"/> over real gates and the real <see cref="LiveDedup"/>
    /// through the same branch the loop takes, with a counter standing in for each of the four
    /// things a tick costs. The source scans below pin that the loop's branch has this shape; this
    /// pins that the shape does what OQ-B says.
    ///
    /// <para>It is the half a scan cannot reach: it fails if <c>PauseNow</c> ever answers "not
    /// paused" for a fully blocked chain, and it asserts <b>AC 3 on the other side of the pause</b>,
    /// which is where the player sees it — a message that arrived DURING the outage is translated
    /// the moment the window elapses (the row this story stops burning), while one already
    /// translated before it stays suppressed because the dedup clock never advanced.</para>
    /// </summary>
    [Fact]
    public void TP_LIVE_01_twenty_paused_ticks_cost_nothing_and_the_feed_catches_up_after_them()
    {
        const double match = 0.85, confirm = 0.70;
        const string before = "proBlemka: ТС ЛЕГА 2 ДД";
        const string during = "kotik: го в лк, нужен хил";

        var clock = new FakeClock();
        var dict = new ProviderGate(clock.Read);
        var gtx = new ProviderGate(clock.Read);
        var chain = Chain(dict, gtx);
        var dedup = new LiveDedup();

        int captures = 0, ocrReads = 0, requests = 0;
        var feed = new List<string>();

        // The loop's tick with its four costs counted instead of incurred. The order, the branch and
        // "confirmed lines are translated" are the loop's own (MainWindow.Live.cs:203-275).
        void Tick(string[] onScreen)
        {
            if (chain.PauseNow().AllPaused) return;          // …the whole tick body is skipped
            captures++;
            ocrReads++;
            var confirmed = dedup.Next(onScreen, match, confirm);
            if (confirmed.Count == 0) return;
            requests++;
            feed.AddRange(confirmed);
        }

        Tick(new[] { before });
        Tick(new[] { before });
        Assert.Equal(new[] { before }, feed);
        var (capturesBefore, ocrBefore, requestsBefore) = (captures, ocrReads, requests);

        // The cable comes out — every read tier takes a soft window (GAP-3) — and a new message
        // arrives on screen and stays there for the whole outage.
        dict.ReportFailure(TranslationErrorKind.Network);
        gtx.ReportFailure(TranslationErrorKind.Network);
        for (int i = 0; i < 20; i++) Tick(new[] { before, during });

        Assert.Equal(capturesBefore, captures);              // no capture…
        Assert.Equal(ocrBefore, ocrReads);                   // …no OCR…
        Assert.Equal(requestsBefore, requests);              // …and no request.
        Assert.Equal(new[] { before }, feed);                // the feed is frozen and nothing burned

        // The window elapses: the loop resumes, and the message that sat on screen throughout is
        // still genuinely new. Two ticks because LiveDedup confirms on the second sighting.
        clock.Advance(TimeSpan.FromSeconds(TranslationPolicy.SoftCooldownSecs + 1));
        Tick(new[] { before, during });
        Tick(new[] { before, during });

        Assert.Equal(new[] { before, during }, feed);
        Assert.Equal(capturesBefore + 2, captures);          // and the loop is back at full cadence
    }

    /// <summary>
    /// <b>TP-LIVE-01, OQ-B's full pause.</b> The loop is code-behind and cannot be driven headlessly,
    /// so the four counters are counted where they can be: in the source. The skipped branch must
    /// come BEFORE the capture and must name none of the four things a tick costs — a capture, an
    /// OCR read, a <c>_dedup.Next</c>, a translation. This is the same kind of guard as
    /// <c>ChainCompositionTests</c>' decorator scan and TP-START-02, and it is the one that fails if
    /// somebody later "just adds the OCR back, it's local and free" (which is precisely what
    /// <c>ux-mode-degrade.md</c> §5 flow (e) used to say, superseded by ruling GAP-3 / R-6).
    /// </summary>
    [Fact]
    public void TP_LIVE_01_the_skipped_tick_captures_nothing_ocrs_nothing_and_never_touches_the_dedup()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        int ask = live.IndexOf("_readChain.PauseNow()", StringComparison.Ordinal);
        int branch = live.IndexOf("if (pause.AllPaused)", StringComparison.Ordinal);
        int capture = live.IndexOf("ScreenCapture.Capture", StringComparison.Ordinal);
        Assert.True(ask >= 0, "the loop must ask the chain whether it is paused");
        Assert.True(branch > ask, "the answer must be branched on");
        Assert.True(branch < capture,
            "the pause branch must come BEFORE the capture — that is the whole of OQ-B");

        var body = BracedBlock(live, branch);
        // Non-vacuity: a brace matcher that returned "{}" would make every assertion below pass
        // while proving nothing. The block must be the one that actually computes the back-off.
        Assert.Contains("LiveTickPolicy.BackoffWaitMs", body, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "ScreenCapture.Capture", "ApplyOcrFilter", "ReadLinesAsync",
                     "_dedup.Next", "AppendLinesToHistory", "TranslateBodiesAsync",
                     "_readTranslator",
                     // E5.S2 renamed the counter this line used to say (`consecutiveErrors`) into a
                     // tracker object. The reading is unchanged and it is now structural: the
                     // skipped branch cannot reach the auto-stop at all, which is ruling E5-c
                     // enforced by the shape of the code rather than by the value of a flag.
                     "errors.Record", "LiveTickPolicy.Classify",
                 })
            Assert.False(body.Contains(forbidden, StringComparison.Ordinal),
                $"a skipped tick must not reach {forbidden} — it does nothing at all (OQ-B)");
    }

    /// <summary>
    /// The heartbeat freezes while paused (AC 1 / the R-02 zombie indicator): the skipped branch
    /// does not write <c>LiveIndicator</c> and does not advance <c>_liveTicks</c>, which is the one
    /// counter that drives BOTH the ● / ○ blink and the "check #n" a player reads as progress. A
    /// paused app made no check, so neither moves.
    /// </summary>
    [Fact]
    public void The_heartbeat_and_the_check_number_freeze_while_paused()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var body = BracedBlock(live, live.IndexOf("if (pause.AllPaused)", StringComparison.Ordinal));

        Assert.DoesNotContain("LiveIndicator", body);
        Assert.DoesNotContain("_liveTicks++", body);
        // …and SetLiveUi is not the way to write a status from the loop: it resets the indicator to
        // "●  LIVE" on every call, which would un-freeze the heartbeat it is meant to freeze.
        Assert.DoesNotContain("SetLiveUi", body);
    }

    /// <summary>
    /// The pause reaches BOTH surfaces (AC 1) and it reaches them through <c>SetScreenStatus</c>,
    /// which already writes <c>ScreenReadStatus</c> and the overlay — so the loop never has to touch
    /// <c>CompactOverlay</c> itself, whose own 600 ms blink timer belongs to E7.S4.
    /// </summary>
    [Fact]
    public void The_paused_status_goes_out_through_the_two_surface_helper()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var body = BracedBlock(live, live.IndexOf("if (pause.AllPaused)", StringComparison.Ordinal));

        Assert.Contains("SetScreenStatus(LivePausedStatus(", body);
        Assert.DoesNotContain("_overlay", body);
    }

    /// <summary>
    /// A skipped tick waits the back-off in full, and the loop adds <b>no timer</b> to do it: the
    /// countdown is repainted by the skipped tick itself. "Nothing may lag the game" is the product
    /// requirement, and a <c>DispatcherTimer</c> ticking faster than the loop would be the obvious
    /// way to break it. E7.S2 adds the 1 Hz one deliberately.
    /// </summary>
    [Fact]
    public void The_paused_loop_adds_no_timer_of_its_own()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        Assert.DoesNotContain("DispatcherTimer", live);
        Assert.Contains("int wait = pausedWait", live);
    }

    /// <summary>
    /// Two properties of the wait itself, which the scan above only names.
    ///
    /// <list type="bullet">
    /// <item><b>The skipped tick is what sets it</b> — the branch computes the back-off AND advances
    ///       the step counter, so the curve really is per skipped tick and not per second.</item>
    /// <item><b>Stop during a five-second back-off exits at once</b>: the wait is awaited on the
    ///       loop's own token. A player who presses Stop 4 s into a paused wait must not watch a
    ///       dead LIVE indicator until the wait elapses — <c>StopLive</c> has already run
    ///       <c>SetLiveUi(false)</c> on the dispatcher, and nothing after the break writes a status
    ///       over its "Live stopped." (R-02 again, in the longest window this story introduces).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_skipped_tick_sets_the_wait_and_the_wait_is_cancellable()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var body = BracedBlock(live, live.IndexOf("if (pause.AllPaused)", StringComparison.Ordinal));

        Assert.Contains("pausedWait = LiveTickPolicy.BackoffWaitMs(CurrentLiveIntervalMs(), backoffSteps)",
                        body, StringComparison.Ordinal);
        Assert.Contains("backoffSteps = LiveTickPolicy.NextBackoffSteps(backoffSteps, LiveTickOutcome.Paused)",
                        body, StringComparison.Ordinal);

        Assert.Contains("await Task.Delay(wait, ct);", live, StringComparison.Ordinal);
        Assert.Contains("catch (TaskCanceledException) { break; }", live, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>AC 5 / T7.</b> The back-off multiplies the WAIT; the pure slider mapping is untouched, so
    /// <c>DefaultsAndResizeTests</c> and <c>LiveDefaultsTests</c> stay green with zero edits. Pinned
    /// here as well because "while we're here, let's fold the back-off into LiveIntervalMs" is a
    /// plausible refactor that would silently move the shipped 700 ms default.
    /// </summary>
    [Fact]
    public void The_pure_interval_mapping_is_not_where_the_back_off_lives()
    {
        Assert.Equal(700, MainWindow.LiveIntervalMs(92));
        Assert.Equal(3000, MainWindow.LiveIntervalMs(0));
        Assert.Equal(500, MainWindow.LiveIntervalMs(100));
    }

    // ---- TP-LIVE-15: the dedup clock is frozen while paused ------------------------------------

    /// <summary>
    /// <b>AC 3, and it is the behaviour the story exists for.</b> <c>LiveDedup</c>'s internal frame
    /// counter advances only inside <c>Next</c>, so a pause that never calls it genuinely freezes
    /// the dedup clock: a message that sat on screen throughout a twenty-tick outage is still
    /// suppressed when the feed resumes — it was already translated, and it has not scrolled off.
    ///
    /// <para>The contrast is the whole test. The second half runs the SAME twenty ticks <i>through</i>
    /// <c>Next</c>, as an app that kept OCR-ing while it could not translate would: the entry ages
    /// out, the message reads as new, and it is translated a second time. That second translation is
    /// the burned row OQ-B's full pause removes — and it is why "OCR is local and free" was the
    /// wrong answer.</para>
    /// </summary>
    [Fact]
    public void TP_LIVE_15_a_message_that_stayed_on_screen_through_a_pause_is_not_retranslated()
    {
        const double match = 0.85, confirm = 0.70;
        const string line = "proBlemka: ТС ЛЕГА 2 ДД";

        var paused = new LiveDedup();
        paused.Next(new[] { line }, match, confirm);                      // first sight
        Assert.Single(paused.Next(new[] { line }, match, confirm));       // confirmed and translated
        // …twenty skipped ticks happen here. Next is NOT called — that is the entire mechanism.
        Assert.Empty(paused.Next(new[] { line }, match, confirm));        // still the same message

        var kept = new LiveDedup();
        kept.Next(new[] { line }, match, confirm);
        Assert.Single(kept.Next(new[] { line }, match, confirm));
        for (int i = 0; i < 20; i++) kept.Next(Array.Empty<string>(), match, confirm);
        kept.Next(new[] { line }, match, confirm);
        Assert.Single(kept.Next(new[] { line }, match, confirm));         // burned again
    }

    // ---- E5.S2: the loop half of the honest auto-stop -------------------------------------------

    /// <summary>
    /// <b>The one line E5.S2 exists to delete.</b> <c>consecutiveErrors = 0</c> ran on every
    /// non-throwing tick, empty ones included, so a calm chat forgave a real failure streak between
    /// two failures and the auto-stop never reached two (<c>analyse…</c> A2, S4c). The counter is
    /// gone from the loop entirely: what the loop keeps is one <c>LiveErrorTracker</c>, fed the same
    /// <c>LiveTickOutcome</c> that drives E5.S1's back-off — two independent notions of "a good
    /// tick" is precisely how that line became a bug.
    /// </summary>
    [Fact]
    public void The_reset_that_forgave_every_calm_tick_is_gone()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        Assert.DoesNotContain("consecutiveErrors", live);
        Assert.Contains("var errors = new LiveErrorTracker();", live, StringComparison.Ordinal);
        // ONE outcome, read by both counters, on adjacent lines.
        Assert.Contains("errors.Record(outcome);", live, StringComparison.Ordinal);
        Assert.Contains("backoffSteps = LiveTickPolicy.NextBackoffSteps(backoffSteps, outcome);",
                        live, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ruling E5-c at the loop.</b> The generic <c>catch</c> no longer decides anything about the
    /// exception itself: it hands it to <see cref="LiveTickPolicy.Classify"/>, which is where "this
    /// failure cost a request" is written down once, and reads the tracker's verdict instead of
    /// running its own <c>++n &gt;= 5</c>. Without this the auto-stop counts refusals — a dead cable
    /// with a busy chat would stop LIVE for the system working correctly (R-02 / R6).
    /// </summary>
    [Fact]
    public void The_catch_classifies_the_failure_before_it_counts_it()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        Assert.Contains("if (errors.Record(LiveTickPolicy.Classify(ex)))", live, StringComparison.Ordinal);
        // The old hard-coded trigger is gone with it: five is a graded constant now, read inside
        // LiveErrorTracker and nowhere else.
        Assert.DoesNotContain(">= 5", live);
        // I3 is untouched — a timeout must still fall through to the counted handler.
        Assert.Contains("catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }",
                        live, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stop path itself is unchanged and deliberately so: <c>StopLive()</c> first (it writes
    /// "Live stopped.") and then the real reason over it, composed from the last failure's own
    /// sentence.
    ///
    /// <para><b>E7.S1 did the copy pass</b> (amendment A6, ruling E5-d): the wrapper is now
    /// <c>UserMessages.LiveAutoStopped(n, reason)</c> — "repeated errors" was the app declining to
    /// say how many, and ▶ was nowhere in the sentence. The ORDER this case exists for is
    /// untouched, and the needle moved with the wrapper rather than being deleted.</para>
    /// </summary>
    [Fact]
    public void The_stop_still_names_the_reason_and_leaves_the_ui_cleaned_up()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        int reason = live.IndexOf("SetScreenStatus(UserMessages.LiveAutoStopped(",
                                  StringComparison.Ordinal);
        Assert.True(reason > 0, "the auto-stop must still name the reason it stopped for");

        int stop = live.LastIndexOf("StopLive();", reason, StringComparison.Ordinal);
        Assert.True(stop > 0 && reason - stop < 200,
                    "StopLive() must run immediately before the reason is written over its \"Live stopped.\"");

        // …and the bug report is written BEFORE the UI is cleaned up, with the exception in hand.
        // Scoped to the stop block: an unscoped Contains would be satisfied by any other log call
        // in the file and would say nothing about the path this test is named for.
        int log = live.LastIndexOf("Services.Logging.Error(", stop, StringComparison.Ordinal);
        Assert.True(log > 0 && stop - log < 400,
                    "the auto-stop must log the failure it stopped for, next to the StopLive() call");
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>The braced block that starts at the first <c>{</c> at or after
    /// <paramref name="from"/>, brace-matched — so the scan reads the skipped branch and not the
    /// rest of the loop.</summary>
    private static string BracedBlock(string source, int from)
    {
        Assert.True(from >= 0, "the branch this scan is about was not found in MainWindow.Live.cs");
        int open = source.IndexOf('{', from);
        Assert.True(open >= 0, "no block after the branch");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail("the branch's block is not closed");
        return "";
    }

    /// <summary>Code only — a <c>//</c> mention is prose, and the skipped branch is heavily
    /// commented precisely about the four things it must not do.</summary>
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

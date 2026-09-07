using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// TP-LIVE-02 / TP-LIVE-03 — the arithmetic of a paused LIVE loop, at L1. The loop itself is
/// code-behind and needs a window; the DECISIONS it makes are pure by construction (test-plan §3.7),
/// which is the whole reason <see cref="LiveTickPolicy"/> exists. Nothing here sleeps, reaches a
/// gate or touches a file.
/// </summary>
public class LiveTickPolicyTests
{
    // ---- TP-LIVE-02: the back-off curve, ×2 per skipped tick, capped ---------------------------

    /// <summary>
    /// The shipped speed (92% → 700 ms, pinned by <c>DefaultsAndResizeTests</c>) walked through
    /// eight skipped ticks. The values are the ones the story's manual verification watches on a
    /// stopwatch: 0.7 s → 1.4 → 2.8 → 5 → 5…
    /// </summary>
    [Theory]
    [InlineData(0, 700)]
    [InlineData(1, 1400)]
    [InlineData(2, 2800)]
    [InlineData(3, 5000)]   // 5600, capped
    [InlineData(4, 5000)]
    [InlineData(5, 5000)]
    [InlineData(6, 5000)]
    [InlineData(7, 5000)]
    public void TP_LIVE_02_the_wait_doubles_per_skipped_tick_and_stops_at_the_cap(int steps, int expected)
        => Assert.Equal(expected, LiveTickPolicy.BackoffWaitMs(700, steps));

    /// <summary>
    /// <b>The one arithmetic trap in this story.</b> C# masks a shift count to its low five bits, so
    /// an unclamped <c>interval &lt;&lt; 32</c> is <c>interval &lt;&lt; 0</c> — the 32nd skipped tick
    /// would silently drop back to a 700 ms busy-wait after nine minutes of pause. The 20-step clamp
    /// is what stops it, and 40 is well past the point where an unclamped shift wraps.
    ///
    /// <para>The second half is the overflow: <c>3000 &lt;&lt; 20</c> does not fit in an
    /// <see cref="int"/> and becomes NEGATIVE, which <c>Math.Min</c> would then prefer to the cap.
    /// Both readings of "the back-off silently stopped backing off" are pinned here.</para>
    /// </summary>
    [Theory]
    [InlineData(700, 32)]
    [InlineData(700, 40)]
    [InlineData(700, int.MaxValue)]
    [InlineData(3000, 20)]      // the slowest slider setting, at the clamp — must not overflow
    [InlineData(3000, 64)]
    public void TP_LIVE_02_a_very_long_pause_never_falls_back_to_the_base_interval(int interval, int steps)
        => Assert.Equal(TranslationPolicy.LiveBackoffCapMs, LiveTickPolicy.BackoffWaitMs(interval, steps));

    /// <summary>A negative step count (nothing produces one, but the clamp is two-sided) reads as
    /// "no back-off yet" rather than as a right shift.</summary>
    [Fact]
    public void A_negative_step_count_is_simply_the_base_interval()
        => Assert.Equal(700, LiveTickPolicy.BackoffWaitMs(700, -3));

    /// <summary>
    /// The value leaves this method and goes STRAIGHT into <c>Task.Delay</c> — the one place in the
    /// loop that is outside its <c>try</c>. A negative would throw there, faulting the
    /// fire-and-forget loop task with no <c>SetLiveUi(false)</c> behind it: the R-02 zombie
    /// indicator, reached through arithmetic rather than through a gate. Nothing reachable produces
    /// a negative interval (the speed slider is clamped to 0–100 → 500–3000 ms), which is exactly
    /// why the floor has to be asserted rather than assumed (review, E5.S1).
    /// </summary>
    [Theory]
    [InlineData(-700, 0)]
    [InlineData(-700, 3)]
    [InlineData(0, 5)]
    public void The_wait_can_never_be_negative_or_a_busy_loop(int interval, int steps)
        => Assert.Equal(LiveTickPolicy.MinWaitMs, LiveTickPolicy.BackoffWaitMs(interval, steps));

    /// <summary>The cap is the graded constant and not a literal repeated in the loop — §9.1's
    /// number, in the one table this project argues about numbers in.</summary>
    [Fact]
    public void The_cap_is_five_seconds()
        => Assert.Equal(5000, TranslationPolicy.LiveBackoffCapMs);

    // ---- TP-LIVE-03: what resets the back-off, and what deliberately does not -------------------

    /// <summary>
    /// AC 4, as the sequence the loop really runs: five skipped ticks, then one that translated.
    /// The reset is total — the next pause starts again at the plain interval, not at 5 s.
    /// </summary>
    [Fact]
    public void TP_LIVE_03_a_tick_that_translated_resets_the_back_off()
    {
        int steps = 0;
        for (int i = 0; i < 5; i++) steps = LiveTickPolicy.NextBackoffSteps(steps, LiveTickOutcome.Paused);
        Assert.Equal(5, steps);
        Assert.Equal(5000, LiveTickPolicy.BackoffWaitMs(700, steps));

        steps = LiveTickPolicy.NextBackoffSteps(steps, LiveTickOutcome.Translated);
        Assert.Equal(0, steps);
        Assert.Equal(700, LiveTickPolicy.BackoffWaitMs(700, steps));
    }

    /// <summary>
    /// The other half of AC 4, and the one that is easy to get wrong: an EMPTY tick leaves the
    /// counter alone. A calm chat asked the providers nothing, so it is no evidence that they came
    /// back — resetting on it would make a paused-but-quiet app climb back to a 700 ms wake-up
    /// every time the screen held still. Same rule for a tick that threw: that is E5.S2's counter.
    /// </summary>
    /// <para>An <c>InlineData</c> per outcome would be the natural shape and does not compile: the
    /// enum is <c>internal</c> (I2 — it is loop policy, not API) and an xUnit test method is public,
    /// so the parameter would be less accessible than the method. Two asserts, one case.</para>
    [Fact]
    public void TP_LIVE_03_an_empty_or_throwing_tick_leaves_the_back_off_exactly_where_it_was()
    {
        Assert.Equal(3, LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.Empty));
        Assert.Equal(3, LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.SentFailure));
    }

    /// <summary>A session left paused overnight must not wrap the counter negative — the clamp is
    /// the same one <see cref="LiveTickPolicy.BackoffWaitMs"/> relies on.</summary>
    [Fact]
    public void The_step_counter_stops_climbing_at_the_shift_ceiling()
    {
        int steps = 0;
        for (int i = 0; i < 5000; i++) steps = LiveTickPolicy.NextBackoffSteps(steps, LiveTickOutcome.Paused);
        Assert.Equal(LiveTickPolicy.MaxBackoffShift, steps);
        Assert.Equal(TranslationPolicy.LiveBackoffCapMs, LiveTickPolicy.BackoffWaitMs(700, steps));
    }

    // ---- the countdown, as a number (the sentence is the code-behind's) ------------------------

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_countdown_rounds_up_so_the_last_second_is_never_zero()
    {
        Assert.Equal(5, LiveTickPolicy.CountdownSeconds(Now + TimeSpan.FromSeconds(5), Now));
        Assert.Equal(5, LiveTickPolicy.CountdownSeconds(Now + TimeSpan.FromMilliseconds(4200), Now));
        Assert.Equal(1, LiveTickPolicy.CountdownSeconds(Now + TimeSpan.FromMilliseconds(1), Now));
    }

    /// <summary>
    /// The three instants that must NOT render as a number. A window already elapsed (the loop can
    /// see one between the skip and the repaint) and a null are "no countdown"; so is
    /// <see cref="DateTimeOffset.MaxValue"/> — the <c>AuthFailed</c> sentinel, whose real exit is
    /// re-saving the key. Rendering that one raw is the "next try in 5843 h" bug.
    /// </summary>
    [Fact]
    public void There_is_no_countdown_for_a_null_a_past_or_the_never_sentinel()
    {
        Assert.Null(LiveTickPolicy.CountdownSeconds(null, Now));
        Assert.Null(LiveTickPolicy.CountdownSeconds(Now, Now));
        Assert.Null(LiveTickPolicy.CountdownSeconds(Now - TimeSpan.FromMinutes(1), Now));
        Assert.Null(LiveTickPolicy.CountdownSeconds(DateTimeOffset.MaxValue, Now));
        Assert.Null(LiveTickPolicy.CountdownSeconds(Now + TimeSpan.FromHours(2), Now));
    }

    /// <summary>A 30-minute window — the longest the breaker can open for — still counts down.</summary>
    [Fact]
    public void The_longest_breaker_window_still_shows_a_number()
        => Assert.Equal(1800, LiveTickPolicy.CountdownSeconds(Now + TimeSpan.FromMinutes(30), Now));

    // ---- the sentence a skipped tick shows ------------------------------------------------------

    /// <summary>
    /// The paused status is <c>internal static</c> on <c>MainWindow</c> for the same reason
    /// <c>LiveIntervalMs</c> is: the copy a player reads is assertable without an STA host. It is a
    /// LIVE status and not a copy-deck sentence, so it does not join <c>UserMessages</c>' table —
    /// which is keyed by <c>TranslationErrorKind</c> and, by its own house rules, holds no
    /// countdown.
    /// </summary>
    [Fact]
    public void The_paused_status_names_the_pause_and_promises_the_recovery()
    {
        Assert.Equal("○ Live — paused, next try in 4 s. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(4));
        Assert.Equal("○ Live — paused, next try in 1 min. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(60));
        Assert.Equal("○ Live — paused, next try in 30 min. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(1800));
        // No number to promise → no number invented, and the reassurance still stands.
        Assert.Equal("○ Live — paused. It resumes on its own; nothing is lost.",
                     MainWindow.LivePausedStatus(null));
    }

    /// <summary>
    /// Two house rules the feed's own statuses live by, applied to the new one: it never starts with
    /// "(" (I4's marker for "this is a failure, not a translation"), and it carries the frozen ○ of
    /// the heartbeat rather than the running 🔴 — the status a stopped loop shows must not look like
    /// the status a working one shows.
    /// </summary>
    [Fact]
    public void The_paused_status_is_not_dressed_as_a_running_loop()
    {
        foreach (var s in new[] { MainWindow.LivePausedStatus(4), MainWindow.LivePausedStatus(null) })
        {
            Assert.DoesNotContain("🔴", s);
            Assert.StartsWith("○", s);
            Assert.Contains("paused", s);
        }
    }

    // ---- E5.S2: the auto-stop, as a pure decision over an injected clock -----------------------
    // TP-LIVE-04..09. Everything below drives LiveErrorTracker directly with a clock this file
    // moves by hand: no Task.Delay, no wall clock, no window (IS-6 / CI-3). The tracker is an
    // INSTANCE, so two cases in one run cannot share a counter.

    private static LiveErrorTracker Tracker() => new();

    /// <summary>The tick shapes as the loop produces them, so a case reads like the session it
    /// describes. <c>Fail</c> is a SENT request that failed — the only outcome that counts
    /// (ruling E5-c).</summary>
    private static bool Fail(LiveErrorTracker t, DateTimeOffset now)
        => t.Record(LiveTickOutcome.SentFailure, now);

    /// <summary>
    /// <b>TP-LIVE-04.</b> A tick that translated is the only evidence the providers are actually
    /// working, and it is total: the streak goes and so does every stamp behind it.
    /// </summary>
    [Fact]
    public void TP_LIVE_04_a_tick_that_translated_clears_the_window()
    {
        var t = Tracker();
        for (int i = 0; i < 4; i++) Assert.False(Fail(t, Now + TimeSpan.FromSeconds(i)));
        Assert.Equal(4, t.RecentFailures);

        Assert.False(t.Record(LiveTickOutcome.Translated, Now + TimeSpan.FromSeconds(5)));
        Assert.Equal(0, t.RecentFailures);

        // …and the five that follow start from zero: a player who saw one good tick gets a clean
        // five, not one.
        for (int i = 6; i < 10; i++) Assert.False(Fail(t, Now + TimeSpan.FromSeconds(i)));
        Assert.True(Fail(t, Now + TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// <b>TP-LIVE-05 — the whole bug, as one assertion.</b> Named after the behaviour and not after
    /// the story: <c>MainWindow.Live.cs</c> used to reset the counter at the end of every
    /// non-throwing tick, so in a calm chat the loop alternated "empty, failing, empty, failing" and
    /// the auto-stop never reached two (<c>analyse…</c> A2, scenario S4c). An empty tick asked the
    /// providers nothing; it is evidence of nothing.
    /// </summary>
    [Fact]
    public void TP_LIVE_05_an_empty_tick_does_not_forgive_a_failure_streak()
    {
        var t = Tracker();
        bool stopped = false;
        for (int i = 0; i < 5 && !stopped; i++)
        {
            stopped = Fail(t, Now + TimeSpan.FromSeconds(i * 2));
            if (!stopped) Assert.False(t.Record(LiveTickOutcome.Empty, Now + TimeSpan.FromSeconds(i * 2 + 1)));
        }
        Assert.True(stopped);          // before E5.S2 this loop ran for ever
        Assert.Equal(5, t.RecentFailures);
    }

    /// <summary>
    /// <b>TP-LIVE-06 / AC 3.</b> A gate-open tick and a refusal inside a tick are the system working
    /// correctly — neither a success nor a failure (§9.2's closing rule, ruling E5-c). They leave
    /// the queue exactly where it was: they do not clear a real streak, and they do not feed it.
    /// </summary>
    [Fact]
    public void TP_LIVE_06_a_pause_or_a_refusal_is_neither_a_success_nor_a_failure()
    {
        var t = Tracker();
        for (int i = 0; i < 3; i++) Assert.False(Fail(t, Now + TimeSpan.FromSeconds(i)));

        Assert.False(t.Record(LiveTickOutcome.Paused, Now + TimeSpan.FromSeconds(4)));
        Assert.False(t.Record(LiveTickOutcome.Refused, Now + TimeSpan.FromSeconds(5)));
        Assert.Equal(3, t.RecentFailures);   // not cleared…

        Assert.False(t.Record(LiveTickOutcome.Paused, Now + TimeSpan.FromSeconds(6)));
        Assert.Equal(3, t.RecentFailures);   // …and not counted
    }

    /// <summary>
    /// <b>TP-LIVE-09, R-02 / R6.</b> The failure mode this rule exists to prevent: an app that stops
    /// itself because it was correctly waiting. A hundred paused ticks and a hundred refused ones
    /// leave LIVE running, whatever order they arrive in.
    /// </summary>
    [Fact]
    public void TP_LIVE_09_a_hundred_paused_or_refused_ticks_never_auto_stop_live()
    {
        var t = Tracker();
        for (int i = 0; i < 100; i++)
        {
            Assert.False(t.Record(LiveTickOutcome.Paused, Now + TimeSpan.FromSeconds(i)));
            Assert.False(t.Record(LiveTickOutcome.Refused, Now + TimeSpan.FromSeconds(i)));
        }
        Assert.Equal(0, t.RecentFailures);
    }

    /// <summary>
    /// <b>TP-LIVE-07.</b> A failing tick stamps the clock it was handed, and stamps older than the
    /// window age out on the NEXT record whatever that record is — a session that fails four times,
    /// runs clean for an hour and fails once more must not stop.
    /// </summary>
    [Fact]
    public void TP_LIVE_07_stamps_older_than_the_window_are_trimmed()
    {
        var t = Tracker();
        for (int i = 0; i < 4; i++) Assert.False(Fail(t, Now + TimeSpan.FromSeconds(i)));
        Assert.Equal(4, t.RecentFailures);

        // An hour later, one ordinary hiccup. The four are long gone, so this is failure #1 and not
        // failure #5 — the "a stale error streak survives an arbitrarily long pause" finding
        // E5.S1's review recorded for this story.
        Assert.False(Fail(t, Now + TimeSpan.FromHours(1)));
        Assert.Equal(1, t.RecentFailures);
    }

    /// <summary>
    /// The window's edge, asserted from both sides so the comparison cannot silently become the
    /// other one. A stamp exactly <see cref="TranslationPolicy.LiveAutoStopWindowSeconds"/> old is
    /// still inside the window; a millisecond past it is not.
    /// </summary>
    [Fact]
    public void The_window_edge_is_inclusive_and_one_millisecond_past_it_is_not()
    {
        var window = TimeSpan.FromSeconds(TranslationPolicy.LiveAutoStopWindowSeconds);

        var onTime = Tracker();
        for (int i = 0; i < 4; i++) Assert.False(Fail(onTime, Now));
        Assert.True(Fail(onTime, Now + window));

        var justLate = Tracker();
        for (int i = 0; i < 4; i++) Assert.False(Fail(justLate, Now));
        Assert.False(Fail(justLate, Now + window + TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, justLate.RecentFailures);
    }

    /// <summary>
    /// <b>TP-LIVE-08 — the two shapes that decide whether this story shipped anything.</b> Five sent
    /// failures inside ten seconds is the streak users already know, and it still stops LIVE. Five
    /// spread over three minutes is a flaky evening rather than a broken app, and it does not: the
    /// window is what tells them apart, and without it the first shape is the only one the counter
    /// could ever describe.
    /// </summary>
    [Fact]
    public void TP_LIVE_08_five_failures_in_ten_seconds_stop_live_and_five_over_three_minutes_do_not()
    {
        var burst = Tracker();
        for (int i = 0; i < 4; i++) Assert.False(Fail(burst, Now + TimeSpan.FromSeconds(i * 2)));
        Assert.True(Fail(burst, Now + TimeSpan.FromSeconds(8)));

        var trickle = Tracker();
        for (int i = 0; i < 5; i++)
            Assert.False(Fail(trickle, Now + TimeSpan.FromSeconds(i * 45)));   // 0, 45, 90, 135, 180
        // Only the last three are inside the two minutes that end at 180 s.
        Assert.Equal(3, trickle.RecentFailures);
    }

    /// <summary>
    /// The queue is bounded as well as trimmed: a pathological burst inside one window cannot grow
    /// it. Five stamps are enough to decide, and the bound is a little above that so the decision
    /// never depends on the bound.
    /// </summary>
    [Fact]
    public void The_stamp_queue_is_bounded()
    {
        var t = Tracker();
        for (int i = 0; i < 500; i++) Fail(t, Now + TimeSpan.FromMilliseconds(i));
        Assert.True(t.RecentFailures <= LiveTickPolicy.MaxErrorStamps,
                    $"the queue grew to {t.RecentFailures} stamps");
        Assert.True(LiveTickPolicy.MaxErrorStamps >= TranslationPolicy.LiveAutoStopThreshold,
                    "the bound must never be able to hide the fifth failure");
    }

    /// <summary>Two LIVE sessions in one run must not share a counter — the reason the tracker is an
    /// instance and not a static. <c>Reset</c> is what <c>StartLive</c> gets for free by building a
    /// new one, asserted here as the property it is.</summary>
    [Fact]
    public void Reset_gives_the_next_session_a_clean_five()
    {
        var t = Tracker();
        for (int i = 0; i < 4; i++) Fail(t, Now + TimeSpan.FromSeconds(i));
        t.Reset();
        Assert.Equal(0, t.RecentFailures);

        for (int i = 0; i < 4; i++) Assert.False(Fail(t, Now + TimeSpan.FromSeconds(10 + i)));
        Assert.True(Fail(t, Now + TimeSpan.FromSeconds(14)));

        // A second session, built the way StartLive builds one, starts at zero and knows nothing
        // about the streak that stopped the first.
        Assert.Equal(0, Tracker().RecentFailures);
    }

    // ---- ruling E5-c: what counts is a request that was actually SENT ---------------------------

    /// <summary>
    /// <b>Ruling E5-c, as the one function both the loop and this test read.</b> A gate refusal cost
    /// no request — <see cref="TranslationException.NotSent"/> — and an
    /// <c>AllProvidersPaused</c> is the chain saying every tier was inside a window. Neither is an
    /// error, however it arrives; both can reach the loop's generic <c>catch</c> because a gate can
    /// open between E5.S1's pre-tick <c>PauseNow()</c> check and the request.
    /// </summary>
    [Fact]
    public void A_refusal_that_cost_no_request_is_classified_as_refused_not_as_a_failure()
    {
        Assert.Equal(LiveTickOutcome.Refused, LiveTickPolicy.Classify(
            new TranslationException(TranslationErrorKind.AllProvidersPaused, "every tier is paused")));
        // The subtle one: a REAL kind (the gate's last one), raised without sending anything.
        Assert.Equal(LiveTickOutcome.Refused, LiveTickPolicy.Classify(
            new TranslationException(TranslationErrorKind.RateLimited, "refused by the gate") { NotSent = true }));
    }

    /// <summary>
    /// The other half, and the one that makes the auto-stop worth having: a failure that cost a real
    /// request counts, whatever kind it carries and whether or not it was ever classified.
    ///
    /// <para><b>I3 / R-03 lives on this line.</b> A 12 s HttpClient timeout arrives as a
    /// <see cref="TaskCanceledException"/> with the loop's token NOT cancelled; the loop's filtered
    /// catch lets it fall through to here, and if this classified it as anything but a sent failure
    /// the auto-stop would quietly stop working for the single most common outage there is
    /// (TP-LIVE-16 pins the loop half).</para>
    /// </summary>
    [Fact]
    public void A_failure_that_cost_a_request_counts_whatever_it_carries()
    {
        Assert.Equal(LiveTickOutcome.SentFailure, LiveTickPolicy.Classify(
            new TranslationException(TranslationErrorKind.RateLimited, "429")));
        Assert.Equal(LiveTickOutcome.SentFailure, LiveTickPolicy.Classify(
            new TranslationException(TranslationErrorKind.Timeout, "took too long")));
        Assert.Equal(LiveTickOutcome.SentFailure, LiveTickPolicy.Classify(new TaskCanceledException()));
        Assert.Equal(LiveTickOutcome.SentFailure, LiveTickPolicy.Classify(new InvalidOperationException("boom")));
    }

    /// <summary>
    /// The dead-cable shape, end to end over the classifier: every read tier is inside a window, so
    /// the chain refuses from inside the tick, twenty times running. LIVE keeps waiting and never
    /// stops itself — E5-c's whole point, and the behaviour E2.S5's "5 refused ticks in ~3 s ⇒
    /// auto-stop" escalation used to get wrong in the opposite direction.
    /// </summary>
    [Fact]
    public void Twenty_refusals_from_inside_the_tick_do_not_auto_stop_live()
    {
        var t = Tracker();
        var refused = new TranslationException(TranslationErrorKind.AllProvidersPaused, "every tier is paused");
        for (int i = 0; i < 20; i++)
            Assert.False(t.Record(LiveTickPolicy.Classify(refused), Now + TimeSpan.FromSeconds(i)));
        Assert.Equal(0, t.RecentFailures);
    }

    /// <summary>The new outcomes leave E5.S1's back-off exactly where it was: only a skipped tick
    /// advances it and only a translated one clears it. A refusal is not a skipped tick — the tick
    /// ran, captured and OCR-ed — so it does not advance the curve either.</summary>
    [Fact]
    public void The_error_outcomes_do_not_disturb_the_back_off()
    {
        Assert.Equal(3, LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.SentFailure));
        Assert.Equal(3, LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.Refused));
    }

    /// <summary>The two numbers behind the rule are the graded ones, read from the table this
    /// project argues about numbers in — never a literal in the loop.</summary>
    [Fact]
    public void The_rule_is_five_failures_inside_two_minutes()
    {
        Assert.Equal(5, TranslationPolicy.LiveAutoStopThreshold);
        Assert.Equal(120, TranslationPolicy.LiveAutoStopWindowSeconds);
    }
}

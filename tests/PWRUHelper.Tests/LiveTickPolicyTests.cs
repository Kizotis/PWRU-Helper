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
        Assert.Equal(3, LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.Threw));
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
}

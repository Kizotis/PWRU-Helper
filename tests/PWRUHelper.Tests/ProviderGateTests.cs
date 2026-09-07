using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The breaker, row by row against `architecture-cible.md` §5.2/§5.3. Every window, escalation,
/// cooldown and clean-reset assertion drives the injected clock — there is no `Task.Delay` and no
/// wall-clock read in this file (CI-3). A test that sleeps for a 30-minute window is not a test.
///
/// The numbers are asserted as RELATIONSHIPS to `TranslationPolicy` (the window doubles, the cap
/// holds, the cooldown is `SoftCooldownSecs`) and not as literals, because all six constants are
/// [ASSUMED] and E2.S7 will tune them from field logs (U9). Where a literal is unavoidable it says
/// so; those are the lines E2.S7 must read.
///
/// `ProviderGate` holds no static state, so these cases construct one directly and need no
/// collection. They move into E2.S2's non-parallel `Gates` collection (IS-5) with the registry,
/// which does have static state.
/// </summary>
public class ProviderGateTests
{
    // ---- the fake clock (IS-6) ---------------------------------------------------------------

    private sealed class FakeClock
    {
        // A fixed instant, not "now": nothing in this file may depend on when it runs.
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
        public void AdvanceSeconds(double s) => Advance(TimeSpan.FromSeconds(s));
        public void AdvanceMinutes(double m) => Advance(TimeSpan.FromMinutes(m));
    }

    private static (ProviderGate Gate, FakeClock Clock) NewGate(GatePolicy? policy = null)
    {
        var clock = new FakeClock();
        return (new ProviderGate(clock.Read, policy), clock);
    }

    private static readonly TimeSpan Base = TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(TranslationPolicy.OpenCapMinutes);
    // Read from the gate rather than re-derived here: since E2.S5 a probe covers a whole logical
    // call (MaxAttempts requests plus the back-off between them), and a mirror of the old
    // one-request formula would have drifted silently.
    private static readonly TimeSpan ProbeTimeout = ProviderGate.ProbeTimeout;

    // §5.4's rate ceiling, as relationships rather than literals (U9: E2.S7 tunes all four).
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(TranslationPolicy.MinSpacingMs);
    private static readonly TimeSpan ProbeDefer = TimeSpan.FromMilliseconds(TranslationPolicy.ProbeDeferMs);
    private static readonly int Capacity = TranslationPolicy.BucketCapacity;

    /// <summary>Asserts the decision is `Open` and hands back its `retryAt`, so a wrong outcome
    /// reads as "the gate let a request through", not as a comparison of two default dates.</summary>
    private static DateTimeOffset OpenUntil(GateDecision d)
    {
        Assert.Equal(GateOutcome.Open, d.Outcome);
        return d.RetryAt;
    }

    /// <summary>Walks an open gate to its probe: advance to `blockedUntil`, take the single probe.
    /// The half-open state is reached the only way production reaches it. Interactive, because
    /// §5.4 defers a `Background` caller by `ProbeDeferMs` and these cases are about the breaker.
    /// <para>Hands back the probe's <b>token</b> (ruling E2-h): only a report quoting it may close
    /// the gate or release the latch, so every case that resolves a probe must pass it back — which
    /// is exactly what `HttpProviderCore` (E2.S5) will do.</para></summary>
    private static long TakeProbe(ProviderGate gate, FakeClock clock)
    {
        var until = gate.Snapshot().BlockedUntil;
        Assert.NotNull(until);
        if (clock.Now < until!.Value) clock.Advance(until.Value - clock.Now);

        var decision = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, decision.Outcome);
        Assert.NotEqual(0, decision.ProbeToken);
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
        return decision.ProbeToken;
    }

    // ---- TP-GATE-01 / 02: it opens, and it stays open ----------------------------------------

    [Fact]
    public void TP_GATE_01_RateLimited_opens_the_gate_for_the_base_window()
    {
        var (gate, clock) = NewGate();
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);

        gate.ReportFailure(TranslationErrorKind.RateLimited);

        var snap = gate.Snapshot();
        Assert.Equal(GateState.Open, snap.State);
        Assert.Equal(clock.Now + Base, snap.BlockedUntil);
        Assert.Equal(1, snap.Strikes);
        Assert.Equal(TranslationErrorKind.RateLimited, snap.LastKind);
        Assert.Equal(clock.Now + Base, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));
    }

    [Fact]
    public void TP_GATE_02_Stays_open_with_the_same_retryAt_until_the_window_passes()
    {
        var (gate, clock) = NewGate();
        var t0 = clock.Now;
        gate.ReportFailure(TranslationErrorKind.Blocked);   // the other opening kind, same row

        clock.Advance(Base / 2);
        Assert.Equal(t0 + Base, OpenUntil(gate.TryEnter(RequestPriority.Background)));

        // One second short of the window is still open: the boundary is where R-01 hides.
        clock.Advance(Base / 2 - TimeSpan.FromSeconds(1));
        Assert.Equal(t0 + Base, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));
        Assert.Equal(GateState.Open, gate.Snapshot().State);
    }

    // ---- TP-GATE-03: exactly one probe -------------------------------------------------------

    [Fact]
    public void TP_GATE_03_At_blockedUntil_exactly_one_caller_gets_the_probe()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base);                                 // exactly at blockedUntil, not past it

        var first = gate.TryEnter(RequestPriority.Interactive);
        var second = gate.TryEnter(RequestPriority.Background);
        var third = gate.TryEnter(RequestPriority.Interactive);

        Assert.Equal(GateOutcome.Probe, first.Outcome);
        Assert.Equal(clock.Now + ProbeTimeout, OpenUntil(second));
        Assert.Equal(clock.Now + ProbeTimeout, OpenUntil(third));
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
    }

    [Fact]
    public void TP_GATE_03_Only_one_of_many_real_threads_gets_the_probe()
    {
        // The lock, exercised for real. Deterministic despite the threads: the assertion is a
        // count, never a timing. Both chains and the LIVE loop share one gate (I9), so "exactly
        // one" has to survive genuine concurrency, not just two sequential calls.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base + ProbeDefer);   // past the §5.4 defer window, so Background may take it

        // The clock is NOT advanced inside the parallel section: a FakeClock read concurrently with
        // a write would be a flake generator, and the property under test is a count, not a timing.
        var decisions = new ConcurrentBag<GateOutcome>();
        Parallel.For(0, 64, _ => decisions.Add(gate.TryEnter(RequestPriority.Background).Outcome));

        Assert.Equal(1, decisions.Count(o => o == GateOutcome.Probe));
        Assert.Equal(63, decisions.Count(o => o == GateOutcome.Open));
    }

    // ---- TP-GATE-04 / 05: how the probe ends -------------------------------------------------

    [Fact]
    public void TP_GATE_04_A_successful_probe_closes_the_gate_and_clears_the_strikes()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportFailure(TranslationErrorKind.RateLimited);   // two strikes to prove they are cleared
        var probe = TakeProbe(gate, clock);

        gate.ReportSuccess(probe);

        Assert.Equal(new GateSnapshot(GateState.Closed, null, 0, TranslationErrorKind.RateLimited),
                     gate.Snapshot());
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void TP_GATE_05_A_failed_probe_re_opens_with_the_window_doubled()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var probe = TakeProbe(gate, clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited, null, probe);

        var snap = gate.Snapshot();
        Assert.Equal(GateState.Open, snap.State);
        Assert.Equal(2, snap.Strikes);
        Assert.Equal(clock.Now + Base * 2, snap.BlockedUntil);
    }

    // ---- TP-GATE-06: the escalation and the cap ----------------------------------------------

    [Fact]
    public void TP_GATE_06_The_window_doubles_per_strike_and_clamps_at_the_cap()
    {
        var (gate, clock) = NewGate();

        // Eight strikes, not six: the sixth is where 60 s × 2^5 = 32 min first exceeds the 30-min
        // cap, and the two after it prove the clamp holds rather than merely arriving once.
        for (var strike = 1; strike <= 8; strike++)
        {
            var probe = strike > 1 ? TakeProbe(gate, clock) : 0;
            var at = clock.Now;

            gate.ReportFailure(TranslationErrorKind.RateLimited, null, probe);

            var expected = TimeSpan.FromSeconds(Math.Min(Base.TotalSeconds * Math.Pow(2, strike - 1),
                                                         Cap.TotalSeconds));
            Assert.Equal(strike, gate.Snapshot().Strikes);
            Assert.Equal(at + expected, gate.Snapshot().BlockedUntil);
        }

        // The sequence the architecture writes out, for a human reading the failure message:
        // 60 s, 2, 4, 8, 16, 30 (clamped), 30, 30.
        Assert.Equal(Cap, gate.Snapshot().BlockedUntil - clock.Now);
    }

    // ---- TP-GATE-07: the clean reset ---------------------------------------------------------

    [Fact]
    public void TP_GATE_07_After_a_clean_run_the_next_failure_opens_for_the_base_window_again()
    {
        var (gate, clock) = NewGate();
        for (var i = 0; i < 3; i++)
        {
            gate.ReportFailure(TranslationErrorKind.RateLimited, null, i > 0 ? TakeProbe(gate, clock) : 0);
        }
        Assert.Equal(3, gate.Snapshot().Strikes);

        // The provider comes back: the probe succeeds, then CleanResetMinutes of successes.
        gate.ReportSuccess(TakeProbe(gate, clock));
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(TranslationPolicy.CleanResetMinutes / 10.0));
            gate.ReportSuccess();
        }

        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.RateLimited);

        Assert.Equal(1, gate.Snapshot().Strikes);
        Assert.Equal(at + Base, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void TP_GATE_07_A_failure_that_takes_no_strike_does_not_forgive_the_earlier_ones()
    {
        // The other half of "not forgiven yet": nothing but a success (or the clean run) clears the
        // escalation, so a 5xx in the middle of a rate-limit storm must not hand the provider a
        // fresh 60-second window.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportFailure(TranslationErrorKind.RateLimited, null, TakeProbe(gate, clock));   // strikes 2

        gate.ReportFailure(TranslationErrorKind.Timeout, null, TakeProbe(gate, clock));       // soft: 5 s, no strike
        Assert.Equal(2, gate.Snapshot().Strikes);

        var probe = TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.RateLimited, null, probe);

        Assert.Equal(3, gate.Snapshot().Strikes);
        Assert.Equal(at + Base * 4, gate.Snapshot().BlockedUntil);
    }

    // ---- TP-GATE-08 / 09: the two key-shaped rows --------------------------------------------

    [Fact]
    public void TP_GATE_08_QuotaExhausted_opens_for_an_hour_without_escalating()
    {
        var (gate, clock) = NewGate();
        var quota = TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes);

        gate.ReportFailure(TranslationErrorKind.QuotaExhausted);
        Assert.Equal(clock.Now + quota, gate.Snapshot().BlockedUntil);
        Assert.Equal(0, gate.Snapshot().Strikes);       // a quota is refilled by a billing period,

        var probe = TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.QuotaExhausted, null, probe);

        Assert.Equal(at + quota, gate.Snapshot().BlockedUntil);   // … not by a longer back-off
        Assert.Equal(0, gate.Snapshot().Strikes);

        gate.ClearAuthBlock();                          // re-saving the key clears it too (§5.3)
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
    }

    [Fact]
    public void TP_GATE_09_AuthFailed_blocks_until_the_key_changes()
    {
        var (gate, clock) = NewGate();

        gate.ReportFailure(TranslationErrorKind.AuthFailed);

        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().BlockedUntil);
        clock.AdvanceMinutes(TranslationPolicy.OpenCapMinutes * 10);
        Assert.Equal(DateTimeOffset.MaxValue, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));

        gate.ClearAuthBlock();                          // ProviderGates.ClearAuthBlock(id) forwards here

        Assert.Equal(new GateSnapshot(GateState.Closed, null, 0, TranslationErrorKind.AuthFailed),
                     gate.Snapshot());
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    // ---- ruling E2-i: what a key save may lift, and what it may not ---------------------------
    //
    // E2.S1's deviation D7 had ClearAuthBlock clear the gate whatever opened it. Ruling E2-i
    // replaces it: a new key clears only what depends on the key — the ACCOUNT-scoped rows of §5.3
    // (`AuthFailed`, `QuotaExhausted`). A `RateLimited` / `Blocked` window is IP-scoped (a counter
    // on the provider's side, keyed to the address this PC dials from) and a soft cooldown is a
    // dead endpoint or a DNS blip; no key the user types moves either, and clearing them would make
    // "I saved my key" mean "resume hammering the provider that just said stop".

    [Theory]
    [InlineData(TranslationErrorKind.RateLimited)]
    [InlineData(TranslationErrorKind.Blocked)]
    public void A_key_save_cannot_lift_an_IP_scoped_block(TranslationErrorKind kind)
    {
        var (gate, _) = NewGate();
        gate.ReportFailure(kind);
        var standing = gate.Snapshot().BlockedUntil;

        gate.ClearAuthBlock();

        var snap = gate.Snapshot();
        Assert.Equal(GateState.Open, snap.State);
        Assert.Equal(standing, snap.BlockedUntil);
        Assert.Equal(1, snap.Strikes);            // the ladder the 429s built is IP-scoped too
        Assert.Equal(standing, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));
    }

    [Theory]
    [InlineData(TranslationErrorKind.Unavailable)]
    [InlineData(TranslationErrorKind.Timeout)]
    [InlineData(TranslationErrorKind.Network)]
    [InlineData(TranslationErrorKind.Unknown)]
    public void A_key_save_cannot_lift_a_soft_cooldown(TranslationErrorKind kind)
    {
        // Five seconds is five seconds: a dead endpoint is not a credentials problem.
        var (gate, clock) = NewGate();
        gate.ReportFailure(kind);

        gate.ClearAuthBlock();

        Assert.Equal(clock.Now + TimeSpan.FromSeconds(TranslationPolicy.SoftCooldownSecs),
                     gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_key_save_cannot_lift_a_bad_response_block()
    {
        var (gate, clock) = NewGate();
        for (var i = 0; i < TranslationPolicy.BadResponseStrikesToOpen; i++)
            gate.ReportFailure(TranslationErrorKind.BadResponse);

        gate.ClearAuthBlock();

        Assert.Equal(clock.Now + Base, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_key_save_lifts_an_auth_block_and_hands_back_the_429_window_underneath_it()
    {
        // The case that makes two timelines necessary rather than one field plus a reason. A 429
        // opens a 60 s window; a 401 then covers it with MaxValue. The key save must lift the
        // MaxValue — it is the only exit AC 4 gives — WITHOUT also buying the provider the seconds
        // it had already refused: E2-i says a key never lifts an IP-scoped window. So the gate
        // stays open for the rest of the 429's own window, and closes when that window elapses.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var ipWindow = clock.Now + Base;
        gate.ReportFailure(TranslationErrorKind.AuthFailed);        // the 401 covers it
        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().BlockedUntil);

        gate.ClearAuthBlock();

        Assert.Equal(GateState.Open, gate.Snapshot().State);
        Assert.Equal(ipWindow, gate.Snapshot().BlockedUntil);       // …the 429's own, still standing
        Assert.Equal(ipWindow, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));

        clock.Advance(Base);                                        // and it is a window, not a wall
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_key_save_lifts_an_auth_block_a_stale_429_landed_on()
    {
        // The mirror image, and the reason the two timelines never shorten each other: here the 429
        // arrives AFTER the 401 (a request that was already in flight, I9). What must never happen
        // is that stale 429 taking ownership of the MaxValue and leaving the user's new key with
        // nothing to lift — a state with no exit at all, which is what AC 4 forbids.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.AuthFailed);
        gate.ReportFailure(TranslationErrorKind.RateLimited);       // the stale request lands
        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().BlockedUntil);
        clock.Advance(Base);                                        // the 429's own window elapses

        gate.ClearAuthBlock();

        // The MaxValue is gone and only the elapsed 429 window is left, so the gate answers the way
        // it answers any elapsed window: one probe, and a success on it closes the gate.
        var probe = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, probe.Outcome);
        gate.ReportSuccess(probe.ProbeToken);
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_key_save_on_a_closed_gate_changes_nothing()
    {
        // The key-save handler fires whether or not the provider ever failed. Nothing to lift means
        // nothing to touch.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportSuccess(TakeProbe(gate, clock));   // a successful probe closes it, strikes and all
        var before = gate.Snapshot();

        gate.ClearAuthBlock();

        Assert.Equal(before, gate.Snapshot());
    }

    // ---- TP-GATE-10 / 11: the rows that must NOT escalate -------------------------------------

    [Theory]
    [InlineData(TranslationErrorKind.Unavailable)]
    [InlineData(TranslationErrorKind.Timeout)]
    [InlineData(TranslationErrorKind.Network)]
    [InlineData(TranslationErrorKind.Unknown)]
    public void TP_GATE_10_The_soft_kinds_cool_down_briefly_and_take_no_strike(TranslationErrorKind kind)
    {
        var (gate, clock) = NewGate();
        var cooldown = TimeSpan.FromSeconds(TranslationPolicy.SoftCooldownSecs);

        gate.ReportFailure(kind);

        Assert.Equal(clock.Now + cooldown, gate.Snapshot().BlockedUntil);
        Assert.Equal(0, gate.Snapshot().Strikes);

        // Repeated soft failures never escalate — the chain moves on, the gate does not punish.
        var probe = TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(kind, null, probe);
        Assert.Equal(at + cooldown, gate.Snapshot().BlockedUntil);
        Assert.Equal(0, gate.Snapshot().Strikes);
    }

    [Fact]
    public void TP_GATE_11_BadResponse_needs_three_in_a_row_and_a_success_resets_the_count()
    {
        var (gate, clock) = NewGate();
        var toOpen = TranslationPolicy.BadResponseStrikesToOpen;

        for (var i = 1; i < toOpen; i++)
        {
            gate.ReportFailure(TranslationErrorKind.BadResponse);
            Assert.Equal(GateState.Closed, gate.Snapshot().State);
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        }

        gate.ReportSuccess();                                   // one good body resets the count …
        for (var i = 1; i < toOpen; i++) gate.ReportFailure(TranslationErrorKind.BadResponse);
        Assert.Equal(GateState.Closed, gate.Snapshot().State);

        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.BadResponse);   // … so this is the third consecutive

        Assert.Equal(GateState.Open, gate.Snapshot().State);
        Assert.Equal(at + Base, gate.Snapshot().BlockedUntil);
        Assert.Equal(0, gate.Snapshot().Strikes);               // a soft strike is not a hard one
    }

    // ---- TP-GATE-12: the row that must change nothing at all ----------------------------------

    [Fact]
    public void TP_GATE_12_A_cancel_never_touches_the_gate()
    {
        // I3, as a test. A genuine user cancel is not a provider failure; a timeout is (an OCE whose
        // token is NOT cancelled maps to Timeout, and the mapper draws that line — the gate must not
        // redraw it). This file may name the kind; no production source may (TP-MAP-17).
        var clock = new FakeClock();
        var edges = 0;
        var gate = new ProviderGate(clock.Read, onTransition: (_, _) => edges++);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportSuccess(TakeProbe(gate, clock));
        clock.AdvanceMinutes(1);
        gate.ReportSuccess();                                   // a clean run is under way

        var before = gate.Snapshot();
        var announced = edges;
        gate.ReportFailure(TranslationErrorKind.Cancelled);
        gate.ReportFailure(TranslationErrorKind.Cancelled, clock.Now + TimeSpan.FromHours(1));

        Assert.Equal(before, gate.Snapshot());                  // whole-state equality, not field by field

        // …and since E2.S6 that has to mean the LOG too. A row that announces no transition can
        // produce no line by construction, which is a stronger statement than counting lines:
        // GateLoggingTests.TP_GATE_12_a_cancel_writes_no_line asserts the other end of the same wire.
        Assert.Equal(announced, edges);

        // cleanSince is not in the snapshot (ruling R-2 fixes its four fields), so it is asserted
        // where it is observable: the clean run must still complete on its original schedule.
        clock.AdvanceMinutes(TranslationPolicy.CleanResetMinutes - 1);
        gate.ReportSuccess();
        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        Assert.Equal(1, gate.Snapshot().Strikes);
        Assert.Equal(at + Base, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void TP_GATE_12_A_cancel_does_not_release_an_outstanding_probe()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var probe = TakeProbe(gate, clock);

        // With its own token, so the case really is "Cancelled changes nothing" and not "the report
        // was ignored because it quoted no probe" (ruling E2-h).
        gate.ReportFailure(TranslationErrorKind.Cancelled, null, probe);

        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    // ---- TP-GATE-13/14/15: the server's own hint ----------------------------------------------
    //
    // Ruling E2-f (architect, 2026-09-06) replaced §5.5's "the hint overrides the computed window"
    // with **a floor, never a shortcut**: the effective window is
    //     max(computed window, clamp(hint, 1 s, max(OpenCapMinutes, the kind's own window)))
    // The three problems it settles, all found by E2.S1's review: a `Retry-After: 1` bought a
    // one-second pause while the strike ladder climbed with no effect; an HTTP-date resolved
    // against a PC running five minutes fast collapsed to that same floor; and the clamp ceiling
    // (30 min) sat BELOW the QuotaExhausted window (60 min), so a hint made a quota block shorter
    // than its own row. A tiny or skewed hint is now a no-op; a large one lengthens, within the
    // kind's own ceiling.

    [Fact]
    public void TP_GATE_13_Retry_After_delta_seconds_lengthens_the_computed_window()
    {
        var (gate, clock) = NewGate();
        var said = TimeSpan.FromSeconds(120);       // literal: it is the header's value, not a policy number
        Assert.True(said > Base);                   // the point of the case: it is LONGER than the window

        gate.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + said);

        Assert.Equal(clock.Now + said, gate.Snapshot().BlockedUntil);
        Assert.Equal(1, gate.Snapshot().Strikes);   // honouring the hint does not skip the strike
    }

    [Fact]
    public void TP_GATE_14_Retry_After_as_an_http_date_is_honoured_the_same_way()
    {
        // The mapper resolves both header forms to an absolute DateTimeOffset
        // (ProviderErrorMapper.RetryAfter), so a date and a delta reach the gate identically —
        // this case pins that the absolute form is used as given rather than re-derived.
        var (gate, clock) = NewGate();
        var date = clock.Now + TimeSpan.FromSeconds(90);

        gate.ReportFailure(TranslationErrorKind.RateLimited, date);

        Assert.Equal(date, gate.Snapshot().BlockedUntil);
    }

    [Theory]
    [InlineData(0)]         // "Retry-After: 0"
    [InlineData(-3600)]     // an HTTP-date that has already elapsed
    [InlineData(1)]         // "Retry-After: 1" — the header that used to switch the breaker off
    [InlineData(-300)]      // the skew case: a PC running five minutes fast reading an HTTP date
    public void TP_GATE_15_A_hint_at_or_below_the_floor_is_a_no_op(int offsetSeconds)
    {
        // Ruling E2-f. Before it, each of these four headers replaced the computed window with one
        // second, so a server could climb the whole strike ladder while never being paused for
        // longer than a heartbeat — the breaker off, on the server's word.
        var (gate, clock) = NewGate();

        gate.ReportFailure(TranslationErrorKind.RateLimited, clock.Now.AddSeconds(offsetSeconds));

        Assert.Equal(clock.Now + Base, gate.Snapshot().BlockedUntil);
        Assert.Equal(1, gate.Snapshot().Strikes);
    }

    [Fact]
    public void TP_GATE_15_A_hint_beyond_the_cap_is_clamped_down_to_it()
    {
        // Without this clamp a provider that says "come back in a day" pauses the app for a day —
        // R-01 with the server's own signature on it. §5.5 bounds it to [1 s, OpenCapMinutes].
        var (gate, clock) = NewGate();

        gate.ReportFailure(TranslationErrorKind.RateLimited, clock.Now + TimeSpan.FromDays(1));

        Assert.Equal(clock.Now + Cap, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_hint_lengthens_the_soft_cooldown_and_the_quota_window_too()
    {
        // §5.5 says "every non-success response", not "the 429s": a 503 with a Retry-After is the
        // one case where a 5 s cooldown would be demonstrably wrong.
        var (soft, clockA) = NewGate();
        soft.ReportFailure(TranslationErrorKind.Unavailable, clockA.Now + TimeSpan.FromSeconds(45));
        Assert.Equal(clockA.Now + TimeSpan.FromSeconds(45), soft.Snapshot().BlockedUntil);

        // Ruling E2-f: the ceiling is max(OpenCapMinutes, the kind's own window), so a day-long hint
        // on a quota row clamps to the QUOTA window (60 min) and not to the breaker cap (30 min).
        // Clamping to the cap made the hint SHORTEN a quota block — §15 R9 says one request an
        // hour, and the server asking for longer must never buy the app a shorter pause.
        var quotaWindow = TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes);
        var (quota, clockB) = NewGate();
        quota.ReportFailure(TranslationErrorKind.QuotaExhausted, clockB.Now + TimeSpan.FromDays(1));
        Assert.Equal(clockB.Now + quotaWindow, quota.Snapshot().BlockedUntil);

        // …and a hint well inside that window changes nothing at all.
        var (quotaShort, clockD) = NewGate();
        quotaShort.ReportFailure(TranslationErrorKind.QuotaExhausted, clockD.Now + TimeSpan.FromMinutes(45));
        Assert.Equal(clockD.Now + quotaWindow, quotaShort.Snapshot().BlockedUntil);

        // The other half of `max(OpenCapMinutes, the kind's own window)`, and the only place it is
        // observable: a row whose own window is FAR below the cap still clamps to the cap, not to
        // its own 5 s. A 503 saying "come back in a day" therefore buys 30 minutes — the breaker's
        // ceiling — and not a day. Worth pinning as the ruling's own arithmetic, because the number
        // it produces (a 30-minute pause on a row that takes no strike) is one E2.S7 may want back.
        var (softLong, clockE) = NewGate();
        softLong.ReportFailure(TranslationErrorKind.Unavailable, clockE.Now + TimeSpan.FromDays(1));
        Assert.Equal(clockE.Now + Cap, softLong.Snapshot().BlockedUntil);

        var (auth, clockC) = NewGate();
        auth.ReportFailure(TranslationErrorKind.AuthFailed, clockC.Now + TimeSpan.FromSeconds(30));
        Assert.Equal(DateTimeOffset.MaxValue, auth.Snapshot().BlockedUntil);  // no window to override
    }

    [Fact]
    public void A_soft_failure_in_flight_cannot_shorten_a_block_that_already_stands()
    {
        // Two requests are in flight when the gate closes behind them — the LIVE batch and the
        // Translator tab share one gate (I9). The 429 lands first and opens the window; the other
        // request then times out. Without the guard the 5-second cooldown would replace the open
        // window and hand a blocked provider a request every five seconds.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var standing = gate.Snapshot().BlockedUntil;

        clock.AdvanceSeconds(3);
        gate.ReportFailure(TranslationErrorKind.Timeout);
        Assert.Equal(standing, gate.Snapshot().BlockedUntil);

        gate.ReportFailure(TranslationErrorKind.QuotaExhausted);   // longer: it may extend
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes),
                     gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void An_escalating_failure_in_flight_cannot_lift_the_two_blocks_only_the_user_can()
    {
        // The other half of the same race, and the one that matters most: an escalating row used
        // to ASSIGN its window, so a stale 429 arriving a second after the block landed replaced
        // "until the key changes" with 60 seconds, and a 60-minute quota window with 60 seconds.
        // AC 4 says only ClearAuthBlock lifts the first; §15 R9 says the second is an hour.
        var (auth, _) = NewGate();
        auth.ReportFailure(TranslationErrorKind.AuthFailed);
        auth.ReportFailure(TranslationErrorKind.RateLimited);       // the stale request lands

        Assert.Equal(DateTimeOffset.MaxValue, auth.Snapshot().BlockedUntil);
        Assert.Equal(1, auth.Snapshot().Strikes);                   // counted, just not obeyed yet
        auth.ClearAuthBlock();                                      // still the only way out of MaxValue
        // …but only out of MaxValue: ruling E2-i (which supersedes E2.S1's D7) says a key never
        // lifts an IP-scoped window, and the stale 429 opened one of its own underneath. The gate
        // therefore reads Open for the rest of that 60 s, and the strike stays counted.
        Assert.Equal(GateState.Open, auth.Snapshot().State);
        Assert.Equal(1, auth.Snapshot().Strikes);

        var (quota, clockQ) = NewGate();
        quota.ReportFailure(TranslationErrorKind.QuotaExhausted);
        var hour = clockQ.Now + TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes);
        clockQ.AdvanceSeconds(1);
        quota.ReportFailure(TranslationErrorKind.Blocked);

        Assert.Equal(hour, quota.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_hint_shorter_than_the_strike_ladders_own_window_is_a_no_op()
    {
        // Ruling E2-f, the case E2.S1 got the other way round: an escalating row used to ASSIGN the
        // hint, so "Retry-After: 90" on the third strike replaced a four-minute window with ninety
        // seconds. The hint is a floor now, so the ladder's own window stands.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportFailure(TranslationErrorKind.RateLimited);        // strikes 2 ⇒ 2 × Base standing

        var said = clock.Now + TimeSpan.FromSeconds(90);             // literal: a header value
        Assert.True(said < gate.Snapshot().BlockedUntil);
        gate.ReportFailure(TranslationErrorKind.RateLimited, said);  // strikes 3 ⇒ 4 × Base computed

        Assert.Equal(clock.Now + Base * 4, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_hint_cannot_lift_the_two_blocks_only_the_user_can_lift_either()
    {
        // The last hole the E2.S1 review's "extend, never shorten" patch left open: it exempted an
        // explicit hint, because §5.5 gave the server the last word. Ruling E2-f took that word
        // back, so a stale 429 carrying a hint can no longer end an auth block or shorten a quota
        // window either — the two states whose only honest exit is the user (AC 4, §15 R9).
        var (auth, clockA) = NewGate();
        auth.ReportFailure(TranslationErrorKind.AuthFailed);
        auth.ReportFailure(TranslationErrorKind.RateLimited, clockA.Now + TimeSpan.FromSeconds(90));
        Assert.Equal(DateTimeOffset.MaxValue, auth.Snapshot().BlockedUntil);

        var (quota, clockB) = NewGate();
        quota.ReportFailure(TranslationErrorKind.QuotaExhausted);
        var hour = clockB.Now + TimeSpan.FromMinutes(TranslationPolicy.QuotaOpenMinutes);
        clockB.AdvanceSeconds(1);
        quota.ReportFailure(TranslationErrorKind.Blocked, clockB.Now + TimeSpan.FromSeconds(90));
        Assert.Equal(hour, quota.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_probe_that_fails_without_setting_a_window_still_re_opens_the_gate()
    {
        // §5.2 has one arrow out of HalfOpen on a failure and it lands on Open. BadResponse is the
        // row that can fail a probe without opening anything (one is not three); the gate would
        // then keep an already-elapsed blockedUntil — reading Open to the UI while handing every
        // caller in turn a fresh probe, at full request rate, against a provider that just failed.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var probe = TakeProbe(gate, clock);

        gate.ReportFailure(TranslationErrorKind.BadResponse, null, probe);   // 1 of 3: no window of its own

        Assert.Equal(GateState.Open, gate.Snapshot().State);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(TranslationPolicy.SoftCooldownSecs),
                     gate.Snapshot().BlockedUntil);
        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_success_from_a_request_that_predates_the_block_does_not_forgive_the_ladder()
    {
        // Both chains share one gate (I9), so a request dispatched before the gate closed can
        // report a 200 a second after it opened. That success is real but it is not evidence the
        // provider is well — only a probe is. It used to start the clean run, and CleanResetMinutes
        // later the probe's own failure ran DecayStrikes first and re-opened at strike 1: one late
        // 200 erased the whole escalation ladder.
        // Five strikes, so the standing window (16 min) outlasts CleanResetMinutes (10) — that is
        // what makes the forgiveness reachable at all.
        var (gate, clock) = NewGate();
        for (var i = 0; i < 5; i++)
        {
            gate.ReportFailure(TranslationErrorKind.RateLimited, null, i > 0 ? TakeProbe(gate, clock) : 0);
        }
        Assert.Equal(5, gate.Snapshot().Strikes);
        var standing = gate.Snapshot().BlockedUntil;
        Assert.True(standing - clock.Now > TimeSpan.FromMinutes(TranslationPolicy.CleanResetMinutes));

        clock.AdvanceSeconds(1);
        gate.ReportSuccess();                                        // the stale 200
        Assert.Equal(standing, gate.Snapshot().BlockedUntil);        // it lifts nothing, either

        var probe = TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.RateLimited, null, probe);

        Assert.Equal(6, gate.Snapshot().Strikes);                    // not 1
        Assert.Equal(at + Cap, gate.Snapshot().BlockedUntil);        // not the 60-second floor
    }

    // ---- the DoD: every row of §5.3 has a case -----------------------------------------------

    /// <summary>
    /// The whole table in one place, so a kind cannot be added to the enum and silently do nothing
    /// (or silently do something). `strikes` and the window are asserted together because they are
    /// what separates the four groups: escalate / open flat / open for ever / do not touch.
    /// </summary>
    [Theory]
    [InlineData(TranslationErrorKind.RateLimited, true, 1)]
    [InlineData(TranslationErrorKind.Blocked, true, 1)]
    [InlineData(TranslationErrorKind.QuotaExhausted, true, 0)]
    [InlineData(TranslationErrorKind.AuthFailed, true, 0)]
    [InlineData(TranslationErrorKind.Unavailable, true, 0)]
    [InlineData(TranslationErrorKind.Timeout, true, 0)]
    [InlineData(TranslationErrorKind.Network, true, 0)]
    [InlineData(TranslationErrorKind.Unknown, true, 0)]
    [InlineData(TranslationErrorKind.BadResponse, false, 0)]         // one is not three
    [InlineData(TranslationErrorKind.AllProvidersPaused, false, 0)]  // never reported to a gate
    [InlineData(TranslationErrorKind.Cancelled, false, 0)]           // never touches it
    public void Every_row_of_the_kind_table_has_the_effect_section_5_3_gives_it(
        TranslationErrorKind kind, bool opens, int strikes)
    {
        // `opens` and not a GateState, because xUnit needs a public test method and GateState is
        // internal — the state it stands for is asserted right below.
        var (gate, _) = NewGate();

        gate.ReportFailure(kind);

        Assert.Equal(opens ? GateState.Open : GateState.Closed, gate.Snapshot().State);
        Assert.Equal(strikes, gate.Snapshot().Strikes);
    }

    [Fact]
    public void The_table_covers_every_kind_the_vocabulary_defines()
    {
        // Non-vacuity for the theory above: if E1's enum grows a row, this fails until the table
        // above gains a line, rather than the new kind quietly landing in the "do nothing" arm.
        var table = typeof(ProviderGateTests)
            .GetMethod(nameof(Every_row_of_the_kind_table_has_the_effect_section_5_3_gives_it))!;
        var covered = table.GetCustomAttributes<InlineDataAttribute>()
                           .Select(a => (TranslationErrorKind)a.GetData(table).Single()[0]!)
                           .ToHashSet();

        Assert.Equal(Enum.GetValues<TranslationErrorKind>().ToHashSet(), covered);
    }

    [Fact]
    public void The_two_untouchable_kinds_change_nothing_even_on_an_open_gate()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var before = gate.Snapshot();

        clock.AdvanceSeconds(1);
        gate.ReportFailure(TranslationErrorKind.AllProvidersPaused);
        gate.ReportFailure(TranslationErrorKind.Cancelled);

        Assert.Equal(before, gate.Snapshot());
    }

    // ---- Snapshot: read-only by contract (ruling R-2) -----------------------------------------

    [Fact]
    public void Snapshot_takes_no_probe_and_changes_no_state()
    {
        // The UI polls this at 1 Hz. A Snapshot that consumed the probe would mean the status chip
        // eats the one request that would have proved the provider is back.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base);

        for (var i = 0; i < 5; i++) Assert.Equal(GateState.Open, gate.Snapshot().State);

        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void Snapshot_is_the_four_fields_the_ruling_names_and_no_event_is_exposed()
    {
        // R-2/OQ-c: poll, do not notify. An event here would put a UI callback on a lock held by
        // the LIVE loop, which is exactly the coupling `Services/` stays free of (I2).
        Assert.Empty(typeof(ProviderGate).GetEvents(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Instance | BindingFlags.Static));
        Assert.Equal(new[] { "BlockedUntil", "LastKind", "State", "Strikes" },
                     typeof(GateSnapshot).GetProperties()
                                         .Select(p => p.Name)
                                         .Where(n => n != "EqualityContract")
                                         .OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void The_gate_holds_no_static_state()
    {
        // R4/R-08: static state here would leak across xUnit's parallel collections. The registry
        // (E2.S2) is the one place allowed to hold it, behind a non-parallel collection.
        var statics = typeof(ProviderGate)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsLiteral)
            .ToList();

        Assert.All(statics, f => Assert.True(f.IsInitOnly, $"{f.Name} is mutable static state"));
    }

    // ---- the probe cannot pause the app for ever (R-01) --------------------------------------

    [Fact]
    public void An_abandoned_probe_is_re_armed_once_it_could_no_longer_be_running()
    {
        // A caller that takes the probe and never reports — a cancel mid-flight, or a crash — would
        // otherwise leave the gate half-open, admitting nobody, for the life of the process. The
        // bound is the request timeout: a probe is one request and cannot outlive it.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        TakeProbe(gate, clock);

        clock.Advance(ProbeTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        clock.AdvanceSeconds(1);
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
    }

    [Fact]
    public void An_abandoned_probe_stops_reading_HalfOpen_even_if_nobody_calls_TryEnter_again()
    {
        // The re-arm above needs a TryEnter, and a paused app makes none: LIVE is off, the user has
        // walked away, and the only thing still running is the 1 Hz status poll (R-2). Reading the
        // latch alone left that chip on "checking…" for the life of the process. Snapshot answers
        // the question TryEnter would answer, without taking anything.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        TakeProbe(gate, clock);

        clock.Advance(ProbeTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);

        clock.AdvanceSeconds(1);
        Assert.Equal(GateState.Open, gate.Snapshot().State);
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_block_that_landed_while_the_probe_was_out_survives_the_re_arm()
    {
        // Ruling E2-h (this story) is what makes this state reachable, and the re-arm above it
        // predates the ruling. A stale in-flight 429 counts against §5.3 and sets a fresh window
        // but may no longer release the latch, so the gate can sit with a probe outstanding AND a
        // brand-new block standing; the probe is then abandoned (a cancel mid-flight never reports
        // at all — I3/TP-GATE-12 — or the process crashed). Re-arming off the probe timeout alone
        // handed the next caller a real request straight through the window the provider had just
        // asked for, once every RequestTimeoutSeconds for as long as callers kept abandoning: the
        // breaker switched off in the one state it exists to manage (G1).
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        TakeProbe(gate, clock);                                     // half-open

        gate.ReportFailure(TranslationErrorKind.RateLimited);       // the stale 429: no token
        var blockedUntil = gate.Snapshot().BlockedUntil!.Value;
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);    // …and the latch was not stolen
        Assert.True(blockedUntil > clock.Now + ProbeTimeout,
                    "the case needs a window that outlasts the probe timeout, or it proves nothing");

        clock.Advance(ProbeTimeout);                                // the probe is now abandoned
        Assert.Equal(blockedUntil, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));
        Assert.Equal(blockedUntil, gate.Snapshot().BlockedUntil);   // TryEnter and Snapshot agree

        // …and the probe is offered again only once that window has really elapsed.
        clock.Advance(blockedUntil - clock.Now);
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void An_auth_block_that_landed_while_the_probe_was_out_is_not_re_armed_through()
    {
        // The same hole with the block that has no window. AC 4 and ruling E2-i: an AuthFailed is
        // open until the key changes, and `ClearAuthBlock` is the only exit. A re-arm that did not
        // re-read BlockedUntil sent one request per RequestTimeoutSeconds, for ever, with
        // credentials the provider had already rejected — which is the abuse signal, not a probe.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        TakeProbe(gate, clock);
        gate.ReportFailure(TranslationErrorKind.AuthFailed);        // the stale 401: no token

        clock.Advance(ProbeTimeout * 3);
        Assert.Equal(DateTimeOffset.MaxValue, OpenUntil(gate.TryEnter(RequestPriority.Interactive)));
        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().BlockedUntil);

        gate.ClearAuthBlock();                                      // the one exit the user has
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void Priority_never_changes_the_breakers_own_window()
    {
        // E2.S1 pinned "priority changes nothing yet"; E2.S3's reserve is what made that stop being
        // true, so this is its replacement. What priority moves is the CEILING and the probe
        // preference (TP-GATE-16/17/18) — never the breaker's window: a Background caller is told
        // the same retryAt as an Interactive one, and neither is let through early.
        var (gate, clock) = NewGate();
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Background).Outcome);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        Assert.Equal(OpenUntil(gate.TryEnter(RequestPriority.Interactive)),
                     OpenUntil(gate.TryEnter(RequestPriority.Background)));

        // …and an Open spends nothing, so the bucket that refilled during the window is untouched:
        // the probe, once it is offered, is affordable to either priority.
        clock.Advance(Base + ProbeDefer);
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Background).Outcome);
    }

    // ---- §5.4 the rate ceiling: TP-GATE-16 / 17 / 18 -----------------------------------------
    //
    // All four constants are [ASSUMED] and E2.S7 tunes them (U9), so these cases assert
    // RELATIONSHIPS — the bucket holds `BucketCapacity`, it refills one token per `MinSpacingMs`,
    // `Background` is refused the last one, the defer is one `ProbeDeferMs` — and never literals.
    // Every instant is driven by the injected clock; there is no `Task.Delay` here either (CI-3).

    [Fact]
    public void The_four_ceiling_relationships_E2_S7_must_preserve_while_it_tunes_them()
    {
        // U9: all four numbers are [ASSUMED] and E2.S7 tunes them from field logs. These are the
        // relationships the code depends on, stated once so a tuning commit reads them.
        Assert.True(TranslationPolicy.MinSpacingMs > 0,
                    "a refill period of zero divides by zero: `elapsed / RefillPeriod` becomes NaN, "
                    + "every comparison against it is false, and the rate ceiling is silently off");
        Assert.True(TranslationPolicy.BucketCapacity >= 2,
                    "the Interactive reserve IS the capacity above one: at a capacity of 1 both "
                    + "priorities need the same single token and §5.4's reserve quietly disappears");
        Assert.True(TranslationPolicy.ProbeDeferMs > 0,
                    "a defer of zero is no probe preference at all (AC 3)");
        Assert.True(Capacity * TranslationPolicy.MinSpacingMs <= TranslationPolicy.MaxSpacingWaitMs,
                    "the longest wait the bucket can return must stay inside MaxSpacingWaitMs, or "
                    + "the chain would walk away from a perfectly healthy provider");
    }

    [Fact]
    public void TP_GATE_16_The_bucket_holds_BucketCapacity_and_refills_one_token_per_MinSpacingMs()
    {
        var (gate, clock) = NewGate();

        // A full bucket, spent without the clock moving at all.
        for (var i = 0; i < Capacity; i++)
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);

        var empty = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Wait, empty.Outcome);
        Assert.True(empty.Delay > TimeSpan.Zero && empty.Delay <= Spacing,
                    $"the wait for the next token must be at most one refill period; it was {empty.Delay}");

        // A rate, not a step: half a period later the wait is half as long, and the token is not
        // there yet. (A bucket implemented with a timer could not answer this without sleeping.)
        clock.Advance(Spacing / 2);
        var half = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Wait, half.Outcome);
        Assert.True(half.Delay < empty.Delay, "the wait must shrink as the bucket refills");

        clock.Advance(half.Delay);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void TP_GATE_16_An_idle_gate_banks_nothing_beyond_the_capacity()
    {
        // The clamp, which is why the bucket is read lazily rather than accumulated: without it a
        // gate nobody touched for an hour would hand out 7 200 requests back to back.
        //
        // The bucket is SPENT first and only then left idle, because that is the only shape that
        // reaches the clamp: on the very first TryEnter `_tokensAt` is still null, so Refill skips
        // its whole body and an hour of "idling" before any request has been made proves nothing —
        // the clamp line never executes and deleting it leaves the case green.
        var (gate, clock) = NewGate();
        for (var i = 0; i < Capacity; i++)
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);

        clock.AdvanceMinutes(60);                                   // 7 200 tokens' worth of refill

        for (var i = 0; i < Capacity; i++)
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);

        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_refusal_always_names_a_delay_the_caller_can_actually_wait()
    {
        // `_tokens` is a double and the refill is continuous, so a level that is arithmetically
        // exactly at the floor lands a few ULPs UNDER it on perfectly ordinary timings: two
        // requests 0.4 × MinSpacingMs apart and a third 0.6 × later leave 0.4 + 0.6 =
        // 0.9999999999999999. The shortfall is 1.1e-16 of a token, which the wait arithmetic
        // rounds to zero ticks — and `Wait(0)` is not a decision: E3.S3 sleeps through it, asks
        // again at the same instant and is refused again, which is a spin rather than a pause.
        // Either the caller is let through or it is told to come back at a time that is later than
        // now; there is no third answer.
        var (gate, clock) = NewGate();

        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        clock.Advance(Spacing * 0.4);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        clock.Advance(Spacing * 0.6);

        var d = gate.TryEnter(RequestPriority.Interactive);
        if (d.Outcome == GateOutcome.Wait)
            Assert.True(d.Delay > TimeSpan.Zero, "a refusal that names no delay is a spin, not a pause");

        // Whichever way that one lands, the property has to hold everywhere: sweep the bucket over
        // sub-period advances and never accept a zero-length Wait from either priority.
        foreach (var priority in new[] { RequestPriority.Interactive, RequestPriority.Background })
            for (var num = 1; num <= 120; num++)
            {
                var (g, c) = NewGate();
                var step = Spacing * (num / 100.0);
                for (var i = 0; i < 25; i++)
                {
                    var decision = g.TryEnter(priority);
                    if (decision.Outcome == GateOutcome.Wait)
                        Assert.True(decision.Delay > TimeSpan.Zero,
                                    $"Wait(0) after {i} calls {step} apart as {priority}");
                    c.Advance(step);
                }
            }
    }

    [Fact]
    public void A_clock_that_jumps_backwards_neither_throws_nor_hands_out_free_tokens()
    {
        // IS-6's clock is the wall clock in production, and a wall clock moves backwards: an NTP
        // correction, a laptop resuming, a user fixing the date. The refill must not compute a
        // negative elapsed into a negative token count, and the skew must not turn into credit.
        var (gate, clock) = NewGate();
        for (var i = 0; i < Capacity; i++) gate.TryEnter(RequestPriority.Interactive);

        clock.Advance(-TimeSpan.FromHours(1));                      // the correction lands
        var refused = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Wait, refused.Outcome);            // no token appeared out of it
        Assert.True(refused.Delay > TimeSpan.Zero && refused.Delay <= Spacing);

        // …and once the clock is right again the bucket is capped like any idle bucket: two, not
        // an hour's worth. (An hour of "elapsed" is exactly what the skew manufactured.)
        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < Capacity; i++)
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void TP_GATE_17_The_last_token_is_reserved_for_Interactive()
    {
        // AC 2, and the pair is asserted in this order in ONE case on purpose: a regression that
        // drops the reserve makes both Allow, and two separate cases would not catch it.
        var (gate, _) = NewGate();
        for (var i = 0; i < Capacity - 1; i++)
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Background).Outcome);

        var refused = gate.TryEnter(RequestPriority.Background);   // one token left: not yours
        Assert.Equal(GateOutcome.Wait, refused.Outcome);

        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void TP_GATE_17_A_Background_Wait_names_the_token_the_caller_may_actually_take()
    {
        // The wait is measured to `tokens >= BucketCapacity` for Background, not to `tokens >= 1`.
        // Against the wrong threshold a Background caller is told to come back 500 ms too early and
        // is refused again — the same request twice, which is the behaviour this epic removes.
        var (gate, clock) = NewGate();
        for (var i = 0; i < Capacity - 1; i++) gate.TryEnter(RequestPriority.Background);

        var wait = gate.TryEnter(RequestPriority.Background);
        Assert.Equal(GateOutcome.Wait, wait.Outcome);

        clock.Advance(wait.Delay);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Background).Outcome);
    }

    [Fact]
    public void TP_GATE_18_A_Background_caller_stands_aside_from_the_probe_for_one_ProbeDeferMs()
    {
        // AC 3, the architect's concern #1: during an outage the LIVE loop ticks every 700 ms and
        // would win every probe, so the one request the user is watching would be the least likely
        // to be tried.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var eligibleAt = gate.Snapshot().BlockedUntil!.Value;
        clock.Advance(eligibleAt - clock.Now);                    // exactly probe-eligible

        Assert.Equal(eligibleAt + ProbeDefer, OpenUntil(gate.TryEnter(RequestPriority.Background)));
        Assert.Equal(GateState.Open, gate.Snapshot().State);      // …and the latch was NOT taken

        // The user presses Enter inside that second and gets the probe.
        clock.Advance(ProbeDefer / 2);
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
    }

    [Fact]
    public void TP_GATE_18_With_nobody_typing_Background_takes_the_probe_when_the_defer_elapses()
    {
        // The other half: the defer must not become a pause. The window is latched to the INSTANT
        // the gate became probe-eligible, not to the caller — a per-caller "has deferred once" flag
        // would let two Background callers each defer and then both race for the probe, and it
        // could be restarted for ever by a loop that keeps asking.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var eligibleAt = gate.Snapshot().BlockedUntil!.Value;
        clock.Advance(eligibleAt - clock.Now);

        Assert.Equal(eligibleAt + ProbeDefer, OpenUntil(gate.TryEnter(RequestPriority.Background)));
        clock.Advance(ProbeDefer / 2);
        Assert.Equal(eligibleAt + ProbeDefer,                       // a second asker does not restart it
                     OpenUntil(gate.TryEnter(RequestPriority.Background)));

        clock.Advance(ProbeDefer / 2);                              // exactly at the end of the window
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Background).Outcome);
    }

    [Fact]
    public void The_probe_pays_a_token_like_any_other_request()
    {
        // Ruling OQ-b: a half-open probe IS a real request — one rule, no special case. With the
        // bucket at capacity the probe leaves exactly one token behind it.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var probe = TakeProbe(gate, clock);
        gate.ReportSuccess(probe);                                  // the gate closes, bucket at 1

        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void A_probe_that_cannot_afford_a_token_is_refused_and_the_probe_is_not_burnt()
    {
        // The T4 edge case, and the one assertion in this file that has no TP row: it is R-01 (a
        // permanently paused app) arrived at by the back door. If a probe-eligible caller that the
        // bucket refuses were handed `Probe` anyway — or had the latch marked taken — the single
        // probe of a 30-minute window would be spent by a caller that never sent a request, and the
        // gate would stay open for another full window with nothing outstanding.
        //
        // The bucket is emptied through `ProviderGate.DrainBucketForTests` — an explicit, documented
        // test-only seam rather than reflection on the class's own private fields, which would make
        // a rename read as a passing test. It is needed because with today's numbers the state is
        // unreachable from outside: the shortest block window a GatePolicy can set is
        // SoftCooldownSecs, which already refills BucketCapacity × MinSpacingMs. That is a
        // relationship, not a law — it is asserted below so E2.S7 (U9) sees what it is changing the
        // moment it tunes MinSpacingMs up.
        Assert.True(TranslationPolicy.SoftCooldownSecs * 1000 >= Capacity * TranslationPolicy.MinSpacingMs,
                    "no block window is short enough today to leave the bucket empty when it elapses — "
                    + "if E2.S7 breaks that, this guard stops being defensive and starts being load-bearing");

        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var eligibleAt = gate.Snapshot().BlockedUntil!.Value;
        clock.Advance(eligibleAt - clock.Now);
        gate.DrainBucketForTests();                                 // now == eligibleAt

        var refused = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Wait, refused.Outcome);
        Assert.Equal(GateState.Open, gate.Snapshot().State);        // NOT HalfOpen: the latch is free

        // …and the probe is still on offer to the next caller once a token has refilled.
        clock.Advance(refused.Delay);
        var probe = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, probe.Outcome);
        Assert.NotEqual(0, probe.ProbeToken);
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
    }

    [Fact]
    public void An_Open_and_a_Wait_spend_no_token()
    {
        // No request is made on those paths, so no budget may be charged for one — otherwise a LIVE
        // loop ticking against a paused provider would keep the bucket empty for the user.
        //
        // Both halves are asserted with the clock STANDING STILL, because a refill hides a charge:
        // any advance long enough to re-open the gate also refills BucketCapacity × MinSpacingMs
        // and clamps, so a gate that charged all hundred refusals would look identical. The Open
        // half therefore uses an AuthFailed, whose exit is `ClearAuthBlock` (E2-i) and costs no
        // time at all.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.AuthFailed);
        for (var i = 0; i < 50; i++)
            Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        gate.ClearAuthBlock();                                      // no clock advance, no refill
        for (var i = 0; i < Capacity; i++)                          // …and the bucket is still FULL
            Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);

        // Now the same for Wait: the bucket is empty, and fifty refusals later exactly one refill
        // period still buys exactly one token — not one less for every refusal.
        for (var i = 0; i < 50; i++)
            Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);

        clock.Advance(Spacing);
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void Snapshot_neither_refills_nor_spends_the_bucket()
    {
        // T6 / R-2: the UI polls this at 1 Hz. A Snapshot that refilled the bucket would make the
        // rate ceiling depend on whether the About tab is open; one that spent a token would make
        // the status chip eat the user's next request.
        var (gate, clock) = NewGate();
        for (var i = 0; i < Capacity; i++) gate.TryEnter(RequestPriority.Interactive);

        clock.Advance(Spacing);
        for (var i = 0; i < 10; i++) Assert.Equal(GateState.Closed, gate.Snapshot().State);

        // Exactly one token refilled, and exactly one: the ten polls added nothing and took nothing.
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void The_ceiling_never_makes_the_gate_wait_for_anybody()
    {
        // AC 4 stops at the constant and the SHAPE of the decision in this story: `ChainTranslator`
        // (E3.S3) and the LIVE loop (E5.S1) are what act on it. What must be true here is that the
        // gate hands back a TimeSpan a caller can compare — and never blocks, sleeps, takes a
        // CancellationToken or returns a Task. A gate that waits for you is the scheduler §5.4
        // explicitly refuses to build.
        var wait = TimeSpan.FromMilliseconds(TranslationPolicy.MaxSpacingWaitMs);
        Assert.True(GateDecision.WorthWaiting(wait));
        Assert.False(GateDecision.WorthWaiting(wait + TimeSpan.FromMilliseconds(1)));

        // …and the ceiling can never produce one the chain would walk away from: the longest Wait
        // the bucket returns is BucketCapacity × MinSpacingMs. A tuning commit that broke that
        // would have the chain skip a perfectly healthy provider.
        Assert.True(Capacity * TranslationPolicy.MinSpacingMs <= TranslationPolicy.MaxSpacingWaitMs,
                    "the longest wait the bucket can return must stay inside MaxSpacingWaitMs");

        var tryEnter = typeof(ProviderGate).GetMethod(
            "TryEnter", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(tryEnter);
        Assert.Equal(typeof(GateDecision), tryEnter!.ReturnType);
        Assert.Equal(new[] { typeof(RequestPriority) },
                     tryEnter.GetParameters().Select(p => p.ParameterType));
    }

    // ---- ruling E8-e: a LOCAL provider closes its own gate ------------------------------------

    /// <summary>
    /// <b>The bug the ruling names.</b> A local engine never calls <see cref="ProviderGate.TryEnter"/>
    /// — there is no endpoint to be polite to — so nothing ever hands it a probe token, so nothing
    /// could ever close its gate: after one failure the state read <c>Open</c> for the rest of the
    /// process (it outlives its window on purpose, ruling E3-a) and E7's chip said "paused" about an
    /// engine answering every line. The first success after the window closes it.
    /// </summary>
    [Fact]
    public void E8_e_A_local_success_after_the_window_closes_the_gate()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.Unavailable);

        var blocked = gate.Snapshot().BlockedUntil;
        Assert.NotNull(blocked);
        Assert.Equal(GateState.Open, gate.Snapshot().State);

        // Inside the window nothing changes: the chain skips the tier there, so a success can only
        // come from a caller that went round it, and that is not evidence the window was wrong.
        gate.ReportSuccess(selfHealing: true);
        Assert.Equal(blocked, gate.Snapshot().BlockedUntil);
        Assert.Equal(GateState.Open, gate.Snapshot().State);

        // The window elapses. The state still reads Open — that is E3-a, and it is what the chip
        // would render as "paused" for ever.
        clock.Advance(Base + TimeSpan.FromSeconds(1));
        Assert.Equal(GateState.Open, gate.Snapshot().State);

        gate.ReportSuccess(selfHealing: true);

        Assert.Equal(GateState.Closed, gate.Snapshot().State);
        Assert.Null(gate.Snapshot().BlockedUntil);
        Assert.Equal(0, gate.Snapshot().Strikes);
    }

    /// <summary>
    /// <b>And the HTTP gates are untouched</b>, which is the other half of the ruling: the flag is
    /// <c>false</c> by default and no HTTP provider passes it, so for a remote endpoint only the
    /// probe's own report may end a window (E2-h). Same sequence as the case above, without the
    /// flag: the gate stays Open and still hands out a probe.
    /// </summary>
    [Fact]
    public void E8_e_An_ordinary_success_after_the_window_still_leaves_the_probe_to_close_it()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base + TimeSpan.FromSeconds(1));

        gate.ReportSuccess();                                   // an HTTP 200: no token, no flag

        Assert.Equal(GateState.Open, gate.Snapshot().State);
        Assert.NotNull(gate.Snapshot().BlockedUntil);

        // …and the ordinary way out is still the probe, exactly as E2-h leaves it.
        var probe = TakeProbe(gate, clock);
        gate.ReportSuccess(probe);
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
    }

    /// <summary>An outstanding probe still owns the latch: a self-healing report may not resolve
    /// somebody else's half-open window. It cannot happen with one local provider on one gate, and
    /// it is asserted anyway — the flag is a parameter, and a parameter reaches whoever passes
    /// it.</summary>
    [Fact]
    public void E8_e_A_self_healing_success_never_steals_an_outstanding_probe()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var probe = TakeProbe(gate, clock);

        gate.ReportSuccess(selfHealing: true);

        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
        Assert.NotNull(gate.Snapshot().BlockedUntil);

        gate.ReportSuccess(probe);
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
    }

    /// <summary>The <c>AuthFailed</c> sentinel is <see cref="DateTimeOffset.MaxValue"/> and never
    /// elapses, so no success of any kind can wash it out — its exit is re-saving the key (E2-a).
    /// A local engine cannot earn one, and the guard is written rather than assumed.</summary>
    [Fact]
    public void E8_e_The_AuthFailed_sentinel_is_not_something_a_success_can_heal()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.AuthFailed);

        clock.AdvanceMinutes(TranslationPolicy.OpenCapMinutes * 10);
        gate.ReportSuccess(selfHealing: true);

        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().BlockedUntil);
    }

    // ---- ruling E2-h: the probe carries an identity -------------------------------------------

    [Fact]
    public void E2_h_A_success_that_quotes_no_probe_token_cannot_close_the_gate()
    {
        // The race this ruling closes. Both chains share one gate (I9), so a 200 from a request
        // dispatched BEFORE the gate closed behind it lands while the probe is still out. It used
        // to close the gate and reset the whole strike ladder on evidence that proved nothing.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        gate.ReportFailure(TranslationErrorKind.RateLimited);       // two strikes, to prove they survive
        var probe = TakeProbe(gate, clock);

        gate.ReportSuccess();                                       // the stale 200: no token

        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);    // the probe is still out
        Assert.Equal(2, gate.Snapshot().Strikes);
        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        gate.ReportSuccess(probe);                                  // …and the real probe still closes it
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
        Assert.Equal(0, gate.Snapshot().Strikes);
    }

    [Fact]
    public void E2_h_A_failure_that_quotes_no_probe_token_cannot_steal_the_probe()
    {
        // The mirror image: a stale 429 stealing the latch left a provider that is being probed
        // successfully blocked for another full window. The evidence still counts against §5.3 —
        // it is a real 429 — but it may not end the half-open window.
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var probe = TakeProbe(gate, clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);       // the stale 429

        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
        Assert.Equal(2, gate.Snapshot().Strikes);                   // the evidence was not discarded

        gate.ReportSuccess(probe);                                  // the probe itself comes back healthy
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
    }

    [Fact]
    public void E2_h_A_token_from_an_earlier_probe_resolves_nothing_and_neither_does_a_replay()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var first = TakeProbe(gate, clock);
        gate.ReportFailure(TranslationErrorKind.RateLimited, null, first);   // it fails and re-opens

        var second = TakeProbe(gate, clock);
        Assert.NotEqual(first, second);                             // a token is never handed out twice

        gate.ReportSuccess(first);                                  // the first probe's very late 200
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);

        gate.ReportSuccess(second);
        Assert.Equal(GateState.Closed, gate.Snapshot().State);

        gate.ReportFailure(TranslationErrorKind.RateLimited, null, second);  // a replay of a spent token
        Assert.Equal(1, gate.Snapshot().Strikes);                   // …counts as the ordinary failure it is
    }

    [Fact]
    public void E2_h_Only_a_Probe_decision_carries_a_token()
    {
        var (gate, clock) = NewGate();
        Assert.Equal(0, gate.TryEnter(RequestPriority.Interactive).ProbeToken);         // Allow
        for (var i = 1; i < Capacity; i++) gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(0, gate.TryEnter(RequestPriority.Interactive).ProbeToken);         // Wait

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        Assert.Equal(0, gate.TryEnter(RequestPriority.Interactive).ProbeToken);         // Open
        Assert.NotEqual(0, TakeProbe(gate, clock));                                     // Probe
    }

    // ---- IS-12: the overrides hatch (the L1 half of TP-SET-11) --------------------------------

    [Fact]
    public void GatePolicy_Parse_reads_the_overrides_and_drives_short_windows()
    {
        var policy = GatePolicy.Parse(
            """
            { "OpenBaseSeconds": 2, "OpenCapMinutes": 1, "CleanResetMinutes": 3,
              "QuotaOpenMinutes": 4, "SoftCooldownSecs": 5, "BadResponseStrikesToOpen": 2 }
            """);

        Assert.Equal(new GatePolicy(2, 1, 3, 4, 5, 2), policy);

        // And the gate really runs on them — the point of IS-12 is that a field experiment (and a
        // test) never has to wait 30 real minutes.
        var (gate, clock) = NewGate(policy);
        gate.ReportFailure(TranslationErrorKind.BadResponse);
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
        gate.ReportFailure(TranslationErrorKind.BadResponse);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(2), gate.Snapshot().BlockedUntil);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"OpenBaseSeconds\": ")]                      // truncated
    [InlineData("[1, 2, 3]")]                                     // right JSON, wrong shape
    [InlineData("\"a string\"")]
    [InlineData("{ \"OpenBaseSeconds\": \"60\" }")]               // a number written as a string
    [InlineData("{ \"OpenBaseSeconds\": 0 }")]                    // zero = a window that never pauses
    [InlineData("{ \"OpenBaseSeconds\": -5 }")]
    [InlineData("{ \"OpenBaseSeconds\": 1.5 }")]
    [InlineData("{ \"Unrelated\": 7 }")]
    [InlineData("﻿{ \"OpenBaseSeconds\": 60 }")]              // a BOM that survived a hand edit
    public void GatePolicy_Parse_never_throws_and_falls_back_to_the_graded_defaults(string? json)
    {
        // A diagnostic hatch that can crash the app on a typo is worse than no hatch (TP-SET-11 L1).
        var policy = GatePolicy.Parse(json);

        Assert.Equal(GatePolicy.Default, policy);
        Assert.Equal(TranslationPolicy.OpenBaseSeconds, policy.OpenBaseSeconds);
    }

    [Fact]
    public void GatePolicy_Parse_does_not_throw_on_a_string_that_is_not_valid_UTF_16()
    {
        // The one "never throws" hole that is not a JsonException: JsonDocument.Parse(string)
        // transcodes to UTF-8 first and raises ArgumentException on a lone surrogate. Built here
        // rather than as InlineData, because xUnit's data serializer rewrites the character and the
        // case would silently stop testing anything.
        var lone = "{ \"OpenBaseSeconds\": 60 }" + (char)0xD800;

        Assert.Equal(GatePolicy.Default, GatePolicy.Parse(lone));
        Assert.Equal(GatePolicy.Default, GatePolicy.Parse((char)0xDC00 + "{}"));
    }

    [Fact]
    public void GatePolicy_Parse_keeps_the_defaults_for_the_fields_the_hatch_does_not_name()
    {
        // Field by field, so a one-number experiment does not silently zero the other five.
        var policy = GatePolicy.Parse("{ \"openbaseseconds\": 7 }");   // and case does not matter

        Assert.Equal(7, policy.OpenBaseSeconds);
        Assert.Equal(GatePolicy.Default with { OpenBaseSeconds = 7 }, policy);
    }

    [Fact]
    public void GatePolicy_Default_is_TranslationPolicys_graded_table()
    {
        Assert.Equal(new GatePolicy(TranslationPolicy.OpenBaseSeconds, TranslationPolicy.OpenCapMinutes,
                                    TranslationPolicy.CleanResetMinutes, TranslationPolicy.QuotaOpenMinutes,
                                    TranslationPolicy.SoftCooldownSecs, TranslationPolicy.BadResponseStrikesToOpen),
                     GatePolicy.Default);

        // The relationships E2.S7 must preserve while it tunes them from field logs (U9).
        Assert.True(TranslationPolicy.OpenCapMinutes * 60 > TranslationPolicy.OpenBaseSeconds,
                    "the cap must leave room for at least one doubling");
        Assert.True(TranslationPolicy.SoftCooldownSecs < TranslationPolicy.OpenBaseSeconds,
                    "a soft cooldown that outlasts the first open window is not a cooldown");
        Assert.True(TranslationPolicy.BadResponseStrikesToOpen >= 2,
                    "one bad body is a hiccup; opening on it would pause the app on noise");
    }
}

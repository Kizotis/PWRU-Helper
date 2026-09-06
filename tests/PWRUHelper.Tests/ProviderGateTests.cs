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
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds);

    /// <summary>Asserts the decision is `Open` and hands back its `retryAt`, so a wrong outcome
    /// reads as "the gate let a request through", not as a comparison of two default dates.</summary>
    private static DateTimeOffset OpenUntil(GateDecision d)
    {
        Assert.Equal(GateOutcome.Open, d.Outcome);
        return d.RetryAt;
    }

    /// <summary>Walks an open gate to its probe: advance to `blockedUntil`, take the single probe.
    /// The half-open state is reached the only way production reaches it.</summary>
    private static void TakeProbe(ProviderGate gate, FakeClock clock)
    {
        var until = gate.Snapshot().BlockedUntil;
        Assert.NotNull(until);
        if (clock.Now < until!.Value) clock.Advance(until.Value - clock.Now);

        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
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
        clock.Advance(Base);

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
        TakeProbe(gate, clock);

        gate.ReportSuccess();

        Assert.Equal(new GateSnapshot(GateState.Closed, null, 0, TranslationErrorKind.RateLimited),
                     gate.Snapshot());
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    [Fact]
    public void TP_GATE_05_A_failed_probe_re_opens_with_the_window_doubled()
    {
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        TakeProbe(gate, clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);

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
            if (strike > 1) TakeProbe(gate, clock);
            var at = clock.Now;

            gate.ReportFailure(TranslationErrorKind.RateLimited);

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
            if (i > 0) TakeProbe(gate, clock);
            gate.ReportFailure(TranslationErrorKind.RateLimited);
        }
        Assert.Equal(3, gate.Snapshot().Strikes);

        // The provider comes back: the probe succeeds, then CleanResetMinutes of successes.
        TakeProbe(gate, clock);
        gate.ReportSuccess();
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
        TakeProbe(gate, clock);
        gate.ReportFailure(TranslationErrorKind.RateLimited);    // strikes 2

        TakeProbe(gate, clock);
        gate.ReportFailure(TranslationErrorKind.Timeout);        // soft: 5 s, no strike, no forgiveness
        Assert.Equal(2, gate.Snapshot().Strikes);

        TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.RateLimited);

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

        TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(TranslationErrorKind.QuotaExhausted);

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
        TakeProbe(gate, clock);
        var at = clock.Now;
        gate.ReportFailure(kind);
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
        var (gate, clock) = NewGate();
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        TakeProbe(gate, clock);
        gate.ReportSuccess();
        clock.AdvanceMinutes(1);
        gate.ReportSuccess();                                   // a clean run is under way

        var before = gate.Snapshot();
        gate.ReportFailure(TranslationErrorKind.Cancelled);
        gate.ReportFailure(TranslationErrorKind.Cancelled, clock.Now + TimeSpan.FromHours(1));

        Assert.Equal(before, gate.Snapshot());                  // whole-state equality, not field by field

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
        TakeProbe(gate, clock);

        gate.ReportFailure(TranslationErrorKind.Cancelled);

        Assert.Equal(GateState.HalfOpen, gate.Snapshot().State);
        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);
    }

    // ---- TP-GATE-13/14/15: the server's own hint ----------------------------------------------

    [Fact]
    public void TP_GATE_13_Retry_After_delta_seconds_overrides_the_computed_window()
    {
        var (gate, clock) = NewGate();
        var said = TimeSpan.FromSeconds(120);       // literal: it is the header's value, not a policy number
        Assert.NotEqual(Base, said);                // the point of the case is that it is NOT the window

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
    public void TP_GATE_15_A_hint_in_the_past_is_clamped_up_to_one_second(int offsetSeconds)
    {
        var (gate, clock) = NewGate();

        gate.ReportFailure(TranslationErrorKind.RateLimited, clock.Now.AddSeconds(offsetSeconds));

        Assert.Equal(clock.Now + TimeSpan.FromSeconds(1), gate.Snapshot().BlockedUntil);
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
    public void A_hint_bounds_the_soft_cooldown_and_the_quota_window_too()
    {
        // §5.5 says "every non-success response", not "the 429s": a 503 with a Retry-After is the
        // one case where a 5 s cooldown would be demonstrably wrong.
        var (soft, clockA) = NewGate();
        soft.ReportFailure(TranslationErrorKind.Unavailable, clockA.Now + TimeSpan.FromSeconds(45));
        Assert.Equal(clockA.Now + TimeSpan.FromSeconds(45), soft.Snapshot().BlockedUntil);

        var (quota, clockB) = NewGate();
        quota.ReportFailure(TranslationErrorKind.QuotaExhausted, clockB.Now + TimeSpan.FromDays(1));
        Assert.Equal(clockB.Now + Cap, quota.Snapshot().BlockedUntil);   // still clamped

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
    public void Priority_does_not_change_the_breakers_answer_yet()
    {
        // The enum lands here (OQ-a: read-once is Interactive) but nothing reads it until the token
        // bucket (E2.S3). Pinned so the seam is visible: the day this stops being true, it is
        // because §5.4's reserve arrived, and that is E2.S3's case to write.
        var (gate, clock) = NewGate();
        Assert.Equal(GateOutcome.Allow, gate.TryEnter(RequestPriority.Background).Outcome);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        Assert.Equal(OpenUntil(gate.TryEnter(RequestPriority.Interactive)),
                     OpenUntil(gate.TryEnter(RequestPriority.Background)));

        clock.Advance(Base);
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Background).Outcome);
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
    public void GatePolicy_Parse_never_throws_and_falls_back_to_the_graded_defaults(string? json)
    {
        // A diagnostic hatch that can crash the app on a typo is worse than no hatch (TP-SET-11 L1).
        var policy = GatePolicy.Parse(json);

        Assert.Equal(GatePolicy.Default, policy);
        Assert.Equal(TranslationPolicy.OpenBaseSeconds, policy.OpenBaseSeconds);
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

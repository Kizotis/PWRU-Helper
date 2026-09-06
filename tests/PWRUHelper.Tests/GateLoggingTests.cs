using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E2.S6 — one log line per gate transition (§10.2), and nothing else.
///
/// <para><b>TP-LOG-07</b> is the load-bearing case: one open + 200 refused calls + one close is
/// <b>3</b> lines, not 202. <b>TP-LOG-09</b>'s gate half is the other: after a scripted episode,
/// <c>Logging.ReadRecent()</c> — which is literally what the About tab's "Copy error report" copies
/// (<c>MainWindow.xaml.cs:312-322</c>) — holds the whole open/half-open/closed timeline. The AC-2
/// negatives extend E1.S5's I11 discipline to this family: no user text, no key, no URL, no
/// <c>q=</c>, ever.</para>
///
/// <para><b>Two statics, two reasons</b> (IS-5): these cases drive <c>ProviderGates</c> <i>and</i>
/// count lines in a process-wide logging facade, so they join the non-parallel <c>Gates</c>
/// collection and take <see cref="Logging.DirectoryOverride"/> the way <c>LoggingTests:105-110</c>
/// does — set it, restore the previous value in a <c>finally</c>, never assume a clean file. The
/// gate log's storm valve is process-wide too, so every counting case resets it first.</para>
///
/// <para>No <c>Task.Delay</c> anywhere (CI-3): every window, escalation and probe is driven on the
/// registry's injected clock (IS-6).</para>
/// </summary>
[Collection("Gates")]
public class GateLoggingTests : GatesTestBase
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
        public void AdvanceSeconds(double s) => Advance(TimeSpan.FromSeconds(s));
    }

    /// <summary>Its own log directory for the duration of one case, and the previous override back
    /// afterwards — <c>TestLogRedirect</c>'s process-wide one, which no case may leave broken.</summary>
    private sealed class TempLog : IDisposable
    {
        private readonly string? _previous = Logging.DirectoryOverride;
        public string Dir { get; }

        public TempLog()
        {
            Dir = Directory.CreateTempSubdirectory("pwru-gatelog-").FullName;
            Logging.DirectoryOverride = Dir;
            GateLog.ResetSuppression();          // the run it counts is process-wide
        }

        public void Dispose()
        {
            Logging.DirectoryOverride = _previous;
            GateLog.ResetSuppression();
            try { Directory.Delete(Dir, recursive: true); } catch { /* the case already made its point */ }
        }
    }

    private static readonly TimeSpan Base = TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds);
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds);

    /// <summary>Every <c>gate …</c> line written so far, with <c>Logging</c>'s own timestamp and
    /// level stripped — the builder emits from <c>gate </c> onwards and this asserts on exactly
    /// that.</summary>
    private static List<string> GateLines(TempLog log) => Lines(log).Item1;

    /// <summary>The gate lines, and their levels beside them (§10.2: OPEN is a WARN, the rest INFO).</summary>
    private static (List<string>, List<string>) Lines(TempLog log)
    {
        var path = Path.Combine(log.Dir, "log.txt");
        var lines = new List<string>();
        var levels = new List<string>();
        if (!File.Exists(path)) return (lines, levels);

        foreach (var raw in File.ReadAllLines(path))
        {
            var open = raw.IndexOf('[');
            var close = raw.IndexOf("] ", StringComparison.Ordinal);
            if (open < 0 || close < open) continue;
            var body = raw[(close + 2)..];
            if (!body.StartsWith("gate ", StringComparison.Ordinal)) continue;
            lines.Add(body);
            levels.Add(raw[(open + 1)..close]);
        }
        return (lines, levels);
    }

    private static string Local(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Opens the gate with a real 429 and hands back the probe token once the window has
    /// elapsed — the scripted episode §10.2 describes.</summary>
    private static long TakeProbe(ProviderGate gate, FakeClock clock)
    {
        var decision = gate.TryEnter(RequestPriority.Interactive);
        Assert.Equal(GateOutcome.Probe, decision.Outcome);
        return decision.ProbeToken;
    }

    private static ProviderGate Gate(FakeClock clock, string id = ProviderIds.GoogleDict)
    {
        ProviderGates.Clock = clock.Read;
        return ProviderGates.For(id);
    }

    // ---- TP-LOG-07: one line per TRANSITION, never per skipped request -------------------------

    [Fact]
    public void TP_LOG_07_an_open_window_two_hundred_refusals_and_a_close_are_three_lines()
    {
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        var opened = clock.Now;
        gate.ReportFailure(TranslationErrorKind.RateLimited);          // CLOSED -> OPEN

        for (int i = 0; i < 200; i++)                                  // …and 200 refusals
            Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        clock.Advance(Base);
        var probe = TakeProbe(gate, clock);                            // OPEN -> HALF-OPEN
        gate.ReportSuccess(probe);                                     // HALF-OPEN -> CLOSED

        var (lines, levels) = Lines(log);
        Assert.Equal(3, lines.Count);                                  // not 202 — the whole AC

        Assert.Equal(
            $"gate provider=google-dict CLOSED->OPEN kind=RateLimited strikes=1 for=60s " +
            $"until={Local(opened + Base)} reason=failure", lines[0]);
        Assert.Equal(
            $"gate provider=google-dict OPEN->HALF-OPEN kind=RateLimited strikes=1 for=- " +
            $"until={Local(opened + Base)} reason=probe", lines[1]);
        Assert.Equal(
            "gate provider=google-dict HALF-OPEN->CLOSED kind=RateLimited strikes=0 for=- " +
            "until=- reason=success", lines[2]);

        // §10.2's own example levels: an OPEN is a warning, a recovery is not.
        Assert.Equal(new[] { "WARN", "INFO", "INFO" }, levels);
    }

    [Fact]
    public void A_refused_call_a_wait_and_a_strike_reset_write_nothing()
    {
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        // A rate-ceiling Wait: sub-second, one per request, and never a state edge (E2-b).
        gate.DrainBucketForTests();
        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Empty(GateLines(log));

        // A refusal: 50 of them, while the gate is open, are 50 non-events.
        clock.AdvanceSeconds(TranslationPolicy.MinSpacingMs / 1000.0 * TranslationPolicy.BucketCapacity);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        for (int i = 0; i < 50; i++) gate.TryEnter(RequestPriority.Background);
        Assert.Single(GateLines(log));

        // A strike reset (§5.7 makes it a SAVE trigger) and an Open->Open escalation that lengthens
        // a standing window both reach the hook with the state unmoved. E2-b gives neither a line,
        // and the registry's own seam is where that is provable without inventing a gate state.
        ProviderGates.NoteTransition(ProviderIds.GoogleDict, GateState.Closed,
            new GateSnapshot(GateState.Closed, null, 0, TranslationErrorKind.RateLimited));
        ProviderGates.NoteTransition(ProviderIds.GoogleDict, GateState.Open,
            new GateSnapshot(GateState.Open, clock.Now + Base * 4, 3, TranslationErrorKind.RateLimited));

        Assert.Single(GateLines(log));
    }

    [Theory]
    [InlineData(TranslationErrorKind.Unavailable)]
    [InlineData(TranslationErrorKind.Timeout)]
    [InlineData(TranslationErrorKind.Network)]
    [InlineData(TranslationErrorKind.Unknown)]
    public void A_soft_cooldown_cycle_is_invisible(TranslationErrorKind kind)
    {
        // E2-b: no line for soft cooldowns — and that has to cover the whole 5-second cycle the
        // cooldown creates, or a dead DNS writes a probe-grant line every few seconds for as long
        // as the provider is unreachable. It is exactly the rule E2.S4's WorthAWrite applies to the
        // file, which is what the two agreeing means.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        long probe = 0;
        for (int i = 0; i < 5; i++)
        {
            gate.ReportFailure(kind, null, probe);                     // CLOSED/HALF-OPEN -> OPEN
            clock.AdvanceSeconds(TranslationPolicy.SoftCooldownSecs);
            var decision = gate.TryEnter(RequestPriority.Interactive); // OPEN -> HALF-OPEN
            Assert.Equal(GateOutcome.Probe, decision.Outcome);
            probe = decision.ProbeToken;
        }

        Assert.Empty(GateLines(log));
    }

    [Fact]
    public void A_soft_outage_that_recovers_still_says_so()
    {
        // The other half of the rule above: landing on CLOSED is always logged, whatever opened the
        // gate. "It never clears" is the feeling this story exists to replace, so the line that says
        // it cleared may never be filtered out.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.Network);
        clock.AdvanceSeconds(TranslationPolicy.SoftCooldownSecs);
        gate.ReportSuccess(TakeProbe(gate, clock));

        var line = Assert.Single(GateLines(log));
        Assert.Equal("gate provider=google-dict HALF-OPEN->CLOSED kind=Network strikes=0 " +
                     "for=- until=- reason=success", line);
    }

    [Fact]
    public void A_bad_response_below_the_threshold_writes_nothing_and_the_third_one_opens()
    {
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        for (int i = 1; i < TranslationPolicy.BadResponseStrikesToOpen; i++)
        {
            gate.ReportFailure(TranslationErrorKind.BadResponse);
            Assert.Empty(GateLines(log));                              // a soft strike is not an edge
        }

        gate.ReportFailure(TranslationErrorKind.BadResponse);          // …the one that opens it
        var line = Assert.Single(GateLines(log));
        Assert.Contains("CLOSED->OPEN kind=BadResponse", line);
        Assert.Contains("strikes=0", line);                            // a soft strike is not a hard one
    }

    [Fact]
    public void TP_GATE_12_a_cancel_writes_no_line()
    {
        // I3: a genuine user cancel is not a provider failure. TP-GATE-12 already pins that it
        // changes no byte of state; this pins that it changes no byte of the log either.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.Cancelled);
        gate.ReportFailure(TranslationErrorKind.Cancelled, clock.Now + TimeSpan.FromHours(1));
        gate.ReportSuccess();

        Assert.Empty(GateLines(log));
    }

    [Fact]
    public void A_deferred_background_caller_has_not_transitioned()
    {
        // T3: standing aside for ProbeDeferMs is not a probe grant. No latch, no line.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var opened = GateLines(log).Count;
        clock.Advance(Base);

        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Background).Outcome);
        Assert.Equal(opened, GateLines(log).Count);

        // …and the Interactive caller that does take it writes exactly one.
        Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Equal(opened + 1, GateLines(log).Count);
    }

    [Fact]
    public void A_probe_that_cannot_afford_a_token_has_not_transitioned()
    {
        // T3's second edge (E2.S3's T4): the latch and the token are taken together or neither, so
        // a broke probe-eligible caller is a Wait — not a HALF-OPEN.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base);
        gate.DrainBucketForTests();

        Assert.Equal(GateOutcome.Wait, gate.TryEnter(RequestPriority.Interactive).Outcome);
        Assert.Single(GateLines(log));                                 // the OPEN, and nothing since
    }

    // ---- the escalation E2.S7 reads out of these lines ----------------------------------------

    [Fact]
    public void Three_successive_opening_failures_escalate_in_the_line()
    {
        // U9 / E2.S7 reads `strikes=` and `for=` out of the file. The RELATIONSHIP is asserted and
        // not the literals: E2.S1's rule is that E2.S7 may tune the numbers.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);           // strike 1
        for (int i = 0; i < 2; i++)
        {
            clock.Advance(gate.Snapshot().BlockedUntil!.Value - clock.Now);
            var probe = TakeProbe(gate, clock);
            gate.ReportFailure(TranslationErrorKind.RateLimited, null, probe);   // strikes 2, then 3
        }

        var opens = GateLines(log).Where(l => l.Contains("->OPEN ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, opens.Count);

        var strikes = opens.Select(l => Field(l, "strikes")).Select(int.Parse).ToList();
        var windows = opens.Select(l => Field(l, "for").TrimEnd('s')).Select(long.Parse).ToList();

        Assert.Equal(new[] { 1, 2, 3 }, strikes);                      // it increments
        Assert.Equal(windows[0] * 2, windows[1]);                      // …and the window doubles
        Assert.Equal(windows[1] * 2, windows[2]);

        // The first is CLOSED->OPEN; the two that follow a failed probe are HALF-OPEN->OPEN.
        Assert.StartsWith("gate provider=google-dict CLOSED->OPEN ", opens[0]);
        Assert.StartsWith("gate provider=google-dict HALF-OPEN->OPEN ", opens[1]);
    }

    [Fact]
    public void An_auth_block_renders_as_until_key_change_and_the_key_save_clears_it()
    {
        // T1's edge case: AuthFailed opens with DateTimeOffset.MaxValue (§5.3), so `for=` would be
        // an eight-digit number of seconds and `until=` a year nobody will see. One spelling, kept:
        // E2.S7 and the owner will grep for it.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock, ProviderIds.DeepL);

        gate.ReportFailure(TranslationErrorKind.AuthFailed);
        Assert.Equal("gate provider=deepl CLOSED->OPEN kind=AuthFailed strikes=0 " +
                     "for=until-key-change until=- reason=failure", GateLines(log)[0]);

        ProviderGates.ClearAuthBlock(ProviderIds.DeepL);
        Assert.Equal("gate provider=deepl OPEN->CLOSED kind=AuthFailed strikes=0 " +
                     "for=- until=- reason=clear", GateLines(log)[1]);
        Assert.Equal(2, GateLines(log).Count);
    }

    // ---- AC 2 / I11: what may never be in one of these lines -----------------------------------

    [Fact]
    public void No_gate_line_can_carry_user_text_a_key_or_a_url()
    {
        // The load-bearing test of AC 2. The line's inputs are a provider id, a Kind name, an int
        // and two instants — so the adversarial value is fed through the only free-form one, the id,
        // and it comes out bounded, printable, single-line ASCII with the sentence gone.
        const string Sentinel = "приветствую тебя воин";
        const string Key = "0123abcd-4567-89ef-0123-456789abcdef:fx";
        var hostile = $"google-dict?q={Sentinel}&auth_key={Key} https://translate.google.com/\nsecond";

        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock, hostile);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base);
        gate.ReportSuccess(TakeProbe(gate, clock));

        var all = string.Join("\n", GateLines(log));
        Assert.Equal(3, GateLines(log).Count);

        foreach (var banned in new[] { Sentinel, Key, "q=", "auth_key", "http://", "https://", "://" })
            Assert.DoesNotContain(banned, all, StringComparison.OrdinalIgnoreCase);

        // …and every line is still one bounded ASCII line, so nothing was smuggled through as a
        // second line or a control character.
        foreach (var line in GateLines(log))
        {
            Assert.Matches(@"^gate provider=[!-~]{1,24} ", line);
            Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        }
    }

    // ---- AC 3 / TP-LOG-09: the report carries the whole episode --------------------------------

    [Fact]
    public void TP_LOG_09_the_copied_report_holds_the_open_half_open_closed_history()
    {
        // AC 3. "Copy error report" (MainWindow.xaml.cs:312-322) copies Logging.ReadRecent() and
        // nothing else, so this is the assertion that makes §10.3's claim true — E1.S5 deferred the
        // gate half of TP-LOG-09 to this story, and this is it.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base);
        var probe = TakeProbe(gate, clock);
        gate.ReportFailure(TranslationErrorKind.RateLimited, null, probe);   // it did not clear yet
        clock.Advance(Base * 2);
        gate.ReportSuccess(TakeProbe(gate, clock));                          // …and now it did

        var report = Logging.ReadRecent();
        Assert.Contains("CLOSED->OPEN kind=RateLimited strikes=1", report);
        Assert.Contains("OPEN->HALF-OPEN", report);
        Assert.Contains("HALF-OPEN->OPEN kind=RateLimited strikes=2", report);
        Assert.Contains("HALF-OPEN->CLOSED", report);
        Assert.Contains("reason=success", report);
    }

    // ---- the fifth edge E2-b names: reload from disk -------------------------------------------

    [Fact]
    public void A_gate_restored_from_disk_says_so_once()
    {
        // E2-b's "reload-from-disk". E2.S4 seeds gates inside EnsureLoaded, which is where the line
        // is written: a seed is not a §5.2 transition (nothing moved — the gate was born there) and
        // routing it through NoteTransition would queue a save on every single start.
        var clock = new FakeClock();
        using var log = new TempLog();
        using var state = new TempGateState();
        ProviderGates.Clock = clock.Read;

        var until = clock.Now + TimeSpan.FromMinutes(5);
        File.WriteAllText(state.Path,
            "{\"version\":1,\"providers\":{\"google-dict\":{\"blockedUntil\":\"" +
            until.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) +
            "\",\"strikes\":3,\"lastKind\":\"RateLimited\"}}}");

        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        Assert.Empty(GateLines(log));                                  // construction reads nothing

        Assert.Equal(GateOutcome.Open, gate.TryEnter(RequestPriority.Interactive).Outcome);

        var line = Assert.Single(GateLines(log));
        Assert.Equal($"gate provider=google-dict CLOSED->OPEN kind=RateLimited strikes=3 " +
                     $"for=300s until={Local(until)} reason=reload", line);

        // …once. The load is once per process, and the refusals after it are not transitions.
        for (int i = 0; i < 20; i++) gate.TryEnter(RequestPriority.Interactive);
        Assert.Single(GateLines(log));
    }

    // ---- the storm valve -----------------------------------------------------------------------

    [Fact]
    public void A_gate_that_flaps_on_one_edge_is_valved_and_the_summary_says_how_many()
    {
        // The one pattern that repeats a SINGLE edge, and it is reachable: every caller that takes
        // the probe is cancelled before it reports (LIVE switched off mid-probe — I3 means such a
        // caller never reports at all), so the gate re-arms one ProbeTimeout later and grants
        // another. Without a valve that is one OPEN->HALF-OPEN line every RequestTimeoutSeconds for
        // as long as it lasts, which is what rolls a 1 MB log over the incident that started it.
        var clock = new FakeClock();
        using var log = new TempLog();
        var gate = Gate(clock);

        gate.ReportFailure(TranslationErrorKind.RateLimited);
        clock.Advance(Base);

        for (int i = 0; i < LogSuppressor.Threshold + 6; i++)
        {
            Assert.Equal(GateOutcome.Probe, gate.TryEnter(RequestPriority.Interactive).Outcome);
            clock.Advance(ProbeTimeout);                               // abandoned; the gate re-arms
        }

        var probes = GateLines(log).Count(l => l.Contains("OPEN->HALF-OPEN", StringComparison.Ordinal)
                                            && !l.Contains("suppressed=", StringComparison.Ordinal));
        Assert.Equal(LogSuppressor.Threshold, probes);                 // the run went quiet after ten

        // …and nothing is silently lost: the next DIFFERENT line flushes a summary carrying the count.
        var last = gate.TryEnter(RequestPriority.Interactive);         // the seventh held line
        gate.ReportSuccess(last.ProbeToken);                           // HALF-OPEN -> CLOSED

        var summaries = GateLines(log)
            .Where(l => l.Contains("suppressed=", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(summaries);
        foreach (var s in summaries)
            Assert.Matches(@"^gate provider=google-dict OPEN->HALF-OPEN suppressed=\d+ in=\d+s ", s);

        // Every held line is accounted for — one per summary window while the storm runs, and the
        // last of them flushed by the CLOSED line that ends the run. Seven were held: sixteen probe
        // grants above, plus the one just taken, minus the ten written in full.
        Assert.Equal(7, summaries.Sum(s => int.Parse(Field(s, "suppressed"))));
        Assert.Contains(GateLines(log), l => l.Contains("HALF-OPEN->CLOSED", StringComparison.Ordinal));
    }

    // ---- the pure builder ----------------------------------------------------------------------

    [Fact]
    public void The_line_builder_is_a_function_of_its_arguments()
    {
        // T1: no clock, no I/O, everything it renders decided by its caller — the same discipline as
        // RequestLog.Line, and for the same reason.
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new GateSnapshot(GateState.Open, now + TimeSpan.FromSeconds(90), 2,
                                        TranslationErrorKind.Blocked);

        var line = GateLog.Line(ProviderIds.Edge, GateState.Closed, snapshot, "failure", now);

        Assert.Equal($"gate provider=edge CLOSED->OPEN kind=Blocked strikes=2 for=90s " +
                     $"until={Local(now + TimeSpan.FromSeconds(90))} reason=failure", line);
        Assert.Equal(line, GateLog.Line(ProviderIds.Edge, GateState.Closed, snapshot, "failure", now));
    }

    private static string Field(string line, string name)
    {
        var m = Regex.Match(line, $@"\b{name}=(\S+)");
        Assert.True(m.Success, $"{name}= is not in: {line}");
        return m.Groups[1].Value;
    }
}

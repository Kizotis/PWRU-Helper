using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The registry: one gate per provider id, shared by both chains (I9), plus the three test seams
/// this repo has twice learned to add on day one (IS-1 <c>PathOverride</c>, IS-4
/// <c>ResetForTests</c>, IS-6 one shared <c>Clock</c>).
///
/// <para>Every case here touches process-global static state, so the class joins the non-parallel
/// <c>Gates</c> collection and derives from <see cref="GatesTestBase"/>, whose constructor and
/// <c>Dispose</c> reset the registry around each case (IS-5 / R4). No case reads the wall clock, no
/// case sleeps (CI-3) and no case touches the real <c>%AppData%</c> (I10).</para>
/// </summary>
[Collection("Gates")]
public class ProviderGatesTests : GatesTestBase
{
    // ---- the fake clock (IS-6) ----------------------------------------------------------------

    private sealed class FakeClock
    {
        // A fixed instant, and deliberately one the wall clock cannot be: the case that proves
        // ResetForTests puts the WALL clock back compares against DateTimeOffset.UtcNow with a
        // one-minute tolerance, so a fake pinned at today's date would let that assertion pass on a
        // run that restored nothing. A leaked clock has to be visibly not-now.
        public DateTimeOffset Now { get; private set; } = new(2001, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void AdvanceMinutes(double m) => Now += TimeSpan.FromMinutes(m);
    }

    private static readonly TimeSpan Base = TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds);

    // ---- AC 1: one gate per id, the same one every time ---------------------------------------

    [Fact]
    public void For_hands_the_same_gate_to_every_caller_of_an_id()
    {
        // This identity IS invariant I9. The two read chains are built in MainWindow's constructor
        // (E3.S7) and the write chain is rebuilt there and on every key save
        // (MainWindow.Translate.cs); before this registry they held independent gates, so
        // the Translator tab kept hammering the provider LIVE had already been told to leave alone.
        var first = ProviderGates.For(ProviderIds.GoogleDict);
        var second = ProviderGates.For(ProviderIds.GoogleDict);

        Assert.Same(first, second);
        Assert.NotSame(first, ProviderGates.For(ProviderIds.Edge));
    }

    [Fact]
    public void A_block_taken_by_one_caller_is_seen_by_the_next()
    {
        // The same thing stated as behaviour rather than as reference identity, because behaviour
        // is what the two chains actually share.
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        ProviderGates.For(ProviderIds.DeepL).ReportFailure(TranslationErrorKind.RateLimited);

        var seenByTheOtherChain = ProviderGates.For(ProviderIds.DeepL).Snapshot();
        Assert.Equal(GateState.Open, seenByTheOtherChain.State);
        Assert.Equal(clock.Now + Base, seenByTheOtherChain.BlockedUntil);
    }

    [Fact]
    public void For_is_safe_to_call_from_every_thread_at_once()
    {
        // Both chains, the LIVE loop and the Translator tab call For concurrently. The assertion is
        // a COUNT of distinct instances, never a timing (CI-3).
        var seen = new ConcurrentBag<ProviderGate>();

        Parallel.For(0, 64, _ => seen.Add(ProviderGates.For(ProviderIds.GoogleGtx)));

        Assert.Equal(64, seen.Count);
        Assert.Single(seen.Distinct());
    }

    [Fact]
    public void ProviderIds_declares_the_six_ids_of_the_rebuild()
    {
        // Spelling an id as a literal anywhere else is how a typo becomes two gates, so the set and
        // its exact strings are pinned here. `google-gtx` is deliberately today's endpoint's id
        // BEFORE E3.S6's rename — E1.S5's log line already writes it.
        Assert.Equal(
            new[] { "google-dict", "edge", "google-gtx", "deepl", "azure", "bergamot" },
            ProviderIds.All);
        Assert.Equal(ProviderIds.All.Count, ProviderIds.All.Distinct().Count());
    }

    // ---- AC 2: the three seams ----------------------------------------------------------------

    [Fact]
    public void The_registrys_clock_is_the_clock_every_gate_it_builds_runs_on()
    {
        // IS-6. Two clocks would mean a blockedUntil restored from disk (E2.S4) is compared against
        // a "now" it was never measured against. Nothing could fail this before the registry
        // existed, which is why the case lands with it.
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.Azure);
        gate.ReportFailure(TranslationErrorKind.RateLimited);

        Assert.Equal(clock.Now + Base, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void A_clock_installed_after_a_gate_exists_is_honoured_by_that_gate()
    {
        // The order a test naturally writes — build the gate, then take control of time — must work,
        // so gates hold the registry's clock BY REFERENCE and never a copy of its value.
        var gate = ProviderGates.For(ProviderIds.Edge);

        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        gate.ReportFailure(TranslationErrorKind.RateLimited);

        Assert.Equal(clock.Now + Base, gate.Snapshot().BlockedUntil);
    }

    [Fact]
    public void ResetForTests_clears_the_registry()
    {
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        var before = ProviderGates.For(ProviderIds.GoogleDict);
        before.ReportFailure(TranslationErrorKind.RateLimited);
        Assert.Equal(GateState.Open, before.Snapshot().State);

        ProviderGates.ResetForTests();

        Assert.Empty(ProviderGates.All());                        // nothing survived the reset

        var after = ProviderGates.For(ProviderIds.GoogleDict);
        Assert.NotSame(before, after);
        Assert.Equal(GateState.Closed, after.Snapshot().State);   // a fresh, closed gate
    }

    [Fact]
    public void ResetForTests_also_puts_the_clock_the_path_and_the_hook_back()
    {
        // IS-4's real job: everything static, not just the map. A leaked fake clock is the worst of
        // the three — it makes the NEXT case pass or fail depending on what ran before it.
        using (var temp = new TempGateState())
        {
            ProviderGates.Clock = new FakeClock().Read;
            ProviderGates.TransitionHook = (_, _, _) => { };
            Assert.Equal(temp.Path, ProviderGates.StatePath);

            ProviderGates.ResetForTests();

            Assert.Null(ProviderGates.PathOverride);
            // Back to the PRODUCTION default rather than to null: since E2.S6 the hook is where the
            // gate log line hangs, installed as a field initialiser, so "put the process back where
            // it started" means reinstalling it. Nulling it here would let one case that stubs the
            // hook leave the app's own observability off for the rest of the run.
            Assert.Equal((Action<string, GateState, GateSnapshot>)GateLog.Note,
                         ProviderGates.TransitionHook);
        }

        // The wall clock is back: "now" is a real instant again, not the fixture's 2001-01-01 12:00.
        var drift = (ProviderGates.Clock() - DateTimeOffset.UtcNow).Duration();
        Assert.True(drift < TimeSpan.FromMinutes(1), $"the registry clock is still a fake ({drift})");
    }

    [Fact]
    public void ClearAuthBlock_closes_the_gate_it_names_and_leaves_the_others_alone()
    {
        // TP-GATE-09's second half. The registry's part is the routing: the key-save handler knows
        // an id (DeepL today at MainWindow.Translate.cs:235-244, Azure in E6.S3), not a gate.
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var deepl = ProviderGates.For(ProviderIds.DeepL);
        deepl.ReportFailure(TranslationErrorKind.AuthFailed);
        Assert.Equal(DateTimeOffset.MaxValue, deepl.Snapshot().BlockedUntil);

        var azure = ProviderGates.For(ProviderIds.Azure);
        azure.ReportFailure(TranslationErrorKind.RateLimited);
        var standing = azure.Snapshot().BlockedUntil;

        ProviderGates.ClearAuthBlock(ProviderIds.DeepL);

        Assert.Equal(GateState.Closed, deepl.Snapshot().State);   // the only exit from MaxValue
        Assert.Equal(standing, azure.Snapshot().BlockedUntil);    // a neighbour's 429 is untouched
    }

    [Fact]
    public void ClearAuthBlock_does_not_lift_a_window_a_429_opened()
    {
        // T6, second half, as ruling E2-i settles it: a key save clears the ACCOUNT-scoped blocks
        // (AuthFailed, QuotaExhausted) and nothing else. A RateLimited window is a counter on the
        // provider's side keyed to this connection's address — no key the user types moves it, and
        // clearing it would turn "I saved my key" into "resume hammering the provider that just
        // said stop", which is the bug this whole epic exists to remove.
        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;

        var gate = ProviderGates.For(ProviderIds.DeepL);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var standing = gate.Snapshot().BlockedUntil;

        ProviderGates.ClearAuthBlock(ProviderIds.DeepL);

        var after = ProviderGates.Snapshot(ProviderIds.DeepL)!;
        Assert.Equal(GateState.Open, after.State);
        Assert.Equal(standing, after.BlockedUntil);
        Assert.Equal(1, after.Strikes);           // the IP-scoped evidence stands too
    }

    [Fact]
    public void ClearAuthBlock_for_an_id_that_has_no_gate_does_nothing_and_creates_nothing()
    {
        // A key-save handler may fire for a provider that has never made a request. Creating a gate
        // to clear it would be harmless but dishonest — All() is what E7 renders.
        ProviderGates.ClearAuthBlock(ProviderIds.Bergamot);

        Assert.Empty(ProviderGates.All());
        Assert.Null(ProviderGates.Snapshot(ProviderIds.Bergamot));
    }

    // ---- the poll surface E7 reads (ruling R-2: poll, no event) --------------------------------

    [Fact]
    public void Snapshot_reports_a_live_gate_and_never_creates_one()
    {
        // The status chip polls at 1 Hz. If polling created gates, six of them would exist a second
        // after the window opens, for providers the user has not even configured.
        Assert.Null(ProviderGates.Snapshot(ProviderIds.GoogleDict));
        Assert.Empty(ProviderGates.All());

        var clock = new FakeClock();
        ProviderGates.Clock = clock.Read;
        ProviderGates.For(ProviderIds.GoogleDict).ReportFailure(TranslationErrorKind.QuotaExhausted);

        var snap = ProviderGates.Snapshot(ProviderIds.GoogleDict);
        Assert.NotNull(snap);
        Assert.Equal(GateState.Open, snap!.State);
        Assert.Equal(new[] { ProviderIds.GoogleDict }, ProviderGates.All().Keys);
    }

    [Fact]
    public void All_reports_every_live_gate_once()
    {
        ProviderGates.For(ProviderIds.Edge);
        ProviderGates.For(ProviderIds.DeepL);
        ProviderGates.For(ProviderIds.Edge);          // the same gate, not a second entry

        var all = ProviderGates.All();

        Assert.Equal(2, all.Count);
        Assert.Equal(GateState.Closed, all[ProviderIds.Edge].State);
        Assert.Equal(GateState.Closed, all[ProviderIds.DeepL].State);
    }

    // ---- the E2.S4 / E2.S6 seam ---------------------------------------------------------------

    [Fact]
    public void The_transition_hook_is_one_delegate_and_a_throwing_one_cannot_break_a_request()
    {
        // E2.S4 hangs the debounced save here and E2.S6 the log line. It is not an event and not a
        // callback list (ruling R-2), and observability may never fail a translation.
        // It also carries everything both consumers need and nothing they would have to keep their
        // own shadow copy of: the id (a gate does not know its own), the state it came FROM, and the
        // snapshot it landed on — new state, reason, strikes, blockedUntil. That is E2.S6's
        // OnTransition(from, to, kind, strikes, blockedUntil) with the id in front of it.
        var seen = new List<string>();
        ProviderGates.TransitionHook = (id, from, to) =>
        {
            seen.Add($"{id} {from}->{to.State} {to.LastKind}");
            throw new InvalidOperationException("disk full");
        };

        ProviderGates.NoteTransition(ProviderIds.DeepL, GateState.Closed,
            new GateSnapshot(GateState.Open, null, 1, TranslationErrorKind.RateLimited));

        Assert.Equal(new[] { "deepl Closed->Open RateLimited" }, seen);

        ProviderGates.TransitionHook = null;
        ProviderGates.NoteTransition(ProviderIds.DeepL, GateState.Open,
            new GateSnapshot(GateState.Closed, null, 0, null));
    }

    // ---- AC 3 / T4: the guard that keeps the suite off the developer's own state ---------------

    [Fact]
    public void A_gate_test_never_points_at_the_real_AppData_state_file()
    {
        // The same guard as LoggingTests.The_test_run_never_writes_to_the_real_AppData_log, for the
        // file E2.S4 will write. This repo has been bitten twice — the suite rewrote a developer's
        // settings.json, then seeded a real error report with fixture failures — and both times the
        // fix was this pair: a static PathOverride and a case that fails if it is ever bypassed.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PWRUHelper", "provider-state.json");

        // The assertion that can actually fail, and the one LoggingTests:95 makes: a redirect is
        // installed for the WHOLE run and this case is not the thing installing it. Asserting the
        // path inside a `using (new TempGateState())` would only prove the line above it ran; the
        // failure mode T4 exists to stop is a future E2.S4 case that never opens one.
        Assert.NotNull(ProviderGates.PathOverride);
        Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(ProviderGates.StatePath));

        using (var temp = new TempGateState())
        {
            Assert.Equal(temp.Path, ProviderGates.StatePath);
            Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(ProviderGates.StatePath));
            Assert.True(Directory.Exists(Path.GetDirectoryName(temp.Path)!),
                "TempGateState must own a real, throwaway directory for E2.S4 to write into");
        }

        // …and Dispose hands the run-wide redirect back, not the real path.
        Assert.Equal(TestGateStateRedirect.Path, ProviderGates.PathOverride);
    }

    [Fact]
    public void Every_test_class_that_touches_the_registry_joins_this_collection()
    {
        // R4 / IS-5, as a scan rather than as a convention: static state plus xUnit's default
        // per-collection parallelism is risk R-08, and the day someone writes a gate case in a class
        // without [Collection("Gates")] the failure is a flake in a DIFFERENT file. The repo already
        // owns this shape (TranslationErrorsTests.No_production_source_names_Kind_Cancelled reads the
        // sources); this is the same idea pointed at the test tree.
        var root = TestSourceRoot();
        var offenders = new List<string>();
        var joined = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            // The two files that ARE the seam, plus anything under bin/obj.
            if (name is "GatesCollection.cs" or "TempGateState.cs" or "TestGateStateRedirect.cs"
                     or "TestBackoffRedirect.cs") continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);
            // Code only: a `//` or `///` mention of the type is prose, and three files legitimately
            // name it in a comment to point the reader at the registry.
            var code = string.Join("\n", text.Split('\n').Select(l =>
            {
                var cut = l.IndexOf("//", StringComparison.Ordinal);
                return cut >= 0 ? l[..cut] : l;
            }));

            // Two triggers, not one. Naming the registry is the obvious way to touch it; DERIVING
            // GatesTestBase is the other, and it is the one that was quietly routed around — the
            // base's constructor and Dispose reset the registry and re-point its clock without the
            // derived file ever writing `ProviderGates.`, so a scan keyed on that literal alone
            // could not see it (E2.S5's review).
            if (!code.Contains("ProviderGates.", StringComparison.Ordinal) &&
                !code.Contains(": GatesTestBase", StringComparison.Ordinal)) continue;

            // …and two accepted answers. "Gates" is the collection; "log-file" is the one class
            // that legitimately cannot join a second collection (RequestLogTests owns the log-file
            // override AND drives real providers), and it carries DisableParallelization for
            // exactly this reason — so it is serialised against "Gates" like a member would be.
            if (code.Contains("[Collection(\"Gates\")]", StringComparison.Ordinal)) joined.Add(name);
            else if (code.Contains("[Collection(LogFileCollection.Name)]", StringComparison.Ordinal))
                joined.Add(name);
            else offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "these test files touch ProviderGates without joining the non-parallel Gates collection: "
            + string.Join(", ", offenders));
        // …and the scan itself is not vacuous: it must at least have found this file.
        Assert.Contains("ProviderGatesTests.cs", joined);
    }

    /// <summary>The test project's source directory, walked up from the assembly location the way
    /// <c>TranslationErrorsTests</c> walks up to the repo root.</summary>
    private static string TestSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.Tests.csproj")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

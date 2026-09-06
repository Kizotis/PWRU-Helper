using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <c>architecture-cible.md</c> §6 — the ordered chain, as the sentence Epic 3 is judged on:
/// <b>a paused tier is skipped, not fatal, and skipping it costs no request at all.</b>
///
/// <para>The class that used to live in <c>TranslationBackendTests.cs</c> as
/// <c>FallbackTranslatorTests</c> is here, re-pointed at <see cref="ChainTranslator"/>: the two I3
/// guards keep their names on purpose (their names are the regression), and the other two became
/// TP-CHN-02 / TP-CHN-03. It joins the non-parallel <c>Gates</c> collection because every chain
/// case drives a real <see cref="ProviderGate"/> (IS-5), reads the run-wide injected clock (IS-6)
/// and asserts on a <see cref="FakeHandler"/>'s request count (IS-11). Nothing sleeps and nothing
/// reaches the network (CI-3, IS-10).</para>
/// </summary>
[Collection("Gates")]
public class ChainTranslatorTests : GatesTestBase
{
    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";

    /// <summary>The double the deleted <c>FallbackTranslatorTests</c> used, kept verbatim plus the
    /// two fields the chain cases need: what was asked for, so I5 and I7 can be pinned on it.</summary>
    private sealed class Fake : ITranslator
    {
        private readonly Func<string> _f;
        public int Calls;
        public readonly List<(string Source, IReadOnlyList<string> Lines)> Batches = new();
        public Func<IReadOnlyList<string>, List<string>>? Lines;
        public Fake(Func<string> f) { _f = f; }

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
        { Calls++; return Task.FromResult(_f()); }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t, CancellationToken ct = default)
        {
            Calls++;
            Batches.Add((s, lines.ToList()));
            return Task.FromResult(Lines != null ? Lines(lines) : lines.Select(_ => _f()).ToList());
        }
    }

    /// <summary>A chain over gates this file owns rather than the registry's, for the cases that
    /// are about the algorithm and not about the wiring.</summary>
    private static ChainTranslator Chain(params (string Id, ITranslator T)[] tiers) =>
        new(tiers.Select(t => new ChainTier(t.Id, new ProviderGate(), t.T)).ToList());

    /// <summary>Open a registry gate until <paramref name="window"/> from now. Goes through the real
    /// <c>ReportFailure</c> — a hand-set field would pin nothing about the state machine.</summary>
    private static DateTimeOffset Block(string providerId, TimeSpan window)
    {
        var gate = ProviderGates.For(providerId);
        gate.ReportFailure(TranslationErrorKind.RateLimited, gate.Now() + window);
        var until = gate.Snapshot().BlockedUntil;
        Assert.NotNull(until);
        return until!.Value;
    }

    // =============================================================================================
    //  TP-CHN-01 … TP-CHN-06 — the algorithm of §6.2
    // =============================================================================================

    /// <summary>
    /// TP-CHN-01, end to end through the real providers: tier 1 answers 429 once, its gate opens,
    /// and the <b>next</b> call spends nothing at all on it. The assertion is the fake handler's
    /// request count, not the chain's own bookkeeping — a chain that believed it had skipped while
    /// the request went out anyway would pass the other reading.
    /// </summary>
    [Fact]
    public async Task TP_CHN_01_a_429_opens_the_gate_and_the_next_call_skips_the_tier_entirely()
    {
        var deepl = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "{}");
        var google = new FakeHandler().RespondJson(GoogleOk).RespondJson(GoogleOk);
        var chain = ChainTranslator.Of(
            (ProviderIds.DeepL, new DeepLTranslator("k:fx", deepl)),
            (ProviderIds.GoogleGtx, new TranslationService(google)));

        // Call 1 — DeepL is tried, refuses, and Google answers.
        Assert.Equal("hello", await chain.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, deepl.Requests);
        Assert.Equal(GateState.Open, ProviderGates.For(ProviderIds.DeepL).Snapshot().State);

        // Call 2 — the window is still running, so the tier is skipped: no second request.
        Assert.Equal("hello", await chain.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, deepl.Requests);
        Assert.Equal(2, google.Requests);

        var outcome = chain.LastOutcome!;
        Assert.Equal(ProviderIds.GoogleGtx, outcome.ProviderId);
        Assert.Equal(new[] { ProviderIds.DeepL }, outcome.Skipped.Select(s => s.ProviderId));
        Assert.Null(outcome.Kind);
    }

    /// <summary>TP-CHN-02 — the first tier answers and nothing below it is touched.</summary>
    [Fact]
    public async Task TP_CHN_02_uses_the_first_tier_when_it_succeeds()
    {
        var second = new Fake(() => "G");
        var chain = Chain((ProviderIds.DeepL, new Fake(() => "P")), (ProviderIds.GoogleGtx, second));

        Assert.Equal("P", await chain.TranslateAsync("x", "ru", "en"));
        Assert.Equal(0, second.Calls);
        Assert.Equal(ProviderIds.DeepL, chain.LastOutcome!.ProviderId);
        Assert.Empty(chain.LastOutcome!.Skipped);
    }

    /// <summary>TP-CHN-03 — a tier that tried and failed hands over to the next one.</summary>
    [Fact]
    public async Task TP_CHN_03_falls_through_to_the_next_tier_when_the_first_fails()
    {
        var second = new Fake(() => "G");
        // The fixture is "the first tier failed"; which Kind is arbitrary — any but Cancelled,
        // which nothing may construct (see TranslationErrorsTests).
        var chain = Chain(
            (ProviderIds.DeepL, new Fake(() => throw new TranslationException(TranslationErrorKind.Unavailable, "deepl down"))),
            (ProviderIds.GoogleGtx, second));

        Assert.Equal("G", await chain.TranslateAsync("x", "ru", "en"));
        Assert.Equal(1, second.Calls);
        // It TRIED — so it is not a skip, and LastOutcome must not report one (D-2).
        Assert.Empty(chain.LastOutcome!.Skipped);
    }

    /// <summary>
    /// TP-CHN-04, end to end: every tier is paused, so the chain throws <c>AllProvidersPaused</c>
    /// carrying the <b>earliest</b> of the three windows — and not one request leaves the machine.
    /// The earliest window is deliberately on the <b>last</b> tier so a <c>First()</c> (or a
    /// <c>Max</c>) implementation fails the case rather than passing it by luck.
    /// </summary>
    [Fact]
    public async Task TP_CHN_04_every_tier_paused_is_one_AllProvidersPaused_with_the_earliest_retryAt_and_no_requests()
    {
        var deepl = new FakeHandler().RespondJson("""{"translations":[{"text":"nope"}]}""");
        var google = new FakeHandler().RespondJson(GoogleOk);
        var dict = new Fake(() => "never");

        var first = Block(ProviderIds.DeepL, TimeSpan.FromMinutes(10));
        var second = Block(ProviderIds.GoogleGtx, TimeSpan.FromMinutes(5));
        var earliest = Block(ProviderIds.GoogleDict, TimeSpan.FromMinutes(2));
        Assert.True(earliest < second && second < first);

        var chain = ChainTranslator.Of(
            (ProviderIds.DeepL, new DeepLTranslator("k:fx", deepl)),
            (ProviderIds.GoogleGtx, new TranslationService(google)),
            (ProviderIds.GoogleDict, dict));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => chain.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.AllProvidersPaused, ex.Kind);
        Assert.Equal(UserMessages.AllProvidersPaused, ex.Message);
        Assert.Equal(earliest, ex.RetryAt);
        Assert.Equal(0, deepl.Requests);
        Assert.Equal(0, google.Requests);
        Assert.Equal(0, dict.Calls);

        var outcome = chain.LastOutcome!;
        Assert.Null(outcome.ProviderId);
        Assert.Equal(TranslationErrorKind.AllProvidersPaused, outcome.Kind);
        Assert.Equal(earliest, outcome.RetryAt);
        Assert.Equal(new[] { ProviderIds.DeepL, ProviderIds.GoogleGtx, ProviderIds.GoogleDict },
                     outcome.Skipped.Select(s => s.ProviderId));
    }

    /// <summary>TP-CHN-05 (AC 4) — a tier that really tried and was refused wins over the "everything
    /// is paused" sentence: the player must read what the provider actually said.</summary>
    [Fact]
    public async Task TP_CHN_05_a_real_403_beats_AllProvidersPaused()
    {
        Block(ProviderIds.DeepL, TimeSpan.FromMinutes(10));
        var google = new FakeHandler().Respond(HttpStatusCode.Forbidden, "nope");
        var chain = ChainTranslator.Of(
            (ProviderIds.DeepL, new DeepLTranslator("k:fx", new FakeHandler().RespondJson("{}"))),
            (ProviderIds.GoogleGtx, new TranslationService(google)));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => chain.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Blocked, ex.Kind);
        Assert.Equal(1, google.Requests);
        Assert.Equal(TranslationErrorKind.Blocked, chain.LastOutcome!.Kind);
    }

    /// <summary>
    /// TP-CHN-06, first half (AC 2): a ceiling wait short enough to be worth waiting for is
    /// <b>absorbed</b> — inside <c>HttpProviderCore</c>, which is where ruling E3-a leaves it — and
    /// the tier is used. The bucket is really empty and the wait is really requested; IS-7 credits
    /// it to the virtual clock instead of to the wall clock, so nothing sleeps (CI-3).
    /// </summary>
    [Fact]
    public async Task TP_CHN_06_a_short_ceiling_wait_is_absorbed_and_the_tier_is_used()
    {
        ProviderGates.For(ProviderIds.GoogleGtx).DrainBucketForTests();
        var google = new FakeHandler().RespondJson(GoogleOk);
        var below = new Fake(() => "G");
        var chain = ChainTranslator.Of(
            (ProviderIds.GoogleGtx, new TranslationService(google)),
            (ProviderIds.DeepL, below));

        Assert.Equal("hello", await chain.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(1, google.Requests);
        Assert.Equal(0, below.Calls);
        Assert.NotEmpty(TestBackoffRedirect.Delays);              // it really waited
        Assert.All(TestBackoffRedirect.Delays,
            d => Assert.True(d <= TimeSpan.FromMilliseconds(TranslationPolicy.MaxSpacingWaitMs)));
        Assert.Empty(chain.LastOutcome!.Skipped);
    }

    /// <summary>
    /// TP-CHN-06, second half (AC 2 / D-2): a wait past <c>MaxSpacingWaitMs</c> is refused by the
    /// core <b>without a request</b> — the shape it raises is a <c>TranslationException</c> with
    /// <c>NotSent</c> and <c>RetryAt = now + t</c> — and the chain must read that as a skip, not as
    /// a failure: the tier is unused and <c>now + t</c> is what <c>LastOutcome</c> remembers.
    /// (Today's constants cannot make the real bucket ask for more than 1 s, so the case is driven
    /// on the shape the core produces rather than on a policy the suite would have to move.)
    /// </summary>
    [Fact]
    public async Task TP_CHN_06_a_wait_past_the_budget_leaves_the_tier_unused_and_remembers_now_plus_t()
    {
        var gate = new ProviderGate();
        var retryAt = gate.Now() + TimeSpan.FromMilliseconds(TranslationPolicy.MaxSpacingWaitMs + 1);
        var refused = new Fake(() => throw new TranslationException(
            TranslationErrorKind.RateLimited, "paused", retryAt, ProviderIds.DeepL) { NotSent = true });
        var second = new Fake(() => "G");
        var chain = new ChainTranslator(new[]
        {
            new ChainTier(ProviderIds.DeepL, gate, refused),
            new ChainTier(ProviderIds.GoogleGtx, new ProviderGate(), second),
        });

        Assert.Equal("G", await chain.TranslateAsync("x", "ru", "en"));
        Assert.Equal(1, second.Calls);
        Assert.Equal(new[] { ProviderIds.DeepL }, chain.LastOutcome!.Skipped.Select(s => s.ProviderId));
        Assert.Equal(retryAt, chain.LastOutcome!.RetryAt);
    }

    /// <summary>
    /// <b>Risk R-01, and the reason ruling E3-a spells the predicate out.</b> The skip is
    /// <c>BlockedUntil &gt; Now()</c> and <b>not</b> <c>State == Open</c>: a gate stays
    /// <see cref="GateState.Open"/> after its window has elapsed, deliberately, until a caller
    /// actually takes the half-open probe. A chain that skipped on the state would skip the tier
    /// <i>forever</i> — no <c>TryEnter</c>, so no probe, so the gate never closes — and every test
    /// that only checks "an open gate is skipped" would still be green. So this case asserts the
    /// other side: once the window has passed, the request <b>must</b> go out.
    /// </summary>
    [Fact]
    public async Task R_01_an_elapsed_window_is_not_skipped_and_the_probe_really_goes_out()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        var google = new FakeHandler().RespondJson(GoogleOk);
        var chain = new ChainTranslator(new[]
        {
            new ChainTier(ProviderIds.GoogleGtx, gate, new TranslationService(google, gate)),
        });

        // Inside the window: skipped, nothing sent.
        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => chain.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(TranslationErrorKind.AllProvidersPaused, ex.Kind);
        Assert.Equal(0, google.Requests);

        clock.Advance(TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds + 1));
        Assert.Equal(GateState.Open, gate.Snapshot().State);   // still Open — that IS the trap

        Assert.Equal("hello", await chain.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, google.Requests);                      // the probe, and the chain let it through
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
    }

    /// <summary>A gate whose clock this file drives, so "the window has (not) elapsed" is a fact and
    /// not a race — the same shape <c>HttpProviderCoreTests</c> uses.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
    }

    // =============================================================================================
    //  TP-CHN-07 / TP-CHN-08 — I3, the OCE trap. THE NAMES ARE THE REGRESSION.
    // =============================================================================================

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_fallback()
    {
        // A GENUINE cancellation: the token passed to the chain IS cancelled and the tier throws an
        // OCE bound to it. This must propagate — never silently drop to the next tier.
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var second = new Fake(() => "G");
        var chain = Chain(
            (ProviderIds.DeepL, new Fake(() => throw new OperationCanceledException(cts.Token))),
            (ProviderIds.GoogleGtx, second));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chain.TranslateAsync("x", "ru", "en", cts.Token));
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Timeout_OCE_with_uncancelled_token_falls_back()
    {
        // On .NET 8 an HttpClient timeout arrives as a TaskCanceledException (subclass of OCE)
        // with the caller's token NOT cancelled. That's a failure, not a cancellation — it must
        // fall through to the next tier instead of propagating like a real stop.
        var second = new Fake(() => "G");
        var chain = Chain(
            (ProviderIds.DeepL, new Fake(() => throw new TaskCanceledException())),
            (ProviderIds.GoogleGtx, second));

        Assert.Equal("G", await chain.TranslateAsync("x", "ru", "en"));
        Assert.Equal(1, second.Calls);
        // …and it is read as the failure it is, never as a skip: a timeout was SENT (D-2).
        Assert.Empty(chain.LastOutcome!.Skipped);
    }

    /// <summary>A real cancel stops the chain from ANY tier, not only from the first — the loop's
    /// filter is per-tier, and a chain that only guarded tier 1 would keep going.</summary>
    [Fact]
    public async Task A_real_cancel_from_a_later_tier_stops_the_chain()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var third = new Fake(() => "G");
        var chain = Chain(
            (ProviderIds.DeepL, new Fake(() => throw new TranslationException(TranslationErrorKind.Unavailable, "down"))),
            (ProviderIds.GoogleGtx, new Fake(() => throw new OperationCanceledException(cts.Token))),
            (ProviderIds.GoogleDict, third));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chain.TranslateAsync("x", "ru", "en", cts.Token));
        Assert.Equal(0, third.Calls);
    }

    // =============================================================================================
    //  TP-CHN-13 and the invariants
    // =============================================================================================

    /// <summary>TP-CHN-13 — anything a tier throws that is not already a <c>TranslationException</c>
    /// is classified through <c>ProviderErrorMapper</c> (E1.S3 owns that vocabulary) and the next
    /// tier is tried.</summary>
    [Fact]
    public async Task TP_CHN_13_an_unexpected_exception_is_classified_and_the_next_tier_runs()
    {
        var second = new Fake(() => "G");
        var chain = Chain(
            (ProviderIds.DeepL, new Fake(() => throw new InvalidOperationException("boom"))),
            (ProviderIds.GoogleGtx, second));

        Assert.Equal("G", await chain.TranslateAsync("x", "ru", "en"));
        Assert.Equal(1, second.Calls);
    }

    /// <summary>…and when it is the LAST tier, the classified failure is what the caller gets —
    /// never a raw <see cref="InvalidOperationException"/> and never <c>AllProvidersPaused</c>.</summary>
    [Fact]
    public async Task An_unexpected_exception_on_the_last_tier_surfaces_as_a_classified_TranslationException()
    {
        var chain = Chain((ProviderIds.GoogleGtx, new Fake(() => throw new InvalidOperationException("boom"))));

        var ex = await Assert.ThrowsAsync<TranslationException>(() => chain.TranslateAsync("x", "ru", "en"));
        Assert.NotEqual(TranslationErrorKind.AllProvidersPaused, ex.Kind);
        Assert.Equal(ProviderIds.GoogleGtx, ex.ProviderId);
    }

    /// <summary>I5 — the chain never pads. A tier that returns fewer lines than it was given is
    /// E3.S8's problem to have a policy about; the chain hands back exactly what it was given.</summary>
    [Fact]
    public async Task I5_the_chain_never_pads_a_short_batch()
    {
        var truncating = new Fake(() => "x") { Lines = _ => new List<string> { "only one" } };
        var chain = Chain((ProviderIds.GoogleGtx, truncating));

        var result = await chain.TranslateLinesAsync(new[] { "a", "b", "c" }, "ru", "en");
        Assert.Equal(new[] { "only one" }, result);
    }

    /// <summary>AC 6 / I1 — <c>ITranslator</c> is exactly the two methods it has always been. The
    /// chain implements the interface; it does not widen it (which is why the priority cannot ride
    /// on a call and why <c>LastOutcome</c> is a property of the chain).</summary>
    [Fact]
    public void I1_ITranslator_is_unchanged()
    {
        var methods = typeof(ITranslator).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "TranslateAsync", "TranslateLinesAsync" }, methods.Select(m => m.Name));
        Assert.Equal("Task`1[String] TranslateAsync(String, String, String, CancellationToken)",
            Signature(methods[0]));
        Assert.Equal("Task`1[List`1[String]] TranslateLinesAsync(IReadOnlyList`1[String], String, String, CancellationToken)",
            Signature(methods[1]));
        Assert.Contains(typeof(ITranslator), typeof(ChainTranslator).GetInterfaces());
    }

    private static string Signature(MethodInfo m) =>
        $"{Pretty(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Pretty(p.ParameterType)))})";

    private static string Pretty(Type t) => t.IsGenericType
        ? $"{t.Name}[{string.Join(", ", t.GetGenericArguments().Select(Pretty))}]"
        : t.Name;

    /// <summary>An empty chain is a programming error, not a runtime state: inventing an
    /// <c>AllProvidersPaused</c> with no <c>retryAt</c> at call time would hide the composition bug
    /// behind a sentence the player cannot act on.</summary>
    [Fact]
    public void An_empty_chain_is_refused_at_construction()
        => Assert.Throws<ArgumentException>(() => new ChainTranslator(Array.Empty<ChainTier>()));

    // =============================================================================================
    //  The I6 / I7 pins (ruling GAP-1 / GAP-2) and AC 6's deletion
    // =============================================================================================

    /// <summary>
    /// <b>I6 — the glossary stays upstream of every engine.</b> The failure mode this pins is a new
    /// provider "helpfully" expanding slang itself: the text would be double-expanded and the
    /// expanded form would end up displayed as the original. Nothing under <c>Services/</c> other
    /// than <c>SlangGlossary.cs</c> may call <c>.Expand(</c>, and the LIVE loop's single call stays
    /// where it is — above the chain, once per body, before the split.
    /// </summary>
    [Fact]
    public void I6_only_the_code_behind_expands_slang_and_never_a_provider()
    {
        var root = RepoRoot();
        var services = Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs")
            .Where(f => Path.GetFileName(f) != "SlangGlossary.cs")
            .Where(f => Code(File.ReadAllText(f)).Contains(".Expand(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(services);

        // Non-vacuity, and the placement itself: the expansion happens once, before the split.
        var live = Code(File.ReadAllText(Path.Combine(root, "MainWindow.Live.cs")));
        Assert.Contains("_slang.Expand(b)", live, StringComparison.Ordinal);
        Assert.True(live.IndexOf("_slang.Expand(b)", StringComparison.Ordinal)
                    < live.IndexOf("IsProbablyRussian", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>I7 — per-message <c>ru</c>/<c>auto</c>.</b> Two halves, because the invariant has two: the
    /// LIVE loop still splits the bodies into two groups and issues two calls with those two source
    /// codes (a source pin — a refactor that collapses them into one "auto" batch is exactly the
    /// regression), and the chain passes each batch through <b>intact</b>: the source verbatim, the
    /// lines in order, nothing merged and nothing reordered.
    /// </summary>
    [Fact]
    public async Task I7_the_ru_auto_split_reaches_the_chain_intact()
    {
        var live = Code(File.ReadAllText(Path.Combine(RepoRoot(), "MainWindow.Live.cs")));
        Assert.Contains("TranslateLinesAsync(ru, \"ru\", target, ct)", live, StringComparison.Ordinal);
        Assert.Contains("TranslateLinesAsync(auto, \"auto\", target, ct)", live, StringComparison.Ordinal);

        var tier = new Fake(() => "?") { Lines = lines => lines.Select(l => l.ToUpperInvariant()).ToList() };
        var chain = Chain((ProviderIds.GoogleGtx, tier));

        Assert.Equal(new[] { "ПРИВЕТ", "ПОКА" },
            await chain.TranslateLinesAsync(new[] { "привет", "пока" }, "ru", "en"));
        Assert.Equal(new[] { "HI" }, await chain.TranslateLinesAsync(new[] { "hi" }, "auto", "en"));

        Assert.Equal(new[] { "ru", "auto" }, tier.Batches.Select(b => b.Source));
        Assert.Equal(new[] { "привет", "пока" }, tier.Batches[0].Lines);
    }

    /// <summary>AC 6's other half: <c>FallbackTranslator</c> is gone, and no production file still
    /// calls it. Code only — two files name the dead type in prose, on purpose, to say what
    /// replaced it.</summary>
    [Fact]
    public void The_FallbackTranslator_is_gone()
    {
        var root = RepoRoot();
        Assert.False(File.Exists(Path.Combine(root, "Services", "FallbackTranslator.cs")));
        Assert.Empty(ProductionSources(root)
            .Where(f => Code(File.ReadAllText(f)).Contains("FallbackTranslator", StringComparison.Ordinal))
            .Select(Path.GetFileName));
    }

    // ---- the source-scan helpers (the shape TranslationErrorsTests already uses) -----------------

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static List<string> ProductionSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f)
                            .Split('/', '\\')
                            .SkipLast(1)
                            .All(seg => !Skipped.Contains(seg) && !seg.StartsWith('.')))
            .ToList();

    private static readonly HashSet<string> Skipped =
        new(StringComparer.OrdinalIgnoreCase) { "tests", "bin", "obj" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

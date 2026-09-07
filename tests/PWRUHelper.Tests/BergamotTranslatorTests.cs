using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E8.S2 — the offline provider, end to end on the fake engine (<see cref="FakeBergamotEngine"/>).
/// <b>Nothing here downloads a model, loads <c>bergamot.dll</c> or reaches the network</b> (CI-8,
/// IS-10): the only disk this file touches is a throwaway temp directory holding a config file the
/// fake never reads.
///
/// <para>It joins the non-parallel <c>Gates</c> collection because the provider's default gate is
/// the registry's (IS-5/R4), and the two failure modes report into it.</para>
/// </summary>
[Collection("Gates")]
public class BergamotTranslatorTests : GatesTestBase
{
    // =============================================================================================
    //  fixtures
    // =============================================================================================

    /// <summary>A model directory that exists, holding the config file the provider asks for. The
    /// fake engine never opens it — its existence is the whole point, because "is the model there?"
    /// is the one disk question this class asks.</summary>
    private sealed class TempModel : IDisposable
    {
        private readonly DirectoryInfo _dir;

        public string Path => _dir.FullName;

        public TempModel(bool withConfig = true)
        {
            _dir = Directory.CreateTempSubdirectory("pwru-bergamot-");
            if (withConfig)
                File.WriteAllText(System.IO.Path.Combine(_dir.FullName,
                    BergamotTranslator.ConfigFileName), "relative-paths: true\n");
        }

        public void Dispose()
        {
            try { _dir.Delete(recursive: true); } catch { /* the test already made its point */ }
        }
    }

    /// <summary>A cloud tier that always fails, so a chain case actually reaches the offline rung.
    /// The shape <c>ChainTranslatorTests.Fake</c> uses, cut down to what this file needs.</summary>
    private sealed class FailingTier : ITranslator
    {
        public int Calls;

        private TranslationException Boom()
        {
            Calls++;
            return new TranslationException(TranslationErrorKind.Network, "no route",
                null, ProviderIds.GoogleGtx);
        }

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
            => throw Boom();

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
            CancellationToken ct = default) => throw Boom();
    }

    private static BergamotTranslator Provider(FakeBergamotEngineFactory factory, TempModel? model,
        IReadOnlyList<(string, string)>? pairs = null, ProviderGate? gate = null,
        Action? onModelLookup = null)
        => new(
            modelDirectory: () => { onModelLookup?.Invoke(); return model?.Path; },
            nativeDirectory: () => null,
            engineFactory: factory.Create,
            pairs: pairs,
            gate: gate);

    // =============================================================================================
    //  I10 / AC 3 — nothing is loaded until something asks
    // =============================================================================================

    /// <summary>Case 1. Constructing the provider opens no file and builds no engine: the store is
    /// not even asked where the model is. One model is ~85 % of the app's working set, and §7.6
    /// constraint 2 plus I10 both say it may never be paid for at startup.</summary>
    [Fact]
    public void Constructing_the_provider_loads_nothing()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var lookups = 0;

        var provider = Provider(factory, model, onModelLookup: () => lookups++);

        Assert.False(provider.IsLoaded);
        Assert.Equal(0, factory.Initialisations);
        Assert.Equal(0, lookups);
    }

    /// <summary>…and neither does an empty ask. A frame the dedup emptied, or a blank line from the
    /// Translator tab, is not a question about a translation.</summary>
    [Fact]
    public async Task Nothing_to_translate_loads_nothing()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        Assert.Equal("", await provider.TranslateAsync("   ", "ru", "en"));
        Assert.Empty(await provider.TranslateLinesAsync(Array.Empty<string>(), "ru", "en"));

        Assert.Equal(0, factory.Initialisations);
        Assert.False(provider.IsLoaded);
    }

    // =============================================================================================
    //  AC 5 — the load happens once, whoever asks
    // =============================================================================================

    /// <summary>Case 2. The first translate brings the engine up, the second reuses it, and the
    /// config path handed to <c>translator_initialize</c> is the store's directory plus the one file
    /// name the two agree on.</summary>
    [Fact]
    public async Task The_first_translation_loads_exactly_one_engine()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        Assert.Equal("[привет]", await provider.TranslateAsync("привет", "ru", "en"));
        Assert.Equal("[пока]", await provider.TranslateAsync("пока", "ru", "en"));

        Assert.Equal(1, factory.Initialisations);
        Assert.True(provider.IsLoaded);
        Assert.Equal(System.IO.Path.Combine(model.Path, BergamotTranslator.ConfigFileName),
            Assert.Single(factory.ConfigPaths));
    }

    /// <summary>Case 2's other half (AC 5): two threads asking at once still build ONE engine —
    /// two would be 254 MiB. The race is made real rather than hoped for: the factory blocks the
    /// first caller inside the load until the second one has certainly reached the provider, so the
    /// two are genuinely inside <c>TranslateAsync</c> together.</summary>
    [Fact]
    public async Task Two_concurrent_first_calls_build_one_engine()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        using var firstIsInsideTheLoad = new ManualResetEventSlim(false);
        using var secondHasStarted = new ManualResetEventSlim(false);
        using var releaseTheLoad = new ManualResetEventSlim(false);

        // The winner of the race is held INSIDE the load, holding the lock, until the test has seen
        // it there and let the second caller run into it. No sleep anywhere (CI-3), and both waits
        // are bounded so a regression fails rather than hangs.
        factory.BeforeReturn = () =>
        {
            firstIsInsideTheLoad.Set();
            releaseTheLoad.Wait(TimeSpan.FromSeconds(10));
        };

        var a = Task.Run(() => provider.TranslateAsync("привет", "ru", "en"));
        var b = Task.Run(() =>
        {
            // Waiting for the FIRST caller to be inside the load before starting the second is what
            // makes this a race instead of a coincidence. Without it a cold pool can schedule `b`
            // after `a` has finished, and `Initialisations == 1` then passes for the trivial reason
            // that the engine was already cached — the assertion holding while exercising nothing.
            firstIsInsideTheLoad.Wait(TimeSpan.FromSeconds(10));
            secondHasStarted.Set();
            return provider.TranslateAsync("пока", "ru", "en");
        });

        Assert.True(firstIsInsideTheLoad.Wait(TimeSpan.FromSeconds(10)), "the load never started");
        Assert.True(secondHasStarted.Wait(TimeSpan.FromSeconds(10)), "the second caller never ran");
        releaseTheLoad.Set();
        var answers = await Task.WhenAll(a, b);

        Assert.Equal(1, factory.Initialisations);
        Assert.Single(factory.All);
        // WhenAll answers in the order it was given, so each caller got its own line back and not
        // the other's — a single shared engine must not become a shared buffer.
        Assert.Equal(new[] { "[привет]", "[пока]" }, answers);
    }

    // =============================================================================================
    //  AC 4 / ruling E8-c — the two failure modes
    // =============================================================================================

    /// <summary>Case 3, failure mode 2: initialisation failed. A typed
    /// <see cref="TranslationErrorKind.Unavailable"/> carrying this provider's id — never a
    /// <c>(</c>-prefixed return, which nothing in this app does since E1 — and <b>never
    /// <c>NotSent</c></b>: ruling E3-b's flag means "the gate refused before anything left the
    /// machine", which is a statement about a request a local engine never makes.</summary>
    [Fact]
    public async Task A_failed_initialisation_is_a_typed_Unavailable_and_never_NotSent()
    {
        var factory = new FakeBergamotEngineFactory { FailWith = new InvalidOperationException("no") };
        using var model = new TempModel();
        var provider = Provider(factory, model);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        Assert.Equal(ProviderIds.Bergamot, ex.ProviderId);
        Assert.False(NotSentOf(ex));
        Assert.False(provider.IsLoaded);
        // The sentence the player reads names the engine the way §3.0 does, and this file invents
        // no name: it is UserMessages' Unavailable row with ProviderNames' "Offline engine" in it.
        Assert.Contains("Offline engine", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Failure mode 1: the model is gone. E8.S5 builds this tier only when the model is
    /// present, so this is a race — the user pressed Remove mid-session — and it is answered
    /// without a native library ever being asked for.</summary>
    [Fact]
    public async Task A_missing_model_is_a_typed_Unavailable_and_loads_nothing()
    {
        var factory = new FakeBergamotEngineFactory();

        // Two shapes of "not installed": no directory at all, and a directory with no config in it.
        var noStore = new BergamotTranslator(engineFactory: factory.Create);
        using var empty = new TempModel(withConfig: false);
        var emptyStore = Provider(factory, empty);

        foreach (var provider in new[] { noStore, emptyStore })
        {
            var ex = await Assert.ThrowsAsync<TranslationException>(
                () => provider.TranslateAsync("привет", "ru", "en"));
            Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
            Assert.Equal(ProviderIds.Bergamot, ex.ProviderId);
            Assert.False(NotSentOf(ex));
            Assert.False(provider.IsLoaded);
        }

        Assert.Equal(0, factory.Initialisations);
    }

    /// <summary>Case 4, and it is the point of using a gate at all: after either failure the gate
    /// holds a soft window, and a chain carrying this tier <b>skips</b> it — no exception, no flag,
    /// no second 200 ms spent finding out again. Driven end-to-end through a real
    /// <see cref="ChainTranslator"/> and the real registry gate.</summary>
    [Fact]
    public async Task A_failure_opens_a_gate_window_and_the_chain_then_skips_the_tier()
    {
        var factory = new FakeBergamotEngineFactory();
        var cloud = new FailingTier();
        var provider = new BergamotTranslator(engineFactory: factory.Create);   // nothing installed
        var chain = ChainTranslator.Of((ProviderIds.GoogleGtx, cloud), (ProviderIds.Bergamot, provider));

        var gate = ProviderGates.For(ProviderIds.Bergamot);
        Assert.Null(gate.Snapshot().BlockedUntil);

        // First call: the cloud tier fails, the offline tier is really tried and really fails.
        await Assert.ThrowsAsync<TranslationException>(() => chain.TranslateAsync("привет", "ru", "en"));
        Assert.Empty(chain.LastOutcome!.Skipped);

        var window = gate.Snapshot().BlockedUntil;
        Assert.NotNull(window);
        Assert.True(window > gate.Now(), "the soft window has to be in the future to be a window");

        // Second call: the chain skips the tier before calling it. The failure the player reads is
        // the cloud tier's, which is AC 4 of E3 — a tier that really tried outranks a skipped one.
        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => chain.TranslateAsync("пока", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Network, ex.Kind);
        Assert.Contains(ProviderIds.Bergamot, chain.LastOutcome!.Skipped.Select(s => s.ProviderId));
        Assert.Equal(2, cloud.Calls);
    }

    /// <summary>A pair nobody installed is <see cref="TranslationErrorKind.Unavailable"/> too — and
    /// it is answered <b>without loading</b> and <b>without a gate report</b>. Nothing is broken and
    /// nothing heals in five seconds: "French is not installed" is a permanent fact, and opening a
    /// soft window for it would make the chain skip the tier for the pair that IS installed.</summary>
    [Fact]
    public async Task An_uninstalled_pair_is_refused_without_loading_and_without_a_gate_window()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var lookups = 0;
        var provider = Provider(factory, model, onModelLookup: () => lookups++);

        foreach (var pair in new[] { ("fr", "en"), ("ru", "fr"), ("auto", "en") })
        {
            var ex = await Assert.ThrowsAsync<TranslationException>(
                () => provider.TranslateAsync("привет", pair.Item1, pair.Item2));
            Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
            Assert.Equal(ProviderIds.Bergamot, ex.ProviderId);
            Assert.False(NotSentOf(ex));
        }

        await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateLinesAsync(new[] { "a", "b" }, "auto", "en"));

        Assert.Equal(0, lookups);
        Assert.Equal(0, factory.Initialisations);
        Assert.False(provider.IsLoaded);
        Assert.Null(ProviderGates.For(ProviderIds.Bergamot).Snapshot().BlockedUntil);
    }

    /// <summary>The pair set is the store's answer, not this class's opinion: hand it a different
    /// one and the same call is served.</summary>
    [Fact]
    public async Task The_installed_pairs_are_a_constructor_argument()
    {
        Assert.Equal(new[] { ("ru", "en") }, BergamotTranslator.DefaultPairs);

        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, pairs: new[] { ("ru", "fr") });

        Assert.Equal("[привет]", await provider.TranslateAsync("привет", "RU", "fr"));
        await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));
    }

    /// <summary>A native failure mid-translate is mapped, never let out raw. The chain would send an
    /// unclassified exception through <c>ProviderErrorMapper</c> and read it as <c>Unknown</c>,
    /// which §4.1 says is a bug report about the mapper. "The engine is not there" is
    /// <c>Unavailable</c>; "the engine is there and misbehaved" is <c>BadResponse</c>.</summary>
    [Fact]
    public async Task A_native_failure_mid_translate_is_typed()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        // Bring it up on a good call first, so this is the translate leg and not the load leg.
        await provider.TranslateAsync("привет", "ru", "en");

        // "The engine is there and misbehaved" — BadResponse, and the engine STAYS: it answered, it
        // answered wrongly, and three of those are what open the gate.
        factory.Last!.OnTranslate = (_, _) => throw new InvalidOperationException("garbage out");
        var broken = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(TranslationErrorKind.BadResponse, broken.Kind);
        Assert.Equal(ProviderIds.Bergamot, broken.ProviderId);
        Assert.False(NotSentOf(broken));
        Assert.True(provider.IsLoaded);
        Assert.Equal(1, factory.Initialisations);
        Assert.Equal(0, factory.Last!.Disposals);

        // "The engine is not THERE" — Unavailable, and the dead handle goes with it. Leaving it
        // cached would hand the same corpse to every later call, tell E8.S4 that 121 MiB is resident
        // and well, and make the tier unrecoverable inside the session.
        factory.Last!.OnTranslate = (_, _) => throw new DllNotFoundException("bergamot");
        var dead = factory.Last!;
        var gone = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(TranslationErrorKind.Unavailable, gone.Kind);
        Assert.Equal(ProviderIds.Bergamot, gone.ProviderId);
        Assert.False(NotSentOf(gone));
        Assert.False(provider.IsLoaded);
        Assert.Equal(1, dead.Disposals);
    }

    /// <summary>
    /// The breaker has to cover the leg it is documented to cover. Before this case the two failure
    /// modes BEFORE the engine is up reported to the gate and every failure AFTER it was up did not,
    /// so a loaded-but-broken engine was re-asked on every LIVE tick for ever — with the gate window
    /// as this class's only "the engine is broken" cache (there is no <c>_initFailed</c> bool by
    /// design), that left the post-load modes with no cache at all.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_engine_is_up_reaches_the_gate_too()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);
        var gate = ProviderGates.For(ProviderIds.Bergamot);

        await provider.TranslateAsync("привет", "ru", "en");
        Assert.Null(gate.Snapshot().BlockedUntil);

        // BadResponse is a SOFT strike: three consecutive open the window, one does not (§5.3).
        factory.Last!.OnBatch = lines => lines.Take(lines.Count - 1).ToList();
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<TranslationException>(
                () => provider.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en"));

        var window = gate.Snapshot().BlockedUntil;
        Assert.NotNull(window);
        Assert.True(window > gate.Now(), "three BadResponses in a row have to leave a future window");
    }

    /// <summary>…and the other half of §5.3, which has to land in the same commit as the half above:
    /// a success in between RESETS the soft count. Without it the strike ladder is
    /// cumulative-for-ever and three bad frames an hour apart would open a window on a healthy
    /// engine — the opposite bug, and the more insidious one.</summary>
    [Fact]
    public async Task A_success_between_two_bad_frames_resets_the_soft_count()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);
        var gate = ProviderGates.For(ProviderIds.Bergamot);

        await provider.TranslateAsync("привет", "ru", "en");

        for (var i = 0; i < 5; i++)
        {
            factory.Last!.OnBatch = lines => lines.Take(lines.Count - 1).ToList();
            await Assert.ThrowsAsync<TranslationException>(
                () => provider.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en"));

            factory.Last!.OnBatch = null;                       // a good frame in between
            await provider.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en");

            Assert.Null(gate.Snapshot().BlockedUntil);
        }
    }

    /// <summary>E8.S3's store is injected code and it is allowed to fail. A locator that throws must
    /// not send a raw <c>IOException</c> up the chain, where <c>ProviderErrorMapper</c> reads it as
    /// <c>Unknown</c> — §4.1 says an Unknown in a field log is a bug report about the mapper, not
    /// about the provider.</summary>
    [Fact]
    public async Task A_store_that_throws_is_still_a_typed_Unavailable()
    {
        var factory = new FakeBergamotEngineFactory();
        var provider = new BergamotTranslator(
            modelDirectory: () => throw new IOException("the store is not ready"),
            engineFactory: factory.Create);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        Assert.Equal(ProviderIds.Bergamot, ex.ProviderId);
        Assert.False(NotSentOf(ex));
        Assert.Equal(0, factory.Initialisations);
    }

    /// <summary>A factory that answers <c>null</c> initialised nothing. Storing it would leave a
    /// class that believes it is loaded and NREs on the next line — reported as
    /// <c>BadResponse</c>, which is the wrong Kind for an engine that never ran.</summary>
    [Fact]
    public async Task A_factory_that_returns_null_is_a_typed_Unavailable()
    {
        using var model = new TempModel();
        var provider = new BergamotTranslator(
            modelDirectory: () => model.Path,
            engineFactory: _ => null!);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        Assert.False(provider.IsLoaded);
    }

    /// <summary>
    /// <b>TP-BRG-09, asserted rather than argued.</b> §7.6 constraint 7's words are "a `(`-prefixed
    /// placeholder <i>so nothing is cached</i>", and ruling E8-c keeps the second half while
    /// replacing the first: what the AC protects is <b>I4</b>. The throw satisfies it more strongly
    /// than a return did — <c>CachingTranslator</c> is never handed a value at all — but "more
    /// strongly" is a claim, and the TP id is claimed on it, so it is driven through a real
    /// <c>CachingTranslator</c> here for both failure modes and the store is asked afterwards.
    /// </summary>
    [Fact]
    public async Task TP_BRG_09_neither_failure_mode_puts_anything_in_the_cache()
    {
        var store = new TranslationCacheStore(50);
        var factory = new FakeBergamotEngineFactory();

        // Mode 1 — the model is not there.
        var missing = new CachingTranslator(new BergamotTranslator(engineFactory: factory.Create), store);
        await Assert.ThrowsAsync<TranslationException>(
            () => missing.TranslateAsync("привет", "ru", "en"));
        await Assert.ThrowsAsync<TranslationException>(
            () => missing.TranslateLinesAsync(new[] { "привет", "пока" }, "ru", "en"));

        // Mode 2 — initialisation failed.
        using var model = new TempModel();
        var broken = new FakeBergamotEngineFactory { FailWith = new InvalidOperationException("no") };
        var failing = new CachingTranslator(Provider(broken, model), store);
        await Assert.ThrowsAsync<TranslationException>(
            () => failing.TranslateAsync("привет", "ru", "en"));

        Assert.Equal(0, store.Count);

        // Non-vacuity: the same store DOES take a success, so "0" above is the failure being
        // refused and not a store that never stores.
        var working = new CachingTranslator(Provider(new FakeBergamotEngineFactory(), model), store);
        Assert.Equal("[привет]", await working.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, store.Count);
    }

    // =============================================================================================
    //  I5 — the batch is never padded
    // =============================================================================================

    /// <summary>Case 5. N lines in, N lines out, in ONE native call: §6.2's frame shape, which
    /// E8.S1 measured at 3.75 ms/line against 8.34 ms one at a time.</summary>
    [Fact]
    public async Task A_frame_is_one_native_call_and_comes_back_line_for_line()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        var lines = new[] { "привет", "пока", "нужен хил" };
        var answer = await provider.TranslateLinesAsync(lines, "ru", "en");

        Assert.Equal(new[] { "[привет]", "[пока]", "[нужен хил]" }, answer);
        Assert.Equal(lines, Assert.Single(factory.Last!.Batches));
    }

    /// <summary>Case 5's other half — I5, and it is the invariant this project paid three releases
    /// for. A local engine returning the wrong number of lines is a bug, not a shape to route
    /// around: <c>BadResponse</c>, never a pad, never a truncation, never a per-line retry.</summary>
    [Theory]
    [InlineData(2)]   // short
    [InlineData(4)]   // long
    public async Task A_batch_count_mismatch_is_a_BadResponse_and_never_padded(int returned)
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);
        var lines = new[] { "a", "b", "c" };

        // The engine has to exist before its batch can misbehave.
        await provider.TranslateAsync("привет", "ru", "en");
        factory.Last!.OnBatch = _ => Enumerable.Range(0, returned).Select(i => "x" + i).ToList();

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateLinesAsync(lines, "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        Assert.Equal(ProviderIds.Bergamot, ex.ProviderId);
        Assert.False(NotSentOf(ex));
    }

    /// <summary>One line is the per-line export, not a one-element frame: it is the cheaper native
    /// shape and it needs no markup round trip. The <c>html</c> flag is what says which is
    /// which.</summary>
    [Fact]
    public async Task One_line_takes_the_per_line_call_and_a_frame_takes_the_html_one()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        await provider.TranslateLinesAsync(new[] { "привет" }, "ru", "en");
        Assert.Empty(factory.Last!.Batches);
        Assert.Equal((Text: "привет", Html: false), Assert.Single(factory.Last!.Calls));

        await provider.TranslateLinesAsync(new[] { "привет", "пока" }, "ru", "en");
        Assert.Single(factory.Last!.Batches);
    }

    // =============================================================================================
    //  Cancellation — T4's honest contract, and I3's one sentence
    // =============================================================================================

    /// <summary>A token cancelled before the call starts one: nothing is loaded and nothing is
    /// asked of the engine.</summary>
    [Fact]
    public async Task A_cancelled_token_never_starts_a_native_call()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateAsync("привет", "ru", "en", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en", cts.Token));

        Assert.Equal(0, factory.Initialisations);
    }

    /// <summary>A cancel that lands while the frame is inside the native call: the engine cannot be
    /// interrupted, so what this class promises is that the result is <b>discarded</b> and the
    /// cancellation propagates as an <c>OperationCanceledException</c> — not wrapped into a
    /// <c>TranslationException</c>, which is the one Kind nothing in this app may construct
    /// (I3/§4.1).</summary>
    [Fact]
    public async Task A_cancel_during_a_frame_propagates_and_the_answer_is_discarded()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);
        using var cts = new CancellationTokenSource();

        await provider.TranslateAsync("warm", "ru", "en");
        factory.Last!.OnBatch = lines =>
        {
            cts.Cancel();                                   // the user pressed Stop mid-frame
            return lines.Select(l => "[" + l + "]").ToList();
        };

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en", cts.Token));

        Assert.IsNotType<TranslationException>(ex);
    }

    // =============================================================================================
    //  T6 — unload, and the memory with it
    // =============================================================================================

    /// <summary>Case 6. <c>translator_free</c> exactly once, idempotent, and the next translation
    /// brings a fresh engine up — which is what makes E8.S4's policy possible at all. Nothing here
    /// owns a timer: WHEN to unload is E8.S4's, with an injected clock.</summary>
    [Fact]
    public async Task Unload_frees_the_engine_once_and_is_safe_to_call_twice()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        await provider.TranslateAsync("привет", "ru", "en");
        var engine = factory.Last!;
        Assert.True(provider.IsLoaded);

        provider.Unload();
        provider.Unload();
        provider.Dispose();

        Assert.False(provider.IsLoaded);
        Assert.Equal(1, engine.Disposals);
        Assert.Equal(1, factory.Initialisations);

        // …and the capability is genuinely a capability: asking again brings a new engine up.
        await provider.TranslateAsync("пока", "ru", "en");
        Assert.Equal(2, factory.Initialisations);
        Assert.True(provider.IsLoaded);
        Assert.Equal(2, factory.All.Count);
    }

    /// <summary>
    /// <b>The thread contract, as a pin.</b> The binding is not documented thread-safe and
    /// <c>BlockingService</c> is a synchronous native engine, not a server — so what AC 5 buys is
    /// that two threads may CALL this class at once, not that two translations run at once. LIVE
    /// (the read chain) and the Translator tab (the write chain) share one process and one engine,
    /// and the lock is what stands between that and two threads inside <c>translator_translate</c>.
    ///
    /// <para>Deterministic in the direction that matters: it passes in microseconds when the lock
    /// holds and cannot pass by luck when it does not — the yields inside each call give a broken
    /// build every chance to overlap.</para>
    /// </summary>
    [Fact]
    public async Task The_engine_never_sees_two_callers_at_once()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        await provider.TranslateAsync("warm", "ru", "en");
        var engine = factory.Last!;

        // No sleep (CI-3): a yield hands the core away, which is all an unsynchronised build needs
        // to be caught, and costs nothing when the calls really are serialised.
        static void Overlap() { for (var i = 0; i < 200; i++) Thread.Yield(); }
        engine.OnTranslate = (t, _) => { Overlap(); return "[" + t + "]"; };
        engine.OnBatch = lines => { Overlap(); return lines.Select(l => "[" + l + "]").ToList(); };

        var callers = Enumerable.Range(0, 8).Select(i => Task.Run(() => i % 2 == 0
            ? provider.TranslateAsync("line" + i, "ru", "en")
            : (Task)provider.TranslateLinesAsync(new[] { "a" + i, "b" + i }, "ru", "en"))).ToArray();
        await Task.WhenAll(callers);

        Assert.Equal(1, engine.PeakConcurrentCalls);
        Assert.Equal(1, factory.Initialisations);
    }

    /// <summary>
    /// <b>An <see cref="BergamotTranslator.Unload"/> racing a translate in flight.</b> This is the
    /// property T6 asks for and the one the <c>_sync</c> doc comment claims, and until now nothing
    /// drove it: every unload case in this file was sequential, on one thread, with nothing running.
    /// A refactor that moved the native call out of the lock — the obvious answer to "the lock is
    /// held across a 150 ms frame" — would free a handle under a running call with no test failing.
    ///
    /// <para>What is pinned: the in-flight call gets its own answer, the engine is <b>not</b> freed
    /// while it is inside one, and it is freed exactly once afterwards. Whether the unload waits or
    /// the translate is refused is this class's choice — it waits — but "touches a freed handle" is
    /// not on the menu.</para>
    /// </summary>
    [Fact]
    public async Task An_Unload_racing_a_translate_never_frees_the_handle_under_it()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        await provider.TranslateAsync("warm", "ru", "en");
        var engine = factory.Last!;

        using var insideTheCall = new ManualResetEventSlim(false);
        using var unloadWasAsked = new ManualResetEventSlim(false);

        var freedDuringTheCall = -1;
        engine.OnTranslate = (t, _) =>
        {
            insideTheCall.Set();
            unloadWasAsked.Wait(TimeSpan.FromSeconds(10));   // the unload is now blocked on the lock
            freedDuringTheCall = engine.Disposals;           // …and must not have freed anything yet
            return "[" + t + "]";
        };

        var translating = Task.Run(() => provider.TranslateAsync("привет", "ru", "en"));
        Assert.True(insideTheCall.Wait(TimeSpan.FromSeconds(10)), "the native call never started");

        var unloading = Task.Run(() =>
        {
            unloadWasAsked.Set();
            provider.Unload();
        });

        Assert.Equal("[привет]", await translating);
        await unloading;

        Assert.Equal(0, freedDuringTheCall);
        Assert.False(engine.WasCalledAfterDispose);
        Assert.False(engine.WasDisposedWhileInFlight);
        Assert.Equal(1, engine.Disposals);
        Assert.False(provider.IsLoaded);
    }

    /// <summary>
    /// <b>The resolver is installed once per process, and the latch is a contract rather than an
    /// optimisation.</b> <see cref="NativeLibrary.SetDllImportResolver"/> throws
    /// <see cref="InvalidOperationException"/> on a second registration for the same assembly, so
    /// "call it twice" is not a wasted call, it is a crash — which is why this is pinned rather than
    /// trusted, and why the latch is taken under a lock and not before the registration (a latch
    /// claimed first lets a second thread leave with no resolver installed and take a
    /// <c>DllNotFoundException</c> on a machine where the engine is correctly installed).
    ///
    /// <para>Nothing native happens here: the callback is registered, never invoked — resolution
    /// only runs when a <c>[LibraryImport]</c> member is first called, which this suite never does
    /// (CI-8). With no native directory configured the callback would answer
    /// <see cref="IntPtr.Zero"/> anyway.</para>
    /// </summary>
    [Fact]
    public void The_dll_resolver_is_installed_once_per_process()
    {
        var ensure = typeof(BergamotEngine).GetMethod("EnsureResolver",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ensure);

        // Twice, from here, and a third time from a parallel caller: the second and third must be
        // no-ops rather than the InvalidOperationException the runtime raises on a re-registration.
        ensure!.Invoke(null, null);
        ensure.Invoke(null, null);
        Parallel.For(0, 8, _ => ensure.Invoke(null, null));

        var latch = typeof(BergamotEngine).GetField("_resolverInstalled",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(latch);
        Assert.True((bool)latch!.GetValue(null)!, "the latch has to be set once the resolver is in");
    }

    /// <summary><see cref="BergamotTranslator.Load"/> is the other half E8.S4 needs: bring the
    /// engine up on the first LIVE tick, without translating anything.</summary>
    [Fact]
    public void Load_brings_the_engine_up_on_its_own_and_is_idempotent()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model);

        provider.Load();
        provider.Load();

        Assert.True(provider.IsLoaded);
        Assert.Equal(1, factory.Initialisations);
        Assert.Empty(factory.Last!.Calls);
    }

    // =============================================================================================
    //  The pins — source scans, because a comment can be deleted and a scan cannot
    // =============================================================================================

    /// <summary>Case 7 / T7 — <b>I6, as a pin</b>. This provider never expands slang: the text
    /// reaching it has already been through that layer upstream (the same expanded text the cloud
    /// tiers were handed), and doing it again inside a provider would expand it twice. Measured, not
    /// stylistic: raw, this engine renders <c>данж</c> as "dangling" and <c>хил</c> as "heel", which
    /// is why the layer is upstream — and why it must be there and only there.</summary>
    [Fact]
    public void I6_the_offline_provider_never_expands_slang()
    {
        var provider = Code(File.ReadAllText(ServiceFile("BergamotTranslator.cs")))
                     + Code(File.ReadAllText(ServiceFile("BergamotEngine.cs")));

        Assert.DoesNotContain("Expand", provider, StringComparison.Ordinal);
        Assert.DoesNotContain("SlangGlossary", provider, StringComparison.Ordinal);

        // Non-vacuity: the expansion really is upstream, in the two call sites that own it, so this
        // scan is asserting an absence that means something.
        var root = RepoRoot();
        foreach (var caller in new[] { "MainWindow.Live.cs", "MainWindow.Translate.cs" })
            Assert.Contains(".Expand(", File.ReadAllText(System.IO.Path.Combine(root, caller)),
                StringComparison.Ordinal);
    }

    // Ruling E8-c's negative half is NOT re-asserted here, and that is the story's instruction
    // ("verify it still passes rather than writing a duplicate"), not an oversight. The one scan
    // lives in ChainTranslatorTests.NotSent_is_written_in_exactly_one_place: its ProductionSources
    // enumerates every *.cs outside tests/bin/obj, so both files this story adds are already in
    // scope, and it asserts EXACT equality with { "HttpProviderCore.cs" } — a second writer here
    // fails it. Verified green against this story. A local copy would be a second thing to keep in
    // step with ruling E3-b, and the five NotSentOf assertions above already say it behaviourally.

    /// <summary>The provider is <b>local</b>, and that has to stay structurally true rather than
    /// merely intended: no <c>HttpClient</c>, no <c>HttpProviderCore</c>, no <c>System.Net</c> of any
    /// kind. IS-10's guard enumerates providers by the client they hold, so a local one is outside
    /// it by construction — this is what keeps that exemption honest.</summary>
    [Fact]
    public void The_offline_provider_owns_nothing_that_can_reach_the_network()
    {
        var source = Code(File.ReadAllText(ServiceFile("BergamotTranslator.cs")))
                   + Code(File.ReadAllText(ServiceFile("BergamotEngine.cs")));

        foreach (var forbidden in new[] { "HttpClient", "HttpProviderCore", "System.Net", "Uri(" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);

        var fields = typeof(BergamotTranslator)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true);
    }

    /// <summary>Every await on this path configures the context away, for the same reason the seven
    /// files in <c>HttpProviderCoreTests</c>' scan do: the Translator tab awaits from the dispatcher,
    /// and the continuation after a 100 ms native call has no business going back there to finish.
    /// Written here rather than added to that scan because its derivation is "names
    /// <c>HttpProviderCore</c>", and this file structurally cannot — see the case above.</summary>
    [Fact]
    public void Every_await_in_the_offline_provider_configures_away_the_context()
    {
        var statements = Code(File.ReadAllText(ServiceFile("BergamotTranslator.cs")))
            .Split(';').Select(s => s.Replace("\n", " ").Trim()).Where(s => s.Length > 0).ToList();

        var awaits = statements.Where(s => Regex.IsMatch(s, @"(^|[^\w.])await\s")).ToList();

        Assert.NotEmpty(awaits);        // a scan that found nothing would pass while checking nothing
        Assert.All(awaits, s => Assert.Contains(".ConfigureAwait(false)", s, StringComparison.Ordinal));
    }

    /// <summary>
    /// U7, as a build-output pin: the package is referenced with <c>ExcludeAssets="native"</c>, so
    /// the normal build must not require — and must not produce — a 21.4 MB <c>bergamot.dll</c>. It
    /// is downloaded beside the models by E8.S3. The managed binding IS expected: it is what the
    /// production seam calls, and it is 0 MB of exe growth (E8.S1 §7).
    /// </summary>
    [Fact]
    public void The_native_library_is_not_in_the_build_output()
    {
        var output = AppContext.BaseDirectory;

        Assert.False(File.Exists(System.IO.Path.Combine(output, "bergamot.dll")),
            "bergamot.dll reached the build output — ExcludeAssets=\"native\" is what keeps the exe "
            + "at 0 MB of growth and %TEMP%\\.net\\PWRUHelper at five files (U7).");
        // …and not in a `runtimes/win-x64/native/` subtree either, which is where a PackageReference
        // without ExcludeAssets="native" would have put it.
        Assert.DoesNotContain(
            Directory.EnumerateFiles(output, "*bergamot*.dll", SearchOption.AllDirectories),
            f => System.IO.Path.GetFileName(f).Equals("bergamot.dll", StringComparison.OrdinalIgnoreCase));

        Assert.True(File.Exists(System.IO.Path.Combine(output, "BergamotTranslatorSharp.dll")),
            "the managed binding is missing — the production seam cannot compile without it.");
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary><c>NotSent</c> is <c>internal</c> with an <c>init</c>-only setter, so it is read by
    /// reflection rather than by construction — the assertion is that no code path in this provider
    /// ever turns it on.</summary>
    private static bool NotSentOf(TranslationException ex) =>
        (bool)typeof(TranslationException)
            .GetProperty("NotSent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ex)!;

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static string ServiceFile(string name)
    {
        var path = System.IO.Path.Combine(RepoRoot(), "Services", name);
        Assert.True(File.Exists(path), $"{name} not found at {path}");
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

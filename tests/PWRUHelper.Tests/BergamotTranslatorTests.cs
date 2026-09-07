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
        var b = Task.Run(() => provider.TranslateAsync("пока", "ru", "en"));

        Assert.True(firstIsInsideTheLoad.Wait(TimeSpan.FromSeconds(10)), "the load never started");
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

        factory.Last!.OnTranslate = (_, _) => throw new DllNotFoundException("bergamot");
        var gone = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(TranslationErrorKind.Unavailable, gone.Kind);
        Assert.Equal(ProviderIds.Bergamot, gone.ProviderId);
        Assert.False(NotSentOf(gone));

        factory.Last!.OnTranslate = (_, _) => throw new InvalidOperationException("garbage out");
        var broken = await Assert.ThrowsAsync<TranslationException>(
            () => provider.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(TranslationErrorKind.BadResponse, broken.Kind);
        Assert.Equal(ProviderIds.Bergamot, broken.ProviderId);
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

    /// <summary>Ruling E8-c's negative half, from this side. <c>ChainTranslatorTests</c> already
    /// asserts <c>HttpProviderCore</c> is the only writer of <c>NotSent</c> across the whole app —
    /// this is the local statement of the same rule, so a reader of THIS file finds it here, and it
    /// covers the seam file too.</summary>
    [Fact]
    public void The_offline_provider_never_writes_NotSent()
    {
        foreach (var file in new[] { "BergamotTranslator.cs", "BergamotEngine.cs" })
            Assert.False(Regex.IsMatch(Code(File.ReadAllText(ServiceFile(file))), @"NotSent\s*=(?!=)"),
                $"{file} writes NotSent — ruling E3-b gives the flag one owner (HttpProviderCore).");
    }

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

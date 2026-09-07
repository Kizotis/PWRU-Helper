using System.IO;
using System.Linq;
using System.Reflection;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E3.S7 — <c>architecture-cible.md</c> §8.1, the composition itself: which tiers each chain has,
/// in which order, with which priority, and — the one with a money consequence — which tier the
/// read chain <b>cannot</b> have.
///
/// <para>Everything here asserts on a <b>built chain</b> and never on source text: the failure this
/// file exists to prevent is a later story putting DeepL on the read path, and a scan for the word
/// "DeepL" in a file would pass happily while a settings-driven builder put it there. The tiers are
/// read back out of the constructed <see cref="ChainTranslator"/> by reflection, which is also the
/// only way to reach a provider's per-instance <see cref="RequestPriority"/> (I1 keeps it off
/// <see cref="ITranslator"/>, so there is nothing public to ask).</para>
///
/// <para>Nothing here translates: the chains are built exactly as production builds them, i.e.
/// without a test handler, so a case that called them would reach the real Internet (IS-10). The
/// two cases that do drive a request build their providers directly, with a
/// <see cref="FakeHandler"/> and the same priorities the chains carry.
/// <c>[Collection("Gates")]</c> because building a chain resolves the process-global registry
/// (IS-5).</para>
/// </summary>
[Collection("Gates")]
public class ChainCompositionTests : GatesTestBase
{
    private const string DictOk = """["hello"]""";

    /// <summary>Every case here builds a chain, and since E4.S4 building one materialises the
    /// process-wide <c>TranslationChains.Cache</c>. A store pins its path on first use, so leaving
    /// this instance alive would hand the next case — in this file or in
    /// <c>TranslationCachePersistenceTests</c> — a store carrying this run's entries and, one day, a
    /// path into a deleted temp directory (the trap E4.S2's review found the hard way). Dropping it
    /// per case is free: nothing here translates, so nothing here fills it.</summary>
    protected override void DisposeCore() => TranslationChains.ResetCacheForTests();

    // ---- reading a built chain back ------------------------------------------------------------

    /// <summary>The tiers of a built chain, in order. Reflection because <c>_tiers</c> is private
    /// and stays private: the chain's shape is this file's business and nobody else's, and widening
    /// the class to make a test easier would put the tier list in reach of the code-behind.
    ///
    /// <para><b>One hop further in since E4.S4:</b> a builder returns the chain already wrapped in
    /// the <see cref="CachingTranslator"/> that carries the one shared store (§8.2), so the code-
    /// behind names neither a decorator nor a store. The <c>IsType</c> is deliberate — it makes
    /// every case in this file assert the wrapping as a side effect, so a builder that quietly
    /// stopped caching would fail here as well as in the case that owns it.</para></summary>
    private static IReadOnlyList<ChainTier> TiersOf(ITranslator chain)
    {
        var decorator = Assert.IsType<CachingTranslator>(chain);
        var inner = (ITranslator)decorator.GetType()
            .GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decorator)!;

        var field = inner.GetType().GetField("_tiers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"{inner.GetType().Name} has no _tiers field — the reader below is blind");
        return (IReadOnlyList<ChainTier>)field!.GetValue(inner)!;
    }

    /// <summary>The store a built chain's decorator was handed, and the capacity that store was
    /// built with. Both by reflection for the same reason as the tiers: neither is public, and
    /// neither may become public to make a test shorter.</summary>
    private static TranslationCacheStore StoreOf(ITranslator chain)
    {
        var decorator = Assert.IsType<CachingTranslator>(chain);
        return (TranslationCacheStore)decorator.GetType()
            .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decorator)!;
    }

    private static int CapacityOf(TranslationCacheStore store) =>
        (int)store.GetType()
            .GetField("_capacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static List<string> IdsOf(ITranslator chain) => TiersOf(chain).Select(t => t.ProviderId).ToList();

    /// <summary>A provider's per-instance priority. <c>DeepLTranslator</c> has no such field on
    /// purpose — it is a write-path provider that is always <c>Interactive</c> and by I8 can never
    /// appear on the read chain — so "no field" reads as <c>Interactive</c> here, which is what it
    /// passes to the core.</summary>
    private static RequestPriority PriorityOf(ITranslator provider)
    {
        var field = provider.GetType().GetField("_priority", BindingFlags.Instance | BindingFlags.NonPublic);
        return field == null ? RequestPriority.Interactive : (RequestPriority)field.GetValue(provider)!;
    }

    /// <summary>
    /// Every settings shape the app can be in, plus the ones it cannot be in yet. The loop over
    /// <see cref="AppSettings"/>'s own writable properties is the part that matters: E6 adds
    /// <c>AzureApiKey</c> and <c>UseKeyForReading</c> and E8 adds <c>OfflineFallbackEnabled</c>, and
    /// each of them is covered by TP-CHN-14 the day it is declared, without anybody remembering to
    /// come back here. Only strings and bools are touched — an int setting is a slider position and
    /// no chain reads one.
    /// </summary>
    private static List<AppSettings> EveryPermutation()
    {
        var all = new List<AppSettings>
        {
            new(),                                          // defaults
            new() { DeepLApiKey = "" },
            new() { DeepLApiKey = "   " },                  // whitespace is not a key
            new() { DeepLApiKey = "abc-123:fx" },
        };

        foreach (var p in typeof(AppSettings).GetProperties()
                     .Where(p => p.CanWrite && (p.PropertyType == typeof(string)
                                             || p.PropertyType == typeof(bool))))
        {
            // Both poles of every flag, and a set/unset pair for every string: the point is that a
            // FUTURE setting cannot open the read path to DeepL, and only one of its two values
            // would be the dangerous one.
            foreach (var value in p.PropertyType == typeof(bool)
                         ? new object?[] { true, false }
                         : new object?[] { "set", "" })
            {
                var s = new AppSettings();
                p.SetValue(s, value);
                all.Add(s);
            }
        }
        return all;
    }

    // =============================================================================================
    //  TP-CHN-14 — I8: DeepL is unreachable from the read path BY CONSTRUCTION
    // =============================================================================================

    /// <summary>
    /// <b>TP-CHN-14. Do not delete this case, and do not weaken it into a source scan.</b>
    ///
    /// <para>What it costs if it goes: DeepL's free plan is a one-time <b>1 M characters in
    /// total</b> (<c>benchmark-fournisseurs.md</c> §5.3) — not per month. The read path is the LIVE
    /// loop, which translates every new chat line it sees, and it would drain a user's entire
    /// allowance in about five days of heavy play, silently, on text they never asked to spend it
    /// on. I8 says no setting may put DeepL there; this asserts it over every settings shape that
    /// exists and every one that is coming.</para>
    /// </summary>
    [Fact]
    public void TP_CHN_14_DeepL_is_structurally_absent_from_the_read_chain()
    {
        var permutations = EveryPermutation();
        Assert.True(permutations.Count > 4, "the permutation sweep found no settings to vary");

        foreach (var settings in permutations)
            foreach (var priority in new[] { RequestPriority.Background, RequestPriority.Interactive })
                foreach (var tier in TiersOf(TranslationChains.BuildRead(settings, priority)))
                {
                    Assert.IsNotType<DeepLTranslator>(tier.Translator);
                    Assert.NotEqual(ProviderIds.DeepL, tier.ProviderId);
                }

        // Non-vacuity: the same sweep DOES find DeepL on the write chain when a key is set, so the
        // assertion above is about where DeepL is allowed and not about a builder that never
        // constructs one.
        Assert.Contains(ProviderIds.DeepL,
            IdsOf(TranslationChains.BuildWrite(new AppSettings { DeepLApiKey = "abc-123:fx" })));
    }

    // =============================================================================================
    //  Tier order — a reordering is a failing test, not a silent change of vendor
    // =============================================================================================

    /// <summary>
    /// §8.1's read order, as shipped in A.1: <c>google-dict</c> first (the owner's decision 2 — the
    /// endpoint switch) and <c>google-gtx</c> last. <c>edge</c> is missing because U2 is
    /// owner-blocked and ruling <b>E3-d</b> allows A.1 to ship without it; when the owner designates
    /// a capture, this array grows by one in the middle and so does the builder.
    /// </summary>
    [Fact]
    public void The_read_chain_is_google_dict_then_google_gtx()
    {
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildRead(new AppSettings())));

        // The settings do not reorder it, and today they cannot shorten it either.
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildRead(new AppSettings { DeepLApiKey = "abc-123:fx" })));
    }

    /// <summary>§8.1's write order: the user's own key first when they have one — it is theirs, and
    /// it is the best engine on this path — then exactly the read chain's free tiers behind it.
    /// Without a key the write chain and the read chain have the same shape and differ only in the
    /// priority their providers carry.</summary>
    [Fact]
    public void The_write_chain_puts_a_configured_key_first_and_the_free_tiers_behind_it()
    {
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(new AppSettings { DeepLApiKey = "" })));
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(new AppSettings { DeepLApiKey = "   " })));
        Assert.Equal(new[] { ProviderIds.DeepL, ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(new AppSettings { DeepLApiKey = " abc-123:fx " })));
    }

    // =============================================================================================
    //  Priority — the only automated proof that E2.S3's reserve is no longer inert
    // =============================================================================================

    /// <summary>
    /// E2.S5 shipped the reserve and left it inert on purpose: <c>Background</c> did not reach the
    /// read chain, because doing so meant a call-site change and TP-START-02 allows exactly one
    /// <c>ProviderGates</c> reference outside <c>Services/</c>. This is where that ends — the LIVE
    /// read chain's providers carry <c>Background</c>, the write chain's carry <c>Interactive</c>,
    /// and read-once gets its own <c>Interactive</c> read chain (ruling OQ-a: it is a user click).
    /// </summary>
    [Fact]
    public void The_read_chain_is_Background_read_once_and_the_write_chain_are_Interactive()
    {
        var settings = new AppSettings { DeepLApiKey = "abc-123:fx" };

        Assert.All(TiersOf(TranslationChains.BuildRead(settings)),
            t => Assert.Equal(RequestPriority.Background, PriorityOf(t.Translator)));

        Assert.All(TiersOf(TranslationChains.BuildRead(settings, RequestPriority.Interactive)),
            t => Assert.Equal(RequestPriority.Interactive, PriorityOf(t.Translator)));

        Assert.All(TiersOf(TranslationChains.BuildWrite(settings)),
            t => Assert.Equal(RequestPriority.Interactive, PriorityOf(t.Translator)));

        // Non-vacuity: PriorityOf really reads a field rather than always answering Interactive.
        Assert.Contains(TiersOf(TranslationChains.BuildRead(settings)),
            t => t.Translator.GetType()
                  .GetField("_priority", BindingFlags.Instance | BindingFlags.NonPublic) != null);
    }

    /// <summary>
    /// …and the priority the chain carries really changes what the gate does — the half a reflection
    /// assert cannot prove. §5.4: <c>Background</c> may draw the token bucket down but may not take
    /// the last token, so after one <c>Interactive</c> request the bucket holds one token and a
    /// <c>Background</c> caller is sent back to wait for a full one, while a second
    /// <c>Interactive</c> caller goes straight through.
    ///
    /// <para>Built directly with a <see cref="FakeHandler"/> and the two priorities the chains
    /// carry, because a chain built by <see cref="TranslationChains"/> has production's real client
    /// (IS-10). Asserted on the delay the core <b>requested</b> (IS-7), never on elapsed time
    /// (CI-3) — nothing here sleeps.</para>
    /// </summary>
    [Fact]
    public async Task Background_stands_aside_for_the_reserve_where_Interactive_goes_straight_through()
    {
        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        var fake = new FakeHandler().RespondJson(DictOk);

        // Interactive first: the bucket goes from BucketCapacity to one token.
        await new GoogleDictTranslator(fake, gate, RequestPriority.Interactive)
            .TranslateAsync("привет", "ru", "en");
        Assert.Empty(TestBackoffRedirect.Delays);

        // A Background caller needs the whole bucket and is made to wait for the refill.
        await new GoogleDictTranslator(fake, gate, RequestPriority.Background)
            .TranslateAsync("пока", "ru", "en");
        var waited = Assert.Single(TestBackoffRedirect.Delays);
        Assert.True(waited > TimeSpan.Zero, "Background took the reserved token instead of standing aside");

        // The same two requests, both Interactive: no wait at all. This is the comparison that makes
        // the case about the PRIORITY rather than about the bucket.
        ResetGates();
        var shared = ProviderGates.For(ProviderIds.GoogleDict);
        var interactive = new GoogleDictTranslator(fake, shared, RequestPriority.Interactive);
        await interactive.TranslateAsync("привет", "ru", "en");
        await interactive.TranslateAsync("пока", "ru", "en");
        Assert.Empty(TestBackoffRedirect.Delays);
    }

    // =============================================================================================
    //  I9 / I10 — one gate per provider, and nothing read at startup
    // =============================================================================================

    /// <summary>
    /// <b>I9.</b> Three chains, three sets of provider objects, but <c>google-dict</c> has exactly
    /// ONE gate — the registry's. That shared identity is the invariant: the external condition a
    /// gate mirrors is per endpoint, so a provider paused for the LIVE loop is paused for the
    /// Translator tab too, and the reverse. Asserted by reference, which is the only thing that
    /// could ever be wrong here.
    /// </summary>
    [Fact]
    public void Both_chains_share_one_gate_per_provider()
    {
        var settings = new AppSettings { DeepLApiKey = "abc-123:fx" };
        var read = TiersOf(TranslationChains.BuildRead(settings));
        var readOnce = TiersOf(TranslationChains.BuildRead(settings, RequestPriority.Interactive));
        var write = TiersOf(TranslationChains.BuildWrite(settings));

        foreach (var id in new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx })
        {
            var expected = ProviderGates.For(id);
            Assert.Same(expected, read.Single(t => t.ProviderId == id).Gate);
            Assert.Same(expected, readOnce.Single(t => t.ProviderId == id).Gate);
            Assert.Same(expected, write.Single(t => t.ProviderId == id).Gate);
        }

        // …and the provider objects are NOT shared: they differ in the one thing that may not be,
        // their priority. (This is the sentence TranslationChains' comment makes; here it is true.)
        Assert.NotSame(read.First().Translator, write.First().Translator);
    }

    // =============================================================================================
    //  E4.S4 — one shared store behind all three chains (§8.2, AC 1)
    // =============================================================================================

    /// <summary>
    /// <b>The epic's whole value, as an object identity.</b> <c>SharedCacheStoreTests</c> proves what
    /// sharing a store DOES (TP-CACHE-13's zero inner calls); this proves that the three chains
    /// production actually builds share <b>the</b> store — the same shape, and the same reason, as
    /// <see cref="Both_chains_share_one_gate_per_provider"/> one case up: shared state resolved once,
    /// at composition.
    ///
    /// <para><b>And the capacity, which is the half that would otherwise ship wrong in silence.</b>
    /// <c>new TranslationCacheStore()</c> is 2000 (§8.2); a builder that passed
    /// <c>CacheCapacityToday</c> by copy-paste would end release A.2 with a 500-deep cache and every
    /// other test still green, because nothing else asserts what the shared instance was built with
    /// (E4.S1's review, deferred here). AC 1's user-visible half lands on this line.</para>
    ///
    /// <para><b>Ruling E4-a is asserted here on purpose.</b> One store behind three decorators means
    /// a value DeepL produced for the Translator tab can later be served to the LIVE feed. That is
    /// accepted: §8.2's key is provider-agnostic by design, and I8 is about <i>requests</i> — the
    /// read chain still cannot construct a <see cref="DeepLTranslator"/> (TP-CHN-14 above), so no
    /// read ever spends a character of the user's one-time million. A cached string costs no quota.
    /// The producing tier is recorded in the entry's <c>"p"</c> for the log and the Bergamot drop
    /// rule, and for nothing else — it is deliberately not part of the key.</para>
    /// </summary>
    [Fact]
    public void The_three_chains_are_decorators_over_one_shared_store_of_the_default_capacity()
    {
        var settings = new AppSettings { DeepLApiKey = "abc-123:fx" };

        var read = StoreOf(TranslationChains.BuildRead(settings));
        var readOnce = StoreOf(TranslationChains.BuildRead(settings, RequestPriority.Interactive));
        var write = StoreOf(TranslationChains.BuildWrite(settings));

        // One instance, and it is the process's one persistent store — not merely "the same as each
        // other", which three private stores of a single builder call would also satisfy.
        Assert.Same(TranslationChains.Cache, read);
        Assert.Same(TranslationChains.Cache, readOnce);
        Assert.Same(TranslationChains.Cache, write);

        // Still the same instance on a REBUILD, which is AC 2: the key-save handler re-runs
        // BuildWriteChain() and the session's translations survive it.
        Assert.Same(read, StoreOf(TranslationChains.BuildWrite(settings)));

        Assert.Equal(TranslationPolicy.CacheCapacity, CapacityOf(read));
        Assert.Equal(2000, CapacityOf(read));   // spelled out: the number a player feels, not a symbol

        // Non-vacuity: 2000 is not what a decorator built the legacy way would have.
        Assert.Equal(TranslationPolicy.CacheCapacityToday,
                     CapacityOf(StoreOf(new CachingTranslator(TranslationChains.BuildRead(settings)))));
    }

    /// <summary>
    /// <b>I10 / TP-START-01.</b> Both chains are built in <c>MainWindow</c>'s constructor, before
    /// first paint, so building one may not touch the disk: <c>ProviderGates.For</c> constructs, and
    /// the first <c>TryEnter</c> — inside <c>HttpProviderCore</c>, on the first real request — is
    /// what loads <c>provider-state.json</c>. The Services-level half of the startup guard, for the
    /// one operation this story adds to the constructor.
    /// </summary>
    [Fact]
    public void Building_the_chains_touches_no_gate_state_file()
    {
        using var temp = new TempGateState();
        // A file that WOULD be noticed if it were read: google-dict paused 30 s from now, in the
        // schema the store actually parses — `providers` is a MAP, and the empty ARRAY this case
        // used to write made the second assertion vacuous twice over (it would not have parsed, and
        // it named no provider to seed). The window is inside OpenCapMinutes so the seed needs no
        // clamping and therefore queues no corrective write of its own.
        var until = ProviderGates.Clock() + TimeSpan.FromSeconds(30);
        File.WriteAllText(temp.Path, $$"""
            { "version": 1, "providers": { "google-dict": {
                "blockedUntil": "{{until:o}}",
                "keyBlockedUntil": null,
                "strikes": 1,
                "lastKind": "RateLimited",
                "lastAt": null,
                "cleanSince": null } } }
            """);
        var before = File.GetLastWriteTimeUtc(temp.Path);

        var settings = new AppSettings { DeepLApiKey = "abc-123:fx" };
        TranslationChains.BuildRead(settings);
        TranslationChains.BuildRead(settings, RequestPriority.Interactive);
        TranslationChains.BuildWrite(settings);

        // Not written — and, the half that actually is I10, not READ: had a builder loaded the file,
        // the gate it resolved through the registry would be carrying that pause right now.
        Assert.Equal(before, File.GetLastWriteTimeUtc(temp.Path));
        Assert.Null(ProviderGates.Snapshot(ProviderIds.GoogleDict)?.BlockedUntil);

        // Non-vacuity, and it is the whole point of the rewrite: that file really does seed that
        // gate the moment something asks for it to be read (the first TryEnter, after first paint).
        ProviderGates.EnsureLoaded();
        Assert.NotNull(ProviderGates.Snapshot(ProviderIds.GoogleDict)?.BlockedUntil);

        // The E4.S4 half of the same invariant, for the second file the constructor now brings into
        // existence: building a chain CONSTRUCTS the shared store and asks it nothing, so
        // translation-cache.json is not read before first paint either. `Count` is the assert
        // precisely because it does not trigger the lazy load (TranslationCacheStore.Count) — the
        // first MISS does, after the window is up.
        Assert.Equal(0, TranslationChains.Cache.Count);
    }

    // =============================================================================================
    //  The wiring in the code-behind
    // =============================================================================================

    /// <summary>
    /// The two call sites, pinned in source because the alternative is constructing a
    /// <c>MainWindow</c> on an STA thread to read two private fields. The LIVE loop hands the
    /// <c>Background</c> chain to <c>TranslateBodiesAsync</c> and read-once hands it the
    /// <c>Interactive</c> one (ruling OQ-a) — swapping them would be invisible until a user's click
    /// sat behind their own screen loop.
    ///
    /// <para>The other half is TP-START-02's own scan
    /// (<c>ProviderStateStoreTests.No_startup_path_mentions_ProviderGates</c>), which is what keeps
    /// the composition in <c>Services/</c>: it stays untouched by this story.</para>
    /// </summary>
    [Fact]
    public void LIVE_reads_through_the_Background_chain_and_read_once_through_the_Interactive_one()
    {
        var root = RepoRoot();
        var live = Code(File.ReadAllText(Path.Combine(root, "MainWindow.Live.cs")));
        var ocr = Code(File.ReadAllText(Path.Combine(root, "MainWindow.Ocr.cs")));
        var main = Code(File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs")));

        Assert.Contains("_readTranslator, ct)", live, StringComparison.Ordinal);
        Assert.DoesNotContain("_readOnceTranslator", live, StringComparison.Ordinal);
        Assert.Contains("_readOnceTranslator, default)", ocr, StringComparison.Ordinal);

        // Both chains are assigned in the ctor body — never back in a field initializer, where they
        // would run BEFORE _settings and read a null — and _readTranslator stays readonly so no
        // handler can swap the LIVE chain for an Interactive one later.
        //
        // The line lost its `new CachingTranslator(…)` in E4.S4: the builder returns the chain
        // already wrapped in the decorator that carries the shared store, so this file names a chain
        // and nothing else. The exact-line assert is kept rather than loosened to a substring match
        // on BuildRead(_settings) — the ordering assert below already does the loose half, and it is
        // this one that stops the assignment creeping back into a field initializer.
        Assert.Contains("private readonly ITranslator _readTranslator;", main, StringComparison.Ordinal);
        Assert.Contains("_readTranslator = TranslationChains.BuildRead(_settings);",
            main, StringComparison.Ordinal);
        Assert.True(main.IndexOf("TranslationChains.BuildRead(_settings)", StringComparison.Ordinal)
                    < main.IndexOf("InitializeComponent()", StringComparison.Ordinal),
            "the chains must be built before InitializeComponent() fires the change handlers");

        // …and the write chain's rebuild, which is AC 2's source-level companion: the key-save
        // handler builds a new CHAIN, and the store it caches into is TranslationChains' — so a
        // merge that dropped the sharing could not compile past this line without also changing it.
        var translate = Code(File.ReadAllText(Path.Combine(root, "MainWindow.Translate.cs")));
        Assert.Contains("private ITranslator BuildWriteChain() => TranslationChains.BuildWrite(_settings);",
            translate, StringComparison.Ordinal);

        // The code-behind names no decorator at all — the same rule, and the same reason, as
        // TranslationCachePersistenceTests' "no source outside Services/ names the store" and
        // TP-START-02's ProviderGates scan: the composition lives in Services/ (ruling E3-c), so the
        // next CachingTranslator outside it has to be a decision rather than a convenience.
        foreach (var file in new[] { "MainWindow.xaml.cs", "MainWindow.Translate.cs",
                                     "MainWindow.Live.cs", "MainWindow.Ocr.cs", "MainWindow.Compact.cs" })
            Assert.DoesNotContain("new CachingTranslator",
                Code(File.ReadAllText(Path.Combine(root, file))), StringComparison.Ordinal);
    }

    // ---- the source-scan helpers (the shape ChainTranslatorTests already uses) -------------------

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

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
    /// per case is free: nothing here translates, so nothing here fills it.
    ///
    /// <para>Before as well as after, which is <see cref="GatesTestBase"/>'s own rule for the
    /// registry applied to the store (review, E4.S4): this collection is serialised against every
    /// other one, but it is not the first thing to run — a <c>new MainWindow()</c> in the WPF
    /// collection now builds three chains, and nothing there drops the singleton afterwards. What
    /// arrives here must be this file's own store, not whatever a window left behind.</para></summary>
    public ChainCompositionTests() => TranslationChains.ResetCacheForTests();

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
        var inner = InnerOf(chain);

        var field = inner.GetType().GetField("_tiers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"{inner.GetType().Name} has no _tiers field — the reader below is blind");
        return (IReadOnlyList<ChainTier>)field!.GetValue(inner)!;
    }

    /// <summary>What a builder's decorator actually delegates to. Split out of <see cref="TiersOf"/>
    /// at review of E5.S1, which needs the reference itself and not its tiers.</summary>
    private static ITranslator InnerOf(ITranslator chain)
    {
        var decorator = Assert.IsType<CachingTranslator>(chain);
        return (ITranslator)decorator.GetType()
            .GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decorator)!;
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

    /// <summary>
    /// <b>E5.S1's <c>out</c> seam, pinned as an IDENTITY.</b> The code-behind translates through the
    /// returned decorator and asks the chain handed back beside it whether every rung is paused
    /// (<c>ChainTranslator.PauseNow</c>). Those two references answer for one object graph only as
    /// long as they really are one object graph.
    ///
    /// <para>A builder that handed back a SECOND <c>ChainTranslator.Of(...)</c> would compile, and
    /// would pass every other case in this file — the gates are process-global (I9), so even
    /// <c>PauseNow</c> would agree. What it would silently break is the half nothing else covers:
    /// <b>E7.S3 reads <c>LastOutcome</c> from this same field</b>, and a chain nothing translates
    /// through has no last outcome. Cheap to assert, and impossible to notice at runtime.</para>
    /// </summary>
    [Fact]
    public void The_chain_handed_back_is_the_one_the_returned_translator_wraps()
    {
        var read = TranslationChains.BuildRead(new AppSettings(), RequestPriority.Background,
                                               out var chain);

        Assert.Same(chain, InnerOf(read));
        // …and it is a real chain, not an empty shell that happens to be the same reference.
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     TiersOf(read).Select(t => t.ProviderId));
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
    //  E6.S3 — Azure on the WRITE chain, and the key save that resets its gate
    // =============================================================================================

    private const string AzureKey = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// §8.1's write path with both keys configured, and <c>ux</c> flow (c).6 says the order out
    /// loud: "DeepL, then Azure, then the free engines". All four key combinations, because the
    /// order is the whole assertion — a reordering is a silent change of vendor for every line the
    /// user writes, and nothing else in the app would notice.
    ///
    /// <para><c>BuildRead</c> is deliberately not asserted here: Azure reaches it only behind
    /// <c>UseKeyForReading</c>, which is E6.S4's. TP-CHN-14's sweep already covers the new settings
    /// from the I8 side.</para>
    /// </summary>
    [Fact]
    public void The_write_chain_gains_the_azure_tier_after_DeepL_and_before_the_free_engines()
    {
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(new AppSettings())));

        Assert.Equal(new[] { ProviderIds.DeepL, ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(new AppSettings { DeepLApiKey = "abc-123:fx" })));

        Assert.Equal(new[] { ProviderIds.Azure, ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(
                         new AppSettings { AzureApiKey = AzureKey, AzureRegion = "westeurope" })));

        Assert.Equal(new[] { ProviderIds.DeepL, ProviderIds.Azure,
                             ProviderIds.GoogleDict, ProviderIds.GoogleGtx },
                     IdsOf(TranslationChains.BuildWrite(new AppSettings
                     {
                         DeepLApiKey = "abc-123:fx",
                         AzureApiKey = AzureKey,
                         AzureRegion = " WestEurope ",   // trimmed here as it is at the Save button
                     })));
    }

    /// <summary>
    /// <b>Half of a credential adds no tier at all</b>, and this is E6.S2's review finding rather
    /// than tidiness: the provider's own guard throws <c>AuthFailed</c> <b>without</b>
    /// <c>NotSent</c> (ruling E3-b gives that flag one writer), so a tier built from half a
    /// credential is counted by <see cref="ChainTranslator"/> as a tier that tried and failed — and
    /// its sentence outranks every skipped tier. The player would be told their key was refused by
    /// a request that was never worth sending.
    /// </summary>
    [Theory]
    [InlineData(AzureKey, "")]
    [InlineData("", "westeurope")]
    [InlineData("   ", "westeurope")]
    [InlineData(AzureKey, "   ")]
    [InlineData("abc\u0007def", "westeurope")]      // unsendable: a header may carry no control char
    [InlineData(AzureKey, "west\u0001europe")]
    public void A_half_entered_azure_credential_adds_no_tier(string key, string region)
    {
        var ids = IdsOf(TranslationChains.BuildWrite(
            new AppSettings { AzureApiKey = key, AzureRegion = region }));

        Assert.DoesNotContain(ProviderIds.Azure, ids);
        Assert.Equal(new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx }, ids);
    }

    /// <summary>
    /// TP-SET-09 / AC 6, at the facade the code-behind actually calls. Ruling <b>E2-a</b> is why it
    /// exists — "no state may lock the user out without a way back" — and ruling <b>E2-i</b> is its
    /// exact bound: a new key lifts the ACCOUNT-scoped block (<c>AuthFailed</c>,
    /// <c>QuotaExhausted</c>) and never the IP-scoped one (<c>RateLimited</c>, <c>Blocked</c>),
    /// which is about the address this PC dials from and which no key can change.
    ///
    /// <para>It is asserted here, in <c>Services/</c>'s own test file, because that is where the
    /// registry may be named: <c>MainWindow.Translate.cs</c> calls
    /// <c>TranslationChains.OnKeySaved</c> and TP-START-02's allow-list stays one line long.</para>
    /// </summary>
    [Fact]
    public void TP_SET_09_Saving_a_key_lifts_that_provider_account_scoped_block_and_nothing_else()
    {
        var azure = ProviderGates.For(ProviderIds.Azure);
        var dict = ProviderGates.For(ProviderIds.GoogleDict);

        azure.ReportFailure(TranslationErrorKind.AuthFailed);
        dict.ReportFailure(TranslationErrorKind.RateLimited);

        Assert.NotNull(azure.Snapshot().BlockedUntil);
        var otherProvidersWindow = dict.Snapshot().BlockedUntil;
        Assert.NotNull(otherProvidersWindow);

        TranslationChains.OnKeySaved(ProviderIds.Azure);

        Assert.Null(azure.Snapshot().BlockedUntil);                       // the key's own block: gone
        Assert.Equal(otherProvidersWindow, dict.Snapshot().BlockedUntil); // another provider: untouched
    }

    /// <summary>The other half of E2-i, on the SAME provider: a 429 window is the provider counting
    /// requests from this IP, and pasting a new key does not move it.</summary>
    [Fact]
    public void Saving_a_key_does_not_lift_a_rate_limit_on_the_same_provider()
    {
        var azure = ProviderGates.For(ProviderIds.Azure);
        azure.ReportFailure(TranslationErrorKind.RateLimited);
        var ipWindow = azure.Snapshot().BlockedUntil;
        Assert.NotNull(ipWindow);

        azure.ReportFailure(TranslationErrorKind.AuthFailed);
        Assert.Equal(DateTimeOffset.MaxValue, azure.Snapshot().BlockedUntil);

        TranslationChains.OnKeySaved(ProviderIds.Azure);

        Assert.NotNull(azure.Snapshot().BlockedUntil);
        Assert.NotEqual(DateTimeOffset.MaxValue, azure.Snapshot().BlockedUntil);
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
        // `, ct)` and no longer `, default)`: E5.S4's whole AC 2 is that the argument stopped being
        // `default`, so this line changed ON PURPOSE. It still pins the chain — the pair of asserts
        // is about WHICH chain each path reads through — and now pins the token with it, which is
        // the one thing that would silently bring back ≈36.9 s of uncancellable UI.
        Assert.Contains("_readOnceTranslator, ct)", ocr, StringComparison.Ordinal);

        // Both chains are assigned in the ctor body — never back in a field initializer, where they
        // would run BEFORE _settings and read a null — and _readTranslator stays readonly so no
        // handler can swap the LIVE chain for an Interactive one later.
        //
        // The line lost its `new CachingTranslator(…)` in E4.S4: the builder returns the chain
        // already wrapped in the decorator that carries the shared store, so this file names a chain
        // and nothing else. The exact-line assert is kept rather than loosened to a substring match
        // on BuildRead(_settings) — the ordering assert below already does the loose half, and it is
        // this one that stops the assignment creeping back into a field initializer.
        //
        // E5.S1 gained the `out _readChain`: the LIVE loop has to ask the CHAIN whether every rung
        // is paused (ChainTranslator.PauseNow) before it captures anything, and it may not ask the
        // registry (TP-START-02). It is an `out` rather than a changed return type — which is what
        // the story sketched, before E4.S4 landed first — precisely so the decorator keeps being
        // built inside Services/ and the scan at the bottom of this case stays green. One call, one
        // object graph: a second BuildRead would build a second chain over the same gates.
        //
        // E6.S3 dropped `readonly` from the read pair and from _readChain, DELIBERATELY: a key save
        // has to rebuild the read chain too (its AC 6 — the key may already be opted into reading
        // from a previous session), and a readonly field cannot be reassigned. What replaces the
        // compiler's guarantee is the pair of asserts below the ctor ones: RebuildReadChains() is
        // the only other writer and it reassigns all THREE together. That is the failure `readonly`
        // never prevented anyway — reassigning two of the three compiles, runs, and leaves
        // PauseNow() answering for a chain nothing translates through (I9: same gates, so no
        // behaviour test can see it).
        Assert.Contains("private ITranslator _readTranslator;", main, StringComparison.Ordinal);
        Assert.Contains("private ChainTranslator _readChain;", main, StringComparison.Ordinal);
        Assert.Contains(
            "_readTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Background, out _readChain);",
            main, StringComparison.Ordinal);
        Assert.True(main.IndexOf("TranslationChains.BuildRead(_settings", StringComparison.Ordinal)
                    < main.IndexOf("InitializeComponent()", StringComparison.Ordinal),
            "the chains must be built before InitializeComponent() fires the change handlers");

        // …and the write chain's rebuild, which is AC 2's source-level companion: the key-save
        // handler builds a new CHAIN, and the store it caches into is TranslationChains' — so a
        // merge that dropped the sharing could not compile past this line without also changing it.
        var translate = Code(File.ReadAllText(Path.Combine(root, "MainWindow.Translate.cs")));
        Assert.Contains("private ITranslator BuildWriteChain() => TranslationChains.BuildWrite(_settings);",
            translate, StringComparison.Ordinal);

        // …and the READ chain's rebuild, E6.S3's companion to it. All three references are
        // reassigned in ONE method, and that method is the only writer besides the constructor —
        // two occurrences of each assignment across the two files, no more. `out _readChain` on the
        // same line as `_readTranslator =` is what makes "together" structural rather than
        // remembered.
        var both = main + "\n" + translate;
        Assert.Contains("private void RebuildReadChains()", translate, StringComparison.Ordinal);
        foreach (var assignment in new[]
                 {
                     "_readTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Background, out _readChain);",
                     "_readOnceTranslator = TranslationChains.BuildRead(_settings, RequestPriority.Interactive);",
                 })
            Assert.Equal(2, Occurrences(both, assignment));

        foreach (var field in new[] { "_readTranslator =", "_readOnceTranslator =", "_readChain =" })
            Assert.Equal(field == "_readChain =" ? 0 : 2, Occurrences(both, field));

        // NO source outside Services/ names a decorator — the same rule, and the same reason, as
        // TranslationCachePersistenceTests' "no source outside Services/ names the store" and
        // TP-START-02's ProviderGates scan: the composition lives in Services/ (ruling E3-c), so the
        // next CachingTranslator outside it has to be a decision rather than a convenience.
        //
        // SCANNED, not listed by name (review, E4.S4): a hardcoded list of five MainWindow partials
        // cannot see the sixth, and MainWindow.Phrasebook.cs, MainWindow.Squad.cs,
        // MainWindow.Update.cs and CompactOverlay.xaml.cs were all already outside it. A guard that
        // misses the file it exists for is worth nothing.
        var decorators = ProductionSources(root)
            .Where(f => !Path.GetDirectoryName(f)!.EndsWith("Services", StringComparison.Ordinal))
            .SelectMany(f => Code(File.ReadAllText(f)).Split('\n')
                .Where(l => l.Contains("new CachingTranslator", StringComparison.Ordinal))
                .Select(l => Path.GetFileName(f) + ": " + l.Trim()))
            .ToList();

        Assert.True(decorators.Count == 0,
            "outside Services/ the code names a chain and never a decorator (ruling E3-c): "
            + string.Join(" | ", decorators));
    }

    /// <summary>How many times <paramref name="needle"/> appears in <paramref name="haystack"/> —
    /// the "exactly one other writer" assert above needs a count, not a contains.</summary>
    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ---- the source-scan helpers (the shape ChainTranslatorTests already uses) -------------------

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    /// <summary>Every shipped <c>.cs</c> — the app's own sources, not the suite's and not a build
    /// output. Deliberately the same shape as
    /// <c>TranslationCachePersistenceTests.ProductionSources</c>, because the two scans are the two
    /// halves of one rule (no store and no decorator outside <c>Services/</c>) and a copy that
    /// drifted would be worse than a copy that did not.</summary>
    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f)
                            .Split('/', '\\')
                            .SkipLast(1)
                            .All(seg => !seg.StartsWith('.')
                                        && !seg.Equals("tests", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

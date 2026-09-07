namespace PWRUHelper.Services;

/// <summary>
/// <b>E8.S5 — the offline rung's two halves, as one argument to the builders.</b>
///
/// <para><b>The engine is passed in and never constructed here</b> (E8.S4's review pin). There is
/// exactly ONE <c>BergamotTranslator</c> in the process — <c>MainWindow._offlineEngine</c> — because
/// a second would be 121 MiB nothing could unload and the two would disagree about what is resident.
/// <c>BergamotLifetimeTests.The_app_constructs_exactly_one_offline_provider</c> scans for it. The
/// tier can be in BOTH chains, and both then wrap the SAME instance: the provider's own lock
/// serialises them, which is the design and not an oversight (its own contract says a batch is one
/// native call at a time).</para>
///
/// <para><b><see cref="IsInstalled"/> is a <c>Func</c> because the store's answer is NOT cached.</b>
/// <c>OfflineModelStore.IsInstalled</c> stats every manifest file on every call, and the builders run
/// in <c>MainWindow</c>'s constructor before first paint — asking it there is exactly the I10
/// violation E8.S3's AC 7 exists to prevent. So the code-behind hands its own in-memory
/// <c>_offlineInstalled</c> field: seeded from <c>OfflineFallbackEnabled</c> (ruling R-4 makes the
/// setting a faithful proxy — Download and Remove are its only writers) and corrected after first
/// paint by the pool-thread probe, which then rebuilds both chains through E6.S3's seam.</para>
/// </summary>
/// <param name="Engine">The one offline provider instance the app owns.</param>
/// <param name="IsInstalled">"Are the model files really there?", answered without touching a
/// disk.</param>
internal sealed record OfflineTier(ITranslator Engine, Func<bool> IsInstalled);

/// <summary>
/// <c>architecture-cible.md</c> §8.1 — the two chains, built here and nowhere else.
///
/// <para><b>Why the composition is in <c>Services/</c> and not in the code-behind</b> (ruling E3-c,
/// and it is not a style preference). A chain needs a <see cref="ProviderGate"/> per tier, and
/// <c>ProviderStateStoreTests.No_startup_path_mentions_ProviderGates</c> (TP-START-02) asserts —
/// as an exact-equality assert on a one-element array — that <c>ProviderGates.Flush();</c> in
/// <c>MainWindow.xaml.cs</c> is the ONLY reference to the registry outside this folder in the whole
/// app. So the tier list is assembled here, <see cref="ChainTranslator.Of"/> resolves the gates,
/// and the constructor names this class instead. The next reference to the registry from outside
/// <c>Services/</c> has to be a decision rather than a convenience.</para>
///
/// <para><b>I8 — DeepL is unreachable from the read path by construction.</b>
/// <see cref="BuildRead"/> does not mention <see cref="DeepLTranslator"/> at all, so no setting,
/// present or future, can put it there. That is the single line of this file with a money
/// consequence: DeepL's free plan is a one-time <b>1 M characters in total</b>
/// (<c>benchmark-fournisseurs.md</c> §5.3), and a LIVE loop translating every new chat line would
/// drain it in about five days of a heavy user. Pinned by
/// <c>ChainCompositionTests.TP_CHN_14_DeepL_is_structurally_absent_from_the_read_chain</c>.</para>
///
/// <para><b>I9 — one gate per provider, shared by both chains.</b> The two chains build their own
/// provider objects (a provider is a URL, a payload and a parser — a few bytes) and both resolve
/// the <i>same</i> gate through <see cref="ProviderGates.For"/>, which is exactly right: the
/// external condition a gate mirrors is per endpoint, not per chain. Do not try to share the
/// provider instances to "save" a gate — the gate is already shared, and the two instances differ
/// in the one thing that may not be shared, their <see cref="RequestPriority"/>.</para>
///
/// <para><b>I10 — nothing reads <c>provider-state.json</c> before first paint.</b>
/// <see cref="ProviderGates.For"/> constructs only; the first <see cref="ProviderGate.TryEnter"/>,
/// inside <see cref="HttpProviderCore"/> on the first real request, is what loads the file. That is
/// what makes it safe to call this from the constructor.</para>
///
/// <para><b>I2</b>: no WPF type, no dispatcher, no <c>MessageBox</c> — it reads
/// <see cref="AppSettings"/> and constructs providers, which is what lets the I8 test assert on a
/// <b>built chain</b> without a window.</para>
///
/// <para><b>It also owns the shared cache</b> (E4.S2), for the same reason it owns the tier lists:
/// so the code-behind names a chain and never a store. <see cref="FlushCache"/> is the one line
/// <c>MainWindow.OnClosing</c> adds, beside <c>ProviderGates.Flush()</c>.</para>
///
/// <para><b>And since E4.S4 the builders return the chain already wrapped in it</b> (§8.2,
/// decision F). A <see cref="CachingTranslator"/> can only wrap one inner translator, so "one shared
/// cache behind the read chain, the read-once chain and the write chain" is one shared
/// <see cref="TranslationCacheStore"/> behind three thin decorators — and the wrapping happens
/// <i>here</i>, not at the three call sites, so the code-behind names neither. That is the same
/// ruling as the tier lists (E3-c) applied to the cache, and it is what
/// <c>TranslationCachePersistenceTests.No_source_outside_Services_names_the_store…</c> asserts.
/// The one-object consequence a reader should carry away: a line translated by any of the three
/// paths is free to the other two, and a key save rebuilds a chain without touching the store.</para>
/// </summary>
internal static class TranslationChains
{
    private static readonly object CacheGate = new();
    private static TranslationCacheStore? _cache;

    /// <summary>
    /// The one persistent <see cref="TranslationCacheStore"/> of the process — §8.2's "one shared
    /// cache behind the read chain, the read-once chain and the write chain" (decision F) — and
    /// since E4.S4 every chain <see cref="BuildRead"/> and <see cref="BuildWrite"/> return is a
    /// decorator over <b>this</b> instance.
    ///
    /// <para><b>Built with the DEFAULT capacity, and that is the point:</b>
    /// <c>new TranslationCacheStore()</c> is <see cref="TranslationPolicy.CacheCapacity"/> (2000),
    /// which is §8.2's number and the one a player feels after an hour of play. Passing anything
    /// here — <c>CacheCapacityToday</c> by copy-paste, or a literal — would ship A.2 with the legacy
    /// 500-entry cache and break nothing visible, which is why
    /// <c>ChainCompositionTests.The_three_chains_are_decorators_over_one_shared_store…</c> pins the
    /// number rather than the call.</para>
    ///
    /// <para>Built on demand rather than in a static field: a persistent store is still I/O-free
    /// until its first miss (I10), but constructing one at type-load would put the decision on
    /// whatever path happened to touch this class first. The first thing that asks for it in
    /// production is the constructor's first <c>Build…</c> call, before first paint — it constructs
    /// a map and reads nothing.</para>
    ///
    /// <para><b>E8.S5 — and this accessor is now the SECOND-choice door.</b> The store's
    /// <c>offlineEnabled</c> flag (§8.2 concern #2) is a <b>load-time</b> decision — the file is read
    /// once, lazily, on the first miss — so it has to be supplied at construction, by the first
    /// caller that holds an <see cref="AppSettings"/>. That caller is
    /// <see cref="CacheFor"/>, called from the two builders, and in the app the very first
    /// <c>Build…</c> of the constructor is what really builds this store. This property is what the
    /// facades (<see cref="ClearCache"/>) and the suite reach for afterwards; it builds a store with
    /// the flag <b>false</b> only if nothing has built one yet, which is the safe direction — a
    /// <c>"p":"bergamot"</c> entry is dropped rather than served to a session nobody told about the
    /// offline engine.</para>
    /// </summary>
    internal static TranslationCacheStore Cache => CacheFor(offlineEnabled: false);

    /// <summary>The one store, built on first use with §8.2 concern #2's drop rule already decided.
    /// <paramref name="offlineEnabled"/> is <c>settings.OfflineFallbackEnabled</c> and reaches only
    /// the FIRST construction — a flag arriving later is a flag that did nothing, because the load
    /// is lazy and one-shot, and the code does not pretend otherwise (T4).</summary>
    private static TranslationCacheStore CacheFor(bool offlineEnabled)
    {
        lock (CacheGate)
            return _cache ??= new TranslationCacheStore(persistent: true, offlineEnabled: offlineEnabled);
    }

    /// <summary>
    /// Write the cache if a store has anything pending — <c>MainWindow.OnClosing</c>'s one line, so
    /// the last five seconds of a session are not lost to the debounce window (AC 2). It is a
    /// facade on purpose: the code-behind names a chain, not a store, exactly as it names this
    /// class instead of <c>ProviderGates</c> for the tier lists.
    ///
    /// <para><c>?.</c> and not <see cref="Cache"/>: a close that never built a chain — the suite,
    /// and any future headless path — must not be the thing that builds a cache.</para>
    /// </summary>
    internal static void FlushCache() => Volatile.Read(ref _cache)?.SaveNow();

    /// <summary>
    /// Drops the process-wide store so the next <see cref="Cache"/> builds a fresh one. For the
    /// suite only, and it is not optional there: a store pins its path on first use, so a case that
    /// let this instance resolve a <c>TempCache</c> file would leave every later case — E4.S4's
    /// included — writing into a directory that no longer exists, with this session's entries still
    /// in the map. Cancels the pending save first, which is IS-4's rule and
    /// <c>ProviderGates.ResetForTests</c>'s order.
    ///
    /// <para>It deliberately does <b>not</b> touch <c>TranslationCacheStore.PathOverride</c>: that
    /// belongs to <c>TempCache</c> / <c>TestCacheRedirect</c>, and nulling a path override from a
    /// reset is precisely how this repo once pointed a test at a developer's own file.</para>
    /// </summary>
    internal static void ResetCacheForTests()
    {
        lock (CacheGate)
        {
            _cache?.CancelPendingSave();
            _cache = null;
        }
    }

    /// <summary>
    /// The READ chain — OCR read-once and the LIVE feed (<c>MainWindow.Live.cs</c>,
    /// <c>MainWindow.Ocr.cs</c>). Free tiers only, in §8.1's order.
    ///
    /// <para><b>Priority is per instance because <see cref="ITranslator"/> may not grow a parameter
    /// (I1).</b> The LIVE loop asks for <see cref="RequestPriority.Background"/> — §5.4's reserve:
    /// it may draw the token bucket down but not take the last token, and it stands aside for the
    /// half-open probe, so a screen loop can never spend the allowance a person waiting on a
    /// keystroke needs. Read-once is a user click and asks for
    /// <see cref="RequestPriority.Interactive"/> (ruling OQ-a); it gets its own chain instance over
    /// the same gates, which costs a few bytes and keeps I1 intact.</para>
    ///
    /// <para>E2.S3 built that reserve and E2.S5 left it inert on purpose ("<c>Background</c> did not
    /// reach the read chain"). This parameter is where it stops being inert.</para>
    ///
    /// <para><b>Returns the chain wrapped in the shared cache</b> (E4.S4): the caller gets an
    /// <see cref="ITranslator"/> and never names a decorator or a store. The two read instances and
    /// the write instance are three <see cref="CachingTranslator"/>s over one
    /// <see cref="Cache"/> — thin by design, since the key format and the <c>(</c> rule (I4) are the
    /// decorator's and storage is the store's (§3.1).</para>
    /// </summary>
    internal static ITranslator BuildRead(AppSettings settings,
        RequestPriority priority = RequestPriority.Background, OfflineTier? offline = null)
        => BuildRead(settings, priority, out _, offline);

    /// <summary>
    /// The same chain, with the <see cref="ChainTranslator"/> itself handed back — what E5.S1's LIVE
    /// loop keeps so it can ask <see cref="ChainTranslator.PauseNow"/> before it captures anything,
    /// and what E7.S3 will read <see cref="ChainTranslator.LastOutcome"/> from.
    ///
    /// <para><b>An <c>out</c> and not a changed return type</b>, which the story sketched before
    /// E4.S4 landed: the decorator is what the caller must translate through (§8.2's shared cache),
    /// and it is built HERE precisely so the code-behind never names one
    /// (<c>ChainCompositionTests</c> scans every file outside <c>Services/</c> for
    /// <c>new CachingTranslator</c>). One call site therefore gets both halves of one object graph,
    /// rather than two calls building two chains over the same gates.</para>
    ///
    /// <para>Which instance answers <c>PauseNow()</c> does not actually matter — every read chain
    /// resolves the SAME process-global gates (I9), so the Background instance's answer is also the
    /// Interactive one's. It matters that there is exactly one field for it, which is why the
    /// read-once path deliberately does not get a second (E5.S4 reuses this one).</para>
    /// </summary>
    internal static ITranslator BuildRead(AppSettings settings, RequestPriority priority,
        out ChainTranslator chain, OfflineTier? offline = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tiers = new List<(string Id, ITranslator Translator)>();

        // Azure — E6.S4, and the ONLY keyed tier this method may ever construct. Three conditions,
        // all three load-bearing, and the tier is FIRST: a key the user has deliberately opted into
        // is the engine they are paying attention to (§8.1's read path).
        //
        //  · UseKeyForReading — the opt-in itself, default FALSE and never seeded from "a key
        //    exists". AC 1 is asserted on the BUILT CHAIN and not by a runtime guard, deliberately:
        //    a guard is a line someone can move, a builder that never constructs the provider
        //    cannot be talked into it by a setting or a refactor. Same shape as I8 gives DeepL.
        //  · The REGION, with the key — a key without one is a guaranteed 401 (§12), and the
        //    provider raises that as a failed TRANSLATION (AuthFailed without NotSent, ruling
        //    E3-b), so a guaranteed-401 tier placed first in the LIVE chain would open its own gate
        //    on the first tick of every session and outrank every skipped tier after it. Same
        //    predicate as the write chain's, so the two cannot drift.
        //  · priority: — the read chain is built TWICE, Background for the LIVE loop and
        //    Interactive for read-once (ruling OQ-a). Dropping it would give a screen loop
        //    Interactive, which lets it take the last token of the ceiling's bucket and stand in
        //    front of the half-open probe — §5.4's reserve exists precisely to stop that, and
        //    nothing else in the app would notice.
        //
        // Why off by default is arithmetic, not caution (R-15): F0 is 2 M characters a month and a
        // heavy LIVE user reads ≈48 k an hour of busy chat, unattended, for as long as the app is
        // open. An opt-in that arrives pre-ticked is not an opt-in (ruling OQ-12).
        if (AzureReadsTheScreen(settings))
            tiers.Add((ProviderIds.Azure, new AzureTranslator(
                (settings.AzureApiKey ?? "").Trim(), (settings.AzureRegion ?? "").Trim(),
                priority: priority)));

        tiers.AddRange(new (string Id, ITranslator Translator)[]
        {
            // The default first free tier since the owner's decision 2 (the endpoint switch):
            // translate_a/t answers with the shape this app wants and is the one being hardened.
            (ProviderIds.GoogleDict, new GoogleDictTranslator(priority: priority)),
            // [Edge — E3.S5, NOT SHIPPED IN A.1 (ruling E3-d): U2 is owner-blocked, so there is no
            //  EdgeTranslator to put here. Vendor independence (R1) is raised in the release note,
            //  and this line is the one that changes when the owner designates a capture.]
            // (ProviderIds.Edge, new EdgeTranslator(priority: priority)),   // [UNKNOWN until U2]
            (ProviderIds.GoogleGtx, new GoogleGtxTranslator(priority: priority)),
        });

        // Bergamot — E8.S5, and it goes LAST. Not before GoogleGtx "because 6.5 ms a line is faster
        // than any network call": that is exactly the argument that would put a COMET-0.8497 engine
        // in front of a COMET-0.8785 one. It is the rung for when there is nothing else, and §7.6
        // amendment A-1 says so.
        //
        // Two conditions, both load-bearing (AC 2), and both asked by ONE predicate that the write
        // builder calls too — the shape IsSendableAzureCredential and AzureReadsTheScreen already
        // have, so the two builders cannot drift. A tier built over a model the user deleted is a
        // tier that fails on every line of every frame.
        //
        // The instance comes from the caller and is never constructed here: there is one offline
        // provider in the process (E8.S4), and it is the code-behind's.
        if (OfflineTierIsAvailable(settings, offline))
            tiers.Add((ProviderIds.Bergamot, offline!.Engine));

        // DeepL is not written in this method at all. That is what "structural, not configured"
        // means (I8) — read the class comment before changing it.
        //
        // The shared store can nevertheless SERVE this chain a value DeepL produced for the
        // Translator tab, and that is accepted (ruling E4-a): §8.2's key is provider-agnostic on
        // purpose — keying by provider would multiply the cache by the tier count and defeat the one
        // property the epic exists for. I8 is about REQUESTS, and none is made: a cached string
        // costs no quota, and this method still cannot construct a DeepLTranslator. The producing
        // tier is recorded in the entry's "p" for the log and the Bergamot drop rule, and it is
        // deliberately not part of the key.
        // The delegate is E8.S5's T3, option (i): the store is handed a key and a value and does not
        // know which tier answered; this decorator does not know either — the CHAIN does. One
        // nullable Func, supplied by the builder that already holds the chain, rather than a fourth
        // decorator on a stack that is already three deep. It is not a Bergamot special case: every
        // tier's id now lands in the entry's "p", which is what §8.2 wanted from day one and what
        // makes the drop rule below work at all.
        var built = ChainTranslator.Of(tiers.ToArray());
        chain = built;
        return new CachingTranslator(built, CacheFor(settings.OfflineFallbackEnabled),
                                     () => built.LastOutcome?.ProviderId);
    }

    /// <summary>
    /// The WRITE chain — the Translator tab and the overlay's quick reply
    /// (<c>MainWindow.Translate.cs</c>). The user's own key goes first when they have one, then the
    /// same free tiers in the same order.
    ///
    /// <para>Every tier is <see cref="RequestPriority.Interactive"/>, which is the default — so it
    /// is the READ chain that is the exception, and a future reader can see which of the two was
    /// deliberate.</para>
    ///
    /// <para><b>Wrapped in the shared cache, exactly like the read chain</b> (E4.S4) — and this is
    /// the one with a behaviour change attached: <c>DeepLSaveKey_Click</c> re-runs
    /// <c>BuildWriteChain()</c> so a corrected key takes effect on the next translation, and what it
    /// rebuilds is the CHAIN. <see cref="Cache"/> is a property of this class, not of the chain, so
    /// the session's accumulated translations survive the save (amplifier A5, closed here).</para>
    /// </summary>
    internal static ITranslator BuildWrite(AppSettings settings, OfflineTier? offline = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tiers = new List<(string Id, ITranslator Translator)>();

        var key = (settings.DeepLApiKey ?? "").Trim();
        if (key.Length > 0)
            tiers.Add((ProviderIds.DeepL, new DeepLTranslator(key)));

        // Azure goes between the two: after DeepL, before the free engines (§8.1's write path, and
        // ux flow (c).6 says it out loud — "the order is DeepL, then Azure, then the free
        // engines"). BOTH halves or no tier at all, which is E6.S2's review finding and not
        // tidiness: the provider's own guard throws AuthFailed WITHOUT NotSent (ruling E3-b gives
        // that flag one writer), so a tier built from half a credential is counted by the chain as
        // a tier that tried and failed, and its sentence outranks every skipped tier — the player
        // would be told their key was refused by a request that was never worth sending.
        var azureKey = (settings.AzureApiKey ?? "").Trim();
        var azureRegion = (settings.AzureRegion ?? "").Trim();
        if (IsSendableAzureCredential(azureKey, azureRegion))
            tiers.Add((ProviderIds.Azure, new AzureTranslator(azureKey, azureRegion)));

        tiers.Add((ProviderIds.GoogleDict, new GoogleDictTranslator()));
        // [Edge — E3.S5 / ruling E3-d: absent from A.1, see BuildRead.]
        // (ProviderIds.Edge, new EdgeTranslator()),                        // [UNKNOWN until U2]
        tiers.Add((ProviderIds.GoogleGtx, new GoogleGtxTranslator()));
        // Bergamot — E8.S5, LAST, behind the same one predicate BuildRead asks. It is the SAME
        // instance the read chain wraps (I9's reasoning one level up: the thing that may not be
        // duplicated here is not the gate but the 121 MiB the engine allocates).
        if (OfflineTierIsAvailable(settings, offline))
            tiers.Add((ProviderIds.Bergamot, offline!.Engine));

        var chain = ChainTranslator.Of(tiers.ToArray());
        return new CachingTranslator(chain, CacheFor(settings.OfflineFallbackEnabled),
                                     () => chain.LastOutcome?.ProviderId);
    }

    /// <summary>
    /// <b>Is there an offline rung to append?</b> AC 2's two conditions, written <b>once</b> so the
    /// two builders (and the About tab's two id lists) cannot come to disagree — the same rule, and
    /// the same reason, as <see cref="IsSendableAzureCredential"/> and
    /// <see cref="AzureReadsTheScreen"/>.
    ///
    /// <list type="number">
    /// <item><b>The provider exists.</b> A caller with no offline engine — every headless case in
    ///       the suite, and any future path that has none — gets no tier at all, which is what makes
    ///       "the chain never constructs a <c>BergamotTranslator</c>" structural.</item>
    /// <item><b><c>OfflineFallbackEnabled</c>.</b> Default false, written only by Download and
    ///       Remove (ruling R-4). False means the tier is not even CONSTRUCTED — asserted on the
    ///       built chain, never on a runtime guard, for the reason E6.S4 gave for Azure: a guard is
    ///       a line someone can move, a builder that never builds the tier cannot be talked into
    ///       it.</item>
    /// <item><b>The model is really on disk.</b> E8.S3's review recorded exactly this hand-off —
    ///       "setting-true-but-files-gone → E8.S5 gates the tier on the store, not the setting".
    ///       Asked through <see cref="OfflineTier.IsInstalled"/> so no disk is touched here
    ///       (I10).</item>
    /// </list>
    /// </summary>
    private static bool OfflineTierIsAvailable(AppSettings settings, OfflineTier? offline)
        => offline is not null && settings.OfflineFallbackEnabled && offline.IsInstalled();

    // =============================================================================================
    //  What a key save does — E6.S3
    // =============================================================================================

    /// <summary>
    /// The half of a key save that is not a rebuild: the provider's ACCOUNT-scoped block is lifted,
    /// so a corrected key takes effect on the very next translation instead of waiting out an
    /// <c>AuthFailed</c> window the user cannot see. Ruling <b>E2-a</b> is why it has to exist —
    /// "no state may lock the user out without a way back" — and ruling <b>E2-i</b> is its exact
    /// bound: <c>AuthFailed</c> and <c>QuotaExhausted</c> are the user's key, a <c>RateLimited</c>
    /// or <c>Blocked</c> window is the provider counting requests from this IP and no key can move
    /// it. <see cref="ProviderGate.ClearAuthBlock"/> enforces that; this method only routes to it.
    ///
    /// <para><b>Why it is a line of this class rather than of the code-behind.</b>
    /// <c>ProviderStateStoreTests.No_startup_path_mentions_ProviderGates</c> (TP-START-02) asserts,
    /// as an exact-equality assert on a ONE-element array, that <c>ProviderGates.Flush();</c> is the
    /// only reference to the registry outside <c>Services/</c> in the whole app. The key save is a
    /// second thing the registry must hear about — and the answer is the same as it was for the
    /// tier lists and for the cache (ruling E3-c): the code-behind names this class, this class
    /// names the registry, and the allow-list stays one line long. The story that added this call
    /// was expected to widen that list instead; routing it here is strictly the smaller change,
    /// because the next reference from outside still has to be a decision.</para>
    /// </summary>
    /// <remarks>
    /// <b>The file is read first, and that ordering is the whole of the method.</b> A
    /// <c>QuotaExhausted</c> window IS persisted — only the <c>AuthFailed</c> sentinel is dropped
    /// (E2-a) — and the registry reads <c>provider-state.json</c> on the first <c>TryEnter</c>,
    /// i.e. on the first translation of the session (I10). A user who opens the app to fix a
    /// credential and presses Save before translating anything therefore meets a gate nothing has
    /// seeded yet: <c>ClearAuthBlock</c> returns at its own <c>_keyBlockedUntil is null</c> guard,
    /// clears nothing and records nothing — so the load that follows seeds the OLD key's window
    /// onto the NEW key, and the way out is the button that was just pressed. Loading here is
    /// still after first paint (this runs from a click, never from the ctor), so I10 holds.
    /// </remarks>
    internal static void OnKeySaved(string providerId)
    {
        ProviderGates.EnsureLoaded();
        ProviderGates.ClearAuthBlock(providerId);
    }

    // =============================================================================================
    //  What the status chip reads — E7.S3, rulings E6-a / R-2 / R-3 / OQ-c
    // =============================================================================================

    /// <summary>
    /// <b>Ruling E6-a, and the whole of it.</b> Read <c>provider-state.json</c> now, so a pause the
    /// user is already inside reaches the chip about a second after the window rather than after
    /// their first translation. <c>TP-START-04</c>'s "at first paint" becomes "within about a
    /// second of it", which is the closest an honest implementation gets.
    ///
    /// <para><b>Called from <c>OnWindowLoaded</c>, on a pool thread, AFTER first paint</b> — never
    /// from the constructor, never from <c>ApplySettings</c>. That ordering is <b>I10</b> and it is
    /// not negotiable: the file is what P1 protects the first paint from. This method does not
    /// enforce it (a method cannot), the caller does, and <c>StartupSettingsTests</c> pins the
    /// caller.</para>
    ///
    /// <para><b>Why it is a line of this class</b> and not of the code-behind: exactly the reason
    /// <see cref="OnKeySaved"/> is (ruling E3-c). <c>ProviderStateStoreTests.No_startup_path_mentions_ProviderGates</c>
    /// asserts, as an exact-equality assert on a ONE-element array, that <c>ProviderGates.Flush();</c>
    /// is the only reference to the registry outside <c>Services/</c> — and E7.S2's own review
    /// recorded that <c>ProviderGates.EnsureLoaded</c>'s comment rejects "warming it from
    /// <c>OnWindowLoaded</c>" precisely because that would put the registry on a startup path.
    /// E6-a routes AROUND that comment rather than overruling it: the code-behind names this class,
    /// this class names the registry, and the allow-list stays one line long.</para>
    ///
    /// <para><b>Exception-safe by contract.</b> <c>ProviderStateStore.Load</c> never throws and
    /// answers empty on any failure, so there is nothing here to catch — but a status warm-up may
    /// not be able to fail a launch, so the guard is written rather than assumed. A failed load
    /// simply leaves every gate unseeded, which is what the app did before E2.S4.</para>
    /// </summary>
    /// <param name="readChain">The chain whose tiers are warmed — the one the code-behind already
    /// holds. It asks the CHAIN and not the registry for the same reason every other line of this
    /// section does; <see cref="ChainTranslator.EnsureStateLoaded"/> then walks its own gates
    /// through the seam E5.S1 added for <see cref="ChainTranslator.PauseNow"/>. The file is read
    /// whole, so the tiers this chain does not have (the user's keys) are seeded with it and the
    /// tooltip's other rows are honest too.</param>
    internal static void EnsureGateStateLoaded(ChainTranslator readChain)
    {
        ArgumentNullException.ThrowIfNull(readChain);
        try { readChain.EnsureStateLoaded(); }
        catch (Exception ex) { Logging.Warn("gate state warm-up failed: " + ex.Message); }
    }

    /// <summary>
    /// <b>The chip's whole picture, at one instant</b> (<c>ux-mode-degrade.md</c> §2.1–§2.3). The
    /// impure half of <see cref="Services.EngineStatus"/>: it resolves who the read tiers are, what
    /// their gates say right now, which keyed tiers the user actually has, and who answered the last
    /// call — then hands all of it to the pure builder, which the eight-state unit test drives with
    /// literals instead.
    ///
    /// <para><b>Side-effect free</b> (ruling R-2): <see cref="ProviderGates.All"/> and
    /// <see cref="ChainTranslator.Now"/> only, never <c>TryEnter</c> — a status read that took the
    /// half-open probe would leave a gate half-open for a whole window with nobody to report the
    /// result. It does not load the file either: that is <see cref="EnsureGateStateLoaded"/>'s, once,
    /// after first paint, and until it has run the chip says "checking…" rather than claiming a
    /// health it has not verified.</para>
    ///
    /// <para><b>Here and not in the code-behind</b> for the third time in this file (ruling E3-c):
    /// <c>ProviderGates.All()</c> from <c>MainWindow</c> fails TP-START-02 by construction.</para>
    ///
    /// <para><b>One <c>LastOutcome</c> read</b>, into a local, handed on as a value — it is
    /// <c>Volatile</c>-read because a second call may be in flight on a pool thread, and reading it
    /// twice in one repaint can mix two calls' accounts.</para>
    /// </summary>
    /// <param name="settings">Which keyed tiers the user has. The SAME predicates the two builders
    /// apply, so a tooltip cannot claim a key the chain would refuse to build a tier from.</param>
    /// <param name="readChain">The chain the code-behind already holds (<c>_readChain</c>) — its
    /// tier order, its gates' clock and its last outcome.</param>
    /// <param name="stateKnown">Whether <see cref="EnsureGateStateLoaded"/> has run (ruling E6-a).</param>
    internal static EngineStatus EngineStatus(AppSettings settings, ChainTranslator readChain,
        bool stateKnown)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(readChain);

        // Read tiers first, then the keyed tiers the WRITE chain would really build — the union is
        // "engines this user has". Azure can be in both; a set, not a list, so it is asked once.
        var configured = new HashSet<string>(readChain.TierIds, StringComparer.Ordinal);
        if ((settings.DeepLApiKey ?? "").Trim().Length > 0) configured.Add(ProviderIds.DeepL);
        if (IsSendableAzureCredential((settings.AzureApiKey ?? "").Trim(),
                                      (settings.AzureRegion ?? "").Trim())) configured.Add(ProviderIds.Azure);

        return Services.EngineStatus.Of(readChain.TierIds, ProviderGates.All(), configured,
            readChain.LastOutcome, stateKnown, readChain.Now());
    }

    // =============================================================================================
    //  What the About tab renders — E7.S7
    // =============================================================================================

    /// <summary>
    /// <b>§4.2's "Chain" line for the write path</b>, as ids: what <see cref="BuildWrite"/> would
    /// really construct for these settings, in chain order.
    ///
    /// <para><b>Ids and not a chain</b>, deliberately. The About tab is refreshed from
    /// <c>ApplySettings</c>, from every key save and from E7.S2's 1 Hz tick while a provider is
    /// paused; building a real chain there would construct four providers a second and throw them
    /// away. It is also the reason this lives here and not in the code-behind: the composition is
    /// <c>Services/</c>' (ruling E3-c) and <c>MainWindow</c> may not name <see cref="ProviderGates"/>
    /// (TP-START-02).</para>
    ///
    /// <para><b>It is a second expression of §8.1's order, and that is a real risk</b> — so it is
    /// pinned rather than trusted: <c>EngineStatusTests</c> asserts these ids equal the ids of the
    /// chain the builder actually returns, over every settings shape
    /// <c>ChainCompositionTests.EveryPermutation()</c> knows and at both read priorities. A tier
    /// added to a builder without a line here fails that case.</para>
    /// </summary>
    internal static IReadOnlyList<string> WriteTierIds(AppSettings settings,
                                                       OfflineTier? offline = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ids = new List<string>(5);
        if ((settings.DeepLApiKey ?? "").Trim().Length > 0) ids.Add(ProviderIds.DeepL);
        if (AzureWritesWhatYouType(settings)) ids.Add(ProviderIds.Azure);
        ids.Add(ProviderIds.GoogleDict);
        // [Edge — ruling E3-d: no EdgeTranslator exists, so the tab may not name one.]
        ids.Add(ProviderIds.GoogleGtx);
        // Bergamot — E8.S5. The rung is named here only when the builder really appends it, which
        // is the whole point of EngineStatusTests' "these ids equal the ids the builders construct":
        // the tab is a SECOND expression of §8.1's order, so it learns about a new tier at the same
        // moment the chain does or the two drift. When it is absent the tab is not silent about it —
        // the offline block a few rows down says "not installed" and the chip's tooltip carries the
        // row (AC 6).
        if (OfflineTierIsAvailable(settings, offline)) ids.Add(ProviderIds.Bergamot);
        return ids;
    }

    /// <summary>The same for the READ path — and the reason §4.2's single Chain row became two
    /// (I8): <see cref="BuildRead"/> cannot construct a DeepL tier at all, so the two lines differ
    /// structurally and a player who has just been told their key is for what they WRITE can see
    /// it.</summary>
    internal static IReadOnlyList<string> ReadTierIds(AppSettings settings,
                                                      OfflineTier? offline = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ids = new List<string>(4);
        if (AzureReadsTheScreen(settings)) ids.Add(ProviderIds.Azure);
        ids.Add(ProviderIds.GoogleDict);
        ids.Add(ProviderIds.GoogleGtx);
        if (OfflineTierIsAvailable(settings, offline)) ids.Add(ProviderIds.Bergamot);
        return ids;
    }

    /// <summary>
    /// Does <see cref="BuildWrite"/> put the user's Azure key on the write path for these settings?
    /// The About tab's status line asks THIS and no longer <c>hasKey &amp;&amp; region.Length > 0</c>
    /// — E6.S4's review recorded that expression as not the sendability predicate, and E7.S7 owns
    /// the block that carried it. A region carrying a control character (pasted with a line break,
    /// persisted by the combo handler, which trims but does not filter) made the line say
    /// "Azure key set — used for what you write" over a credential the builder adds <b>no tier</b>
    /// for. Same rule, one expression, and it is the same one
    /// <see cref="AzureCredentialProblem"/> and <see cref="AzureReadsTheScreen"/> already share.
    /// </summary>
    internal static bool AzureWritesWhatYouType(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsSendableAzureCredential((settings.AzureApiKey ?? "").Trim(),
                                         (settings.AzureRegion ?? "").Trim());
    }

    /// <summary>
    /// <b>Amendment A10's button, behind the facade the code-behind already uses</b> — the store is
    /// emptied in memory and its file is deleted, in that order, and the number of entries removed
    /// comes back for the sentence that reports it (§4.2). No confirmation dialog: clearing the
    /// cache destroys nothing the app cannot rebuild, and the cost is a few extra requests.
    ///
    /// <para>Here and not in <c>MainWindow.Translate.cs</c> for the reason
    /// <see cref="FlushCache"/> is: <c>TranslationCachePersistenceTests</c> asserts, over every
    /// production source outside <c>Services/</c>, that nothing names
    /// <see cref="TranslationCacheStore"/> at all. The code-behind names a chain; this class names
    /// the store.</para>
    ///
    /// <para><see cref="Cache"/> and not <c>?.</c>: a user pressing the button before any chain has
    /// translated anything still means "remove what is on disk", and the store's own
    /// <c>Clear</c> loads the file first so the count it reports is the truth.</para>
    /// </summary>
    internal static int ClearCache() => Cache.Clear();

    /// <summary>
    /// Is this pair one <see cref="BuildWrite"/> would actually build a tier from? One predicate,
    /// so the builder and the About tab's Save button cannot drift: the UI must refuse exactly what
    /// the chain would refuse, or a player sees "saved" over a credential that silently adds no
    /// engine. Both halves present, and neither carrying anything an HTTP header may not.
    /// </summary>
    private static bool IsSendableAzureCredential(string key, string region) =>
        key.Length > 0 && region.Length > 0
        && !AzureTranslator.HasControlChar(key) && !AzureTranslator.HasControlChar(region);

    /// <summary>
    /// Does <see cref="BuildRead"/> put an Azure tier on the screen reader for these settings?
    /// E6.S4's three conditions, written <b>once</b> — the builder above asks this and so does the
    /// About tab's status line, for the same reason <see cref="AzureCredentialProblem"/> shares
    /// <see cref="IsSendableAzureCredential"/> with the write chain: a line that claims the screen
    /// reader is using the user's key while the chain never built the tier is the lie this whole
    /// increment exists to prevent (§1's fourth principle — honest status).
    ///
    /// <para>It is a QUESTION about settings, not about a built chain, and that is the honest shape:
    /// the chain's tier list is private and stays private (<c>ChainCompositionTests</c> reaches it
    /// by reflection precisely so nothing in the app has to). The guarantee that the two agree is
    /// that this is the only expression of the rule, and AC 1 is still asserted on the built
    /// chain.</para>
    /// </summary>
    internal static bool AzureReadsTheScreen(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UseKeyForReading
            && IsSendableAzureCredential((settings.AzureApiKey ?? "").Trim(),
                                         (settings.AzureRegion ?? "").Trim());
    }

    /// <summary>
    /// What is wrong with an Azure credential the user is trying to save, as the sentence to show —
    /// or <c>null</c> when there is nothing wrong with it (E6.S3 AC 5). The copy is
    /// <see cref="UserMessages"/>' (ruling GAP-4); the RULE is this class's, because it is the same
    /// rule <see cref="IsSendableAzureCredential"/> applies to the chain.
    ///
    /// <para><b>An empty KEY is not a problem — it is the gesture that removes Azure</b> (ruling
    /// <b>E6-e</b>), and it takes the region with it. E6.S3 refused an empty key over a leftover
    /// region and told the user to clear the region box as well; its own review recorded that as a
    /// dead end — an answer to a question they had not asked — and referred the AC change upward.
    /// One box cleared, one engine gone. The remaining refusal is the half-pair with no other
    /// reading: a key with NO region, which the provider would take, spend a request on and earn an
    /// <c>AuthFailed</c> gate for, with nothing on screen explaining why.</para>
    ///
    /// <para>Both arguments are expected already trimmed — the caller has to trim to decide what to
    /// persist anyway, and a validator that quietly trims a different string from the one that gets
    /// saved is the kind of near-miss this whole story exists to avoid.</para>
    /// </summary>
    internal static string? AzureCredentialProblem(string key, string region)
    {
        // The clearing gesture is answered FIRST, ahead of the unsendable check (review, E6.S4).
        // Order matters here and it is a way out, not a preference: a region box carrying a pasted
        // line break over an emptied key box is a credential nothing will ever send — and refusing
        // it would leave the user unable to REMOVE Azure until they also tidied a field they are
        // about to discard. E6-e says an empty key is the whole gesture; the caller blanks the
        // region with it, so nothing unsendable is persisted either way (§1's second principle, and
        // R-01's "no refusal without an exit" in miniature).
        if (key.Length == 0) return null;                        // clearing the pair (E6-e)
        if (AzureTranslator.HasControlChar(key) || AzureTranslator.HasControlChar(region))
            return UserMessages.AzureCredentialUnsendable();
        if (region.Length == 0) return UserMessages.AzureNeedsARegion();
        return null;
    }
}

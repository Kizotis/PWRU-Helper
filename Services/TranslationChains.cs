namespace PWRUHelper.Services;

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
/// </summary>
internal static class TranslationChains
{
    private static readonly object CacheGate = new();
    private static TranslationCacheStore? _cache;

    /// <summary>
    /// The one persistent <see cref="TranslationCacheStore"/> of the process — §8.2's "one shared
    /// cache behind the read chain, the read-once chain and the write chain" (decision F).
    ///
    /// <para><b>Nothing passes it to a decorator yet, and that is deliberate.</b> E4.S4 is the
    /// one-line flip: the three <c>new CachingTranslator(…)</c> call sites gain this argument and
    /// the three private 500-entry stores go away. Until then the three private stores stay
    /// non-persistent (<c>TranslationCacheStore</c>'s <c>persistent</c> parameter defaults to
    /// false), because three of them pointed at one file would spend a session overwriting each
    /// other — so A.2 behaves exactly as it did, and <see cref="FlushCache"/> below has nothing to
    /// write until the flip.</para>
    ///
    /// <para>Built on demand rather than in a static field: a persistent store is still I/O-free
    /// until its first miss (I10), but constructing one at type-load would put the decision on
    /// whatever path happened to touch this class first.</para>
    /// </summary>
    internal static TranslationCacheStore Cache
    {
        get { lock (CacheGate) return _cache ??= new TranslationCacheStore(persistent: true); }
    }

    /// <summary>
    /// Write the cache if a store has anything pending — <c>MainWindow.OnClosing</c>'s one line, so
    /// the last five seconds of a session are not lost to the debounce window (AC 2). It is a
    /// facade on purpose: the code-behind names a chain, not a store, exactly as it names this
    /// class instead of <c>ProviderGates</c> for the tier lists.
    ///
    /// <para><c>?.</c> and not <see cref="Cache"/>: closing an app that never translated must not
    /// be the thing that builds a cache.</para>
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
    /// </summary>
    internal static ITranslator BuildRead(AppSettings settings,
        RequestPriority priority = RequestPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // [Azure — E6.S4: only if AzureApiKey is set AND UseKeyForReading is on. The setting names
        //  are architecture §12's (ruling R-7) so E6 does not have to rename them; UseKeyForReading
        //  defaults to FALSE — a metered key must be opted into for an unmetered loop.]
        var tiers = new List<(string Id, ITranslator Translator)>
        {
            // The default first free tier since the owner's decision 2 (the endpoint switch):
            // translate_a/t answers with the shape this app wants and is the one being hardened.
            (ProviderIds.GoogleDict, new GoogleDictTranslator(priority: priority)),
            // [Edge — E3.S5, NOT SHIPPED IN A.1 (ruling E3-d): U2 is owner-blocked, so there is no
            //  EdgeTranslator to put here. Vendor independence (R1) is raised in the release note,
            //  and this line is the one that changes when the owner designates a capture.]
            // (ProviderIds.Edge, new EdgeTranslator(priority: priority)),   // [UNKNOWN until U2]
            (ProviderIds.GoogleGtx, new GoogleGtxTranslator(priority: priority)),
            // [Bergamot — E8.S3: only if OfflineFallbackEnabled and the model is present.]
        };

        // DeepL is not written in this method at all. That is what "structural, not configured"
        // means (I8) — read the class comment before changing it.
        return ChainTranslator.Of(tiers.ToArray());
    }

    /// <summary>
    /// The WRITE chain — the Translator tab and the overlay's quick reply
    /// (<c>MainWindow.Translate.cs</c>). The user's own key goes first when they have one, then the
    /// same free tiers in the same order.
    ///
    /// <para>Every tier is <see cref="RequestPriority.Interactive"/>, which is the default — so it
    /// is the READ chain that is the exception, and a future reader can see which of the two was
    /// deliberate.</para>
    /// </summary>
    internal static ITranslator BuildWrite(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var tiers = new List<(string Id, ITranslator Translator)>();

        var key = (settings.DeepLApiKey ?? "").Trim();
        if (key.Length > 0)
            tiers.Add((ProviderIds.DeepL, new DeepLTranslator(key)));
        // [Azure — E6.S3: only if AzureApiKey is set.]

        tiers.Add((ProviderIds.GoogleDict, new GoogleDictTranslator()));
        // [Edge — E3.S5 / ruling E3-d: absent from A.1, see BuildRead.]
        // (ProviderIds.Edge, new EdgeTranslator()),                        // [UNKNOWN until U2]
        tiers.Add((ProviderIds.GoogleGtx, new GoogleGtxTranslator()));
        // [Bergamot — E8.S3: only if OfflineFallbackEnabled and the model is present.]

        return ChainTranslator.Of(tiers.ToArray());
    }
}

namespace PWRUHelper.Services;

/// <summary>
/// An <see cref="ITranslator"/> that remembers recent results so identical text is never
/// re-translated. On the RU server players spam the same LFM/greeting lines constantly and the
/// live OCR loop re-reads the same messages frame after frame, so this both makes the UI feel
/// instant and cuts calls to the translation backend (far fewer 429 rate-limits).
///
/// It wraps ANY inner translator, so the cache benefits whatever backend is active (Google
/// today, DeepL later). The cache is a bounded LRU — at most <c>capacity</c> entries, the
/// least-recently-used evicted first — so memory stays flat over a long session. Only genuine
/// successes are cached; the inner translator's failure placeholders (which start with "(")
/// are never stored, so a transient rate-limit can't get stuck on screen forever.
///
/// <para>Since E4.S1 the LRU itself lives in <see cref="TranslationCacheStore"/> and this class is
/// the decorator over it: the key format and the "(" rule are its (§3.1), storage and eviction are
/// the store's. Built without a store it keeps a private one, exactly as before; built WITH one it
/// shares it — which is how one cache ends up behind three chains (§8.2, E4.S4).</para>
/// </summary>
public class CachingTranslator : ITranslator
{
    private readonly ITranslator _inner;
    private readonly TranslationCacheStore _store;

    public CachingTranslator(ITranslator inner, int capacity = TranslationPolicy.CacheCapacityToday)
    {
        _inner = inner;
        _store = new TranslationCacheStore(capacity);   // private, and 500 deep unless told otherwise
    }

    /// <summary>Shares an existing store instead of owning one — E4.S4's constructor, and the whole
    /// reason the LRU moved out.
    ///
    /// <para><c>internal</c> rather than public, and not by taste:
    /// <c>TranslationPolicyTests.The_call_sites_that_now_read_the_policy_still_behave_identically</c>
    /// pins the capacity default through <c>GetConstructors().Single()</c>, which throws the moment a
    /// second PUBLIC constructor exists. <c>InternalsVisibleTo PWRUHelper.Tests</c> keeps it reachable
    /// from the suite, and every call site that will pass a store is in this assembly.</para></summary>
    internal CachingTranslator(ITranslator inner, TranslationCacheStore store)
    {
        _inner = inner;
        _store = store;
    }

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        var key = Key(source, target, text);
        if (TryGet(key, out var cached)) return cached;

        // ConfigureAwait(false) on both awaits (ruling E6-d): since E4.S4 this decorator is the
        // OUTERMOST await of every translation the app makes, and both call sites are UI-thread
        // methods — without it the continuation, and the store's synchronous work behind it, resume
        // on the dispatcher. Same obligation as every file HttpProviderCoreTests' scan covers, and
        // this one is now on that floor.
        var result = await _inner.TranslateAsync(text, source, target, ct).ConfigureAwait(false);
        if (IsCacheable(text, result)) Store(key, result);
        return result;
    }

    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        // Serve the lines already known from cache; only ask the inner translator for the
        // misses, then splice results back into their original positions.
        var result = new string[lines.Count];
        var missIndexes = new List<int>();
        var missLines = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (TryGet(Key(source, target, lines[i]), out var cached)) result[i] = cached;
            else { missIndexes.Add(i); missLines.Add(lines[i]); }
        }

        if (missLines.Count > 0)
        {
            var fresh = await _inner.TranslateLinesAsync(missLines, source, target, ct)
                .ConfigureAwait(false);

            // I5, one layer above every provider (ruling E6-d). This used to splice
            // `j < fresh.Count ? fresh[j] : missLines[j]` — padding a short answer with the
            // UNTRANSLATED SOURCE LINE and handing it back as a translation. That is the exact bug
            // I5 exists for: DeepL's own padding once bypassed the fallback (the chain read the
            // padded list as a success and never tried the next tier) AND cached raw Russian source
            // as if it were English. It was unreachable here only because every provider throws
            // first — and unreachable is not absent: since E4.S4 this wraps the WHOLE chain, so the
            // day a tier answers 1:1-wrong without throwing, the padding would be the app's answer.
            // A short OR long list is a BadResponse, and nothing from it is stored.
            if (fresh.Count != missLines.Count)
                throw new TranslationException(TranslationErrorKind.BadResponse,
                    "The translator returned a different number of lines than it was asked for.");

            for (int j = 0; j < missIndexes.Count; j++)
            {
                var value = fresh[j];
                result[missIndexes[j]] = value;
                if (IsCacheable(missLines[j], value))
                    Store(Key(source, target, missLines[j]), value);
            }
        }
        return result.ToList();
    }

    // ----- cache internals -----

    private const string Sep = "|";   // language codes are [a-z]+, so a pipe can never collide

    private static string Key(string source, string target, string text)
        // Trim so leading/trailing-whitespace variants collapse to one entry (the backend trims
        // anyway); include source+target so "ru→en" and "ru→fr" of the same text stay distinct.
        => source + Sep + target + Sep + text.Trim();

    // Don't cache empty input, and never cache the inner translator's failure placeholders
    // ("(translation failed…)", "(rate-limited…)", "(skipped…)") — all of which start with "(".
    private static bool IsCacheable(string source, string value)
        => !string.IsNullOrWhiteSpace(source)
           && !string.IsNullOrEmpty(value)
           && !value.StartsWith('(');

    // The LRU, the lock and the eviction are TranslationCacheStore's; these two lines are all that
    // is left of them here, so the call sites above read the same as they always did.
    private bool TryGet(string key, out string value) => _store.TryGet(key, out value);

    private void Store(string key, string value) => _store.Store(key, value);
}

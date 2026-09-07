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

    /// <summary>E8.S5's T3, option (i): "which tier answered?", asked of the thing that knows — the
    /// chain — through one nullable delegate the builder supplies. Null for a decorator built over
    /// something that is not a chain, and then the <c>"p"</c> field is written empty exactly as it
    /// was before, which is the honest answer rather than a guess.</summary>
    private readonly Func<string?>? _lastProvider;

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
    /// <param name="lastProvider">E8.S5. Who answered the call that just returned, so the entry can
    /// carry §8.2's <c>"p"</c>. Read <b>once</b> per store and into a local: the chain's
    /// <c>LastOutcome</c> is a <c>Volatile</c> field a second call may already have overwritten from
    /// a pool thread, and reading it twice in one batch would stamp two calls' accounts onto each
    /// other's entries — a wrong <c>"p"</c> is invisible until somebody presses Remove and keeps
    /// getting offline answers.</param>
    internal CachingTranslator(ITranslator inner, TranslationCacheStore store,
                               Func<string?>? lastProvider = null)
    {
        _inner = inner;
        _store = store;
        _lastProvider = lastProvider;
    }

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        var key = Key(source, target, text);
        if (TryGet(key, out var cached)) return cached;

        // ConfigureAwait(false) on both awaits (ruling E6-d): since E4.S4 this decorator is the
        // OUTERMOST await of every translation the app makes, and both call sites are UI-thread
        // methods — without it the continuation, and the store's synchronous work behind it, resume
        // on the dispatcher. Same obligation as every file the request-path scan covers, and this
        // one is now on its floor.
        //
        // The wording is deliberate and must stay that way: that scan derives its file set from the
        // raw text of every file in Services/, so naming the core (or its test class) ANYWHERE here
        // — a comment included — would put this file on the first arm of the derivation and quietly
        // make the `decorators` arm that exists for it dead code. Review of E6.S4 found exactly that
        // and the scan now asserts the arm is load-bearing, which is why this comment does not spell
        // the name out.
        var result = await _inner.TranslateAsync(text, source, target, ct).ConfigureAwait(false);
        if (IsCacheable(text, result)) Store(key, result, AnsweringProvider());
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

            // ONE read for the whole batch (E8.S5): one call to the chain, one answering provider,
            // so every line of this frame carries the same "p". Reading it inside the loop would ask
            // the chain forty times about forty different instants and let a second call in flight
            // relabel the tail of this one's entries.
            var answered = AnsweringProvider();

            for (int j = 0; j < missIndexes.Count; j++)
            {
                var value = fresh[j];
                result[missIndexes[j]] = value;
                if (IsCacheable(missLines[j], value))
                    Store(Key(source, target, missLines[j]), value, answered);
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

    private void Store(string key, string value, string? providerId)
        => _store.Store(key, value, providerId);

    /// <summary>Who answered, or null when nobody supplied a way to ask. One invocation per store
    /// site, never one per entry — see the batch's own comment.</summary>
    private string? AnsweringProvider() => _lastProvider?.Invoke();
}

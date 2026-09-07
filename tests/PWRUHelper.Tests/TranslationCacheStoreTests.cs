using System.Threading;
using System.Threading.Tasks;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The LRU on its own, now that it is a thing of its own (E4.S1). `CachingTranslatorTests` still
/// owns the decorator's behaviour and is deliberately unedited by this story; what is here is what
/// only becomes assertable once the structure has a name — the real capacity, and the MRU discipline
/// that a "tidied" rewrite loses in silence.
///
/// <para>No collection attribute on purpose: the store is an instance with no static state, no file
/// and no network, so joining `Gates` or `WPF` would serialise cheap tests for nothing.</para>
/// </summary>
public class TranslationCacheStoreTests
{
    // ---- TP-CACHE-06: the capacity is 2000, and it is the policy's number --------------------

    [Fact]
    public void The_default_capacity_is_the_policys_2000_and_the_tail_is_what_goes()
    {
        var store = new TranslationCacheStore();

        for (int i = 0; i < 2000; i++) store.Store("k" + i, "v" + i);
        Assert.Equal(2000, store.Count);
        Assert.True(store.TryGet("k0", out _), "the first entry is still in a store that is exactly full");

        // 2001st entry. "k0" was just touched by the TryGet above, so the least-recently-used is
        // "k1" — which is the whole point of asserting on it rather than on "k0".
        store.Store("k2000", "v2000");

        Assert.Equal(2000, store.Count);
        Assert.False(store.TryGet("k1", out _), "the least-recently-used entry must be the one evicted");
        Assert.True(store.TryGet("k2000", out var last));
        Assert.Equal("v2000", last);
    }

    [Fact]
    public void The_capacity_comes_from_the_policy_and_not_from_a_second_literal()
    {
        var store = new TranslationCacheStore(TranslationPolicy.CacheCapacity);
        for (int i = 0; i <= TranslationPolicy.CacheCapacity; i++) store.Store("k" + i, "v" + i);
        Assert.Equal(TranslationPolicy.CacheCapacity, store.Count);
    }

    // ---- TP-CACHE-07 (the in-memory half): front is MRU, back is LRU -------------------------

    [Fact]
    public void A_read_promotes_its_entry_so_the_untouched_one_is_evicted()
    {
        var store = new TranslationCacheStore(capacity: 2);

        store.Store("a", "A");
        store.Store("b", "B");
        Assert.True(store.TryGet("a", out _));   // touch → a is MRU, b is now LRU

        store.Store("c", "C");                   // evicts the LRU

        Assert.True(store.TryGet("a", out var a), "the entry read last must survive");
        Assert.Equal("A", a);
        Assert.False(store.TryGet("b", out _), "the untouched entry is the one that goes");
        Assert.True(store.TryGet("c", out _));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Storing_an_existing_key_overwrites_it_and_promotes_it()
    {
        var store = new TranslationCacheStore(capacity: 2);

        store.Store("a", "A");
        store.Store("b", "B");
        store.Store("a", "A2");   // overwrite → a is MRU again, and there are still two entries
        Assert.Equal(2, store.Count);

        store.Store("c", "C");    // evicts b, not a

        Assert.True(store.TryGet("a", out var a));
        Assert.Equal("A2", a);
        Assert.False(store.TryGet("b", out _));
    }

    [Fact]
    public void A_miss_yields_an_empty_string_and_never_a_null()
    {
        var store = new TranslationCacheStore(capacity: 2);
        Assert.False(store.TryGet("nothing", out var value));
        Assert.Equal("", value);
    }

    // ---- the responsibility split (§3.1), pinned from the store's side -----------------------

    [Fact]
    public void The_store_is_a_dumb_string_map_it_does_not_know_about_keys()
    {
        // The key format — trim, "source|target|text" — is the DECORATOR's (AC 1 pins the format,
        // §3.1 puts its construction in CachingTranslator). Two keys that a human reads as "the
        // same text" are two entries here, because the store compares strings and nothing else.
        var store = new TranslationCacheStore(capacity: 8);
        store.Store("ru|en|привет", "hello");
        store.Store("ru|fr|привет", "salut");
        store.Store("ru|en| привет ", "untrimmed");   // the decorator would have trimmed this away

        Assert.Equal(3, store.Count);
        Assert.True(store.TryGet("ru|en|привет", out var en));
        Assert.Equal("hello", en);
    }

    [Fact]
    public void The_store_does_not_enforce_I4_because_the_decorator_does()
    {
        // AC 2: the "(" rule stays upstream, in CachingTranslator.IsCacheable, so that E4.S4's three
        // decorators over ONE store cannot each forget it. Handed a placeholder directly, the store
        // keeps it — this case exists so that moving the guard down here fails a test instead of
        // quietly duplicating a rule.
        var store = new TranslationCacheStore(capacity: 2);
        store.Store("k", "(rate-limited — try again shortly)");

        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("k", out var value));
        Assert.Equal("(rate-limited — try again shortly)", value);
    }

    [Fact]
    public void A_capacity_of_zero_cannot_make_the_store_a_no_op()
    {
        // Math.Max(1, capacity), carried over verbatim from the decorator: a zero would otherwise
        // evict every entry as it arrives and every lookup would miss for ever.
        var store = new TranslationCacheStore(capacity: 0);
        store.Store("a", "A");

        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("a", out _));
    }

    // ---- thread safety: one lock, and the LIVE loop reads while a click writes ---------------

    [Fact]
    public async Task Parallel_readers_and_writers_never_throw_and_never_exceed_the_capacity()
    {
        const int capacity = 64;
        var store = new TranslationCacheStore(capacity);

        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                store.Store($"w{worker}:{i}", "v" + i);
                store.TryGet($"w{(worker + 1) % 8}:{i}", out _);
                store.TryGet($"w{worker}:{i}", out _);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(store.Count <= capacity, $"the store grew past its capacity ({store.Count})");
        Assert.True(store.Count > 0);
    }

    // ---- the store-taking constructor (T3), which is what E4.S4 will wire up ------------------

    [Fact]
    public async Task Two_decorators_over_one_store_share_what_either_of_them_translated()
    {
        var first = new CountingTranslator();
        var second = new CountingTranslator();
        var shared = new TranslationCacheStore(capacity: 8);

        var a = new CachingTranslator(first, shared);
        var b = new CachingTranslator(second, shared);

        var written = await a.TranslateAsync("привет", "ru", "en");
        var read = await b.TranslateAsync("привет", "ru", "en");

        Assert.Equal(written, read);
        Assert.Equal(1, first.SingleCalls);
        Assert.Equal(0, second.SingleCalls);   // served from the store the other decorator filled
    }

    [Fact]
    public async Task A_decorator_built_without_a_store_keeps_its_cache_to_itself()
    {
        // The legacy ctor's private store, stated as a test rather than as a comment: E4.S4 changes
        // this by passing a store, and nothing else does.
        var first = new CountingTranslator();
        var second = new CountingTranslator();

        await new CachingTranslator(first).TranslateAsync("привет", "ru", "en");
        await new CachingTranslator(second).TranslateAsync("привет", "ru", "en");

        Assert.Equal(1, first.SingleCalls);
        Assert.Equal(1, second.SingleCalls);
    }

    /// <summary>Counts how often the backend was asked, like `CachingTranslatorTests`' own double —
    /// duplicated rather than shared because that file must stay byte-identical (AC 3).</summary>
    private sealed class CountingTranslator : ITranslator
    {
        public int SingleCalls;

        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult("T:" + text);
        }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source, string target,
            CancellationToken ct = default)
            => Task.FromResult(lines.Select(l => "T:" + l).ToList());
    }
}

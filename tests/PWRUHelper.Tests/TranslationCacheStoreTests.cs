using System.Reflection;
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
        // The DEFAULT is the thing under test, so it may not be passed in: handing the constant to
        // the ctor and asserting it comes back would pass with any default at all (review finding).
        // Read the parameter the way TranslationPolicyTests reads CachingTranslator's — the store's
        // ctor is internal, so the non-public binding flags are the only difference.
        var capacity = typeof(TranslationCacheStore)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single()
            .GetParameters().Single(p => p.Name == "capacity");

        Assert.Equal(TranslationPolicy.CacheCapacity, capacity.DefaultValue);
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
    public void The_in_memory_store_does_not_enforce_I4_because_the_decorator_does()
    {
        // AC 2: the "(" rule stays upstream, in CachingTranslator.IsCacheable, so that E4.S4's three
        // decorators over ONE store cannot each forget it. Handed a placeholder directly, the store
        // keeps it — this case exists so that moving the guard down here fails a test instead of
        // quietly duplicating a rule.
        //
        // IN MEMORY is the whole claim, and the name says so since the E4.S1 review: I4 reads "never
        // stored, in memory or on disk", and the architect's ruling for E4.S2 is that the PERSISTED
        // layer refuses a "("-prefixed value on load AND on save — belt and braces behind this
        // decorator rule, not instead of it. That guard belongs to the file paths, not to Store().
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
    public async Task Parallel_readers_and_writers_contend_on_the_same_entries_and_stay_consistent()
    {
        // Two phases, because they fail differently. Phase 1 makes eight workers fight over ONE
        // small key set: every writer overwrites and promotes nodes the others are reading, which
        // is the interleaving the lock exists for — disjoint key spaces never touch a shared node
        // and would let an unsynchronised store pass (review finding). Phase 2 then proves the
        // bookkeeping survived it: 8 x 200 distinct keys into a capacity-64 store must leave
        // EXACTLY 64 entries, which a _map/_order that drifted apart cannot do.
        const int capacity = 64;
        const int shared = 16;
        var store = new TranslationCacheStore(capacity);
        var written = new HashSet<string>(Enumerable.Range(0, 8).Select(w => "v" + w));

        var phase1 = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 400; i++)
            {
                var key = "shared:" + (i % shared);
                store.Store(key, "v" + worker);
                if (store.TryGet("shared:" + ((i + 1) % shared), out var value))
                    Assert.Contains(value, written);   // never a torn or foreign value
            }
        })).ToArray();

        // A lock that is not there corrupts the Dictionary and typically HANGS inside TryGetValue
        // rather than throwing; without this the CI run would stall instead of going red.
        await Task.WhenAll(phase1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(shared, store.Count);

        var phase2 = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++) store.Store($"w{worker}:{i}", "v" + i);
        })).ToArray();

        await Task.WhenAll(phase2).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(capacity, store.Count);
    }

    // ---- the store-taking constructor (T3), which is what E4.S4 will wire up ------------------

    [Fact]
    public async Task Two_decorators_over_one_store_share_what_either_of_them_translated()
    {
        // The two doubles answer DIFFERENTLY on purpose: with both emitting "T:" + text the value
        // assertion would pass whether or not the store was shared (review finding), and the call
        // counts would be carrying the whole test on their own.
        var first = new CountingTranslator("A");
        var second = new CountingTranslator("B");
        var shared = new TranslationCacheStore(capacity: 8);

        var a = new CachingTranslator(first, shared);
        var b = new CachingTranslator(second, shared);

        var written = await a.TranslateAsync("привет", "ru", "en");
        var read = await b.TranslateAsync("привет", "ru", "en");

        Assert.Equal("A:привет", written);
        Assert.Equal(written, read);           // B's answer would have been "B:привет"
        Assert.Equal(1, first.SingleCalls);
        Assert.Equal(0, second.SingleCalls);   // served from the store the other decorator filled
    }

    [Fact]
    public async Task A_batch_is_served_from_the_store_the_other_decorator_filled()
    {
        // TranslateLinesAsync is the LIVE loop's path and the one that splices misses back into
        // place, so the shared store has to be exercised through it too — not only through the
        // single-call path (review finding).
        var first = new CountingTranslator("A");
        var second = new CountingTranslator("B");
        var shared = new TranslationCacheStore(capacity: 8);

        var a = new CachingTranslator(first, shared);
        var b = new CachingTranslator(second, shared);
        var lines = new[] { "привет", "как дела" };

        var written = await a.TranslateLinesAsync(lines, "ru", "en");
        var read = await b.TranslateLinesAsync(lines, "ru", "en");

        Assert.Equal(new[] { "A:привет", "A:как дела" }, written);
        Assert.Equal(written, read);
        Assert.Equal(1, first.BatchCalls);
        Assert.Equal(0, second.BatchCalls);   // every line was a hit, so the backend was never asked
    }

    [Fact]
    public async Task A_decorator_built_without_a_store_keeps_its_cache_to_itself()
    {
        // The legacy ctor's private store, stated as a test rather than as a comment: E4.S4 changes
        // this by passing a store, and nothing else does.
        var first = new CountingTranslator("A");
        var second = new CountingTranslator("B");

        await new CachingTranslator(first).TranslateAsync("привет", "ru", "en");
        await new CachingTranslator(second).TranslateAsync("привет", "ru", "en");

        Assert.Equal(1, first.SingleCalls);
        Assert.Equal(1, second.SingleCalls);
    }

    /// <summary>Counts how often the backend was asked — both methods, because the batch path is
    /// the LIVE loop's — and tags its answers so a value can be traced back to the instance that
    /// produced it. Like `CachingTranslatorTests`' own double, duplicated rather than shared
    /// because that file must stay byte-identical (AC 3).</summary>
    private sealed class CountingTranslator : ITranslator
    {
        private readonly string _tag;
        public int SingleCalls;
        public int BatchCalls;

        public CountingTranslator(string tag) => _tag = tag;

        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult(_tag + ":" + text);
        }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source, string target,
            CancellationToken ct = default)
        {
            BatchCalls++;
            return Task.FromResult(lines.Select(l => _tag + ":" + l).ToList());
        }
    }
}

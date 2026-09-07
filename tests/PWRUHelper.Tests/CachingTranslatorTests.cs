using System.Reflection;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class CachingTranslatorTests
{
    // Test double: records how often it's asked, so we can prove the cache absorbs repeats.
    private sealed class CountingTranslator : ITranslator
    {
        public int SingleCalls;
        public readonly List<List<string>> BatchRequests = new();
        public Func<string, string> Transform = s => "T:" + s;

        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult(Transform(text));
        }

        /// <summary>How badly the inner translator mis-counts its answer: -1 is one line short,
        /// +1 is one too many. Ruling E6-d's whole subject — a 1:1 contract broken one layer below
        /// the decorator.</summary>
        public int Drift;

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source, string target,
            CancellationToken ct = default)
        {
            BatchRequests.Add(lines.ToList());
            var outp = lines.Select(Transform).ToList();
            if (Drift < 0) outp.RemoveRange(outp.Count + Drift, -Drift);
            for (var i = 0; i < Drift; i++) outp.Add("T:extra");
            return Task.FromResult(outp);
        }
    }

    [Fact]
    public async Task Identical_single_calls_hit_the_backend_once()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);

        var a = await cache.TranslateAsync("привет", "ru", "en");
        var b = await cache.TranslateAsync("привет", "ru", "en");

        Assert.Equal("T:привет", a);
        Assert.Equal(a, b);
        Assert.Equal(1, inner.SingleCalls);
    }

    [Fact]
    public async Task Same_text_different_target_are_separate_entries()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);

        await cache.TranslateAsync("привет", "ru", "en");
        await cache.TranslateAsync("привет", "ru", "fr");

        Assert.Equal(2, inner.SingleCalls);
    }

    [Fact]
    public async Task Leading_trailing_whitespace_maps_to_the_same_entry()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);

        await cache.TranslateAsync("  привет  ", "ru", "en");
        await cache.TranslateAsync("привет", "ru", "en");

        Assert.Equal(1, inner.SingleCalls);
    }

    [Fact]
    public async Task Batch_only_asks_the_backend_for_cache_misses()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);

        await cache.TranslateLinesAsync(new[] { "a", "b", "c" }, "ru", "en");         // all miss
        var r = await cache.TranslateLinesAsync(new[] { "a", "x", "c" }, "ru", "en"); // only "x" misses

        Assert.Equal(new[] { "T:a", "T:x", "T:c" }, r);
        Assert.Equal(2, inner.BatchRequests.Count);
        Assert.Equal(new[] { "x" }, inner.BatchRequests[1]);   // second batch requested only the miss
    }

    [Fact]
    public async Task Fully_cached_batch_never_touches_the_backend()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);

        await cache.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en");
        inner.BatchRequests.Clear();
        var r = await cache.TranslateLinesAsync(new[] { "a", "b" }, "ru", "en");

        Assert.Empty(inner.BatchRequests);
        Assert.Equal(new[] { "T:a", "T:b" }, r);
    }

    [Fact]
    public async Task Failure_placeholders_are_not_cached()
    {
        var inner = new CountingTranslator
        {
            Transform = s => s == "bad" ? "(rate-limited — try again shortly)" : "T:" + s,
        };
        var cache = new CachingTranslator(inner);

        await cache.TranslateAsync("bad", "ru", "en");
        await cache.TranslateAsync("bad", "ru", "en");

        Assert.Equal(2, inner.SingleCalls);   // placeholder never stored → asked again
    }

    [Fact]
    public async Task Least_recently_used_entry_is_evicted_first()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner, capacity: 2);

        await cache.TranslateAsync("a", "ru", "en");   // {a}
        await cache.TranslateAsync("b", "ru", "en");   // {a,b}
        await cache.TranslateAsync("a", "ru", "en");   // hit → a becomes most-recently-used (b now LRU)
        Assert.Equal(2, inner.SingleCalls);

        await cache.TranslateAsync("c", "ru", "en");   // miss → evicts b; {a,c}
        Assert.Equal(3, inner.SingleCalls);

        await cache.TranslateAsync("a", "ru", "en");   // still cached
        Assert.Equal(3, inner.SingleCalls);

        await cache.TranslateAsync("b", "ru", "en");   // was evicted → re-fetched
        Assert.Equal(4, inner.SingleCalls);
    }

    // =============================================================================================
    //  Ruling E6-d — I5 one layer above every provider: a mis-counted batch is never padded
    // =============================================================================================

    /// <summary>
    /// <b>Ruling E6-d.</b> This decorator used to splice <c>j &lt; fresh.Count ? fresh[j] : missLines[j]</c>
    /// — it padded a short inner answer with the <b>untranslated source line</b> and handed it back
    /// as a translation. That is the exact bug I5 exists for, one layer higher than the provider it
    /// was fixed in: <c>DeepLTranslator</c> stopped padding after it "bypassed the fallback AND
    /// cached raw Russian source as if it were a translation", and the same splice sat here, above
    /// <i>every</i> provider, unreached only because each of them throws first.
    ///
    /// <para>Unreachable is not the same as absent: since E4.S4 this decorator wraps the WHOLE
    /// chain, so the day a tier answers 1:1-wrong without throwing — a future provider, a per-line
    /// fallback that drops a line — the padding would be the app's answer and nothing would see it.
    /// A short or long inner list is a <c>BadResponse</c>, and the cache keeps nothing from it.</para>
    /// </summary>
    [Theory]
    [InlineData(-1)]   // one line short
    [InlineData(1)]    // one line too many
    public async Task A_miscounted_inner_batch_is_a_BadResponse_and_is_never_padded(int drift)
    {
        var inner = new CountingTranslator { Drift = drift };
        var cache = new CachingTranslator(inner);
        var lines = new[] { "раз", "два", "три" };

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => cache.TranslateLinesAsync(lines, "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        // I11's habit applied to an exception message: it names the contract, never the text.
        Assert.All(lines, l => Assert.DoesNotContain(l, ex.Message, StringComparison.Ordinal));

        // …and NOTHING was cached from the bad answer (I4's sibling: a value produced by a broken
        // contract is not a success). The same batch, from a translator that counts properly, is
        // three misses again — a decorator that had stored the aligned prefix would ask for fewer.
        inner.Drift = 0;
        var second = await cache.TranslateLinesAsync(lines, "ru", "en");

        Assert.Equal(new[] { "T:раз", "T:два", "T:три" }, second);
        Assert.Equal(2, inner.BatchRequests.Count);
        Assert.Equal(lines, inner.BatchRequests[1]);
    }

    /// <summary>
    /// <b>The decision E6-d leaves open, pinned rather than discovered</b> (review of E6.S4): a
    /// batch that is PART cache hit and part miss, where the miss batch comes back mis-counted.
    /// The hits were already in hand — should they be served, with the misses left as failure
    /// placeholders, or does the whole call fail?
    ///
    /// <para><b>The whole call fails, deliberately.</b> I5's rule is "never pad", and every way to
    /// return the hits alone is a padding of some shape: a partial list breaks the 1:1 contract this
    /// method's callers rely on (they zip the answer against the lines they asked for), and filling
    /// the gaps with anything — placeholders included — is the decorator inventing a translation
    /// nobody produced. A <c>BadResponse</c> is what the caller can actually act on: the chain's
    /// next tier gets the whole batch, and it will serve the hits from this same cache for free.
    /// Nothing is lost by throwing, which is what makes throwing the cheap answer as well as the
    /// honest one.</para>
    ///
    /// <para>And the hits themselves are NOT lost — the throw is a refusal to answer, never an
    /// eviction: the same lines come straight back out of the store on the next call, and the inner
    /// translator is asked only for what is still missing.</para>
    /// </summary>
    [Fact]
    public async Task A_batch_of_hits_and_a_miscounted_miss_fails_whole_and_keeps_the_hits()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);

        await cache.TranslateLinesAsync(new[] { "раз", "два" }, "ru", "en");   // warm two lines
        inner.Drift = -1;

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => cache.TranslateLinesAsync(new[] { "раз", "два", "три" }, "ru", "en"));
        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);

        // The inner translator was asked for the ONE miss and nothing else, so the hits really were
        // hits and the failure really is the miss batch's.
        Assert.Equal(new[] { "три" }, inner.BatchRequests[1]);

        // …and the two good entries survived it. A retry (the chain's next tier, or the next LIVE
        // tick) pays only for the line that was never translated.
        inner.Drift = 0;
        Assert.Equal(new[] { "T:раз", "T:два", "T:три" },
                     await cache.TranslateLinesAsync(new[] { "раз", "два", "три" }, "ru", "en"));
        Assert.Equal(3, inner.BatchRequests.Count);
        Assert.Equal(new[] { "три" }, inner.BatchRequests[2]);
    }

    /// <summary>The cache half of the same ruling from the other side: the throw must not become a
    /// way to lose entries that were already good. A batch whose lines are all cached never reaches
    /// the inner translator at all, so a mis-counting inner cannot even be asked.</summary>
    [Fact]
    public async Task A_miscounting_inner_translator_cannot_spoil_what_is_already_cached()
    {
        var inner = new CountingTranslator();
        var cache = new CachingTranslator(inner);
        var lines = new[] { "раз", "два" };

        await cache.TranslateLinesAsync(lines, "ru", "en");     // warm, while it still counts
        inner.Drift = -1;

        Assert.Equal(new[] { "T:раз", "T:два" }, await cache.TranslateLinesAsync(lines, "ru", "en"));
        Assert.Single(inner.BatchRequests);
    }

    // =============================================================================================
    //  E8.S5 / T3 — the "p" field, actually written
    // =============================================================================================

    /// <summary>The producing provider of an entry, read back out of the store. Reflection because
    /// the map and the entry record are private and stay private: the schema is the store's
    /// business, and widening it to make a test shorter would put the entry type in reach of the
    /// app.</summary>
    private static string ProviderIdOf(TranslationCacheStore store, string key)
    {
        var map = (System.Collections.IDictionary)store.GetType()
            .GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Assert.True(map.Contains(key), $"the store has no entry for {key}");
        var node = map[key]!;
        var entry = node.GetType().GetProperty("Value")!.GetValue(node)!;
        return (string)entry.GetType().GetProperty("ProviderId")!.GetValue(entry)!;
    }

    /// <summary>
    /// <b>The gap E8.S5 was written to close.</b> <c>TranslationCacheStore.Store</c> has taken a
    /// provider id since E4.S2 and every caller passed nothing, so every entry on every user's disk
    /// carries <c>"p": ""</c> — harmless while nothing could equal "bergamot", a silent correctness
    /// bug the moment the offline tier can answer (§8.2 concern #2: "I turned the offline engine off
    /// and it is still giving me its answers").
    ///
    /// <para><b>And it is not a Bergamot-shaped special case</b>, which is the half worth asserting:
    /// whatever the delegate names lands in the field. Every tier's id, which is what the schema
    /// wanted from day one and what makes the drop rule work at all.</para>
    /// </summary>
    [Theory]
    [InlineData(ProviderIds.Bergamot)]
    [InlineData(ProviderIds.GoogleDict)]
    [InlineData(ProviderIds.GoogleGtx)]
    [InlineData(ProviderIds.DeepL)]
    [InlineData(ProviderIds.Azure)]
    public async Task Whoever_answered_is_written_into_the_entry(string providerId)
    {
        var store = new TranslationCacheStore();
        var cache = new CachingTranslator(new CountingTranslator(), store, () => providerId);

        await cache.TranslateAsync("привет", "ru", "en");

        Assert.Equal(providerId, ProviderIdOf(store, "ru|en|привет"));
    }

    /// <summary>Without a delegate the field is written empty rather than guessed — a decorator over
    /// something that is not a chain has nobody to ask, and inventing an id would poison the drop
    /// rule in the one direction nobody can notice.</summary>
    [Fact]
    public async Task With_no_way_to_ask_the_field_stays_empty()
    {
        var store = new TranslationCacheStore();
        var cache = new CachingTranslator(new CountingTranslator(), store);

        await cache.TranslateAsync("привет", "ru", "en");

        Assert.Equal("", ProviderIdOf(store, "ru|en|привет"));
    }

    /// <summary>
    /// <b>One batch, one answering provider, ONE read.</b> The per-line stores all carry the same id
    /// — asking inside the loop would put forty questions to a <c>Volatile</c> field a second call
    /// on a pool thread may already have overwritten, and two concurrent calls would then stamp each
    /// other's provider onto each other's entries. A wrong <c>"p"</c> is invisible until somebody
    /// presses Remove and keeps getting offline answers.
    /// </summary>
    [Fact]
    public async Task A_batch_asks_once_and_every_line_of_it_carries_that_answer()
    {
        var store = new TranslationCacheStore();
        var asked = 0;
        var answers = new[] { ProviderIds.GoogleDict, ProviderIds.Bergamot, ProviderIds.DeepL };
        var cache = new CachingTranslator(new CountingTranslator(), store,
                                          () => answers[Math.Min(asked++, answers.Length - 1)]);

        await cache.TranslateLinesAsync(new[] { "раз", "два", "три" }, "ru", "en");

        Assert.Equal(1, asked);
        foreach (var line in new[] { "раз", "два", "три" })
            Assert.Equal(ProviderIds.GoogleDict, ProviderIdOf(store, "ru|en|" + line));
    }

    /// <summary>A cached hit stores nothing, so it cannot relabel an entry either: a frame whose
    /// lines are all hits never asks who answered, because nobody did.</summary>
    [Fact]
    public async Task A_frame_of_pure_hits_never_asks_who_answered()
    {
        var store = new TranslationCacheStore();
        var asked = 0;
        var cache = new CachingTranslator(new CountingTranslator(), store,
                                          () => { asked++; return ProviderIds.GoogleDict; });
        var lines = new[] { "раз", "два" };

        await cache.TranslateLinesAsync(lines, "ru", "en");
        Assert.Equal(1, asked);

        await cache.TranslateLinesAsync(lines, "ru", "en");
        Assert.Equal(1, asked);
    }
}

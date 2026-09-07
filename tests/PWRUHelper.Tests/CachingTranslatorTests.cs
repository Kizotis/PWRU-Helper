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
}

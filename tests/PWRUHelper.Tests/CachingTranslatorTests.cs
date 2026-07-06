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

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source, string target,
            CancellationToken ct = default)
        {
            BatchRequests.Add(lines.ToList());
            return Task.FromResult(lines.Select(Transform).ToList());
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
}

using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E4.S4 — <b>T18</b>: one store, three decorators. This is the epic's headline expressed as a
/// number, and the number is an inner CALL COUNT: the way this feature silently does not exist is a
/// decorator quietly keeping its own store, which compiles, returns the right string, and is
/// invisible to every case that asserts on the returned text.
///
/// <para>Nothing here builds a production chain and nothing here touches a file: the shape under
/// test is <c>CachingTranslator(inner, store)</c>, which is what
/// <c>TranslationChains.BuildRead</c>/<c>BuildWrite</c> return, and the pure sharing cases need no
/// cache file at all (IS-3 — a case that creates one it does not need is a case that can poison the
/// next). The link to production — that the three builders really wrap <b>the</b> shared store, with
/// the default capacity — is <c>ChainCompositionTests</c>' business, because building a chain
/// resolves the process-global gate registry and must join <c>[Collection("Gates")]</c> (IS-5).</para>
///
/// <para>No collection attribute and none needed: every store here is local to its case, the A.2
/// default (non-persistent), and therefore never resolves a path.</para>
/// </summary>
public class SharedCacheStoreTests
{
    /// <summary>Records how often it is asked. Deliberately a second copy of
    /// <c>CachingTranslatorTests</c>' double rather than a shared one: that file is pinned as
    /// "7 cases, zero edits" by this story, and widening its private nested class to reach it here
    /// would be an edit to it.</summary>
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

    // =============================================================================================
    //  TP-CACHE-13 — the shared store across decorators
    // =============================================================================================

    /// <summary>
    /// <b>TP-CACHE-13.</b> The sentence the whole epic exists for: a line the LIVE feed translated is
    /// free when the player types it into the Translator tab. Asserted as <c>SingleCalls == 0</c> on
    /// the write side's inner translator — never on the returned string, which is identical whether
    /// the value came from the cache or from a second request.
    /// </summary>
    [Fact]
    public async Task TP_CACHE_13_The_write_decorator_serves_what_the_read_decorator_stored()
    {
        var store = new TranslationCacheStore();
        var readInner = new CountingTranslator();
        var writeInner = new CountingTranslator();
        var read = new CachingTranslator(readInner, store);
        var write = new CachingTranslator(writeInner, store);

        var live = await read.TranslateAsync("привет всем", "ru", "en");
        var typed = await write.TranslateAsync("привет всем", "ru", "en");

        Assert.Equal(1, readInner.SingleCalls);
        Assert.Equal(0, writeInner.SingleCalls);   // the epic, as a number
        Assert.Equal(live, typed);
    }

    /// <summary>Non-vacuity for the case above, and the regression it is really guarding: two
    /// decorators over two stores — A.2 before this story — ask twice for the same line.</summary>
    [Fact]
    public async Task Two_private_stores_pay_twice_which_is_what_this_story_removed()
    {
        var readInner = new CountingTranslator();
        var writeInner = new CountingTranslator();
        var read = new CachingTranslator(readInner, new TranslationCacheStore());
        var write = new CachingTranslator(writeInner, new TranslationCacheStore());

        await read.TranslateAsync("привет всем", "ru", "en");
        await write.TranslateAsync("привет всем", "ru", "en");

        Assert.Equal(1, readInner.SingleCalls);
        Assert.Equal(1, writeInner.SingleCalls);
    }

    /// <summary>
    /// The <b>three</b>-decorator version, and the one that fails if a later story quietly gives
    /// <c>_readOnceTranslator</c> its own store again (E3.S7 gave read-once its own chain instance —
    /// ruling OQ-a — and that is exactly the shape a shared store must survive). Both directions:
    /// LIVE → read-once, and Translator tab → read-once.
    /// </summary>
    [Fact]
    public async Task One_store_serves_the_live_chain_the_read_once_chain_and_the_write_chain()
    {
        var store = new TranslationCacheStore();
        var readInner = new CountingTranslator();
        var readOnceInner = new CountingTranslator();
        var writeInner = new CountingTranslator();
        var read = new CachingTranslator(readInner, store);
        var readOnce = new CachingTranslator(readOnceInner, store);
        var write = new CachingTranslator(writeInner, store);

        await read.TranslateAsync("привет всем", "ru", "en");        // the LIVE feed pays once
        await readOnce.TranslateAsync("привет всем", "ru", "en");    // a read-once of the same screen
        await write.TranslateAsync("привет всем", "ru", "en");       // the player retypes it

        Assert.Equal(1, readInner.SingleCalls);
        Assert.Equal(0, readOnceInner.SingleCalls);
        Assert.Equal(0, writeInner.SingleCalls);

        // …and the reverse direction, which is the half a two-decorator story would not have: what
        // the player typed is free when read-once meets it on screen.
        await write.TranslateAsync("го пати", "ru", "en");
        await readOnce.TranslateAsync("го пати", "ru", "en");

        Assert.Equal(1, writeInner.SingleCalls);
        Assert.Equal(0, readOnceInner.SingleCalls);
    }

    /// <summary>The LIVE feed reads in BATCHES (<c>TranslateLinesAsync</c>) and the Translator tab
    /// asks for one line, so the sharing has to work across the two paths and not only within
    /// one: a batch that stored six lines must make each of them free to the write decorator.</summary>
    [Fact]
    public async Task A_batch_stored_by_the_live_feed_is_free_line_by_line_on_the_write_side()
    {
        var store = new TranslationCacheStore();
        var readInner = new CountingTranslator();
        var writeInner = new CountingTranslator();
        var read = new CachingTranslator(readInner, store);
        var write = new CachingTranslator(writeInner, store);

        await read.TranslateLinesAsync(new[] { "го пати", "привет всем" }, "ru", "en");

        Assert.Equal("T:го пати", await write.TranslateAsync("го пати", "ru", "en"));
        Assert.Equal("T:привет всем", await write.TranslateAsync("привет всем", "ru", "en"));
        Assert.Equal(0, writeInner.SingleCalls);

        // …and the other way round: a batch asks its inner translator only for what the write side
        // has not already paid for.
        await write.TranslateAsync("го цс", "ru", "en");
        readInner.BatchRequests.Clear();
        await read.TranslateLinesAsync(new[] { "го цс", "новая строка" }, "ru", "en");
        Assert.Equal(new[] { "новая строка" }, Assert.Single(readInner.BatchRequests));
    }

    /// <summary>I4 across the share: a failure placeholder refused by one decorator must not be
    /// findable through another. The <c>(</c> rule lives upstream of the store precisely so one
    /// shared store cannot inherit it from whichever decorator happened to be asked first.</summary>
    [Fact]
    public async Task A_failure_placeholder_is_not_shared_because_it_is_never_stored()
    {
        var store = new TranslationCacheStore();
        var readInner = new CountingTranslator
        {
            Transform = _ => "(rate-limited — try again shortly)",
        };
        var writeInner = new CountingTranslator();
        var read = new CachingTranslator(readInner, store);
        var write = new CachingTranslator(writeInner, store);

        await read.TranslateAsync("привет всем", "ru", "en");

        Assert.Equal(0, store.Count);
        Assert.Equal("T:привет всем", await write.TranslateAsync("привет всем", "ru", "en"));
        Assert.Equal(1, writeInner.SingleCalls);
    }

    // =============================================================================================
    //  TP-CACHE-14 — a key save no longer empties the cache (AC 2, amplifier A5)
    // =============================================================================================

    /// <summary>
    /// <b>TP-CACHE-14.</b> <c>DeepLSaveKey_Click</c> rebuilds the write chain
    /// (<c>MainWindow.Translate.cs</c>: <c>_writeTranslator = BuildWriteChain();</c>) so a corrected
    /// key takes effect on the next translation. Before this story that threw the session's
    /// translations away with the decorator; now the decorator is rebuilt and the <b>store</b> is
    /// not.
    ///
    /// <para>The shape of <c>BuildWriteChain()</c> rather than a <c>MainWindow</c>: the behaviour
    /// lives in the store's lifetime, not in the window, and constructing a window here would buy a
    /// dispatcher and an STA thread to observe the same two objects.</para>
    /// </summary>
    [Fact]
    public async Task TP_CACHE_14_A_key_save_rebuilds_the_decorator_and_keeps_the_store()
    {
        var store = new TranslationCacheStore();
        var beforeInner = new CountingTranslator();
        var write = new CachingTranslator(beforeInner, store);

        await write.TranslateAsync("привет всем", "ru", "en");
        await write.TranslateAsync("го пати", "ru", "en");
        Assert.Equal(2, store.Count);

        // The key save: a NEW chain (DeepL is now in front of the free tiers) over the SAME store.
        var afterInner = new CountingTranslator();
        var rebuilt = new CachingTranslator(afterInner, store);

        Assert.Equal(2, store.Count);
        Assert.Equal("T:привет всем", await rebuilt.TranslateAsync("привет всем", "ru", "en"));
        Assert.Equal("T:го пати", await rebuilt.TranslateAsync("го пати", "ru", "en"));
        Assert.Equal(0, afterInner.SingleCalls);

        // …and the rebuild is not a no-op: a line the session has NOT seen goes to the new inner
        // translator, which is what "a corrected key takes effect immediately" means.
        Assert.Equal("T:го цс", await rebuilt.TranslateAsync("го цс", "ru", "en"));
        Assert.Equal(1, afterInner.SingleCalls);
        Assert.Equal(2, beforeInner.SingleCalls);   // the old chain is never asked again
    }
}

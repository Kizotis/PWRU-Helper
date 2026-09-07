using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E4.S2 — the cache's FILE. <c>TranslationCacheStoreTests</c> still owns the in-memory LRU and is
/// left alone by this story; what is here is everything that only exists once the map has somewhere
/// to survive a restart: the lazy load, the schema, the atomic write, the debounce, and the two
/// guards (I4 on disk, the Bergamot drop).
///
/// <para><b>No collection attribute, on purpose, and it is a decision rather than an omission.</b>
/// The store's only process-wide state is <see cref="TranslationCacheStore.PathOverride"/> and
/// <see cref="TranslationCacheStore.SaveDebounceMs"/>, and this is the only class in the suite that
/// writes either — xUnit serialises the cases inside one class, so a third non-parallel collection
/// would buy nothing (<c>GatesCollection.cs:12-14</c> calls the second one an accepted, bounded
/// cost; a third would be noise). Every other class builds NON-persistent stores, which is the A.2
/// default and which never resolves a path at all.</para>
///
/// <para>Every case runs inside a <see cref="TempCache"/> (IS-3), and
/// <c>TestCacheRedirect</c>'s <c>[ModuleInitializer]</c> covers the ones that do not (IS-2).</para>
/// </summary>
public class TranslationCachePersistenceTests
{
    private const string Placeholder = "(rate-limited — try again shortly)";

    // ---- TP-CACHE-10: nothing is read until a MISS asks (AC 1, I10) ---------------------------

    [Fact]
    public void The_constructor_reads_nothing_and_creates_nothing()
    {
        using var cache = new TempCache();
        cache.Write(FileJson(("ru|en|привет", "hello"), ("ru|en|пока", "bye")));

        // Empty although the file it points at has two entries: constructing is not asking.
        Assert.Equal(0, new TranslationCacheStore(persistent: true).Count);

        // …and a directory that does not exist yet, so "created a folder at startup" is visible as
        // well as "read a file": the store may do neither before something asks it a question.
        var dir = Path.Combine(Path.GetDirectoryName(cache.Path)!, "not-yet");
        TranslationCacheStore.PathOverride = Path.Combine(dir, "translation-cache.json");

        var store = new TranslationCacheStore(persistent: true);

        Assert.Equal(0, store.Count);
        Assert.False(Directory.Exists(dir), "constructing the store must not create %AppData%\\PWRUHelper");
        Assert.False(File.Exists(TranslationCacheStore.CachePath));
    }

    [Fact]
    public void Neither_a_store_nor_a_Count_loads_the_file_only_a_miss_does()
    {
        // The trigger is exactly one thing (AC 1). A Store is not a question and Count is not a
        // translation; making either of them load would put the file on the path of the very first
        // request instead of on its first miss.
        using var cache = new TempCache();
        cache.Write(FileJson(("ru|en|привет", "hello"), ("ru|en|пока", "bye")));

        var store = new TranslationCacheStore(persistent: true);

        store.Store("ru|en|да", "yes");
        Assert.Equal(1, store.Count);      // …and not 3: the file has not been read

        Assert.True(store.TryGet("ru|en|да", out _));
        Assert.Equal(1, store.Count);      // a HIT is not a miss either

        Assert.False(store.TryGet("ru|en|нет", out _));   // the miss
        Assert.Equal(3, store.Count);
        Assert.True(store.TryGet("ru|en|привет", out var loaded));
        Assert.Equal("hello", loaded);
    }

    [Fact]
    public async Task The_load_runs_synchronously_on_the_thread_that_missed()
    {
        // Ruling E2-e as corrected: no Task.Run, no second lock, no window in which the caller has
        // returned and the entry is not there yet. "Off the UI thread" (AC 1) is true because the
        // first miss happens inside an awaited translation HttpProviderCore has already
        // ConfigureAwait(false)-ed — this case is the half that can be asserted without a window.
        //
        // TP-CACHE-11 (the same claim through a real MainWindow) is deferred to E4.S4, which is the
        // story that actually puts a persistent store in the constructor — until then the window
        // builds three NON-persistent stores and there is nothing for it to observe.
        using var cache = new TempCache();
        cache.Write(FileJson(("ru|en|привет", "hello")));

        var store = new TranslationCacheStore(persistent: true);

        var seen = await Task.Run(() =>
        {
            Assert.False(store.TryGet("ru|en|нет", out _));    // the miss that loads
            return store.TryGet("ru|en|привет", out var v) ? v : null;   // already there, no wait
        });

        Assert.Equal("hello", seen);
    }

    // ---- TP-CACHE-03 / TP-CACHE-04: the round trip and the schema ------------------------------

    [Fact]
    public void A_session_that_stored_translations_serves_them_after_a_restart()
    {
        using var cache = new TempCache();

        var first = new TranslationCacheStore(persistent: true);
        first.Store("ru|en|привет", "hello");
        first.Store("ru|en|пока", "bye");
        first.SaveNow();

        var second = new TranslationCacheStore(persistent: true);   // the "restart"
        Assert.True(second.TryGet("ru|en|привет", out var hello));
        Assert.Equal("hello", hello);
        Assert.True(second.TryGet("ru|en|пока", out var bye));
        Assert.Equal("bye", bye);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void The_file_is_version_1_with_k_v_p_t_and_an_invariant_UTC_timestamp()
    {
        using var cache = new TempCache();

        var store = new TranslationCacheStore(persistent: true);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        store.Store("ru|en|привет", "hello");
        store.Store("ru|en|пока", "bye", ProviderIds.DeepL);
        store.SaveNow();

        using var doc = JsonDocument.Parse(cache.Read());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());

        var rows = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        foreach (var row in rows)
        {
            Assert.Equal(new[] { "k", "v", "p", "t" }, row.EnumerateObject().Select(p => p.Name).ToArray());

            var t = row.GetProperty("t").GetString();
            Assert.True(DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var stamp), $"'{t}' is not an invariant round-trip date");
            Assert.Equal(TimeSpan.Zero, stamp.Offset);          // UTC, not the box's local offset
            Assert.InRange(stamp, before, DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.DoesNotContain(",", t);                      // the fr-FR decimal comma, E2.S6's rule
        }

        // "p" is written from day one and is EMPTY for everything A.2 produces: the store is handed
        // a key and a value and does not know which tier answered. A caller that does know (E7.S3
        // reads ChainTranslator.LastOutcome.ProviderId) can pass one, and it round-trips.
        Assert.Equal("", rows[1].GetProperty("p").GetString());        // ru|en|привет, no provider
        Assert.Equal(ProviderIds.DeepL, rows[0].GetProperty("p").GetString());
    }

    [Fact]
    public void A_provider_id_survives_the_round_trip()
    {
        using var cache = new TempCache();

        var first = new TranslationCacheStore(persistent: true);
        first.Store("ru|en|привет", "hello", ProviderIds.DeepL);
        first.SaveNow();

        // Read back, then written out again: the field has to survive the MAP as well as the file,
        // which is the half a load-only assertion cannot see.
        var second = new TranslationCacheStore(persistent: true);
        Assert.False(second.TryGet("ru|en|пока", out _));   // the miss that loads
        second.Store("ru|en|да", "yes");
        second.SaveNow();

        using var doc = JsonDocument.Parse(cache.Read());
        var row = doc.RootElement.GetProperty("entries").EnumerateArray()
                     .Single(r => r.GetProperty("k").GetString() == "ru|en|привет");
        Assert.Equal(ProviderIds.DeepL, row.GetProperty("p").GetString());
    }

    // ---- TP-CACHE-07: the MRU order is the file order, and it survives a reload -----------------

    [Fact]
    public void The_file_is_written_MRU_first_and_a_reload_keeps_the_hottest_entries()
    {
        using var cache = new TempCache();

        var first = new TranslationCacheStore(persistent: true);
        first.Store("a", "A");
        first.Store("b", "B");
        first.Store("c", "C");
        Assert.True(first.TryGet("a", out _));   // a READ between the writes: a is MRU again
        first.SaveNow();

        using (var doc = JsonDocument.Parse(cache.Read()))
            Assert.Equal(new[] { "a", "c", "b" },
                doc.RootElement.GetProperty("entries").EnumerateArray()
                   .Select(r => r.GetProperty("k").GetString()).ToArray());

        // …and the order is what a smaller store keeps: the head of the file is the head of the
        // list, so "b" — cold at save time — is the one that does not make it back.
        var second = new TranslationCacheStore(capacity: 2, persistent: true);
        Assert.False(second.TryGet("miss", out _));
        Assert.Equal(2, second.Count);
        Assert.True(second.TryGet("a", out _));
        Assert.True(second.TryGet("c", out _));
        Assert.False(second.TryGet("b", out _), "an AddFirst loop on load reverses the file and evicts the hottest entries");
    }

    [Fact]
    public void A_load_that_arrives_after_a_store_keeps_this_sessions_value()
    {
        // Store does not load (above), so a session CAN write a key before the file is read. The
        // live value is the fresher one and the file may not overwrite it.
        using var cache = new TempCache();
        cache.Write(FileJson(("k", "from-the-file")));

        var store = new TranslationCacheStore(persistent: true);
        store.Store("k", "from-this-session");
        Assert.False(store.TryGet("miss", out _));          // loads now

        Assert.True(store.TryGet("k", out var value));
        Assert.Equal("from-this-session", value);
    }

    // ---- TP-CACHE-08 / TP-CACHE-09: corrupt, wrong-rooted, future, oversize ---------------------

    [Theory]
    [InlineData("")]
    [InlineData("{\"version\":1,\"entries\":[{\"k\":\"a\",\"v\":\"A\"")]   // truncated mid-entry
    [InlineData("[]")]                                                     // wrong-rooted
    [InlineData("{\"version\":1}")]                                        // no entries
    [InlineData("{\"entries\":[]}")]                                       // no version
    [InlineData("{\"version\":\"1\",\"entries\":[]}")]                     // version is not a number
    [InlineData("{\"version\":1,\"entries\":{}}")]                         // entries is not an array
    [InlineData("not json at all")]
    public void A_file_this_build_cannot_use_reads_as_an_empty_cache_and_never_throws(string json)
    {
        using var cache = new TempCache();
        cache.Write(json);

        var store = new TranslationCacheStore(persistent: true);

        Assert.False(store.TryGet("a", out var value));
        Assert.Equal("", value);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_future_version_is_a_forward_guard_and_not_a_migration()
    {
        using var cache = new TempCache();
        cache.Write("{\"version\":2,\"entries\":[{\"k\":\"a\",\"v\":\"A\",\"p\":\"\",\"t\":\"\"}]}");

        var store = new TranslationCacheStore(persistent: true);

        Assert.False(store.TryGet("a", out _));
        Assert.Equal(0, store.Count);

        // …and the next save simply rewrites it as version 1. Unlike provider-state.json there is
        // nothing here worth refusing to replace: what a newer build wrote is a translation, and
        // one request re-earns it.
        store.Store("b", "B");
        store.SaveNow();
        using var doc = JsonDocument.Parse(cache.Read());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void An_entry_without_a_key_or_a_value_costs_that_entry_and_not_the_file()
    {
        using var cache = new TempCache();
        cache.Write("{\"version\":1,\"entries\":["
                    + "{\"v\":\"no key\"},"
                    + "{\"k\":\"no value\"},"
                    + "\"not an object\","
                    + "{\"k\":\"good\",\"v\":\"G\",\"p\":\"\",\"t\":\"nonsense\"}]}");

        var store = new TranslationCacheStore(persistent: true);

        Assert.False(store.TryGet("miss", out _));
        Assert.Equal(1, store.Count);
        Assert.True(store.TryGet("good", out var g));
        Assert.Equal("G", g);          // an unreadable timestamp does not cost the translation
    }

    [Fact]
    public void An_absurdly_large_file_is_refused_unread()
    {
        // The bound is checked on the FileInfo, before a byte is read: this read happens under the
        // store's lock, which the next translation waits on, so a hand-edited or corrupted file may
        // never become a hang. 1 MB against §8.2's ≈300 KB for a full 2000 entries.
        using var cache = new TempCache();
        var padding = new string('x', 1024 * 1024);
        cache.Write("{\"version\":1,\"entries\":[{\"k\":\"a\",\"v\":\"A\",\"p\":\"\",\"t\":\"\"}],\"pad\":\""
                    + padding + "\"}");
        Assert.True(new FileInfo(cache.Path).Length > 1024 * 1024);

        var store = new TranslationCacheStore(persistent: true);

        Assert.False(store.TryGet("a", out _));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_file_this_process_could_not_read_is_never_rewritten_from_an_empty_map()
    {
        // ProviderGates' `_keepFile` rule (:260), and the one read failure that is not the file's
        // fault: an AV or a sync agent holding it open for the 50 ms of the first miss. Rewriting
        // from an empty map there would cost the user the 2000 entries they earned to save the one
        // this session has — and re-earning them is exactly the rate-limit pressure this whole
        // cache exists to avoid.
        using var cache = new TempCache();
        cache.Write(FileJson(("ru|en|привет", "hello")));
        var before = cache.Read();

        var store = new TranslationCacheStore(persistent: true);

        using (File.Open(cache.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.TryGet("miss", out _));   // the miss that tries to load, and cannot
            Assert.Equal(0, store.Count);
        }

        store.Store("ru|en|пока", "bye");
        store.SaveNow();

        Assert.Equal(before, cache.Read());

        // Non-vacuous: a file this build merely cannot USE is a different thing and IS replaced —
        // see A_future_version_is_a_forward_guard_and_not_a_migration. The rewrite is the repair
        // there; here it is the loss.
    }

    // ---- TP-CACHE-02: I4 on disk, on the way out AND on the way in -----------------------------

    [Fact]
    public async Task A_failure_placeholder_the_decorator_refused_is_absent_from_the_file()
    {
        // Driven through the DECORATOR with a failing inner translator and asserted on the FILE —
        // the only shape of this assertion that would survive somebody adding a second Store call
        // site. CachingTranslator.IsCacheable is the authoritative rule (§3.1).
        using var cache = new TempCache();
        var store = new TranslationCacheStore(persistent: true);
        var translator = new CachingTranslator(new HalfBrokenTranslator(), store);

        Assert.Equal("hello", await translator.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(Placeholder, await translator.TranslateAsync("пока", "ru", "en"));
        store.SaveNow();

        var rows = Rows(cache.Read());
        Assert.Equal(new[] { "ru|en|привет" }, rows.Select(r => r.GetProperty("k").GetString()).ToArray());
        Assert.DoesNotContain("(", cache.Read(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_that_reached_the_map_some_other_way_still_never_reaches_the_file()
    {
        // The architect's E4.S2 ruling, save half: the in-memory Store stays dumb (§3.1, and
        // TranslationCacheStoreTests.The_in_memory_store_does_not_enforce_I4_because_the_decorator_does
        // pins that), so the guard belongs to the file path. Belt and braces BEHIND IsCacheable.
        using var cache = new TempCache();
        var store = new TranslationCacheStore(persistent: true);

        store.Store("ru|en|привет", "hello");
        store.Store("ru|en|пока", Placeholder);
        store.SaveNow();

        Assert.Equal(2, store.Count);          // still there in memory — that is the decorator's job
        Assert.Equal(new[] { "ru|en|привет" },
                     Rows(cache.Read()).Select(r => r.GetProperty("k").GetString()).ToArray());
    }

    [Fact]
    public void A_placeholder_written_by_an_older_or_hand_edited_build_is_refused_on_load()
    {
        // The load half. A cached "(rate-limited …)" restored from a file outlives the condition it
        // describes — the exact complaint that started this investigation.
        using var cache = new TempCache();
        cache.Write(FileJson(("ru|en|привет", "hello"), ("ru|en|пока", Placeholder)));

        var store = new TranslationCacheStore(persistent: true);

        Assert.False(store.TryGet("miss", out _));
        Assert.Equal(1, store.Count);
        Assert.False(store.TryGet("ru|en|пока", out _));
        Assert.True(store.TryGet("ru|en|привет", out _));
    }

    // ---- TP-CACHE-05: the Bergamot drop (AC 6, §8.2 concern #2) ---------------------------------

    [Fact]
    public void A_bergamot_entry_is_dropped_on_load_while_the_offline_tier_is_off()
    {
        using var cache = new TempCache();
        cache.Write("{\"version\":1,\"entries\":["
                    + "{\"k\":\"ru|en|привет\",\"v\":\"hello\",\"p\":\"bergamot\",\"t\":\"\"},"
                    + "{\"k\":\"ru|en|пока\",\"v\":\"bye\",\"p\":\"\",\"t\":\"\"}]}");

        // A.2's structural default: OfflineFallbackEnabled arrives in E6.S3 and the tier in E8, so
        // there is nothing that could have written this row — it is a fixture, and the DROP RULE is
        // what is under test, not a future feature.
        var off = new TranslationCacheStore(persistent: true);
        Assert.False(off.TryGet("miss", out _));
        Assert.Equal(1, off.Count);
        Assert.False(off.TryGet("ru|en|привет", out _));
        Assert.True(off.TryGet("ru|en|пока", out _));
    }

    [Fact]
    public void The_same_entry_is_kept_when_the_offline_tier_is_the_one_the_user_runs()
    {
        // Non-vacuity: the row is well-formed and only the flag decides. E8 passes
        // settings.OfflineFallbackEnabled here and nothing else changes.
        using var cache = new TempCache();
        cache.Write("{\"version\":1,\"entries\":["
                    + "{\"k\":\"ru|en|привет\",\"v\":\"hello\",\"p\":\"bergamot\",\"t\":\"\"}]}");

        var on = new TranslationCacheStore(persistent: true, offlineEnabled: true);

        Assert.False(on.TryGet("miss", out _));
        Assert.True(on.TryGet("ru|en|привет", out var value));
        Assert.Equal("hello", value);
    }

    // ---- TP-CACHE-12: the debounce coalesces, and SaveNow is the flush --------------------------

    [Fact]
    public void Many_stores_inside_the_window_produce_one_write_and_SaveNow_is_what_writes_it()
    {
        // CI-3: driven through the SaveDebounceMs seam, never by waiting real seconds.
        using var cache = new TempCache();
        TranslationCacheStore.SaveDebounceMs = 60_000;

        var store = new TranslationCacheStore(persistent: true);
        for (int i = 0; i < 20; i++) store.Store("k" + i, "v" + i);

        Assert.False(File.Exists(cache.Path), "the debounce window has not elapsed");

        store.SaveNow();
        Assert.Equal(20, Rows(cache.Read()).Count);

        // …and that write emptied the queue: a second SaveNow has nothing left to do, so a close
        // after a quiet minute never rewrites the file.
        File.Delete(cache.Path);
        store.SaveNow();
        Assert.False(File.Exists(cache.Path));
    }

    [Fact]
    public void A_read_never_schedules_a_write_even_though_it_reorders_the_file()
    {
        // Ruling E4-b. TryGet promotes, so a calm LIVE session of pure hits changes the order the
        // file records while storing nothing — and saving on reads would be a 300 KB write every
        // tick. The on-disk order lags the reads, deliberately.
        using var cache = new TempCache();
        TranslationCacheStore.SaveDebounceMs = 60_000;

        var store = new TranslationCacheStore(persistent: true);
        store.Store("a", "A");
        store.Store("b", "B");
        store.SaveNow();
        var written = cache.Read();

        Assert.True(store.TryGet("a", out _));   // promotes a; the file still says b, a
        store.SaveNow();

        Assert.Equal(written, cache.Read());
        Assert.Equal(new[] { "b", "a" }, Rows(written).Select(r => r.GetProperty("k").GetString()).ToArray());
    }

    [Fact]
    public void A_pending_save_can_be_cancelled_so_it_never_outlives_its_test()
    {
        // IS-4's second half. Without it a write queued by one case lands during the next one — in
        // the next case's temp directory, or after PathOverride has gone back to the real file.
        using var cache = new TempCache();
        TranslationCacheStore.SaveDebounceMs = 60_000;

        var store = new TranslationCacheStore(persistent: true);
        store.Store("a", "A");
        store.CancelPendingSave();
        store.SaveNow();

        Assert.False(File.Exists(cache.Path));
    }

    // ---- the atomic write ----------------------------------------------------------------------

    [Fact]
    public void The_temp_file_is_swapped_in_and_never_left_behind()
    {
        using var cache = new TempCache();

        var store = new TranslationCacheStore(persistent: true);
        store.Store("a", "A");
        store.SaveNow();
        store.Store("b", "B");
        store.SaveNow();          // the second write is the File.Replace branch

        Assert.True(File.Exists(cache.Path));
        Assert.False(File.Exists(cache.Path + ".tmp"),
            "the temp file must be swapped in, not left behind");
        Assert.Equal(2, Rows(cache.Read()).Count);
    }

    [Fact]
    public void A_write_that_cannot_happen_leaves_the_previous_file_exactly_as_it_was()
    {
        using var cache = new TempCache();

        var store = new TranslationCacheStore(persistent: true);
        store.Store("a", "A");
        store.SaveNow();
        var before = cache.Read();

        // A DIRECTORY where the temp file goes: WriteAllText throws, the catch swallows it, and the
        // old file is untouched — which is the whole reason for writing beside it and swapping.
        Directory.CreateDirectory(cache.Path + ".tmp");
        store.Store("b", "B");
        store.SaveNow();

        Assert.Equal(before, cache.Read());
        Directory.Delete(cache.Path + ".tmp");
    }

    // ---- the A.2 default: a store nobody made persistent touches no disk at all -----------------

    [Fact]
    public void A_non_persistent_store_neither_reads_nor_writes_and_that_is_the_legacy_decorator()
    {
        // Persistence is opt-in, and since E4.S4 TranslationChains.Cache is the only instance in
        // the app that opts in — every chain the builders return decorates THAT one. What is left
        // on the default is the legacy CachingTranslator constructor (no store, its own private
        // 500-entry one), which nothing in production calls any more: a second persistent store
        // pointed at the same file would spend the session overwriting the first one's entries,
        // which is why the default may not flip.
        using var cache = new TempCache();
        cache.Write(FileJson(("ru|en|привет", "hello")));
        var before = cache.Read();

        var store = new TranslationCacheStore();      // the default, and what CachingTranslator builds

        Assert.False(store.TryGet("ru|en|привет", out _), "a private store must not read the shared file");
        store.Store("ru|en|пока", "bye");
        store.SaveNow();

        Assert.Equal(before, cache.Read());
    }

    [Fact]
    public void The_shared_store_is_persistent_and_the_facade_is_what_flushes_it()
    {
        // MainWindow.OnClosing names TranslationChains.FlushCache(), never a store — the same rule
        // that keeps the chain composition in Services/ (ruling E3-c). Since E4.S4 this instance is
        // what all three builders hand their decorator (ChainCompositionTests asserts that identity).
        using var cache = new TempCache();

        var shared = TranslationChains.Cache;
        Assert.Same(shared, TranslationChains.Cache);

        shared.Store("ru|en|привет", "hello");
        TranslationChains.FlushCache();

        try
        {
            Assert.Equal(new[] { "ru|en|привет" },
                         Rows(cache.Read()).Select(r => r.GetProperty("k").GetString()).ToArray());
        }
        finally
        {
            // The singleton would otherwise outlive this case with a pending save AND a path
            // pinned to the temp directory the using below is about to delete — E4.S4 is the story
            // that would discover that the hard way.
            TranslationChains.ResetCacheForTests();
        }

        Assert.NotSame(shared, TranslationChains.Cache);
        TranslationChains.ResetCacheForTests();
    }

    /// <summary>
    /// <b>TP-CACHE-11, end to end</b> — the epic's payoff sentence, driven through the process's one
    /// real store and its real file: a line the LIVE feed translated is free on the Translator tab,
    /// and it is still free after a restart. E4.S2 could not write this (nothing was wired to the
    /// shared store yet, so its "How to verify manually" steps 2–6 were not exercisable); E4.S4 is
    /// what makes it true, and this is those steps without a window.
    ///
    /// <para>The decorators are built the way <c>TranslationChains</c> builds them — over
    /// <see cref="TranslationChains.Cache"/> — but with counting inner translators rather than real
    /// chains: a real chain carries production's HttpClient and a miss would reach the Internet
    /// (IS-10). What the store is handed to in production is <c>ChainCompositionTests</c>' identity
    /// assert; what that sharing buys is here, as a call count of zero.</para>
    ///
    /// <para>The UI-thread half of TP-CACHE-11 stays where it can be asserted without a dispatcher:
    /// <see cref="The_load_runs_synchronously_on_the_thread_that_missed"/>. The load happens on
    /// whichever thread missed, and every miss in this app is already off the UI thread — a LIVE
    /// tick or a click's await — so there is no dispatcher hop to observe.</para>
    /// </summary>
    [Fact]
    public async Task TP_CACHE_11_A_line_the_live_feed_translated_is_free_when_the_player_types_it()
    {
        using var cache = new TempCache();
        try
        {
            // The LIVE feed pays for the line, once.
            var liveInner = new CountingTranslator();
            var live = new CachingTranslator(liveInner, TranslationChains.Cache);
            Assert.Equal("T:привет всем", await live.TranslateAsync("привет всем", "ru", "en"));
            Assert.Equal(1, liveInner.SingleCalls);

            // The player retypes it in the Translator tab, same session: zero requests.
            var writeInner = new CountingTranslator();
            var write = new CachingTranslator(writeInner, TranslationChains.Cache);
            Assert.Equal("T:привет всем", await write.TranslateAsync("привет всем", "ru", "en"));
            Assert.Equal(0, writeInner.SingleCalls);

            // Close the app: OnClosing's one line, and the file really holds the LIVE feed's entry.
            TranslationChains.FlushCache();
            Assert.Equal(new[] { "ru|en|привет всем" },
                         Rows(cache.Read()).Select(r => r.GetProperty("k").GetString()).ToArray());

            // …and start it again. A new store instance, which reads the file on its first MISS.
            var closed = TranslationChains.Cache;
            TranslationChains.ResetCacheForTests();
            Assert.NotSame(closed, TranslationChains.Cache);   // genuinely a restart, not a re-read

            var restartedInner = new CountingTranslator();
            var restarted = new CachingTranslator(restartedInner, TranslationChains.Cache);
            Assert.Equal("T:привет всем", await restarted.TranslateAsync("привет всем", "ru", "en"));
            Assert.Equal(0, restartedInner.SingleCalls);

            // Non-vacuity: a line the previous session never saw still costs a request.
            Assert.Equal("T:го пати", await restarted.TranslateAsync("го пати", "ru", "en"));
            Assert.Equal(1, restartedInner.SingleCalls);
        }
        finally
        {
            // The singleton may not outlive the TempCache whose path it pinned (E4.S2's review).
            TranslationChains.ResetCacheForTests();
        }
    }

    [Fact]
    public void TranslationChains_Cache_is_the_only_persistent_store_production_builds()
    {
        // The architect's E4.S2 ruling on the `persistent: false` default: it is accepted precisely
        // BECAUSE one instance opts in, and a second one pointed at the same file would spend the
        // session overwriting the first one's entries. A default is exactly the kind of guard a
        // copied constructor line silently loses, so it is pinned as an exact-equality scan —
        // TP-START-02's shape. It is what stops E4.S4's sharing being undone one `new` at a time.
        var root = RepoRoot();

        var builders = ProductionSources(root)
            .Where(f => Code(File.ReadAllText(f))
                .Contains("new TranslationCacheStore(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // CachingTranslator's is the private, NON-persistent one of the three legacy decorators.
        Assert.Equal(new[] { "CachingTranslator.cs", "TranslationChains.cs" }, builders);

        var optIns = ProductionSources(root)
            .SelectMany(f => Code(File.ReadAllText(f)).Split('\n')
                .Where(l => l.Contains("persistent: true", StringComparison.Ordinal))
                .Select(_ => Path.GetFileName(f)))
            .ToArray();

        Assert.Equal(new[] { "TranslationChains.cs" }, optIns);
    }

    // ---- I10 / I11 as scans ---------------------------------------------------------------------

    [Fact]
    public void No_source_outside_Services_names_the_store_and_no_source_at_all_logs_its_path()
    {
        // The I10 half: the way this breaks is invisible — a convenience call added to the
        // constructor by the next person, and 300 KB of JSON is read before first paint again. The
        // shape is ProviderStateStoreTests.No_startup_path_mentions_ProviderGates, and the answer
        // here is stricter: the code-behind names the FACADE, so the store is a Services/ type with
        // no reference outside the folder at all.
        var root = RepoRoot();

        var strays = ProductionSources(root)
            .Where(f => !Path.GetDirectoryName(f)!.EndsWith("Services", StringComparison.Ordinal))
            .SelectMany(f => Code(File.ReadAllText(f)).Split('\n')
                .Where(l => l.Contains("TranslationCacheStore", StringComparison.Ordinal))
                .Select(l => Path.GetFileName(f) + ": " + l.Trim()))
            .ToList();
        Assert.Empty(strays);

        // …and the carve-out is not vacuous: the shutdown flush really is there, in OnClosing and
        // nowhere else.
        var main = Code(File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs")));
        Assert.Equal(1, main.Split('\n').Count(l => l.Contains("FlushCache()", StringComparison.Ordinal)));
        Assert.Contains("TranslationChains.FlushCache();",
                        Body(main, "protected override void OnClosing("), StringComparison.Ordinal);
        Assert.DoesNotContain("FlushCache", Body(main, "public MainWindow()"), StringComparison.Ordinal);

        // I11: the file carries the user's own chat text, so neither its path nor its content may
        // reach the log or the error report. No production source may name it beside a log call.
        foreach (var file in ProductionSources(root))
        {
            var text = Code(File.ReadAllText(file));
            foreach (var line in text.Split('\n').Where(l => l.Contains("translation-cache", StringComparison.Ordinal)))
                Assert.DoesNotContain("Log", line, StringComparison.Ordinal);
        }
    }

    // ---- the %AppData% guard (T1) ---------------------------------------------------------------

    [Fact]
    public void A_write_never_reaches_the_real_AppData_cache_file()
    {
        // The fourth file this suite could poison, and the first that would carry the user's own
        // chat text. UNTOUCHED rather than absent: a developer who has run the app legitimately has
        // one, and a suite that fails on their machine is a suite they turn off.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PWRUHelper", "translation-cache.json");
        var existed = File.Exists(real);
        var stamp = existed ? File.GetLastWriteTimeUtc(real) : default;

        using (var cache = new TempCache())
        {
            var store = new TranslationCacheStore(persistent: true);
            store.Store("ru|en|привет", "hello");
            store.SaveNow();

            Assert.True(File.Exists(cache.Path));
            Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(TranslationCacheStore.CachePath));
        }

        // Outside the using, i.e. with no TempCache in scope: the module initializer's redirect is
        // still what the store would resolve, exactly as LoggingTests asserts for the log.
        Assert.NotNull(TranslationCacheStore.PathOverride);
        Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(TranslationCacheStore.CachePath));

        Assert.Equal(existed, File.Exists(real));
        if (existed) Assert.Equal(stamp, File.GetLastWriteTimeUtc(real));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>A hand-written file in the story's schema. Fixtures live in the test source rather
    /// than under <c>Fixtures/</c> (IS-9): what is being asserted is the shape, and the shape should
    /// be readable where it is asserted.</summary>
    private static string FileJson(params (string Key, string Value)[] entries) =>
        "{\"version\":1,\"entries\":["
        + string.Join(",", entries.Select(e =>
            $"{{\"k\":{JsonSerializer.Serialize(e.Key)},\"v\":{JsonSerializer.Serialize(e.Value)},"
            + "\"p\":\"\",\"t\":\"2026-09-07T10:00:00.0000000+00:00\"}"))
        + "]}";

    private static List<JsonElement> Rows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("entries").EnumerateArray()
                  .Select(e => e.Clone()).ToList();
    }

    /// <summary>An inner translator that answers one line and refuses the other with the shape every
    /// provider uses for a failure — a leading "(" (<c>UserMessages</c>).</summary>
    private sealed class HalfBrokenTranslator : ITranslator
    {
        public Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default)
            => Task.FromResult(text == "привет" ? "hello" : Placeholder);

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source, string target,
            CancellationToken ct = default)
            => Task.FromResult(lines.Select(l => l == "привет" ? "hello" : Placeholder).ToList());
    }

    /// <summary>Counts what it is asked, so the shared store can be asserted on the number that
    /// matters — requests NOT made. Local to this class rather than shared with
    /// <c>SharedCacheStoreTests</c>: those cases need no file and must not be able to reach one.</summary>
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

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is no longer in MainWindow.xaml.cs — fix the scan, not the guard");

        var open = source.IndexOf('{', start);
        Assert.True(open > 0, $"'{signature}' has no body");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"'{signature}' has an unbalanced body");
        return "";
    }

    private static IEnumerable<string> ProductionSources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f)
                            .Split('/', '\\')
                            .SkipLast(1)
                            .All(seg => !seg.StartsWith('.')
                                        && !seg.Equals("tests", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

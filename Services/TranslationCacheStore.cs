using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>
/// The bounded LRU that <see cref="CachingTranslator"/> used to keep inside itself — a
/// <c>string → string</c> map with an eviction order — and, since E4.S2, the file that map survives
/// a restart in. It moved out (E4.S1) for one reason: a decorator can only wrap one inner
/// translator, so "one shared cache behind the read chain, the read-once chain and the write chain"
/// (§8.2, decision F) has to be one shared <b>store</b> behind three thin decorators. E4.S4 handed
/// the same instance to all three, inside <see cref="TranslationChains"/>' builders.
///
/// <para><b>What it does not know.</b> The key format — <c>source|target|text.Trim()</c> — and the
/// rule that failure placeholders (anything starting with <c>(</c>, I4) are never cached both stay in
/// <see cref="CachingTranslator"/> (§3.1's responsibility table). That is not tidiness: the "(" rule
/// upstream of one shared store cannot be forgotten by one of three decorators, whereas the same rule
/// duplicated in three places can. Handed a placeholder directly, this class will store it
/// <b>in memory</b> — the two FILE paths refuse it, which is the architect's E4.S2 ruling and is
/// belt-and-braces behind <c>IsCacheable</c>, never instead of it.</para>
///
/// <para><b>The order is the file format.</b> The front of <see cref="_order"/> is the
/// most-recently-used entry and the back is the one eviction takes; the file is written MRU-first so
/// the order survives a restart. An <c>AddLast</c> where this says <c>AddFirst</c> is invisible to
/// any test that only fills a fresh store and evicts the wrong half of a long session's cache — which
/// is why <c>TranslationCacheStoreTests</c>' two MRU cases drive a READ between the writes: they are
/// what turns that inversion red.</para>
///
/// <para><b>A read reorders what a write persists, deliberately.</b> <see cref="TryGet"/> promotes,
/// so a calm LIVE session of pure hits changes the order this file records while storing nothing —
/// and only a <b>store</b> queues a save (AC 2, ruling E4-b). The on-disk order therefore lags the
/// reads. Do not "fix" that by saving on reads: that is a 300 KB write every LIVE tick.</para>
///
/// <para><b>I11 — this file holds the user's chat text.</b> It is never named in the log, never
/// included in the error report, and its path is never written anywhere a report could pick it up.
/// That is also why it is written un-indented: nobody is meant to read it.</para>
///
/// <para>UI-free (I2) and, until something asks it a question, I/O-free (I10): the constructor opens
/// nothing — the first <b>miss</b> of the process is what reads the file.</para>
/// </summary>
internal sealed class TranslationCacheStore
{
    /// <summary>§8.2's <c>version</c>. A <b>forward guard, not a migration</b> — exactly
    /// <c>ProviderStateStore.SchemaVersion</c>'s contract (<c>:49-58</c>): anything but this reads as
    /// an empty cache, and there is no <c>Migrate</c> here and must not be one. Unlike
    /// <c>settings.json</c> this file holds nothing the user typed and everything is re-earnable by
    /// one translation.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>The most this file may be before it is refused <b>unread</b>. It exists so a
    /// hand-edited or corrupted 400 MB file cannot become a startup hang: the read happens under
    /// <see cref="_gate"/>, which the next translation waits on, and the size is checked on the
    /// <c>FileInfo</c> before a byte is opened.
    ///
    /// <para><b>Four megabytes, and it was one until E4.S3 measured the file.</b> §8.2 estimated
    /// ~150 B an entry ⇒ ≈300 KB for a full 2000, which is what a megabyte was "generous" against.
    /// The estimate counted UTF-8 Cyrillic at two bytes a character; the file was then written with
    /// <c>JsonSerializer</c>'s DEFAULT encoder, which escapes every non-ASCII character as
    /// <c>\uXXXX</c> — <b>six</b> bytes — so a realistic full cache measured <b>502 B an entry, 981 KB
    /// at 2000 entries</b> (U8, 2026-09-07, chat lines averaging 68 characters). A 1 MB bound left
    /// 4% of headroom and crossed at ≈2088 entries: the very users this cache is for would have had
    /// it silently refused, with no error and no log line — the worst shape of failure this file
    /// has.</para>
    ///
    /// <para><b>E4.S5 then took the encoder itself</b> (see <see cref="Options"/>): the same 2000
    /// entries, written as UTF-8, are <b>277 B an entry and 541 KB</b> — the file that forced this
    /// bound up now fits inside the megabyte it broke. Not §8.2's ~150 either, and that is the
    /// honest half of the story: only the Russian key was ever escaped, while the English value, the
    /// 33-byte timestamp and the field names are ~130 B of every row and no encoder touches them.
    /// The bound STAYS at four megabytes rather than following the file down — it is the guard
    /// against a corrupt or hand-edited monster, not a budget for the cache, and headroom is what
    /// E4.S3 learned to keep. ×7.6 a measured full cache, crossing at ≈15 000 entries.</para></summary>
    // [MEASURED] E4.S3 / U8, 2026-09-07: 502 B/entry, 981 KB at 2000 entries, 17.8 ms to load.
    // [MEASURED] E4.S5, 2026-09-07: 277 B/entry, 541 KB at 2000, 9.7 ms — same harness, UTF-8 encoder.
    private const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>Un-indented on purpose (unlike <c>provider-state.json</c>, which a user is asked to
    /// zip and send): this file is chat text and nobody reads it by hand — I11 says it may not even
    /// reach the error report. Indenting 2000 entries would roughly double it for no reader. One
    /// options object for the life of the process, per the footprint rule.
    ///
    /// <para><b>The encoder is the other half of the size, and it is E4.S5's whole story.</b>
    /// <c>JsonSerializer</c>'s DEFAULT encoder escapes every non-ASCII character as <c>\uXXXX</c> —
    /// <b>six</b> bytes for a Cyrillic letter that is two in UTF-8 — which is what made a realistic
    /// full cache measure 502 B an entry instead of the ≈150 §8.2 estimated (U8). Writing it with
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> puts the Cyrillic in the file as
    /// UTF-8 and divides a Russian line by three.</para>
    ///
    /// <para>"Unsafe" names one hazard and it is not one this file has: the relaxed encoder stops
    /// escaping <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>'</c>, which matters only where JSON is
    /// interpolated into HTML or a script. <b>This file is written and read by this app alone</b>,
    /// parsed by <see cref="JsonDocument"/> and never rendered anywhere — and the JSON stays
    /// strictly valid either way, control characters and quotes still escaped, so an old
    /// <c>\uXXXX</c> file keeps loading (the parser cannot tell the two forms apart).</para></summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Where <c>translation-cache.json</c> is read from / written to (IS-2). Tests point
    /// this at a temp directory through <c>TempCache</c>, and <c>TestCacheRedirect</c>'s
    /// <c>[ModuleInitializer]</c> points it at one before any case runs. Null = the real file.</summary>
    internal static string? PathOverride;

    /// <summary>The default location, computed on demand rather than in a static field: this type's
    /// static initialiser must do no I/O and must cost nothing at type-load (I10) — the same shape,
    /// and the same reason, as <c>ProviderGates.DefaultPath</c> (<c>:81-86</c>).</summary>
    private static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PWRUHelper", "translation-cache.json");

    /// <summary>The file a persistent store will actually use. Exposed so the suite's own guard case
    /// can assert it is never the developer's real <c>%AppData%</c> path — the guard
    /// <c>LoggingTests.The_test_run_never_writes_to_the_real_AppData_log</c> and
    /// <c>ProviderStateStoreTests.A_write_never_reaches_the_real_AppData_state_file</c> already
    /// are, for the two files this suite has already poisoned.</summary>
    internal static string CachePath => PathOverride ?? DefaultPath;

    /// <summary>The debounce window, as a test seam — <c>ProviderGates.SaveDebounceMs</c>'s shape
    /// (<c>:146</c>), and for its reason: CI-3 forbids a case that waits real seconds to prove a
    /// coalesce. A test that moves it restores it (<c>TempCache.Dispose</c> does).</summary>
    internal static int SaveDebounceMs = TranslationPolicy.CacheSaveDebounceMs;

    private readonly int _capacity;
    private readonly bool _persistent;
    private readonly bool _offlineEnabled;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();   // front = most-recently-used

    /// <summary>Serialises the FILE, not the map. Separate from <see cref="_gate"/> because a full
    /// cache is ≈300 KB and holding the map's lock across that write would stall every translation
    /// for the length of a disk write — the one place this store may not copy
    /// <c>ProviderGates</c>, whose file is a few hundred bytes.
    ///
    /// <para><b>The lock order is that there is none: the two are never nested.</b>
    /// <see cref="SaveNow"/> takes <see cref="_gate"/>, snapshots, <b>releases it</b>, and only then
    /// takes this one; nothing anywhere takes <see cref="_gate"/> while holding this. Neither lock
    /// is ever held while the other is acquired, in either direction, so the pair cannot deadlock —
    /// not the debounce timer against a <see cref="SaveNow"/> from the close path, and not a load
    /// against a write. Keep it that way: the moment one of them is taken inside the other, the
    /// order becomes a rule somebody has to remember.</para></summary>
    private readonly object _io = new();

    private string? _path;
    private bool _loaded;
    private bool _keepFile;      // the file is there and this process could not read it
    private bool _savePending;
    private System.Threading.Timer? _saveTimer;

    /// <summary>The snapshot counter, under <see cref="_gate"/>, and the newest snapshot a write has
    /// committed to, under <see cref="_io"/>. Two <see cref="SaveNow"/> calls really can be in
    /// flight at once — the debounce timer and <c>OnClosing</c>'s flush, with a <see cref="Store"/>
    /// between them — and because <see cref="_gate"/> is released before <see cref="_io"/> is taken,
    /// the OLDER snapshot could otherwise reach the disk second and silently drop what the newer one
    /// carried. Numbering the snapshots is what makes the two locks safe to keep un-nested.</summary>
    private long _snapshotSeq;
    private long _writtenSeq;

    /// <summary>Capacity defaults to §8.2's 2000, which since E4.S4 is what the app's one store is
    /// built with; the legacy <see cref="CachingTranslator"/> constructor still passes its own 500
    /// (<see cref="TranslationPolicy.CacheCapacityToday"/>) to the private store it makes for a
    /// caller that supplied none.
    ///
    /// <para><paramref name="persistent"/> is what makes an instance touch the disk at all, and it
    /// defaults to <b>false</b> for a reason that is not taste: a second persistent store pointed at
    /// the same file would spend the session overwriting the first one's entries. There is exactly
    /// one, <c>TranslationChains.Cache</c>, and all three chains decorate it (E4.S4); the default is
    /// what keeps the next store somebody constructs from silently becoming a second writer, and
    /// <c>TranslationCachePersistenceTests</c> scans for it.</para>
    ///
    /// <para><paramref name="offlineEnabled"/> is AC 6's drop rule as a <b>parameter</b>, not a
    /// setting: <c>OfflineFallbackEnabled</c> arrives in E6.S3 (ruling R-7) and the Bergamot tier in
    /// E8, so in A.2 it is structurally false and every <c>"p":"bergamot"</c> entry is dropped on
    /// load. E8 passes <c>settings.OfflineFallbackEnabled</c> here and nothing else changes.</para>
    ///
    /// <para>One constructor, and it has to stay one:
    /// <c>TranslationCacheStoreTests.The_capacity_comes_from_the_policy_and_not_from_a_second_literal</c>
    /// reads it through <c>GetConstructors().Single()</c>.</para></summary>
    internal TranslationCacheStore(int capacity = TranslationPolicy.CacheCapacity,
                                   bool persistent = false,
                                   bool offlineEnabled = false)
    {
        _capacity = Math.Max(1, capacity);   // a zero would make every store a no-op
        _persistent = persistent;
        _offlineEnabled = offlineEnabled;
        _map = new Dictionary<string, LinkedListNode<Entry>>(_capacity);
    }

    /// <summary>Entries held right now. Exists so tests assert on the store instead of reaching for
    /// <c>_map</c> by reflection — a test that does that breaks on the next refactor. It does
    /// <b>not</b> trigger the lazy load: a count is not a question about a translation (I10, AC 1 —
    /// the trigger is a miss and only a miss).</summary>
    internal int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>A hit also promotes: reading an entry makes it the most-recently-used one, which is
    /// what makes this an LRU rather than a first-in-first-out queue. A miss yields <c>""</c>, never
    /// null — and, once per process, loads the file (see <see cref="EnsureLoaded"/>).</summary>
    internal bool TryGet(string key, out string value)
    {
        lock (_gate)
        {
            if (Lookup(key, out value)) return true;
            EnsureLoaded();                       // the first MISS of the process reads the file
            return Lookup(key, out value);
        }
    }

    /// <summary>Stores or overwrites, promotes the entry to most-recently-used, evicts the tail once
    /// the capacity is exceeded, and schedules a debounced write (AC 2).
    ///
    /// <para><paramref name="providerId"/> is the <c>"p"</c> of §8.2's schema and is <c>""</c> for
    /// everything A.2 writes: the store is handed a key and a value and does not know which tier
    /// answered, and <see cref="CachingTranslator"/> does not know either — the chain does.
    /// E7.S3 already has to read <c>ChainTranslator.LastOutcome.ProviderId</c> for the status chip
    /// and is where a real id could reach this parameter. The field is written from day one because
    /// retrofitting one into a file people already have is how a version bump gets earned for
    /// nothing, and because the Bergamot drop rule (AC 6) reads it.</para>
    ///
    /// <para>It deliberately does NOT load first: a store is not a question, and letting it load
    /// would put the file on the path of the very first translation instead of on its first miss.
    /// A load arriving after some stores keeps this session's values (see
    /// <see cref="EnsureLoaded"/>).</para></summary>
    internal void Store(string key, string value, string? providerId = null)
    {
        lock (_gate)
        {
            var entry = new Entry(key, value, providerId ?? "", DateTimeOffset.UtcNow);

            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = entry;
                _order.Remove(existing);
                _order.AddFirst(existing);
            }
            else
            {
                var node = new LinkedListNode<Entry>(entry);
                _order.AddFirst(node);
                _map[key] = node;
                Trim();
            }

            QueueSave();
        }
    }

    /// <summary>
    /// Write whatever is pending now and cancel the timer: the debounce callback's own body, and the
    /// call <c>MainWindow.OnClosing</c> makes through <c>TranslationChains.FlushCache()</c> so a
    /// session's last five seconds of translations are not lost. <c>ProviderGates.Flush</c>
    /// (<c>:240</c>) is the same method for the gate file, and this is the seam every persistence
    /// test uses instead of waiting five real seconds (CI-3).
    ///
    /// <para>Nothing pending means nothing to do — a session of pure cache hits closes without
    /// touching the disk. Best-effort: the write swallows its own I/O failures, and this swallows
    /// anything else, because it runs on <c>OnClosing</c> ABOVE the settings save and outside its
    /// try — <c>ProviderGates.Flush</c>'s outer catch exists for exactly that reason and this is
    /// the same path.</para>
    /// </summary>
    internal void SaveNow()
    {
        try
        {
            string path;
            long seq;
            List<Entry> snapshot;

            lock (_gate)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                if (!_savePending) return;
                _savePending = false;

                // The file is not ours to replace: it is there and this process could not read it
                // (see ReadFile). Writing from a map that holds only this session's handful would
                // erase what the user has earned — ProviderGates' `_keepFile` rule (:260),
                // and not persisting this session is the cheaper failure by far.
                if (_keepFile) return;

                seq = ++_snapshotSeq;
                path = ResolvePath();
                snapshot = new List<Entry>(_order);   // MRU-first, which IS the file order
            }

            WriteFile(seq, path, snapshot);
        }
        catch
        {
            // Persistence must never be able to fail a translation or a window close.
        }
    }

    /// <summary>
    /// <b>Amendment A10 — "Clear cache".</b> Empties the map and deletes the file, and answers with
    /// the number of entries that were removed. Reached only through
    /// <c>TranslationChains.ClearCache()</c>: the code-behind names a chain, never a store.
    ///
    /// <para><b>The order is the whole of it.</b> The pending save is cancelled and the map emptied
    /// under <see cref="_gate"/>, and the snapshot counter is bumped there too — so a
    /// <see cref="SaveNow"/> that had already snapshotted and is waiting on <see cref="_io"/> finds
    /// its sequence stale and writes nothing. Then the file is deleted under <see cref="_io"/>, so
    /// nothing can land between the two. Without that, A10's own scenario — a debounced save queued
    /// by the translation the player made a second before pressing the button — would resurrect the
    /// file they just cleared.</para>
    ///
    /// <para><b>It loads first, and that is deliberate.</b> The store reads lazily, on the first MISS
    /// (I10), so a player who clears the cache before translating anything would otherwise be told
    /// "0 removed" over a file holding a thousand lines of their own chat. A one-off synchronous
    /// read (≈10 ms for a full cache, measured in E4.S3/E4.S5) on an explicit gesture is the only
    /// way the sentence can be true.</para>
    ///
    /// <para><see cref="_keepFile"/> is cleared too: it means "this process could not READ the file,
    /// so do not overwrite what the user has earned" — and the user has just asked for exactly that
    /// file to go. The delete is best-effort like every other I/O here; a cache is a convenience and
    /// may never be the reason a click fails (R-01).</para>
    /// </summary>
    internal int Clear()
    {
        int removed;
        long seq;
        string path;

        lock (_gate)
        {
            EnsureLoaded();                       // so the count is of everything, file included

            _savePending = false;
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            removed = _map.Count;
            _map.Clear();
            _order.Clear();

            _loaded = true;                       // there is nothing left on disk to load
            _keepFile = false;                    // the user asked for the file to go
            seq = ++_snapshotSeq;                 // an older snapshot may no longer write
            path = ResolvePath();
        }

        lock (_io)
        {
            _writtenSeq = seq;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                // The half-written swap file of an interrupted save, if there is one: leaving it
                // would be a copy of the user's chat text under a name nothing ever reads again.
                var tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception)
            {
                // Held open by an AV or a sync agent — the map is empty either way, and the next
                // save rewrites the file from it.
            }
        }

        return removed;
    }

    /// <summary>
    /// IS-4's second half, for tests: drop the pending save and the timer with it. Without it a
    /// write queued by one case lands during the next one — in the next case's temp directory, or
    /// after <see cref="PathOverride"/> has gone back to the developer's real <c>%AppData%</c>.
    /// <c>ProviderGates.CancelPendingSave</c> (<c>:415</c>) is the precedent, and it is why that
    /// registry's reset runs it first.
    /// </summary>
    internal void CancelPendingSave()
    {
        lock (_gate)
        {
            _savePending = false;                 // a callback already past its Change() sees this
            var timer = _saveTimer;
            _saveTimer = null;
            timer?.Dispose();
        }
    }

    // ---- the map ------------------------------------------------------------------------------

    /// <summary>The lookup half of <see cref="TryGet"/>, so the miss path can run it twice — once
    /// before the lazy load and once after — without duplicating the promotion. Caller holds
    /// <see cref="_gate"/>.</summary>
    private bool Lookup(string key, out string value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);                // touch → most-recently-used
            value = node.Value.Value;
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>Evict from the tail until the capacity holds. Caller holds <see cref="_gate"/>.</summary>
    private void Trim()
    {
        while (_map.Count > _capacity)
        {
            var lru = _order.Last!;               // least-recently-used
            _order.RemoveLast();
            _map.Remove(lru.Value.Key);
        }
    }

    // ---- the file -----------------------------------------------------------------------------

    /// <summary>
    /// Read <c>translation-cache.json</c> once, on the <b>first miss of the process</b> (AC 1, I10)
    /// — never from the constructor, never from <see cref="Store"/>, never from <see cref="Count"/>.
    /// Caller holds <see cref="_gate"/>.
    ///
    /// <para><b>"Off the UI thread", honestly</b> — ruling E2-e as corrected, copied from
    /// <c>ProviderGates.EnsureLoaded</c> (<c>:166</c>) rather than re-derived, because E2.S4's story
    /// got it wrong first and its review corrected it. The read runs <b>synchronously on the calling
    /// thread</b>, inside the first miss. AC 1 is satisfied because that miss happens inside an
    /// awaited translation that <c>HttpProviderCore</c> has already <c>ConfigureAwait(false)</c>-ed
    /// — not because anybody posted work to a pool. <b>Do not add a <c>Task.Run</c></b>: it would
    /// make the first translation race the load and would need a second lock.</para>
    ///
    /// <para>Entries are appended at the <b>tail</b> in file order. Into the usual empty store that
    /// reproduces the file exactly, MRU-first — an <c>AddFirst</c> loop would reverse it, which is
    /// the shape of mistake that costs a user their hottest entries at the next eviction. Into a
    /// store some session already wrote to, it also puts this session's entries ahead of the file's,
    /// which is right: they are newer, and a key both have keeps the live value.</para>
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded || !_persistent) return;
        _loaded = true;   // one attempt per process: a failure may not re-read on every miss

        foreach (var entry in ReadFile(ResolvePath()))
        {
            if (_map.ContainsKey(entry.Key)) continue;   // this session's value is the fresher one
            _map[entry.Key] = _order.AddLast(entry);
        }

        Trim();           // a file written by a build with a larger capacity is cut to ours
    }

    /// <summary>The path this instance uses, resolved <b>once, on first use</b>. Not in the
    /// constructor: E4.S4 builds the store on the startup path and
    /// <c>Environment.GetFolderPath</c> there is exactly the cost I10 refuses. Not re-read per call
    /// either — a store must not follow a <see cref="PathOverride"/> that moved under it mid-session,
    /// which is precisely how a straggler debounce lands in another test's temp directory. Caller
    /// holds <see cref="_gate"/>.</summary>
    private string ResolvePath() => _path ??= CachePath;

    /// <summary>
    /// The file, or nothing. Missing, oversize, truncated, wrong-rooted, future-versioned, an entry
    /// without a <c>k</c> or a <c>v</c> — every one of them costs that entry or the whole file, and
    /// none of them throws (AC 4). Broad by design: a cache is a convenience and may never be the
    /// reason the app fails to translate.
    ///
    /// <para><b>One of those failures is not the file's fault, and that one is kept.</b> A file this
    /// process could not OPEN — an AV or a sync agent holding it for the 50 ms of the first miss —
    /// latches <see cref="_keepFile"/> and this session simply does not persist, mirroring
    /// <c>ProviderGates</c> (<c>:260</c>). Every other failure IS ours to replace, because there the
    /// rewrite is the repair: a corrupt, truncated or future-versioned file is one whose content
    /// this build can do nothing with, and what it holds is a translation, not a standing pause.</para>
    /// </summary>
    private List<Entry> ReadFile(string path)
    {
        var entries = new List<Entry>();
        string text;

        try
        {
            // FileInfo rather than File.Exists so the size is known before anything is read: a
            // directory, a missing file and an absurd one all leave here without an allocation.
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return entries;

            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            // Could not read a file that is there — a sharing violation, a permission change, a
            // disk error. NOT a reason to overwrite it from an empty map: that would cost the user
            // the 2000 entries they earned to save the handful this session will.
            _keepFile = true;
            return entries;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return entries;

            // A version this build does not understand is a newer build's file: empty cache, no
            // migration (see SchemaVersion). Unlike provider-state.json there is no "keep the file"
            // fork — the next save simply rewrites it, and what it would have preserved is a
            // translation, not a standing pause.
            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v)
                || v != SchemaVersion) return entries;

            if (!root.TryGetProperty("entries", out var rows)
                || rows.ValueKind != JsonValueKind.Array) return entries;

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;

                var key = Text(row, "k");
                var value = Text(row, "v");
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;

                // I4 on the way IN — the architect's E4.S2 ruling. CachingTranslator.IsCacheable is
                // the authoritative rule (§3.1) and in principle no placeholder ever reached this
                // file; a file written by an older or hand-edited build is where one could, and a
                // cached "(rate-limited …)" outlives the condition it describes, which is the exact
                // complaint that started this investigation.
                if (value.StartsWith('(')) continue;

                var provider = Text(row, "p") ?? "";

                // §8.2 concern #2: an offline translation is only trustworthy while the offline
                // tier is the one the user chose to run. With OfflineFallbackEnabled off — which in
                // A.2 it structurally is — its entries are dropped rather than served.
                if (!_offlineEnabled
                    && string.Equals(provider, ProviderIds.Bergamot, StringComparison.Ordinal)) continue;

                entries.Add(new Entry(key, value, provider, Date(row, "t")));
            }
        }
        catch (Exception)
        {
            // Truncated JSON, a number where an object belongs, a hand edit that lost a brace — all
            // the same answer: no cache this session, and nothing surfaces to the user. A partially
            // enumerated array is dropped whole rather than half-kept.
            entries.Clear();
        }

        return entries;
    }

    /// <summary>
    /// The whole file, atomically and best-effort — <c>SettingsService.Save</c>
    /// (<c>:184-196</c>) and <c>ProviderStateStore.Save</c> (<c>:205-249</c>) are the two prior
    /// copies of this shape and this is deliberately not a variant of them: create the directory,
    /// write <c>path + ".tmp"</c>, <c>File.Replace</c> it over the target (or <c>File.Move</c> when
    /// there is none — <c>Replace</c> throws without one), all inside one <c>try/catch</c> so a
    /// read-only disk costs nothing.
    /// </summary>
    private void WriteFile(long seq, string path, List<Entry> entries)
    {
        lock (_io)
        {
            // Only a NEWER snapshot may write. Two SaveNow calls can queue here — the debounce
            // timer and OnClosing's flush — and _gate is released before this lock is taken, so
            // without this the older of the two could land second and drop the store that happened
            // between them. See _snapshotSeq.
            if (seq <= _writtenSeq) return;
            _writtenSeq = seq;

            try
            {
                var rows = new List<Row>(entries.Count);
                foreach (var e in entries)
                {
                    // I4 on the way OUT — the other half of the architect's ruling. A value that
                    // reached the map some other way (Store is dumb by design, §3.1) must not reach
                    // the file, because the file is where it would outlive its own restart.
                    if (e.Value.StartsWith('(')) continue;

                    // Invariant culture and UTC, always: an fr-FR box once wrote a timestamp with
                    // commas and no other machine could read it back (E2.S6's review made this a
                    // rule).
                    rows.Add(new Row(e.Key, e.Value, e.ProviderId,
                        e.StoredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
                }

                var json = JsonSerializer.Serialize(new CacheFile(SchemaVersion, rows), Options);

                // Empty for a bare filename, null for a volume root — CreateDirectory throws on
                // both, and the throw would land in the catch below and kill every save for good.
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception)
            {
                // Not writable — this session's translations simply do not survive the restart.
                // Never a failed translation, and never a dialog: the cache is disposable (R-01).
            }
        }
    }

    /// <summary>
    /// A store happened, so the file is stale. Debounced by
    /// <see cref="TranslationPolicy.CacheSaveDebounceMs"/> and coalesced: N stores inside the window
    /// produce <b>one</b> write, of the state at flush time — <c>ProviderGates.QueueSave</c>
    /// (<c>:217-230</c>), one <c>System.Threading.Timer</c> reused for the life of the store. A
    /// <c>System.Threading.Timer</c> and not a <c>DispatcherTimer</c> because <c>Services/</c> is
    /// UI-free (I2), and the window is fixed from the FIRST pending store rather than restarted by
    /// each one, so a busy LIVE minute cannot postpone the write indefinitely. Caller holds
    /// <see cref="_gate"/>.
    /// </summary>
    private void QueueSave()
    {
        if (!_persistent) return;
        if (_savePending) return;                 // already inside a window: coalesced
        _savePending = true;
        _saveTimer ??= new System.Threading.Timer(
            static s => ((TranslationCacheStore)s!).SaveNow(), this, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
    }

    // ---- one field at a time, none of which throws ---------------------------------------------

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    /// <summary>The <c>t</c> of an entry. Round-trip, invariant, and a missing or unreadable one is
    /// simply <c>default</c>: nothing reads the timestamp yet (it is there for the eventual "how
    /// old is this translation" question and for a future age-based sweep), so a bad one may not
    /// cost the entry it is attached to.</summary>
    private static DateTimeOffset Date(JsonElement row, string name) =>
        Text(row, name) is { Length: > 0 } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var v) ? v : default;

    // ---- shapes --------------------------------------------------------------------------------

    /// <summary>One cached translation, in memory. E4.S1 kept a bare
    /// <c>KeyValuePair&lt;string, string&gt;</c> here on purpose — a field no code writes looks
    /// wired and is not — and E4.S2 is the story that gives <c>p</c> and <c>t</c> something to
    /// be.</summary>
    private sealed record Entry(string Key, string Value, string ProviderId, DateTimeOffset StoredAt);

    /// <summary>One row of the file. The short names are §8.2's schema and they are what makes 2000
    /// entries ≈300 KB instead of noticeably more; the camelCase policy maps <c>K</c> → <c>k</c>.
    /// <c>T</c> is a string rather than a <see cref="DateTimeOffset"/> so the invariant round-trip
    /// format is written here, once, and not left to a serialiser setting somebody changes.</summary>
    private sealed record Row(string K, string V, string P, string T);

    /// <summary>The root object, as a type rather than an anonymous one so the naming policy and the
    /// shape are both obvious at a glance — <c>ProviderStateStore.StateFile</c>'s reasoning.</summary>
    private sealed record CacheFile(int Version, IReadOnlyList<Row> Entries);
}

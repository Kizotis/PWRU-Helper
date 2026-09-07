using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using PWRUHelper.Services;
using Xunit;
using Xunit.Abstractions;

namespace PWRUHelper.Tests;

/// <summary>
/// E4.S3 / U8 — the measurement harness, and <b>only</b> a measurement harness. It answers one
/// question: what does a full <c>translation-cache.json</c> cost to write, to load and to hold, so
/// that §8.2's "the capacity is the knob" can be turned with a number instead of an estimate.
///
/// <para><b>It reports and it never fails on a duration</b> (CI-3's single carve-out, TP-START-03).
/// The only assertions below are safety ones — that the run stayed inside its temp directory and
/// that the file was actually read — because a spike that goes red on a slow build agent teaches
/// the team to ignore it.</para>
///
/// <para><b>Why it is invisible to a normal <c>dotnet test</c>.</b> The method bodies compile on
/// every build, so this file cannot rot behind a flag; the <c>[Fact]</c> attributes only exist when
/// <c>PWRU_SPIKE=1</c> is in the environment (see <c>PWRUHelper.Tests.csproj</c>, which turns that
/// variable into the <c>PWRU_SPIKE</c> define). CI therefore discovers <b>zero</b> cases here — the
/// suite count and its two seconds are unchanged — and the owner runs it on demand with
///
/// <code>set PWRU_SPIKE=1 &amp;&amp; dotnet test tests/PWRUHelper.Tests --filter Category=Spike --logger "console;verbosity=detailed"</code>
///
/// A <c>[Fact(Skip=…)]</c> would have been the obvious shape and cannot work: <c>Skip</c> is a
/// compile-time constant, so an environment variable can never lift it, and the skipped cases would
/// still show up in every CI summary.</para>
///
/// <para><b>Isolation</b> (IS-2/IS-3): every case runs inside a <see cref="TempCache"/>, the
/// debounce window is stretched so no timer writes underneath a measurement, and each case asserts
/// that the developer's real <c>%AppData%\PWRUHelper\translation-cache.json</c> is exactly as it was
/// — this is the one class in the suite that writes megabytes, and it writes them to <c>%TEMP%</c>.</para>
/// </summary>
[Trait("Category", "Spike")]
public class CacheLoadSpike
{
    /// <summary>Timed repetitions per number, after one warm-up that is discarded: the first pass
    /// pays for JIT, for the file appearing in the OS cache and for the first <c>JsonDocument</c>
    /// of the process. Seven, so the median has three on each side.</summary>
    private const int Runs = 7;

    /// <summary>The debounce window while a case runs. A store schedules a save, and a 5-second
    /// timer firing in the middle of a load measurement would write 900 KB underneath it. Restored
    /// by <see cref="TempCache.Dispose"/>, which is why it may be set this bluntly.</summary>
    private const int NoTimerMs = 10 * 60 * 1000;

    private readonly ITestOutputHelper _out;

    public CacheLoadSpike(ITestOutputHelper output) => _out = output;

    // ---- the curve: 500 / 2000 / 5000 ----------------------------------------------------------

#if PWRU_SPIKE
    [Fact]
#endif
    public void The_cost_of_a_full_cache_at_500_2000_and_5000_entries()
    {
        var appData = RealFileState();

        _out.WriteLine(Machine());
        _out.WriteLine("");
        _out.WriteLine("| entries | file KB | B/entry | ctor ms | load ms (median / min / max) | save ms | managed KB | working-set KB |");
        _out.WriteLine("|---|---|---|---|---|---|---|---|");

        foreach (var n in new[] { 500, 2000, 5000 })
        {
            using var temp = new TempCache();
            TranslationCacheStore.SaveDebounceMs = NoTimerMs;

            var entries = Entries(n);
            var save = WriteThroughTheStore(temp, entries);
            var bytes = new FileInfo(temp.Path).Length;

            var ctor = Repeat(() =>
            {
                var sw = Stopwatch.StartNew();
                var store = new TranslationCacheStore(capacity: n);
                sw.Stop();
                GC.KeepAlive(store);
                return sw.Elapsed.TotalMilliseconds;
            });

            var load = new List<double>();
            long managed = 0, working = 0;
            var loaded = 0;

            for (var i = 0; i <= Runs; i++)
            {
                // A fresh store every time: the load happens once per process per store, and a
                // second TryGet on the same instance measures nothing but a dictionary miss.
                var store = new TranslationCacheStore(capacity: n, persistent: true);

                var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
                var wsBefore = Working();

                var sw = Stopwatch.StartNew();
                store.TryGet("ru|en|это ключ, которого в файле нет", out _);   // the first MISS reads the file
                sw.Stop();

                var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
                var wsAfter = Working();

                if (i == 0) { GC.KeepAlive(store); continue; }                 // warm-up, discarded

                load.Add(sw.Elapsed.TotalMilliseconds);
                managed = heapAfter - heapBefore;
                working = wsAfter - wsBefore;
                loaded = store.Count;

                store.CancelPendingSave();
                GC.KeepAlive(store);
            }

            // Reported, never asserted (CI-3): a file over MaxBytes is refused UNREAD, and the
            // 0.02 ms that costs would otherwise be published as a wonderfully fast load. The
            // second case is where that bound is measured; this row just refuses to lie about it.
            if (loaded == 0)
            {
                _out.WriteLine($"| {n} | {bytes / 1024.0:N0} | {bytes / (double)n:N0} | — | REFUSED UNREAD "
                               + $"(over MaxBytes = {MaxBytes():N0} B) | — | — | — |");
                continue;
            }

            _out.WriteLine(string.Format(
                "| {0} | {1:N0} | {2:N0} | {3:F3} | {4:F2} / {5:F2} / {6:F2} | {7:F2} | {8:N0} | {9:N0} |",
                loaded, bytes / 1024.0, bytes / (double)n, ctor,
                Median(load), load.Min(), load.Max(), save,
                managed / 1024.0, working / 1024.0));
        }

        _out.WriteLine("");
        _out.WriteLine("Warm-file numbers: the file was written by this process moments before it was read, so it is");
        _out.WriteLine("in the OS cache. The cold, Defender-scanned number is the owner's half of AC 1.");

        AssertRealFileUntouched(appData);
    }

    // ---- the read bound, 1 MB until this spike (E4.S2's deferred review finding) ---------------

#if PWRU_SPIKE
    [Fact]
#endif
    public void Where_a_realistic_file_crosses_the_read_bound()
    {
        var appData = RealFileState();
        var max = MaxBytes();

        using var temp = new TempCache();
        TranslationCacheStore.SaveDebounceMs = NoTimerMs;

        // One reference file, one arithmetic answer: the row size is flat (the four fields are the
        // same shape for every entry), so bytes-per-entry from a 2000-entry file is the crossing.
        // This is the case that moved MaxBytes from 1 MB to 4: at 1 MB the crossing was ≈2088
        // entries, i.e. 4% above what a FULL cache at capacity 2000 actually weighs.
        var entries = Entries(2000);
        WriteThroughTheStore(temp, entries);
        var bytes = new FileInfo(temp.Path).Length;
        var perEntry = bytes / 2000.0;
        var crossing = (int)(max / perEntry);

        _out.WriteLine($"MaxBytes                 : {max:N0} B ({max / 1024.0 / 1024.0:F2} MB)");
        _out.WriteLine($"2000 realistic entries   : {bytes:N0} B ({bytes / 1024.0:N0} KB), {perEntry:N0} B/entry");
        _out.WriteLine($"§8.2's own estimate      : ~150 B/entry ⇒ ~300 KB at 2000");
        _out.WriteLine($"crossing                 : ≈ {crossing:N0} entries at this average length");
        _out.WriteLine($"average source line      : {entries.Average(e => e.Key.Length - e.Key.IndexOf('|') - 4):F0} characters");
        _out.WriteLine($"headroom at capacity 2000: ×{max / (double)bytes:F2}");

        // The demonstration, because arithmetic about a bound is not the bound. A file one entry
        // over is refused UNREAD, and the store that asked comes back empty — which is exactly the
        // failure mode this bound has to be sized away from: not an error, a silently empty cache.
        var over = (int)(crossing * 1.05) + 1;
        using (var big = new TempCache())
        {
            TranslationCacheStore.SaveDebounceMs = NoTimerMs;
            WriteThroughTheStore(big, Entries(over));
            var overBytes = new FileInfo(big.Path).Length;

            var store = new TranslationCacheStore(capacity: over, persistent: true);
            store.TryGet("ru|en|нет такого ключа", out _);
            var count = store.Count;
            store.CancelPendingSave();

            _out.WriteLine($"a {over:N0}-entry file      : {overBytes:N0} B → the store loaded {count} entries");
            Assert.True(overBytes > max, "the over-bound file did not actually exceed MaxBytes");
            Assert.Equal(0, count);
        }

        AssertRealFileUntouched(appData);
    }

    // ---- AC 2 / I10, on the real startup path ---------------------------------------------------

#if PWRU_SPIKE
    [Fact]
#endif
    public void Building_the_chains_over_a_full_file_still_reads_nothing()
    {
        var appData = RealFileState();

        using var temp = new TempCache();
        TranslationCacheStore.SaveDebounceMs = NoTimerMs;
        WriteThroughTheStore(temp, Entries(2000));
        TranslationChains.ResetCacheForTests();

        try
        {
            var settings = new AppSettings();

            var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            var sw = Stopwatch.StartNew();
            var read = TranslationChains.BuildRead(settings);
            var write = TranslationChains.BuildWrite(settings);
            sw.Stop();
            var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

            GC.KeepAlive(read);
            GC.KeepAlive(write);

            _out.WriteLine($"building all chains over a full file: {sw.Elapsed.TotalMilliseconds:F3} ms, "
                           + $"{(heapAfter - heapBefore) / 1024.0:N0} KB managed, {TranslationChains.Cache.Count} entries read");

            // AC 2, measured rather than asserted: the file is there, full, and the store that the
            // window's constructor builds has not opened it. `Count` deliberately does not trigger
            // the lazy load, so this reads "constructed, not read" — TP-CACHE-10's claim, made on
            // the path MainWindow actually walks.
            Assert.Equal(0, TranslationChains.Cache.Count);
        }
        finally
        {
            TranslationChains.ResetCacheForTests();
        }

        AssertRealFileUntouched(appData);
    }

    // ---- the synthetic file ---------------------------------------------------------------------

    /// <summary>Store every entry and flush, timing the flush: the file is produced by the store's
    /// own save path (schema, camelCase names, invariant timestamps, MRU order) rather than by a
    /// hand-written serialiser that would drift from it. The returned number is the median
    /// <see cref="TranslationCacheStore.SaveNow"/> of a FULL store — a store is re-touched between
    /// writes because <c>SaveNow</c> on nothing pending is a no-op by design.</summary>
    private static double WriteThroughTheStore(TempCache temp,
                                               List<(string Key, string Value, string Provider)> entries)
    {
        var store = new TranslationCacheStore(capacity: entries.Count, persistent: true);
        foreach (var (key, value, provider) in entries) store.Store(key, value, provider);

        var times = new List<double>();
        for (var i = 0; i <= Runs; i++)
        {
            // Re-store an entry that is already there: same size, same row count, `_savePending`
            // true again. Overwriting the MRU head also leaves the file order alone.
            store.Store(entries[0].Key, entries[0].Value, entries[0].Provider);

            var sw = Stopwatch.StartNew();
            store.SaveNow();
            sw.Stop();
            if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
        }

        store.CancelPendingSave();
        Assert.StartsWith(Path.GetTempPath(), temp.Path, StringComparison.OrdinalIgnoreCase);
        return Median(times);
    }

    /// <summary>Russian chat lines with their English translations, 20–90 characters, composed from
    /// a phrase pool rather than repeated: 2000 copies of one short entry would measure
    /// <c>JsonDocument</c> on a best case no user will ever have, and Cyrillic is the point — it is
    /// two bytes a character in UTF-8 and was <b>six</b> while the file used <c>JsonSerializer</c>'s
    /// default escaping encoder — which is most of what this spike found out, and what E4.S5 then
    /// changed (the table re-run after it reads 277 B an entry, not 502). Deterministic seed so two
    /// runs on two machines compare.
    ///
    /// <para><b>Internal, and read from outside this class</b> (E4.S5):
    /// <c>TranslationCachePersistenceTests</c>' bytes-per-entry case asserts the ceiling on
    /// <b>these</b> entries, so that the number CI defends and the number the spike publishes are
    /// generated by one method. The method bodies here compile on every build — only the
    /// <c>[Fact]</c>s are behind <c>PWRU_SPIKE</c> — so a normal run can call it.</para></summary>
    internal static List<(string Key, string Value, string Provider)> Entries(int n)
    {
        var rng = new Random(20260907);
        var seen = new HashSet<string>(n);
        var list = new List<(string, string, string)>(n);

        while (list.Count < n)
        {
            var parts = rng.Next(1, 4);
            var ru = new StringBuilder();
            var en = new StringBuilder();

            for (var p = 0; p < parts; p++)
            {
                var i = rng.Next(RuPhrases.Length);
                if (p > 0) { ru.Append(", "); en.Append(", "); }
                ru.Append(RuPhrases[i]);
                en.Append(EnPhrases[i]);
            }

            if (ru.Length is < 20 or > 90) continue;

            // Two thirds "ru" and one third "auto": the OCR paths pick the source per message
            // (IsProbablyRussian) and the Translator tab sends "auto", so a real file holds both.
            var source = list.Count % 3 == 0 ? "auto" : "ru";
            var key = $"{source}|en|{ru}";
            if (!seen.Add(key)) continue;

            list.Add((key, en.ToString(), Providers[list.Count % Providers.Length]));
        }

        return list;
    }

    private static readonly string[] RuPhrases =
    {
        "привет всем", "кто идёт в сквад", "нужен хил на нирвану",
        "сбор через пять минут у портала", "у меня лаги, подождите немного",
        "скинь ссылку на гильдию", "какой уровень нужен для похода",
        "я в игре, где вы стоите", "давайте начнём, все в сборе",
        "спасибо за помощь, было весело", "продам меч плюс десять недорого",
        "ищу группу на босса вечером", "перезайду, вылетело из игры",
        "кто может дать бафы перед боем", "не бейте, я на квесте",
        "встречаемся на западном мосту", "нужен танк, остальные есть",
        "у кого есть свободное место", "подскажите, где взять задание",
        "поздравляю с новым уровнем", "сегодня война гильдий в восемь",
        "жду ещё двоих и выходим", "цена договорная, пишите в личку",
        "извините, отошёл на минуту",
    };

    private static readonly string[] EnPhrases =
    {
        "hello everyone", "who is going to the squad", "need a healer for nirvana",
        "gathering in five minutes at the portal", "i am lagging, wait a bit",
        "send me the guild link", "what level is needed for the run",
        "i am in game, where are you standing", "let's start, everyone is here",
        "thanks for the help, that was fun", "selling a plus ten sword cheap",
        "looking for a group for the boss tonight", "relogging, the game crashed",
        "who can give buffs before the fight", "do not attack, i am on a quest",
        "meeting at the western bridge", "we need a tank, we have the rest",
        "who has a free slot", "tell me where to take the quest",
        "congratulations on the new level", "guild war today at eight",
        "waiting for two more and we go", "price negotiable, write me privately",
        "sorry, i stepped away for a minute",
    };

    private static readonly string[] Providers =
    {
        ProviderIds.GoogleDict, ProviderIds.GoogleGtx, ProviderIds.DeepL, "",
    };

    // ---- plumbing -------------------------------------------------------------------------------

    /// <summary>The private read bound, read rather than repeated: a copy here would keep agreeing
    /// with itself after somebody changed the constant.</summary>
    private static long MaxBytes() => (long)typeof(TranslationCacheStore)
        .GetField("MaxBytes", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!;

    private static double Repeat(Func<double> once)
    {
        var times = new List<double>();
        for (var i = 0; i <= Runs; i++)
        {
            var t = once();
            if (i > 0) times.Add(t);
        }
        return Median(times);
    }

    private static double Median(List<double> xs)
    {
        var a = xs.ToArray();
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static long Working()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64;
    }

    private static string Machine() =>
        $"{Environment.MachineName} · {Environment.ProcessorCount} logical cores · "
        + $"{Environment.OSVersion.VersionString} · {RuntimeInformation()} · {DateTime.Now:yyyy-MM-dd HH:mm}";

    private static string RuntimeInformation() =>
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>IS-2's guard, per case rather than per run: this class writes files measured in
    /// megabytes and the one path it may never write is the developer's own.</summary>
    private static (bool Exists, DateTime Written, long Length) RealFileState()
    {
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PWRUHelper", "translation-cache.json");
        var info = new FileInfo(real);
        return (info.Exists, info.Exists ? info.LastWriteTimeUtc : default, info.Exists ? info.Length : 0);
    }

    private static void AssertRealFileUntouched((bool Exists, DateTime Written, long Length) before)
    {
        Assert.Equal(before, RealFileState());
    }
}

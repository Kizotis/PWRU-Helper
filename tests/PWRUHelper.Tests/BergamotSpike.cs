using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PWRUHelper.Services;
using Xunit;
using Xunit.Abstractions;
#if PWRU_SPIKE
using System.Runtime.InteropServices;
using BergamotTranslatorSharp;
#endif

namespace PWRUHelper.Tests;

/// <summary>
/// E8.S1 / U6 + U7 — the Bergamot prototype harness, and <b>only</b> a harness. It answers the five
/// questions the whole of epic E8 hangs on, on a real machine, with a real model: how long the
/// offline engine takes to come up, what a line costs, what it holds while it is resident, whether
/// that memory comes back when it is unloaded, and whether the native DLL can live in a downloaded
/// directory beside the exe instead of inside the single-file bundle (U7 — the difference between
/// 0 MB and ~22 MB of exe growth, and between 5 and 6 files extracted to <c>%TEMP%\.net\…</c>).
///
/// <para><b>It reports and it never fails on a duration</b> (CI-3's carve-out, TP-START-03), exactly
/// as <see cref="CacheLoadSpike"/> does. The assertions below are safety ones only: that the run
/// stayed inside its temp directory, that the bytes on disk are the bytes Mozilla's registry
/// publishes, and that the developer's real <c>%AppData%\PWRUHelper\</c> and
/// <c>%LocalAppData%\PWRUHelper\</c> are untouched.</para>
///
/// <para><b>Why it is invisible to a normal <c>dotnet test</c>.</b> Same mechanism as
/// <c>CacheLoadSpike</c>: the <c>[Fact]</c> attributes only exist when <c>PWRU_SPIKE=1</c> is in the
/// environment (<c>PWRUHelper.Tests.csproj</c> turns that variable into the <c>PWRU_SPIKE</c>
/// define). <b>One deliberate difference:</b> <c>CacheLoadSpike</c>'s method bodies compile on every
/// build, and here they cannot — the <c>BergamotTranslatorSharp</c> <c>PackageReference</c> is
/// itself conditioned on <c>PWRU_SPIKE</c> so that a normal restore downloads nothing (ruling E8-d
/// authorises exactly one package, on the dev box, for this spike). Everything that does not name a
/// type from that package — the chat lines, the model download and verification, the machine sheet,
/// the isolation guards — is therefore outside the <c>#if</c> and still cannot rot; only the region
/// that talks to the engine is inside it.</para>
///
/// <para><b>What leaves the machine, and what does not</b> (ruling E8-d). Two Mozilla registry
/// requests and three model files into <c>%TEMP%\pwru-bergamot-spike\</c>; the native DLL is copied
/// out of the NuGet cache, never fetched separately. <b>No translation API is called</b> — the
/// "cloud column" of the quality table is deliberately left for E8.S7, per the story's T4.</para>
///
/// <para>Run it with
/// <code>set PWRU_SPIKE=1 &amp;&amp; dotnet test tests/PWRUHelper.Tests --filter "FullyQualifiedName~BergamotSpike" --logger "console;verbosity=detailed"</code></para>
/// </summary>
[Trait("Category", "Spike")]
public class BergamotSpike
{
    /// <summary>Timed repetitions per number, after one discarded warm-up — the same shape and the
    /// same reason as <see cref="CacheLoadSpike"/>: the first pass pays for JIT and for the model
    /// files entering the OS cache. Seven, so the median has three on each side.</summary>
    private const int Runs = 7;

    /// <summary>Lines pushed through the engine when the sustained cost is measured. Big enough that
    /// a per-line median is not one scheduling hiccup, small enough that the case stays under a
    /// minute.</summary>
    private const int SustainedLines = 400;

    /// <summary>Everything this spike writes lives here and is deleted by hand afterwards (E8-d:
    /// "temp dir, deleted after"). Never <c>%AppData%</c>, never <c>%LocalAppData%</c> — the model
    /// store's real home is E8.S3's decision (ruling E8-b: <c>%LocalAppData%\PWRUHelper\models\</c>).</summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pwru-bergamot-spike");

    private static readonly string ModelDir = Path.Combine(Root, "models", "ruen-tiny");
    private static readonly string NativeDir = Path.Combine(Root, "native");

    /// <summary>The <b>live</b> Firefox model registry. <c>mozilla/firefox-translations-models</c>
    /// was archived 2025-12-15 (`benchmark-fournisseurs.md` §6.1) and must not be used.
    /// Two registries, on purpose, because they carry different halves of what this harness needs:
    /// Remote Settings publishes <c>decompressedSize</c> and <c>decompressedHash</c> (so a download
    /// can be <b>verified</b>, not merely fetched) but serves its attachments <b>zstd</b>-compressed,
    /// which .NET 8 cannot decompress without a second NuGet package this spike is not allowed to
    /// add; the Google Cloud Storage mirror named by <c>BergamotTranslatorSharp</c>'s own README
    /// serves the same files <b>gzip</b>-compressed, which <see cref="GZipStream"/> handles in the
    /// box. So: bytes from the mirror, integrity from Remote Settings. They agree exactly — that
    /// agreement is itself asserted below.</summary>
    private const string RemoteSettingsUrl =
        "https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models-v2/records";

    private const string MirrorRegistryUrl =
        "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json";

    private readonly ITestOutputHelper _out;

    public BergamotSpike(ITestOutputHelper output) => _out = output;

    // ---- the whole spike, in one case, because the order of the samples IS the measurement -------

#if PWRU_SPIKE
    [Fact]
#endif
    public void The_offline_engine_measured_end_to_end()
    {
        var appData = RealAppState();

        _out.WriteLine("### 1. Machine");
        _out.WriteLine(Machine());
        _out.WriteLine("");

        // --- provenance: is the package still the one §6.2 measured, and the model the one §6.1 did?
        _out.WriteLine("### 2. Provenance");
        var dll = EnsureNativeDll();
        var files = EnsureModel();
        var configPath = WriteConfig(workspace: 128, "config.txt");
        var smallWorkspace = WriteConfig(workspace: 8, "config-ws8.txt");
        _out.WriteLine($"native DLL      : {dll} — {new FileInfo(dll).Length:N0} B "
                       + $"(benchmark §6.2: 22,460,928 B) · imports: {string.Join(", ", PeImports(dll))}");
        foreach (var f in files)
            _out.WriteLine($"model file      : {Path.GetFileName(f.Path),-32} {f.Size,12:N0} B  sha256 ok ({f.Version})");
        _out.WriteLine($"model total     : {files.Sum(f => f.Size):N0} B (benchmark §6.1 ru→en tiny: 22,530,152 B)");
        _out.WriteLine($"config          : {configPath}");
        _out.WriteLine("");

#if PWRU_SPIKE
        InstallResolver(NativeDir);

        // --- AC 4, first half: the model is on disk and nobody has asked for a translation --------
        _out.WriteLine("### 3. At rest — the model is on disk, the engine has never been constructed");
        var rest0 = Mem();
        var onDisk = Directory.GetFiles(ModelDir).Sum(p => new FileInfo(p).Length);
        var rest1 = Mem();
        _out.WriteLine($"baseline        : ws {Mb(rest0.Ws)} · private {Mb(rest0.Priv)} · managed {Mb(rest0.Managed)}");
        _out.WriteLine($"after touching {onDisk / 1024 / 1024} MB of model on disk:");
        _out.WriteLine($"                : ws {Mb(rest1.Ws)} · private {Mb(rest1.Priv)} — Δ ws {MbD(rest1.Ws - rest0.Ws)}, "
                       + $"Δ private {MbD(rest1.Priv - rest0.Priv)}");
        _out.WriteLine("");

        // --- the lifecycle, sampled where §10 says it must be sampled ---------------------------
        _out.WriteLine("### 4. The resident cost, sampled through one full lifecycle");
        _out.WriteLine("(Marian allocates its fixed pool on the FIRST TRANSLATE, not on init — §10. "
                       + "Sampling after init and calling that 'the RAM cost' understates it by most of the number.)");
        _out.WriteLine("");
        _out.WriteLine("| sample point | working set | private bytes | managed heap | Δ ws vs baseline | Δ private vs baseline |");
        _out.WriteLine("|---|---|---|---|---|---|");

        var life = Lifecycle(configPath, printRows: true);
        _out.WriteLine("");

        // ONE extra data point, and deliberately NOT §10's sweep. §10 swept `workspace`,
        // `mini-batch-words` and `max-length-break` against **RSS** and found ±1 MiB, and the story
        // forbids re-running that. Private bytes is a metric §10 never reported, it came back at
        // three times the working set, and `workspace` is denominated in MB — so a single second
        // point says whether the commit charge is the knob RSS was not. It is not a sweep; it is
        // one number, and whichever way it falls it belongs in the doc.
        var small = Lifecycle(smallWorkspace, printRows: false);
        _out.WriteLine($"`workspace: 8` instead of 128, same everything else — after the first translate: "
                       + $"Δ ws {MbD(small.ActiveWs)} (was {MbD(life.ActiveWs)}), "
                       + $"Δ private {MbD(small.ActivePriv)} (was {MbD(life.ActivePriv)})");
        _out.WriteLine("");

        // --- init and per-line, medians of 7 -----------------------------------------------------
        _out.WriteLine("### 5. Init, per-line latency and throughput (median of 7 after a discarded warm-up)");

        var inits = new List<double>();
        var firsts = new List<double>();
        for (var i = 0; i <= Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            using var svc = new BlockingService(configPath);
            sw.Stop();
            var init = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            svc.Translate(Lines[0], false);
            sw.Stop();

            if (i == 0) continue;                       // warm-up, discarded
            inits.Add(init);
            firsts.Add(sw.Elapsed.TotalMilliseconds);
        }

        var perLine = new List<double>();
        var perLineBatched = new List<double>();
        using (var svc = new BlockingService(configPath))
        {
            foreach (var l in Lines) svc.Translate(l, false);          // warm-up pass, discarded

            for (var r = 0; r < Runs; r++)
            {
                var sw = Stopwatch.StartNew();
                foreach (var l in Lines) svc.Translate(l, false);
                sw.Stop();
                perLine.Add(sw.Elapsed.TotalMilliseconds / Lines.Length);

                // The shape the LIVE loop would actually use: one native call for a whole OCR
                // frame, lines wrapped in <p> and html:true (§6.2).
                sw.Restart();
                var batch = svc.Translate(Lines);
                sw.Stop();
                Assert.Equal(Lines.Length, batch.Length);
                perLineBatched.Add(sw.Elapsed.TotalMilliseconds / Lines.Length);
            }
        }

        _out.WriteLine("");
        _out.WriteLine("| number | median | min | max | reference (benchmark §10, `tiny` ru→en) |");
        _out.WriteLine("|---|---|---|---|---|");
        _out.WriteLine(Stat("init (construct BlockingService)", inits, "103–119 ms"));
        _out.WriteLine(Stat("first translate after init", firsts, "— (the pool is allocated here)"));
        _out.WriteLine(Stat("per line, one call per line", perLine, "6.5–12.1 ms"));
        _out.WriteLine(Stat("per line, HTML-batched frame", perLineBatched, "—"));
        _out.WriteLine($"| throughput, 1 call/line | **{1000.0 / Median(perLine):F0} lines/s** | | | 64–80 lines/s |");
        _out.WriteLine($"| throughput, batched | **{1000.0 / Median(perLineBatched):F0} lines/s** | | | — |");
        _out.WriteLine("");
        _out.WriteLine("All warm: the DLL and the three model files were read by this process moments earlier and are");
        _out.WriteLine("in the OS cache. A cold, Defender-scanned first load is the machine class E8.S7 owns.");
        _out.WriteLine("");

        // --- the five thresholds, stated where they can be read off ------------------------------
        _out.WriteLine("### 6. The five go/no-go thresholds");
        _out.WriteLine("");
        _out.WriteLine($"1. init ≤ 500 ms on first fallback   : {Median(inits):F1} ms → {Verdict(Median(inits) <= 500)}");
        _out.WriteLine($"2. per line ≤ 15 ms                  : {Median(perLine):F2} ms → {Verdict(Median(perLine) <= 15)}");
        _out.WriteLine($"3. resident ≤ 150 MiB while active   : working set {MbD(life.ActiveWs)} → {Verdict(life.ActiveWs <= 150L * 1024 * 1024)}"
                       + $"  ·  private bytes {MbD(life.ActivePriv)} → {Verdict(life.ActivePriv <= 150L * 1024 * 1024)}"
                       + "  (E8-a: 150 MiB binds the shipping config; TP-BRG-02 says *resident*)");
        _out.WriteLine($"4. ≈ 0 MiB after unload              : working set {MbD(life.RestWs)}, private {MbD(life.RestPriv)}"
                       + $" → {Verdict(Math.Abs(life.RestWs) <= 10L * 1024 * 1024)}");
        _out.WriteLine($"5. exe growth 0 MB (download-on-demand): bergamot.dll in build output = "
                       + $"{File.Exists(Path.Combine(AppContext.BaseDirectory, "bergamot.dll"))} → see §7");
        _out.WriteLine("");

        // --- U7 ----------------------------------------------------------------------------------
        _out.WriteLine("### 7. U7 — where the native DLL lived for every number above");
        _out.WriteLine($"The test project references the package with ExcludeAssets=\"native\", so `bergamot.dll` is");
        _out.WriteLine($"NOT in the test output and NOT in any bundle. Every translation above went through a DLL");
        _out.WriteLine($"loaded by NativeLibrary.SetDllImportResolver from:");
        _out.WriteLine($"    {Path.Combine(NativeDir, "bergamot.dll")}");
        _out.WriteLine($"test output contains bergamot.dll: {File.Exists(Path.Combine(AppContext.BaseDirectory, "bergamot.dll"))}");
        _out.WriteLine(ExtractionReport());
        _out.WriteLine("");

        // --- the quality table -------------------------------------------------------------------
        _out.WriteLine("### 8. Quality — 20 lines, raw and after SlangGlossary.Expand");
        var glossary = RealGlossary();
        _out.WriteLine($"(glossary: {glossary.Entries.Count} entries, "
                       + $"{glossary.Entries.Count(e => !string.IsNullOrEmpty(e.Full))} of them with a Russian `full` form — "
                       + "only those are rewritten by Expand)");
        _out.WriteLine("");
        _out.WriteLine("| # | chat line | offline, raw | offline, after Expand |");
        _out.WriteLine("|---|---|---|---|");
        using (var svc = new BlockingService(configPath))
        {
            for (var i = 0; i < Lines.Length; i++)
            {
                var raw = svc.Translate(Lines[i], false);
                var expandedSource = glossary.Expand(Lines[i]);
                var expanded = svc.Translate(expandedSource, false);
                var mark = string.Equals(expandedSource, Lines[i], StringComparison.Ordinal) ? " _(no change)_" : "";
                _out.WriteLine($"| {i + 1} | {Lines[i]} | {raw} | {expanded}{mark} |");
            }
        }
        _out.WriteLine("");
        _out.WriteLine("The cloud column is deliberately absent: a spike does not spend a provider call to fill a");
        _out.WriteLine("column (story T4), and go-criterion 3 gives the judgement to the owner in E8.S7.");
#else
        _out.WriteLine("PWRU_SPIKE is not defined — the engine half of this harness is not compiled.");
#endif

        AssertRealAppUntouched(appData);
        Assert.StartsWith(Path.GetTempPath(), Root, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the model, downloaded on demand and VERIFIED, not merely fetched -------------------------

    internal readonly record struct ModelFile(string Path, long Size, string Sha256, string Version);

    /// <summary>Download the `tiny` ru→en model into <see cref="ModelDir"/> once, keyed by content:
    /// a file whose length and SHA-256 already match Remote Settings' <c>decompressedHash</c> is not
    /// downloaded again, so seven timed runs are not seven downloads.</summary>
    private List<ModelFile> EnsureModel()
    {
        Directory.CreateDirectory(ModelDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // (a) integrity, from Remote Settings.
        var rs = JsonDocument.Parse(http.GetStringAsync(RemoteSettingsUrl).GetAwaiter().GetResult());
        var expected = new Dictionary<string, (long Size, string Hash, string Version)>(StringComparer.Ordinal);
        foreach (var r in rs.RootElement.GetProperty("data").EnumerateArray())
        {
            if (Str(r, "sourceLanguage") != "ru" || Str(r, "targetLanguage") != "en") continue;
            if (Str(r, "architecture") != "tiny") continue;
            expected[Str(r, "name")] = (r.GetProperty("decompressedSize").GetInt64(),
                                        Str(r, "decompressedHash"), Str(r, "version"));
        }
        Assert.Equal(3, expected.Count);   // model + vocab + lexical shortlist

        // (b) bytes, from the gzip mirror named by the binding's own README.
        var reg = JsonDocument.Parse(http.GetStringAsync(MirrorRegistryUrl).GetAwaiter().GetResult());
        var baseUrl = Str(reg.RootElement, "baseUrl");
        var candidate = reg.RootElement.GetProperty("models").GetProperty("ru-en").EnumerateArray()
            .First(m => Str(m, "architecture") == "tiny");

        var results = new List<ModelFile>();
        foreach (var key in new[] { "model", "vocab", "lexicalShortlist" })
        {
            var path = Str(candidate.GetProperty("files").GetProperty(key), "path");
            var name = Path.GetFileName(path);
            if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
            var local = Path.Combine(ModelDir, name);

            var want = expected[name];
            if (!(File.Exists(local) && new FileInfo(local).Length == want.Size && Sha(local) == want.Hash))
            {
                using var gz = http.GetStreamAsync($"{baseUrl}/{path}").GetAwaiter().GetResult();
                using var unzip = new GZipStream(gz, CompressionMode.Decompress);
                using var dest = File.Create(local);
                unzip.CopyTo(dest);
            }

            // The registry's own figures are the assert. A size that disagrees means the registry
            // moved and the benchmark's number, not this harness, is what needs saying so.
            Assert.Equal(want.Size, new FileInfo(local).Length);
            Assert.Equal(want.Hash, Sha(local));
            results.Add(new ModelFile(local, want.Size, want.Hash, want.Version));
        }
        return results;
    }

    /// <summary>The Marian config the engine reads. Written next to the model with
    /// <c>relative-paths: true</c>, exactly as the binding's README specifies, and with the
    /// <c>int8shiftAlphaAll</c> precision the `alphas` file name requires. The three knobs §10
    /// already swept (<c>workspace</c>, <c>mini-batch-words</c>, <c>max-length-break</c>) are left
    /// at the README's values on purpose: the sweep moved RSS by ±1 MiB and re-running it is a day
    /// spent re-proving a [MEASURED] fact.</summary>
    private static string WriteConfig(int workspace, string fileName)
    {
        var path = Path.Combine(ModelDir, fileName);
        File.WriteAllText(path, string.Join("\n", new[]
        {
            "relative-paths: true",
            "models:",
            "- model.ruen.intgemm.alphas.bin",
            "vocabs:",
            "- vocab.ruen.spm",
            "- vocab.ruen.spm",
            "shortlist:",
            "- lex.50.50.ruen.s2t.bin",
            "- false",
            "beam-size: 1",
            "normalize: 1.0",
            "word-penalty: 0",
            "max-length-break: 128",
            "mini-batch-words: 1024",
            $"workspace: {workspace}",
            "max-length-factor: 2.0",
            "skip-cost: true",
            "cpu-threads: 0",
            "quiet: true",
            "quiet-translation: true",
            "gemm-precision: int8shiftAlphaAll",
        }), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Put <c>bergamot.dll</c> where a downloaded model store would put it — a plain
    /// directory under <see cref="Root"/>, nothing beside the test assembly and nothing in a bundle.
    /// It is copied out of the NuGet cache rather than fetched from a fourth URL: the package is
    /// already on disk and ruling E8-d authorised one model download, not two.</summary>
    private static string EnsureNativeDll()
    {
        Directory.CreateDirectory(NativeDir);
        var dest = Path.Combine(NativeDir, "bergamot.dll");

        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var src = Path.Combine(root, "bergamottranslatorsharp", "0.5.1", "runtimes", "win-x64", "native", "bergamot.dll");
        Assert.True(File.Exists(src), $"the pinned package's native asset is not in the NuGet cache: {src}");

        if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(src).Length)
            File.Copy(src, dest, overwrite: true);
        return dest;
    }

#if PWRU_SPIKE
    /// <summary>One engine, from a cold baseline to an unloaded one, sampled at the four points that
    /// matter. <b>The order is the measurement</b>: Marian allocates its fixed pool on the FIRST
    /// TRANSLATE (§10), so "after init" and "after the first translate" are two different answers to
    /// "what does it cost", and only the second one is the one this feature is sold on.</summary>
    private (long ActiveWs, long ActivePriv, long RestWs, long RestPriv) Lifecycle(string configPath, bool printRows)
    {
        var baseline = Mem();
        if (printRows) Row("baseline (nothing loaded)", baseline, baseline);

        (long Ws, long Priv, long Managed) afterFirst;
        {
            var svc = new BlockingService(configPath);
            var afterInit = Mem();
            if (printRows) Row("after init (engine constructed)", afterInit, baseline);

            var first = svc.Translate(Lines[0], false);
            GC.KeepAlive(first);
            afterFirst = Mem();
            if (printRows) Row("after the FIRST translate", afterFirst, baseline);

            for (var i = 0; i < SustainedLines; i++) svc.Translate(Lines[i % Lines.Length], false);
            var afterMany = Mem();
            if (printRows) Row($"after {SustainedLines} more translations", afterMany, baseline);

            svc.Dispose();
        }

        // AC 4's second half — the number A-1(b) and go-criterion 4 stand on. Two collections with a
        // finalizer drain between, because the native handle is freed by Dispose but the managed
        // shell can still be reachable from the frame above until the scope closes.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var afterDispose = Mem();
        if (printRows) Row("after Dispose + 2 collections", afterDispose, baseline);

        return (afterFirst.Ws - baseline.Ws, afterFirst.Priv - baseline.Priv,
                afterDispose.Ws - baseline.Ws, afterDispose.Priv - baseline.Priv);
    }

    private static bool _resolverInstalled;

    /// <summary>U7's whole question, in five lines: the package is referenced with
    /// <c>ExcludeAssets="native"</c> so nothing named <c>bergamot.dll</c> reaches the output or a
    /// bundle, and the binding's <c>[LibraryImport("bergamot")]</c> is satisfied instead from a
    /// directory the app could have downloaded. If this works, the shipping exe grows by <b>0 MB</b>
    /// and <c>%TEMP%\.net\PWRUHelper\&lt;id&gt;</c> keeps its five WPF files.</summary>
    private static void InstallResolver(string dir)
    {
        if (_resolverInstalled) return;
        NativeLibrary.SetDllImportResolver(typeof(BlockingService).Assembly, (name, asm, search) =>
            name == "bergamot" && NativeLibrary.TryLoad(Path.Combine(dir, "bergamot.dll"), out var h)
                ? h
                : IntPtr.Zero);
        _resolverInstalled = true;
    }
#endif

    // ---- the lines ------------------------------------------------------------------------------

    /// <summary>Twenty PW-RU chat lines. The first four are <b>verbatim from `benchmark…` §10</b> so
    /// a divergence from the published result is visible rather than silent; ten more are built
    /// around real keys from the shipping <c>Data/slang.json</c> (лфг · стук · сложка · хил · танк ·
    /// прист · пп · дд · 5-3/лега · вар · лук · гвг · бд · мист · ара · тс · син · ганер · адепты ·
    /// мбг · дру · шам · хс); the last six are ordinary chat with no slang at all, as the control.</summary>
    internal static readonly string[] Lines =
    {
        // §10's four, verbatim
        "Всем привет, кто идет в данж?",
        "го пати на босса, нужен хил",
        "нид на дроп, я хил",
        "спс за пати, было весело",
        // ten built on real slang.json keys
        "лфг сложка, есть хил и танк",
        "стук в гильдию, я прист 105 лвл",
        "в пп нужен дд и хил, стук",
        "кто в 5-3 лега? нужен вар и лук",
        "сбор на гвг в восемь, все в бд",
        "ищем мист на ара, стук в лс",
        "есть места на тс? я син 100",
        "нужен ганер на адепты, лфг",
        "в мбг идем через десять минут",
        "дру и шам в пати на хс, стук",
        // six ordinary lines, the control
        "перезайду, вылетело из игры",
        "подскажите, где взять задание",
        "продам меч плюс десять недорого",
        "у меня лаги, подождите немного",
        "встречаемся на западном мосту",
        "поздравляю с новым уровнем",
    };

    /// <summary>The glossary the <b>app</b> reads, not a stub: <c>Data/slang.json</c> is an
    /// EmbeddedResource <i>and</i> a copy next to the exe, and a spike that measured a hand-written
    /// glossary would be measuring a file nobody ships.</summary>
    private static SlangGlossary RealGlossary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "slang.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Data", "slang.json"),
        };
        foreach (var c in candidates)
            if (File.Exists(c))
                return SlangGlossary.FromJson(File.ReadAllText(c));

        using var s = typeof(SlangGlossary).Assembly
            .GetManifestResourceStream("PWRUHelper.Data.slang.json");
        Assert.NotNull(s);
        using var r = new StreamReader(s!);
        return SlangGlossary.FromJson(r.ReadToEnd());
    }

    // ---- plumbing --------------------------------------------------------------------------------

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Sha(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static (long Ws, long Priv, long Managed) Mem()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return (p.WorkingSet64, p.PrivateMemorySize64, GC.GetTotalMemory(forceFullCollection: true));
    }

    private void Row(string label, (long Ws, long Priv, long Managed) now, (long Ws, long Priv, long Managed) base_) =>
        _out.WriteLine($"| {label} | {Mb(now.Ws)} | {Mb(now.Priv)} | {Mb(now.Managed)} | "
                       + $"**{MbD(now.Ws - base_.Ws)}** | **{MbD(now.Priv - base_.Priv)}** |");

    private static string Mb(long b) => $"{b / 1024.0 / 1024.0:N1} MiB";

    private static string MbD(long b) => $"{(b >= 0 ? "+" : "")}{b / 1024.0 / 1024.0:N1} MiB";

    private static string Verdict(bool pass) => pass ? "**PASS**" : "**FAIL**";

    private static string Stat(string label, List<double> xs, string reference) =>
        $"| {label} | **{Median(xs):F2} ms** | {xs.Min():F2} | {xs.Max():F2} | {reference} |";

    private static double Median(List<double> xs)
    {
        var a = xs.ToArray();
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>U7's baseline half: what the <b>app's</b> single-file bundle extracts on first run.
    /// Reported, never asserted — the directory only exists on a box where the portable exe has been
    /// launched, and its absence is a fact about the machine, not a failure.</summary>
    private static string ExtractionReport()
    {
        var root = Path.Combine(Path.GetTempPath(), ".net", "PWRUHelper");
        if (!Directory.Exists(root)) return "%TEMP%\\.net\\PWRUHelper: absent (the portable exe has not been launched on this box)";

        var sb = new StringBuilder();
        foreach (var d in Directory.GetDirectories(root))
        {
            var fs = Directory.GetFiles(d);
            sb.AppendLine($"%TEMP%\\.net\\PWRUHelper\\{Path.GetFileName(d)}: {fs.Length} files, "
                          + $"{fs.Sum(f => new FileInfo(f).Length):N0} B — baseline is 5 WPF native DLLs, 8,214,968 B");
            foreach (var f in fs.OrderBy(f => f))
                sb.AppendLine($"    {Path.GetFileName(f),-32} {new FileInfo(f).Length,12:N0} B");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The four import-table names <c>benchmark…</c> §6.2 checked, re-read from the DLL
    /// this run actually loaded. A fifth name — an MSVC redistributable above all — is a finding, so
    /// it is printed rather than assumed. Parsed by hand because <c>dumpbin</c> needs a Visual
    /// Studio developer prompt and this harness must run from a plain <c>dotnet test</c>.</summary>
    internal static List<string> PeImports(string path)
    {
        var d = File.ReadAllBytes(path);
        var pe = BitConverter.ToInt32(d, 0x3c);
        var sections = BitConverter.ToUInt16(d, pe + 6);
        var optSize = BitConverter.ToUInt16(d, pe + 20);
        var opt = pe + 24;
        var pe32Plus = BitConverter.ToUInt16(d, opt) == 0x20b;
        var dirs = opt + (pe32Plus ? 112 : 96);
        var importRva = BitConverter.ToUInt32(d, dirs + 8);

        var map = new List<(uint Va, uint Vs, uint Ra, uint Rs)>();
        for (var i = 0; i < sections; i++)
        {
            var o = opt + optSize + 40 * i;
            map.Add((BitConverter.ToUInt32(d, o + 12), BitConverter.ToUInt32(d, o + 8),
                     BitConverter.ToUInt32(d, o + 20), BitConverter.ToUInt32(d, o + 16)));
        }

        int Offset(uint rva)
        {
            foreach (var (va, vs, ra, rs) in map)
                if (rva >= va && rva < va + Math.Max(vs, rs))
                    return (int)(ra + (rva - va));
            return -1;
        }

        var names = new List<string>();
        for (var e = Offset(importRva); e > 0; e += 20)
        {
            var nameRva = BitConverter.ToUInt32(d, e + 12);
            if (nameRva == 0) break;
            var o = Offset(nameRva);
            var end = Array.IndexOf(d, (byte)0, o);
            names.Add(Encoding.ASCII.GetString(d, o, end - o));
        }
        return names;
    }

    private static string Machine() =>
        $"{Environment.MachineName} · {Environment.ProcessorCount} logical cores · "
        + $"{Environment.OSVersion.VersionString} · {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · "
        + $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · {DateTime.Now:yyyy-MM-dd HH:mm}";

    /// <summary>IS-2's guard, widened: this spike is the one case in the suite that could plausibly
    /// write a model store, so BOTH candidate roots from ruling E8-b are watched — the Roaming one
    /// the deck named and the Local one Winston ruled for. Neither may be created here.</summary>
    private static (bool R, bool L, long RN, long LN) RealAppState()
    {
        var roam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PWRUHelper");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PWRUHelper");
        return (Directory.Exists(roam), Directory.Exists(local),
                Directory.Exists(roam) ? Directory.GetFileSystemEntries(roam, "*", SearchOption.AllDirectories).LongLength : 0,
                Directory.Exists(local) ? Directory.GetFileSystemEntries(local, "*", SearchOption.AllDirectories).LongLength : 0);
    }

    private static void AssertRealAppUntouched((bool R, bool L, long RN, long LN) before) =>
        Assert.Equal(before, RealAppState());
}

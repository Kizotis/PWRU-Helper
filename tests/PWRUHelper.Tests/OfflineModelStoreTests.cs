using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <b>E8.S3 — the model store</b> (TP-BRG-07). Everything here runs in CI and <b>nothing downloads
/// anything</b> (CI-8 forbids the model download by name): the whole install path is driven through
/// the <see cref="FakeHandler"/> seam E1.S1 landed, over a manifest of the suite's own with real
/// digests over a few hundred bytes. No case touches the developer's real
/// <c>%LocalAppData%\PWRUHelper\models</c> — <see cref="TestModelsRedirect"/>'s
/// <c>[ModuleInitializer]</c> and <see cref="TempModels"/> are the IS-2/IS-3 pair for that, and the
/// guard at the bottom asserts the real directory is untouched.
///
/// <para>It joins the non-parallel <c>Gates</c> collection because the I10 case builds all three
/// chains, and a chain reaches <c>ProviderGates</c> through <c>TranslationChains</c> whether or not
/// this file names the registry — E7.S8's finding, and the reason that scan has four triggers.</para>
/// </summary>
[Collection("Gates")]
public class OfflineModelStoreTests : GatesTestBase
{
    private static readonly byte[] Native = TempModels.Blob(1, 512);
    private static readonly byte[] Model = TempModels.Blob(7, 300);

    private static OfflineModelManifest Good(string? baseUrl = null)
        => new("test-tag", new[]
           {
               new OfflineFile(OfflineModelManifest.NativeFileName, Native.Length,
                               TempModels.Sha256(Native), null),
               new OfflineFile("model.ruen.intgemm.alphas.bin", Model.Length,
                               TempModels.Sha256(Model), OfflineModelManifest.RuEn),
           }, baseUrl);

    private static FakeHandler Serving()
        => new FakeHandler().RespondBytes(Native).RespondBytes(Model);

    // =============================================================================================
    //  Case 3 — a fake handler serving a manifest's blobs installs them (AC 3, AC 8)
    // =============================================================================================

    [Fact]
    public async Task TP_BRG_07_the_files_are_downloaded_verified_and_installed()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        var handler = Serving();
        var store = new OfflineModelStore(handler, manifest);

        var seen = new List<double?>();
        var result = await store.InstallAsync(new SyncProgress(seen.Add));

        Assert.True(result.Installed);
        Assert.Equal(OfflineInstallFailure.None, result.Failure);
        Assert.False(result.Cancelled);

        // Exactly the layout the provider expects: the native library at the root, the pair's files
        // in the pair's directory, and config.txt beside the model — written by the store, last.
        Assert.Equal(new[]
        {
            "bergamot.dll",
            "ru-en/config.txt",
            "ru-en/model.ruen.intgemm.alphas.bin",
        }, temp.Files());

        Assert.True(store.IsInstalled);
        Assert.Equal(new[] { "ru-en" }, store.InstalledPairs);
        Assert.Equal(Native.Length + Model.Length + Utf8Length(
                         OfflineModelManifest.ConfigText(OfflineModelManifest.RuEn)),
                     store.BytesOnDisk);

        // The two locators BergamotTranslator takes as Func<string?>. E8.S5 wires them; this story
        // only has to be able to answer them.
        Assert.Equal(temp.Root, store.NativeDirectory());
        Assert.Equal(Path.Combine(temp.Root, "ru-en"), store.ModelDirectory());
        Assert.True(File.Exists(Path.Combine(store.ModelDirectory()!, BergamotTranslator.ConfigFileName)),
                    "the provider looks for config.txt by that const — the store must write it");

        // One request per file, both on the allow-list, and nothing else asked for.
        Assert.Equal(2, handler.Requests);
        Assert.All(handler.Calls, c => Assert.Equal("github.com", c.Uri.Host));

        // …and every one of them identifies the app (review, E1's seam lesson): UpdateService's two
        // clients name themselves for the same host, an anonymous 50 MB pull is the shape a mirror
        // rate-limits first, and a header nobody asserts is a header the next refactor drops.
        Assert.All(handler.Calls, c => Assert.Equal("PWRUHelper-OfflineEngine", c.Headers["User-Agent"]));

        // A percentage really was reported and it ended at 100.
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.NotNull(p));
        Assert.Equal(100d, seen[^1]!.Value, 3);
    }

    [Fact]
    public async Task A_wrong_digest_installs_nothing_and_leaves_no_part_behind()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        // The right SIZE and the wrong BYTES — the case a size check alone would wave through, and
        // the one AC 8 is written for: a 22 MB DLL the app then P/Invokes.
        var tampered = TempModels.Blob(9, Native.Length);
        var store = new OfflineModelStore(new FakeHandler().RespondBytes(tampered), manifest);

        var result = await store.InstallAsync();

        Assert.False(result.Installed);
        Assert.False(result.Cancelled);
        Assert.Equal(OfflineInstallFailure.Verification, result.Failure);
        Assert.Empty(temp.Files());
        Assert.False(Directory.Exists(temp.Root), "a failed install must leave no directory at all");
        Assert.False(store.IsInstalled);
    }

    [Fact]
    public async Task A_wrong_size_is_refused_without_hashing_anything()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        // Truncated: the common failure, and the one that costs nothing to reject.
        var store = new OfflineModelStore(
            new FakeHandler().RespondBytes(Native[..(Native.Length - 1)]), manifest);

        var result = await store.InstallAsync();

        Assert.Equal(OfflineInstallFailure.Verification, result.Failure);
        Assert.Empty(temp.Files());
    }

    [Fact]
    public async Task A_second_file_that_fails_takes_the_first_one_with_it()
    {
        // "Nothing was installed." is a promise about the WHOLE install, not about the file that
        // failed: a root holding a verified bergamot.dll and no model is an engine the provider
        // would try to load.
        var manifest = Good();
        using var temp = new TempModels(manifest);
        var store = new OfflineModelStore(
            new FakeHandler().RespondBytes(Native).RespondBytes(TempModels.Blob(3, Model.Length)),
            manifest);

        Assert.Equal(OfflineInstallFailure.Verification, (await store.InstallAsync()).Failure);
        Assert.Empty(temp.Files());
    }

    // =============================================================================================
    //  Case 4 — the allow-list is read, never widened (AC 6)
    // =============================================================================================

    [Fact]
    public async Task An_untrusted_url_is_refused_before_a_request_is_made()
    {
        var manifest = Good("https://models.example.com/releases/download");
        using var temp = new TempModels(manifest);
        var handler = Serving();

        var result = await new OfflineModelStore(handler, manifest).InstallAsync();

        Assert.Equal(OfflineInstallFailure.Untrusted, result.Failure);
        Assert.Equal(0, handler.Requests);      // BEFORE a request, which is the whole assertion
        Assert.Empty(temp.Files());
    }

    [Fact]
    public void The_shipping_manifest_is_on_the_allow_list_and_the_allow_list_is_not_widened()
    {
        var manifest = OfflineModelManifest.Current;

        Assert.NotEmpty(manifest.Files);
        foreach (var file in manifest.Files)
        {
            var url = manifest.UrlFor(file);
            Assert.True(UpdateService.IsTrustedDownload(url), url);
            Assert.StartsWith("https://github.com/Kizotis/PWRU-Helper/releases/download/",
                              url, StringComparison.Ordinal);
        }

        // …and the list itself is untouched. Two hosts, https only — the predicate this story READS
        // (project-context.md's "Critical Don't-Miss Rules"). A story that widens it is wrong even
        // if its own tests pass, so the assertion is on the source and not only on the behaviour:
        // EVERY host literal compared anywhere in UpdateService, as a set.
        var source = Code(File.ReadAllText(ServiceFile("UpdateService.cs")));
        var hosts = Regex.Matches(source, @"u\.Host\.\w+\(\s*""([^""]+)""")
                         .Select(m => m.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(h => h, StringComparer.Ordinal)
                         .ToArray();
        Assert.Equal(new[] { ".githubusercontent.com", "github.com" }, hosts);

        Assert.False(UpdateService.IsTrustedDownload("http://github.com/x"));           // not https
        Assert.False(UpdateService.IsTrustedDownload("https://github.com.evil.net/x"));
        Assert.False(UpdateService.IsTrustedDownload("https://storage.googleapis.com/x"));
    }

    // =============================================================================================
    //  Case 5 — a mirror with no total size gets no percentage (AC 4 / OQ-9)
    // =============================================================================================

    [Fact]
    public async Task A_mirror_that_reports_no_total_size_gets_no_percentage()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        var store = new OfflineModelStore(
            new FakeHandler().RespondBytes(Native).NoContentLength()
                             .RespondBytes(Model).NoContentLength(),
            manifest);

        var seen = new List<double?>();
        var result = await store.InstallAsync(new SyncProgress(seen.Add));

        Assert.True(result.Installed);           // it still installs — only the copy degrades
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Null(p));

        // …and the sentence the row renders really is the plain one, with no number invented.
        Assert.Equal("Downloading…", UserMessages.OfflineDownloading(null));
        Assert.DoesNotContain('%', UserMessages.OfflineDownloading(null));
    }

    [Fact]
    public async Task A_percentage_that_has_gone_unknown_never_comes_back()
    {
        // Halfway is the trap: the first file reports a length, the second does not. A percentage
        // that reappears after vanishing is worse than one that never showed — the player uses it
        // to decide whether to wait.
        var manifest = Good();
        using var temp = new TempModels(manifest);
        var store = new OfflineModelStore(
            new FakeHandler().RespondBytes(Native).RespondBytes(Model).NoContentLength(), manifest);

        var seen = new List<double?>();
        Assert.True((await store.InstallAsync(new SyncProgress(seen.Add))).Installed);

        var firstUnknown = seen.FindIndex(p => p is null);
        Assert.True(firstUnknown >= 0, "the second file's missing length was never noticed");
        Assert.All(seen.Skip(firstUnknown), p => Assert.Null(p));
    }

    // =============================================================================================
    //  Case 6 — Cancel leaves the directory as it was
    // =============================================================================================

    [Fact]
    public async Task Cancel_mid_download_leaves_the_directory_as_it_was()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        using var cts = new CancellationTokenSource();

        // Cancelled on the first progress callback, i.e. genuinely mid-transfer: the first bytes are
        // on disk in the staging directory when the token trips.
        var store = new OfflineModelStore(
            new FakeHandler().RespondBytes(Native).NoContentLength().RespondBytes(Model), manifest);

        var result = await store.InstallAsync(new SyncProgress(_ => cts.Cancel()), cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Installed);
        Assert.Equal(OfflineInstallFailure.None, result.Failure);   // a cancel is not a failure
        Assert.False(Directory.Exists(temp.Root));
        Assert.False(store.IsInstalled);

        // The row goes back to where it was and says nothing about what happened (AC 1's standard
        // for "Not now", applied to the button that stops a transfer).
        Assert.StartsWith("○ Not installed", UserMessages.AboutOfflineNotInstalled(),
                          StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_already_cancelled_costs_no_request_at_all()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        var handler = Serving();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new OfflineModelStore(handler, manifest).InstallAsync(null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(temp.Root));
    }

    // =============================================================================================
    //  Case 7 — I3: a timeout is not a cancel. The one this project has paid for before.
    // =============================================================================================

    [Fact]
    public async Task An_http_timeout_is_a_failure_and_never_a_cancel()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        // TimesOut() raises the real shape: a TaskCanceledException — an OperationCanceledException
        // subclass — carrying a token that is NOT cancelled. An unfiltered catch reads that as the
        // user pressing Cancel, and the player is told they stopped a download the network killed.
        var store = new OfflineModelStore(new FakeHandler().TimesOut(), manifest);

        var result = await store.InstallAsync();

        Assert.False(result.Cancelled);
        Assert.Equal(OfflineInstallFailure.Network, result.Failure);
        Assert.Equal("Download failed — the download did not finish. Nothing was installed.",
                     UserMessages.OfflineDownloadFailed(result.Failure));
        Assert.Empty(temp.Files());
    }

    [Fact]
    public async Task A_transport_failure_and_a_bad_status_both_read_as_the_download_not_finishing()
    {
        var manifest = Good();

        using (var temp = new TempModels(manifest))
        {
            var store = new OfflineModelStore(new FakeHandler().Throws(new HttpRequestException("no route")),
                                              manifest);
            Assert.Equal(OfflineInstallFailure.Network, (await store.InstallAsync()).Failure);
            Assert.Empty(temp.Files());
        }

        using (var temp = new TempModels(manifest))
        {
            var store = new OfflineModelStore(new FakeHandler().Respond(HttpStatusCode.NotFound), manifest);
            Assert.Equal(OfflineInstallFailure.Network, (await store.InstallAsync()).Failure);
            Assert.Empty(temp.Files());
        }
    }

    /// <summary>The source half of I3, in the shape the epic already uses: <b>every</b> catch of an
    /// <c>OperationCanceledException</c> on this path carries the filter. A scan, because the way
    /// this breaks is someone adding a second catch six months from now.</summary>
    [Fact]
    public void Every_cancellation_catch_on_the_download_path_is_filtered()
    {
        foreach (var file in new[] { "OfflineModelStore.cs" })
        {
            var code = Code(File.ReadAllText(ServiceFile(file)));
            var catches = Regex.Matches(code, @"catch\s*\(\s*(?:System\.)?(?:Task|Operation)Canceled\w*")
                               .Count;
            var filtered = Regex.Matches(code,
                @"catch\s*\(\s*(?:System\.)?(?:Task|Operation)Canceled\w*[^)]*\)\s*when\s*\(\s*ct\.IsCancellationRequested\s*\)")
                               .Count;
            Assert.True(catches > 0, $"{file} has no cancellation catch at all — the scan is vacuous");
            Assert.Equal(catches, filtered);
        }

        // …and every await in the store configures the context away, for the same reason the nine
        // files in HttpProviderCoreTests' scan do: the click handler awaits from the dispatcher and
        // a 50 MB copy loop's continuations have no business going back there.
        var statements = Code(File.ReadAllText(ServiceFile("OfflineModelStore.cs")))
            .Split(';').Select(s => s.Replace("\n", " ").Trim()).Where(s => s.Length > 0).ToList();
        var awaits = statements.Where(s => Regex.IsMatch(s, @"(^|[^\w.])await\s")).ToList();
        Assert.NotEmpty(awaits);
        Assert.All(awaits, s => Assert.Contains(".ConfigureAwait(false)", s, StringComparison.Ordinal));
    }

    // =============================================================================================
    //  Case 9 — I10 / AC 7: nothing about the store touches disk before first paint
    // =============================================================================================

    [Fact]
    public void Constructing_the_store_and_all_three_chains_opens_no_file_under_the_models_directory()
    {
        // The shape CacheLoadSpike's chain-composition case already proves for the cache: the root
        // is a path that does not exist, and after everything the constructor of MainWindow does
        // before InitializeComponent it still does not exist. A store that stat-ed its directory,
        // enumerated it or summed its sizes would have created nothing — but the assertions below
        // that DO fire are the ones that matter: no member with I/O in it is reachable from there.
        using var temp = new TempModels(Good());
        Assert.False(Directory.Exists(temp.Root));

        var settings = new AppSettings();
        var store = new OfflineModelStore();
        TranslationChains.BuildWrite(settings);
        TranslationChains.BuildRead(settings, RequestPriority.Background, out _);
        TranslationChains.BuildRead(settings, RequestPriority.Interactive);

        Assert.False(Directory.Exists(temp.Root),
            "something asked the model store a question before first paint (I10 / AC 7)");
        Assert.NotNull(store);
    }

    /// <summary>The other half, as a scan: the way I10 breaks is invisible — a convenience call
    /// added to the constructor by the next person, and 45 MB of directory is walked before first
    /// paint again. The permitted call sites are the post-paint warm-up in <c>OnWindowLoaded</c> and
    /// the two Click handlers, and none of them is the constructor or <c>ApplySettings</c>.</summary>
    [Fact]
    public void No_startup_path_asks_the_model_store_anything()
    {
        var main = File.ReadAllText(RepoFile("MainWindow.xaml.cs"));

        foreach (var member in new[] { "public MainWindow()", "private void ApplySettings()" })
        {
            var body = Code(Body(main, member));
            foreach (var asks in new[] { "IsInstalled", "BytesOnDisk", "InstalledPairs",
                                         "ModelDirectory", "NativeDirectory", "IsVerifiedNative" })
                Assert.False(body.Contains(asks, StringComparison.Ordinal),
                    $"{member} asks the model store \"{asks}\" — that is a disk read before first paint (I10)");
        }

        // …and the carve-out is not vacuous: the post-paint probe really is in OnWindowLoaded.
        Assert.Contains("_offlineStore.IsInstalled",
                        Code(Body(main, "private async void OnWindowLoaded(")), StringComparison.Ordinal);
    }

    // =============================================================================================
    //  Ruling E8-f — the trust chain
    // =============================================================================================

    [Fact]
    public void The_native_library_is_verified_by_its_hash_and_a_tampered_copy_is_refused()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        temp.Install(manifest, Native, Model);

        var dll = Path.Combine(temp.Root, OfflineModelManifest.NativeFileName);
        Assert.True(OfflineModelStore.IsVerifiedNative(dll));

        // The attack this exists to refuse: anything that can write to a user-writable directory
        // would otherwise get code execution inside the app, because NativeLibrary.Load on an
        // absolute path also resolves that library's dependencies from beside it.
        File.WriteAllBytes(dll, TempModels.Blob(11, Native.Length));
        Assert.False(OfflineModelStore.IsVerifiedNative(dll));

        // A marker file beside it changes nothing — which is exactly why the check is a hash and
        // not a marker: a marker is forgeable by copying two files into the folder.
        File.WriteAllText(Path.Combine(temp.Root, "verified.ok"), "yes");
        Assert.False(OfflineModelStore.IsVerifiedNative(dll));

        Assert.False(OfflineModelStore.IsVerifiedNative(Path.Combine(temp.Root, "nope.dll")));
        Assert.False(OfflineModelStore.IsVerifiedNative(null));
    }

    /// <summary>
    /// <b>The dependency half of ruling E8-f</b> (review). Hashing <c>bergamot.dll</c> proves
    /// nothing about the files Windows loads BECAUSE of it: <c>NativeLibrary.Load</c> on an absolute
    /// path uses <c>LOAD_WITH_ALTERED_SEARCH_PATH</c>, so the library's own directory goes to the
    /// front of the import search — and <c>bergamot.dll</c> imports <c>dbghelp</c>, which is not a
    /// KnownDLL. Dropping one beside a byte-perfect library is code execution inside the app without
    /// touching the file the digest covers, which is the attack the ruling's own remark describes.
    /// A dependency has no digest in the manifest to be checked against, so what is asserted is that
    /// there is nothing there to load: an install leaves exactly one <c>.dll</c> in that directory.
    /// </summary>
    [Fact]
    public void A_dll_planted_beside_the_library_refuses_the_load()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        temp.Install(manifest, Native, Model);

        var dll = Path.Combine(temp.Root, OfflineModelManifest.NativeFileName);
        Assert.True(OfflineModelStore.IsVerifiedNative(dll), "the fixture is not a verified install");

        // The library itself is untouched — its digest still matches — and the load is refused
        // anyway, because the bytes that would run are not only its own.
        var planted = Path.Combine(temp.Root, "dbghelp.dll");
        File.WriteAllBytes(planted, TempModels.Blob(13, 64));
        Assert.False(OfflineModelStore.IsVerifiedNative(dll));
        Assert.Equal(TempModels.Sha256(Native), TempModels.Sha256(File.ReadAllBytes(dll)));

        File.Delete(planted);
        Assert.True(OfflineModelStore.IsVerifiedNative(dll));

        // A side-by-side manifest steers the same search, so it is refused for the same reason.
        var sxs = Path.Combine(temp.Root, "bergamot.dll.manifest");
        File.WriteAllText(sxs, "<assembly/>");
        Assert.False(OfflineModelStore.IsVerifiedNative(dll));
        File.Delete(sxs);

        // …and the check is narrow on purpose: a marker an antivirus or the shell drops in the
        // directory may not silently disable the engine.
        File.WriteAllText(Path.Combine(temp.Root, "desktop.ini"), "[.ShellClassInfo]");
        Assert.True(OfflineModelStore.IsVerifiedNative(dll));
    }

    /// <summary>
    /// <b>The refusal <c>InstallAsync</c>'s own contract promises</b> (review): every failure path
    /// in it deletes the WHOLE root, which is only honest while it cannot be running over an install
    /// that was already there. The About tab makes that true by showing Remove instead of Download —
    /// but the store is headless (I2) and a contract that depends on a caller's button label is not
    /// a contract. Before the fix, one flaky response over an existing engine deleted 45 MB the user
    /// had already waited for.
    /// </summary>
    [Fact]
    public async Task An_install_that_is_already_there_is_never_downloaded_over()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        temp.Install(manifest, Native, Model);
        var before = temp.Files();

        // A handler that fails on the first byte: the shape that used to take the existing install
        // down with it.
        var handler = new FakeHandler().Throws(new HttpRequestException("no route"));
        var result = await new OfflineModelStore(handler, manifest).InstallAsync();

        Assert.True(result.Installed);              // it is installed — that is the honest answer
        Assert.False(result.Cancelled);
        Assert.Equal(0, handler.Requests);          // and not one byte was asked for
        Assert.Equal(before, temp.Files());
        Assert.True(new OfflineModelStore(manifest: manifest).IsInstalled);
    }

    /// <summary>…and the resolver really consults it. A behaviour test would have to load 22 MB of
    /// native code (CI-8 forbids it outright), so the pin is a scan of the one method that does the
    /// load — and it asserts the check comes BEFORE the load, which is the whole of the ruling.</summary>
    [Fact]
    public void BergamotEngines_resolver_refuses_a_library_the_manifest_does_not_know()
    {
        var code = Code(File.ReadAllText(ServiceFile("BergamotEngine.cs")));
        var resolve = Body(code, "private static IntPtr Resolve(");

        var check = resolve.IndexOf("OfflineModelStore.IsVerifiedNative(", StringComparison.Ordinal);
        var load = resolve.IndexOf("NativeLibrary.TryLoad(", StringComparison.Ordinal);

        Assert.True(check >= 0, "BergamotEngine.Resolve does not verify the library it loads (ruling E8-f)");
        Assert.True(load >= 0, "the scan lost sight of the load itself");
        Assert.True(check < load, "the hash is checked AFTER the library is loaded — which is not a check");
    }

    // =============================================================================================
    //  The manifest, and the placeholder discipline the owner's release has not lifted yet
    // =============================================================================================

    [Fact]
    public void A_manifest_with_placeholder_digests_installs_nothing_and_loads_nothing()
    {
        // The shipping table until the owner populates his release (T8). It must be HONEST rather
        // than permissive: no silent load, no "installed" row, and a failure sentence that says the
        // files could not be verified.
        var placeholder = new OfflineModelManifest("v", new[]
        {
            new OfflineFile(OfflineModelManifest.NativeFileName, Native.Length,
                            OfflineModelManifest.Todo, null),
        });
        using var temp = new TempModels(placeholder);

        Assert.False(placeholder.IsVerifiable);
        Assert.False(placeholder.Files[0].IsVerifiable);

        var store = new OfflineModelStore(Serving(), placeholder);
        Assert.False(store.IsInstalled);

        // Even with the right bytes already on disk, nothing claims to be installed and nothing
        // loads: with no digest to compare against there is no honest way to say a file is right.
        Directory.CreateDirectory(temp.Root);
        File.WriteAllBytes(Path.Combine(temp.Root, OfflineModelManifest.NativeFileName), Native);
        Assert.False(store.IsInstalled);
        Assert.False(OfflineModelStore.IsVerifiedNative(
            Path.Combine(temp.Root, OfflineModelManifest.NativeFileName)));
    }

    [Fact]
    public async Task The_placeholder_install_fails_with_the_verification_reason()
    {
        var placeholder = new OfflineModelManifest("v", new[]
        {
            new OfflineFile(OfflineModelManifest.NativeFileName, Native.Length,
                            OfflineModelManifest.Todo, null),
        });
        using var temp = new TempModels(placeholder);
        var handler = Serving();

        var result = await new OfflineModelStore(handler, placeholder).InstallAsync();

        Assert.Equal(OfflineInstallFailure.Verification, result.Failure);
        Assert.Equal(0, handler.Requests);      // there is nothing worth downloading
        Assert.Equal("Download failed — the engine's files could not be verified. Nothing was installed.",
                     UserMessages.OfflineDownloadFailed(result.Failure));
    }

    [Fact]
    public void The_shipping_manifest_names_the_files_the_owners_release_has_to_carry()
    {
        var manifest = OfflineModelManifest.Current;

        Assert.Equal(new[]
        {
            "bergamot.dll",
            "model.ruen.intgemm.alphas.bin",
            "vocab.ruen.spm",
            "lex.50.50.ruen.s2t.bin",
        }, manifest.Files.Select(f => f.FileName));

        Assert.Equal(new[] { "ru-en" }, manifest.Pairs);
        Assert.Equal(BergamotTranslator.DefaultPairs.Select(p => p.Source + "-" + p.Target),
                     manifest.Pairs);
        Assert.NotNull(manifest.Native);

        // The config the store writes is the one the spike measured against, and the model names in
        // it are the manifest's — a store that downloaded three files and pointed Marian at a fourth
        // would fail at load with no way for the user to tell why.
        var config = OfflineModelManifest.ConfigText("ru-en");
        Assert.Contains("relative-paths: true", config);
        foreach (var file in manifest.Files.Where(f => f.Pair is not null))
            Assert.Contains(file.FileName, config);
    }

    // =============================================================================================
    //  Ruling E8-b — the directory, and the copy that names it
    // =============================================================================================

    [Fact]
    public void The_model_root_is_LocalAppData_and_the_consent_copy_says_the_same_thing()
    {
        var saved = OfflineModelStore.PathOverride;
        try
        {
            OfflineModelStore.PathOverride = null;
            var real = OfflineModelStore.Root;

            Assert.Equal(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PWRUHelper", "models"), real);

            // …and NOT Roaming, which is where the app's other four files live. A roaming or
            // OneDrive-synced profile copies its contents at logon, and this directory is 45 MB —
            // the exact class of machine-dependent startup cost P1 spent a phase hunting (E8-b).
            Assert.NotEqual(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PWRUHelper", "models"), real);
        }
        finally
        {
            OfflineModelStore.PathOverride = saved;
        }

        // The consent dialog's whole job is to say where the 50 MB went, so the sentence and the
        // code may never disagree — that is the ticket ruling E8-b exists to prevent ("where did my
        // 50 MB go?"). Both halves are asserted: the right variable AND not the wrong one.
        var body = UserMessages.OfflineConsentBody();
        Assert.Contains(@"%LocalAppData%\PWRUHelper\models", body, StringComparison.Ordinal);
        Assert.DoesNotContain(@"%AppData%\PWRUHelper\models", body, StringComparison.Ordinal);

        // AC 1's four required facts, each in its own labelled line.
        Assert.Contains("22 MB", body, StringComparison.Ordinal);
        Assert.Contains("30 MB per language pair", body, StringComparison.Ordinal);
        Assert.Contains("130-310 MB of memory", body, StringComparison.Ordinal);
        Assert.Contains("Remove (deletes the files)", body, StringComparison.Ordinal);
        Assert.Equal("Add the offline engine?", UserMessages.OfflineConsentTitle());
    }

    // =============================================================================================
    //  Remove
    // =============================================================================================

    [Fact]
    public void Remove_deletes_every_file_and_reports_what_came_back()
    {
        // A native library big enough for the megabyte arithmetic in the status line to be worth
        // asserting — three, so "3 MB freed" is not a rounding accident.
        var big = TempModels.Blob(4, 3 * 1024 * 1024);
        var manifest = new OfflineModelManifest("test-tag", new[]
        {
            new OfflineFile(OfflineModelManifest.NativeFileName, big.Length, TempModels.Sha256(big), null),
            new OfflineFile("model.ruen.intgemm.alphas.bin", Model.Length, TempModels.Sha256(Model),
                            OfflineModelManifest.RuEn),
        });
        using var temp = new TempModels(manifest);
        temp.Install(manifest, big, Model);

        var store = new OfflineModelStore(manifest: manifest);
        Assert.True(store.IsInstalled);
        var before = store.BytesOnDisk;

        var freed = store.Remove();

        Assert.Equal(before, freed);
        Assert.False(Directory.Exists(temp.Root), "Remove must delete the files, as the dialog says");
        Assert.False(store.IsInstalled);
        Assert.Empty(store.InstalledPairs);
        Assert.Equal(0, store.BytesOnDisk);
        Assert.Null(store.ModelDirectory());
        Assert.Null(store.NativeDirectory());

        Assert.Equal("Offline engine removed — 3 MB freed from your disk.",
                     UserMessages.OfflineRemovedStatus(freed));

        Assert.Equal(0, store.Remove());        // idempotent: nothing there, nothing came back
    }

    [Fact]
    public void An_install_missing_one_file_is_not_an_install()
    {
        var manifest = Good();
        using var temp = new TempModels(manifest);
        temp.Install(manifest, Native, Model);
        var store = new OfflineModelStore(manifest: manifest);
        Assert.True(store.IsInstalled);

        // The three ways a directory stops being an engine, one at a time.
        var config = Path.Combine(temp.Root, "ru-en", BergamotTranslator.ConfigFileName);
        File.Delete(config);
        Assert.False(store.IsInstalled);
        File.WriteAllText(config, OfflineModelManifest.ConfigText("ru-en"));
        Assert.True(store.IsInstalled);

        File.Delete(Path.Combine(temp.Root, OfflineModelManifest.NativeFileName));
        Assert.False(store.IsInstalled);
    }

    // =============================================================================================
    //  IS-2/IS-3 — the guard, for the biggest thing this suite could write
    // =============================================================================================

    [Fact]
    public async Task No_case_ever_writes_to_the_real_LocalAppData_models_directory()
    {
        // UNTOUCHED rather than absent: a developer who has installed the engine legitimately has
        // one, and a suite that deletes 45 MB of theirs is a suite they turn off.
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PWRUHelper", "models");
        var existed = Directory.Exists(real);
        var stamp = existed ? Directory.GetLastWriteTimeUtc(real) : default;

        var manifest = Good();
        using (var temp = new TempModels(manifest))
        {
            var store = new OfflineModelStore(Serving(), manifest);
            Assert.True((await store.InstallAsync()).Installed);
            Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(OfflineModelStore.Root));
            store.Remove();
        }

        Assert.Equal(existed, Directory.Exists(real));
        if (existed) Assert.Equal(stamp, Directory.GetLastWriteTimeUtc(real));

        // …and the run-wide redirect is back, never null — null is the developer's own directory.
        Assert.Equal(TestModelsRedirect.Path, OfflineModelStore.PathOverride);
    }

    [Fact]
    public void The_store_has_the_two_seams_the_suite_needs_and_both_stay_internal()
    {
        var ctor = Assert.Single(typeof(OfflineModelStore).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.True(ctor.IsAssembly, "the handler seam is a test seam, not public API (IS-10)");
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(HttpMessageHandler));
        Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(OfflineModelManifest));

        // IS-2's other half: the path override exists, so no case can be written against the real
        // directory even by accident.
        Assert.NotNull(typeof(OfflineModelStore).GetField("PathOverride",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>A synchronous <see cref="IProgress{T}"/>. <c>Progress&lt;T&gt;</c> posts to a
    /// captured context, which in a headless test is the thread pool — so the callbacks would arrive
    /// after the assertions. This one runs on the reporting thread, which is what a test needs to
    /// observe an ORDER.</summary>
    private sealed class SyncProgress : IProgress<double?>
    {
        private readonly Action<double?> _report;
        public SyncProgress(Action<double?> report) => _report = report;
        public void Report(double? value) => _report(value);
    }

    private static int Utf8Length(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    private static int Occurrences(string text, string fragment)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0) { count++; at += fragment.Length; }
        return count;
    }

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    /// <summary>The body of a member, brace-matched from its signature — the shape TP-START-02's
    /// scan uses.</summary>
    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");
        var open = source.IndexOf('{', at);
        Assert.True(open >= 0, $"could not find the body of {signature}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced braces after {signature}");
        return "";
    }

    private static string ServiceFile(string name) => RepoFile(Path.Combine("Services", name));

    private static string RepoFile(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), $"expected {relative} at the repo root");
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

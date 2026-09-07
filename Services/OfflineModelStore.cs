using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PWRUHelper.Services;

/// <summary>Why an install did not happen. A <b>kind</b> and not a message: the store is UI-free
/// (I2) and the sentence is <c>UserMessages</c>'s (GAP-4). It is also I11's shape — a failure is
/// logged as one of these words, never as a URL or a path.</summary>
internal enum OfflineInstallFailure
{
    None = 0,
    /// <summary>The transfer did not complete: no route, an HTTP status that is not 200, or an
    /// <c>HttpClient</c> timeout — which is <b>not</b> a cancel (I3), and the distinction is the one
    /// this project has paid three releases for.</summary>
    Network,
    /// <summary>A file's size or SHA-256 did not match the manifest, or the manifest carries no
    /// digest to check against yet. A half-written 22 MB DLL the app then P/Invokes is the one
    /// failure mode worse than no feature at all (AC 8).</summary>
    Verification,
    /// <summary>The bytes arrived and could not be put on disk (no room, no permission).</summary>
    Disk,
    /// <summary>A URL outside the app's one allow-list. Structurally unreachable — the manifest
    /// composes every URL from one host constant — and checked anyway, because the check is what
    /// makes that true rather than merely intended (AC 6).</summary>
    Untrusted,
}

/// <summary>The answer to "did the install happen?", with enough to render AC 3's row and nothing
/// more. <paramref name="Cancelled"/> is its own field rather than a failure kind: a cancel is the
/// user's own gesture and says nothing at all (AC 1's standard for <c>Not now</c>, applied to the
/// button that stops a transfer).</summary>
internal sealed record OfflineInstallResult(bool Installed, OfflineInstallFailure Failure, bool Cancelled)
{
    internal static readonly OfflineInstallResult Ok = new(true, OfflineInstallFailure.None, false);
    internal static readonly OfflineInstallResult CancelledByUser = new(false, OfflineInstallFailure.None, true);
    internal static OfflineInstallResult Failed(OfflineInstallFailure why) => new(false, why, false);
}

/// <summary>
/// <b>The model store</b> — where the offline engine's files live, how they get there, how they are
/// verified and what <c>Remove</c> deletes. It is the class <c>BergamotTranslator</c>'s two
/// <c>Func&lt;string?&gt;</c> locators ask, and it never asks anything of the UI (I2): the About
/// tab calls this, this never calls the About tab, and no <c>System.Windows</c> type appears below.
///
/// <para><b>Where it lives — ruling E8-b, and this is the first file in the app that deliberately
/// diverges from Roaming.</b> <c>settings.json</c>, <c>provider-state.json</c>,
/// <c>translation-cache.json</c> and the log are all under <c>%AppData%</c>
/// (<see cref="Environment.SpecialFolder.ApplicationData"/>) and all four are kilobytes. This
/// directory is 22 MB of native library plus 22–37 MB of model per pair, and a roaming or
/// OneDrive-synced profile copies its contents at logon — the exact class of machine-dependent
/// startup cost P1 spent a whole phase hunting. It is also the Windows convention: machine-local,
/// re-downloadable binary data belongs in Local. The consent dialog's copy and
/// <c>ux-mode-degrade.md</c> §3.6/§4.2 say <c>%LocalAppData%</c> for the same reason and were
/// changed in the same commit — a divergence between the dialog and the directory is a support
/// ticket that reads "where did my 50 MB go?".</para>
///
/// <para><b>I10 — nothing here is asked before first paint.</b> Every member that touches the disk
/// is a method or a lazy property, and none of them is called from a constructor or a static
/// initialiser. <see cref="Root"/> is computed on demand and not cached in a static field, for the
/// same reason <c>ProviderGates.DefaultPath</c> and <c>TranslationCacheStore.DefaultPath</c> are:
/// this type's static initialiser must cost nothing at type-load. The About tab's state is computed
/// after <c>OnWindowLoaded</c>, on a pool thread, in the shape ruling E6-a established for the gate
/// state.</para>
///
/// <para><b>I11 — no URL, no path and no file name reaches the log.</b> A failure logs an
/// <see cref="OfflineInstallFailure"/> and stops there.</para>
/// </summary>
internal sealed class OfflineModelStore
{
    /// <summary>Where the model root is read from / written to (IS-2). Tests point this at a
    /// throwaway directory through <c>TempModels</c>, and <c>TestModelsRedirect</c>'s
    /// <c>[ModuleInitializer]</c> points it at one before any case runs. Null = the real directory.
    ///
    /// <para>This is the class that writes <b>50 MB</b>, so it gets its override on day one: the
    /// suite has already poisoned <c>settings.json</c>, the error-report directory,
    /// <c>provider-state.json</c> and <c>translation-cache.json</c> once each (IS-2/IS-3), and every
    /// one of those was smaller than a single file this one downloads.</para></summary>
    internal static string? PathOverride;

    /// <summary>The default location — <b>Local</b>, not Roaming: see the type's remarks for the
    /// ruling (E8-b) and the reason. Computed on demand rather than in a static field so this type
    /// costs nothing at type-load (I10).</summary>
    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PWRUHelper", "models");

    /// <summary>The directory the store will actually use. Exposed so the suite's own guard case can
    /// assert it is never the developer's real <c>%LocalAppData%</c> path — the guard
    /// <c>LoggingTests.The_test_run_never_writes_to_the_real_AppData_log</c> already is, for a file
    /// four orders of magnitude smaller.</summary>
    internal static string Root => PathOverride ?? DefaultRoot;

    /// <summary>Where a partial download lives while it is still partial: a subdirectory of the
    /// root, never the root itself, so a half-written file can never be named by
    /// <see cref="IsInstalled"/> and a failure has exactly one thing to delete.</summary>
    private static string StagingDirectory => Path.Combine(Root, ".download");

    private readonly HttpMessageHandler? _handler;
    private readonly OfflineModelManifest _manifest;

    /// <summary>
    /// Nothing here touches the disk and nothing here reads a setting (I10, I2): the store is
    /// constructed while <c>MainWindow</c>'s constructor runs, before first paint, and every
    /// question it can answer is asked later.
    /// </summary>
    /// <param name="handler">The HTTP seam E1.S1 landed, so the whole download path — progress,
    /// verification, cancellation, the I3 timeout case — is provable with nothing leaving the box
    /// (CI-8 forbids the model download by name). Null means the real client.</param>
    /// <param name="manifest">The table to install from. Null means <see cref="OfflineModelManifest.Current"/>,
    /// which is the shipping table unless a test has overridden it.</param>
    internal OfflineModelStore(HttpMessageHandler? handler = null, OfflineModelManifest? manifest = null)
    {
        _handler = handler;
        _manifest = manifest ?? OfflineModelManifest.Current;
    }

    // =============================================================================================
    //  The questions — every one of them lazy, and none of them asked before first paint (AC 7)
    // =============================================================================================

    /// <summary>
    /// Is a complete, manifest-shaped install on disk right now? <b>Lazy, and never called from a
    /// constructor or a static initialiser</b> — the wording <c>TranslationCacheStore.Count</c>
    /// already uses applies here too: a count is not a question about a translation, and neither is
    /// "what is in the models directory" a question about painting a window.
    ///
    /// <para>Size only, no hashing: the digests were checked when the files were installed, and the
    /// one file whose bytes are then <b>executed</b> is re-checked at load time by
    /// <see cref="IsVerifiedNative"/>. Hashing 45 MB to paint a row would be the I10 problem in
    /// another costume.</para>
    ///
    /// <para>False whenever the manifest cannot verify anything (the owner's release is not
    /// populated yet): with no digest to check against there is no honest way to say a file is the
    /// right one, so the About row says "not installed" rather than claiming an engine the resolver
    /// would refuse to load.</para>
    /// </summary>
    internal bool IsInstalled
    {
        get
        {
            if (!_manifest.IsVerifiable) return false;
            try
            {
                foreach (var file in _manifest.Files)
                {
                    var info = new FileInfo(PathOf(file));
                    if (!info.Exists || info.Length != file.Size) return false;
                }
                foreach (var pair in _manifest.Pairs)
                    if (!File.Exists(Path.Combine(PairDirectory(pair), OfflineModelManifest.ConfigFileName)))
                        return false;
                return true;
            }
            catch (Exception)
            {
                // A directory this process cannot stat is not an installed engine. It is also not a
                // reason to fail a repaint (the same "a status warm-up may not fail a launch" rule
                // OnWindowLoaded's gate warm-up follows).
                return false;
            }
        }
    }

    /// <summary>The pairs really on disk, for §4.2's row. Empty rather than null when nothing is
    /// installed, so a caller cannot render "ru→en" off a manifest that describes what WOULD be
    /// installed.</summary>
    internal IReadOnlyList<string> InstalledPairs =>
        IsInstalled ? _manifest.Pairs : Array.Empty<string>();

    /// <summary>How much the engine is costing on disk, for the sentence <see cref="Remove"/>'s
    /// caller writes. Lazy like the rest; 0 when nothing is there.</summary>
    internal long BytesOnDisk => DirectorySize(Root);

    /// <summary>The model directory for the installed pair, as <c>BergamotTranslator</c>'s
    /// <c>modelDirectory</c> locator wants it: the directory holding the model and its
    /// <c>config.txt</c>, or <b>null</b> when nothing is installed. A method and not a property
    /// because that is the shape the provider takes (<c>Func&lt;string?&gt;</c>) and because the
    /// answer changes while the app runs — the user can install or remove from About mid-session.
    ///
    /// <para>Wiring it into the chains is <b>E8.S5</b>'s (rulings E8-c/E8-e); this story only makes
    /// the store able to answer.</para></summary>
    internal string? ModelDirectory() =>
        IsInstalled && _manifest.Pairs.Count > 0 ? PairDirectory(_manifest.Pairs[0]) : null;

    /// <summary><c>BergamotTranslator</c>'s <c>nativeDirectory</c> locator: where
    /// <c>bergamot.dll</c> was downloaded to, or null when nothing is installed. The root itself —
    /// the library is shared by every pair.</summary>
    internal string? NativeDirectory() => IsInstalled ? Root : null;

    // =============================================================================================
    //  The trust anchor — ruling E8-f
    // =============================================================================================

    /// <summary>
    /// <b>Is this file the native library the manifest describes?</b> Full SHA-256, computed on the
    /// file about to be loaded, compared against the table that ships inside the exe.
    /// <c>BergamotEngine.Resolve</c> calls it before <c>NativeLibrary.TryLoad</c>, and that is the
    /// whole of ruling E8-f's trust chain: the library is loaded by absolute path out of a
    /// user-writable directory, and <c>NativeLibrary.Load</c> on an absolute path resolves that
    /// library's own dependencies from beside it — so anything that can write there would otherwise
    /// get code execution inside the app.
    ///
    /// <para><b>Why a hash and not a marker file.</b> A "verified" stamp beside the DLL is forgeable
    /// by copying two files into the folder, which is exactly the gesture the check exists to
    /// refuse. Hashing 22 MB costs about 20 ms and happens once per process (the runtime caches a
    /// successful resolve and never asks again), which is a price worth paying for a check that
    /// cannot be faked.</para>
    ///
    /// <para><b>And the library's own dependencies, which is the other half of the same hole</b>
    /// (review). Hashing the file that is named proves nothing about the files Windows loads
    /// BECAUSE of it: <c>NativeLibrary.Load</c> on an absolute path uses
    /// <c>LOAD_WITH_ALTERED_SEARCH_PATH</c>, so the library's directory replaces the app's at the
    /// front of the import search — and dropping a <c>dbghelp.dll</c> beside a byte-perfect
    /// <c>bergamot.dll</c> is code execution inside the app without touching the file this method
    /// hashes. There is nothing to hash there (the manifest describes four files and none of them is
    /// a dependency), so what is checked instead is that there is nothing there at all: the
    /// directory an install produces holds exactly one <c>.dll</c>, and a second one — or a
    /// side-by-side <c>.manifest</c>, which redirects the same search — refuses the load. The scan
    /// is one non-recursive enumeration of a four-entry directory, and it is deliberately narrow:
    /// only the extensions that can steer a native load, so an antivirus dropping a marker or a
    /// <c>desktop.ini</c> cannot silently disable the engine.</para>
    ///
    /// <para>False, never a throw, and false when the manifest has no digest yet: a build whose
    /// manifest is unpopulated refuses to load rather than loading silently.</para>
    /// </summary>
    internal static bool IsVerifiedNative(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var manifest = OfflineModelManifest.Current;
        var expected = manifest.Native;
        if (expected is null || !expected.IsVerifiable) return false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.Size) return false;
            if (!string.Equals(Sha256Of(path), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;

            return NoStrangerBesideIt(info);
        }
        catch (Exception)
        {
            // The resolver must never see an exception out of here — see BergamotEngine.Resolve's
            // own remark. An unreadable file is not a verified one.
            return false;
        }
    }

    /// <summary>The extensions that can steer a native load: a DLL the import table will find
    /// first, and a side-by-side manifest that can redirect one. Anything else in the directory is
    /// somebody's marker file and is none of this check's business.</summary>
    private static readonly string[] LoadSteeringExtensions = { ".dll", ".manifest" };

    /// <summary>Is the verified library alone in its directory, as an install leaves it? See
    /// <see cref="IsVerifiedNative"/>'s remarks — this is the dependency half of ruling E8-f, and it
    /// is a check for ABSENCE because a dependency has no digest in the manifest to be checked
    /// against.</summary>
    private static bool NoStrangerBesideIt(FileInfo library)
    {
        var directory = library.Directory;
        if (directory is null || !directory.Exists) return false;

        foreach (var neighbour in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(neighbour.Name, library.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (LoadSteeringExtensions.Contains(neighbour.Extension, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // =============================================================================================
    //  Install — off the UI thread, cancellable, and it never leaves half an engine behind
    // =============================================================================================

    /// <summary>
    /// <b>Download every file the manifest names, verify each one, and only then put them in
    /// place.</b> AC 3 (progress, cancellable, the app stays usable), AC 4 (no invented
    /// percentage), AC 6 (the allow-list is read, not widened) and AC 8 (size + SHA-256 before a
    /// file is moved into place) all land here.
    ///
    /// <para><b>Nothing is installed until everything is verified.</b> Files land in
    /// <see cref="StagingDirectory"/> as <c>.part</c>, are renamed inside it once they pass, and are
    /// moved into the real layout only after the last one has. Any failure, and any cancel, deletes
    /// the whole root — which is honest precisely because this method refuses to run over an
    /// existing install, so "the directory is as it was" means "the directory is gone again".</para>
    ///
    /// <para><b>I3, and it applies here hard.</b> An <c>HttpClient</c> timeout throws
    /// <see cref="TaskCanceledException"/> — an <see cref="OperationCanceledException"/> subclass —
    /// with the token NOT cancelled. Every catch below filters
    /// <c>when (ct.IsCancellationRequested)</c>, so a timeout renders <c>Download failed</c> and
    /// never <c>Download cancelled</c>. Getting this wrong is this project's most expensive past
    /// bug, and here it would blame the user for a network failure they did not cause.</para>
    /// </summary>
    /// <param name="progress">0–100, or <b>null</b> when the total is not known — AC 4's
    /// degradation, and the caller renders <c>Downloading…</c> with no percentage rather than
    /// inventing one. It is <c>double?</c> and not <c>double</c> so "unknown" is a value the type
    /// carries instead of a convention two files have to agree on.</param>
    internal async Task<OfflineInstallResult> InstallAsync(IProgress<double?>? progress = null,
                                                           CancellationToken ct = default)
    {
        if (!_manifest.IsVerifiable)
            return OfflineInstallResult.Failed(OfflineInstallFailure.Verification);

        // The refusal the remarks above promise, and it is load-bearing rather than tidy (review):
        // every failure path here deletes the WHOLE root, which is only honest while this method
        // cannot be running over an install that was already there. The About tab makes that true by
        // showing Remove instead of Download — but the store is a headless unit (I2) and its own
        // contract may not depend on a caller's button label. Already installed is a success: there
        // is nothing to download and the caller's "downloaded ⇒ enabled" is the right end state.
        if (IsInstalled) return OfflineInstallResult.Ok;

        // Every URL, checked before a single request is made (AC 6). The manifest composes them all
        // from one host constant, so this cannot fail in production — which is the point: the check
        // is what keeps that true, and a widened allow-list somewhere else would still be refused
        // here for the files this story downloads.
        foreach (var file in _manifest.Files)
            if (!UpdateService.IsTrustedDownload(_manifest.UrlFor(file)))
                return OfflineInstallResult.Failed(OfflineInstallFailure.Untrusted);

        var staging = StagingDirectory;
        using var http = CreateClient();

        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception)
        {
            return Abandon(OfflineInstallFailure.Disk);
        }

        long total = _manifest.TotalBytes;
        long done = 0;
        // Deliberately NOT reported before the first response. The manifest always knows the total,
        // so a 0 % reported here would be a percentage the app promised before it had heard from the
        // mirror at all — and AC 4's degradation is about what the MIRROR reports. The caller paints
        // its own starting row; the first honest number arrives with the first chunk.
        bool totalKnown = total > 0;

        // A local function rather than a lambda at the call site, and the reason is a test: the
        // ConfigureAwait scan splits a file into statements on ";", so a multi-statement lambda
        // INSIDE an awaited call cuts the await away from its .ConfigureAwait(false) and the scan
        // reads it as an offender. One statement per await keeps the pin honest.
        void Advance(int read)
        {
            done += read;
            progress?.Report(totalKnown ? Math.Min(100d, done * 100d / total) : null);
        }

        foreach (var file in _manifest.Files)
        {
            var part = Path.Combine(staging, file.FileName + ".part");
            string hash;

            try
            {
                using var response = await http
                    .GetAsync(_manifest.UrlFor(file), HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) return Abandon(OfflineInstallFailure.Network);

                // OQ-9's answer, and AC 4's exception rather than its normal case: a manifest we
                // publish always reports a total, so the no-percentage form is what a mirror that
                // stops answering Content-Length gets. Once unknown it stays unknown for the whole
                // install — a percentage that appears halfway through is worse than none.
                if (response.Content.Headers.ContentLength is null) totalKnown = false;

                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                hash = await CopyAndHashAsync(source, part, Advance, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The user pressed Cancel. The ONLY branch that may say so — see the filter's
                // absence below, which is where an HttpClient timeout goes.
                return Abandon(OfflineInstallFailure.None, cancelled: true);
            }
            catch (IOException)
            {
                return Abandon(OfflineInstallFailure.Disk);
            }
            catch (UnauthorizedAccessException)
            {
                return Abandon(OfflineInstallFailure.Disk);
            }
            catch (Exception)
            {
                // Everything else — no route, DNS, a proxy, and the TaskCanceledException an
                // HttpClient timeout raises with an UNCANCELLED token, which the filtered catch
                // above deliberately let through to here (I3).
                return Abandon(OfflineInstallFailure.Network);
            }

            // AC 8, both halves, before the file is named anything the app would load. A wrong size
            // is rejected without hashing; the digest is what a truncation-plus-padding could not
            // survive.
            try
            {
                var info = new FileInfo(part);
                if (!info.Exists || info.Length != file.Size
                    || !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Abandon(OfflineInstallFailure.Verification);

                File.Move(part, Path.Combine(staging, file.FileName), overwrite: true);
            }
            catch (Exception)
            {
                return Abandon(OfflineInstallFailure.Disk);
            }
        }

        // Everything verified — now, and only now, the real layout.
        try
        {
            foreach (var file in _manifest.Files)
            {
                var destination = PathOf(file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(Path.Combine(staging, file.FileName), destination, overwrite: true);
            }

            // Written last, and by us rather than downloaded: it is generated text with
            // `relative-paths: true`, so there is nothing to verify and nothing to mirror — and
            // being last makes it the honest completion marker BergamotTranslator already looks for.
            foreach (var pair in _manifest.Pairs)
                File.WriteAllText(
                    Path.Combine(PairDirectory(pair), OfflineModelManifest.ConfigFileName),
                    OfflineModelManifest.ConfigText(pair), new UTF8Encoding(false));

            Directory.Delete(staging, recursive: true);
        }
        catch (Exception)
        {
            return Abandon(OfflineInstallFailure.Disk);
        }

        progress?.Report(totalKnown ? 100d : null);
        return OfflineInstallResult.Ok;
    }

    /// <summary>
    /// <b>Delete the engine and say how much came back.</b> The whole root goes — the native
    /// library, every pair, the config and any staging left by a crash — because "Remove deletes
    /// the files" is the promise the confirmation dialog makes, and a store that left 22 MB of DLL
    /// behind would be lying in the one place the user is being asked to trust it.
    /// </summary>
    /// <returns>The bytes deleted, so the status line can report them. 0 when there was nothing
    /// there, which is also what a caller gets if the delete fails: nothing came back.</returns>
    internal long Remove()
    {
        var size = BytesOnDisk;
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return size;
        }
        catch (Exception)
        {
            Logging.Warn("the offline engine's files could not all be removed");
            return Directory.Exists(Root) ? 0 : size;
        }
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>Where one manifest row lands: the native library at the root, a pair's files in the
    /// pair's own directory. One mapping, used by the install, by <see cref="IsInstalled"/> and by
    /// the locators, so the three cannot come to disagree.</summary>
    private static string PathOf(OfflineFile file) =>
        file.Pair is null
            ? Path.Combine(Root, file.FileName)
            : Path.Combine(PairDirectory(file.Pair), file.FileName);

    private static string PairDirectory(string pair) => Path.Combine(Root, pair);

    /// <summary>Tear the whole root down and answer with the verdict. Called from every failure
    /// path, which is what makes "Nothing was installed." true rather than nearly true — this
    /// method only runs over an install that was not there a moment ago.</summary>
    private static OfflineInstallResult Abandon(OfflineInstallFailure why, bool cancelled = false)
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // I11: the kind, never the path. A root that will not delete is a worse problem than
            // this method can solve, and IsInstalled will still answer false — the files it needs
            // are not all there.
            Logging.Warn("a partial offline-engine download could not be cleaned up");
        }

        // A cancel is the user's own gesture and is not a failure (AC 3's Cancel returns the row to
        // "not installed" with no trace, the same standard AC 1 sets for "Not now").
        return cancelled ? OfflineInstallResult.CancelledByUser : OfflineInstallResult.Failed(why);
    }

    /// <summary>Stream to disk and hash on the way past, so a 22 MB file is read from the network
    /// once and from the disk never. <paramref name="onRead"/> is the progress callback and is
    /// invoked per buffer, not per byte.</summary>
    private static async Task<string> CopyAndHashAsync(Stream source, string destination,
        Action<int> onRead, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];

        using (var file = File.Create(destination))
        {
            int read;
            // Checked here as well as passed down: a content stream's own honouring of the token is
            // the transport's business, and "Cancel stops the transfer" may not depend on it.
            ct.ThrowIfCancellationRequested();
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onRead(read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long size = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                size += new FileInfo(file).Length;
            return size;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>A client per install, never a shared static one: the transfer is bounded by the
    /// token and not by a timeout (a 50 MB download over a slow line is not a stuck request), which
    /// is the same choice <c>UpdateService</c>'s asset client makes and the opposite of the 8 s one
    /// its API check uses.</summary>
    private HttpClient CreateClient()
    {
        var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Add("User-Agent", "PWRUHelper-OfflineEngine");
        return client;
    }
}

using System.IO;
using System.Runtime.InteropServices;
using BergamotTranslatorSharp;

namespace PWRUHelper.Services;

/// <summary>
/// The whole native surface of the offline engine, as an interface — <b>three C exports and no
/// abstraction layer</b> (E8.S2 T2, <c>benchmark-fournisseurs.md</c> §6.2):
/// <list type="bullet">
/// <item><c>translator_initialize(configPaths, n)</c> — the <b>factory</b> that hands back one of
/// these. Constructing the object IS the initialise call, which is why there is no
/// <c>Initialize()</c> method here: an engine that exists is an engine that came up.</item>
/// <item><c>translator_translate(handle, text, html)</c> — <see cref="Translate"/>, and
/// <see cref="TranslateBatch"/>, which is the SAME export called once for a whole frame with
/// <c>html: true</c>. The <c>&lt;p&gt;</c> wrapping and the split back out are the binding's
/// (<c>BlockingService.Translate(IEnumerable&lt;string&gt;)</c>), and they stay the binding's: an
/// HTML split re-implemented in this repo would be this app guessing at the engine's own output
/// shape, which is precisely the class of bug I5 exists to refuse.</item>
/// <item><c>translator_free(handle)</c> — <see cref="IDisposable.Dispose"/>. E8.S1 measured the
/// working set coming back (+6.0 MiB of 121 MiB, private bytes +0.4 MiB of 367 MiB), which is what
/// makes an unload policy (E8.S4) buildable at all.</item>
/// </list>
///
/// <para><b>Why it is an interface at all.</b> The fake behind it is the fixture for the whole of
/// epic E8 — E8.S3, E8.S4 and E8.S5 all stub against this seam — and CI-8 forbids the model
/// download and the 22 MB native DLL outright. Without the seam not one automated case in this epic
/// could run; with it, the suite never loads <c>bergamot.dll</c> at all (pinned in
/// <c>BergamotTranslatorTests</c>).</para>
/// </summary>
internal interface IBergamotEngine : IDisposable
{
    /// <summary>One line, one native call. <paramref name="html"/> is the export's own third
    /// argument, not a convenience: <c>false</c> is a plain line, <c>true</c> is markup the engine
    /// must keep the tags of.</summary>
    string Translate(string text, bool html);

    /// <summary>A whole OCR frame in ONE native call (§6.2) — 3.75 ms/line measured against
    /// 8.34 ms/line one call at a time. It returns what the engine returned and <b>nothing is
    /// padded here</b>: a count that does not match the input is the caller's to reject (I5).</summary>
    IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> lines);
}

/// <summary>
/// The production implementation: a thin wrapper over <c>BergamotTranslatorSharp</c>'s
/// <c>BlockingService</c>, and deliberately nothing more — no policy, no lifetime, no error
/// mapping. All three of those belong to <see cref="BergamotTranslator"/>, which is the class the
/// tests can actually reach.
///
/// <para><b>MPL-2.0.</b> The binding, <c>bergamot.dll</c> and the models are all MPL-2.0 while this
/// app is MIT (§7.6 constraint 8, NFR10). File-level copyleft ships beside an MIT app without
/// infecting it — and SignPath Foundation's "the app itself stays OSI-licensed" is met — but it is a
/// SECOND LICENCE: the MPL text ships with the downloaded artefacts and the About tab carries the
/// notice. Placement is E8.S3's block, packaging is E8.S6's; the obligation is written here and in
/// <c>PWRUHelper.csproj</c> so that dropping either of those two cannot ship the engine silently.
/// </para>
/// </summary>
internal sealed class BergamotEngine : IBergamotEngine
{
    /// <summary>The name the binding's <c>[LibraryImport]</c> asks the runtime for, and the file it
    /// resolves to. In one place because the resolver below is the only thing that answers it.</summary>
    private const string NativeLibraryName = "bergamot";

    private const string NativeFileName = "bergamot.dll";

    private readonly BlockingService _service;

    private BergamotEngine(BlockingService service) => _service = service;

    /// <summary>
    /// <c>translator_initialize</c> — U7's answer in one method. The package is referenced with
    /// <c>ExcludeAssets="native"</c>, so <c>bergamot.dll</c> is in no build output and in no bundle;
    /// it is downloaded beside the models by E8.S3's store and reached from there through
    /// <see cref="NativeLibrary.SetDllImportResolver"/>. E8.S1 measured that this costs the exe
    /// 0 bytes and leaves <c>%TEMP%\.net\PWRUHelper\&lt;id&gt;</c> at its five WPF files.
    ///
    /// <para><paramref name="configPath"/> is the Marian config file the engine reads (models,
    /// vocabs, shortlist, <c>relative-paths: true</c>) — <b>a file, not a directory</b>, written
    /// beside the model by the store. Whether it exists is <see cref="BergamotTranslator"/>'s
    /// question, asked before this is ever called.</para>
    /// </summary>
    internal static IBergamotEngine Create(string configPath, Func<string?> nativeDirectory)
    {
        // Assigned before the resolver can run: the runtime allows exactly ONE resolver per assembly
        // for the life of the process, so the callback has to read a static rather than close over
        // one instance's locator.
        //
        // What this write does NOT buy, stated because the obvious reading is wrong: it does not let
        // a second instance re-point an ALREADY LOADED library. The runtime consults the resolver
        // only until `bergamot` resolves, then caches the handle per (assembly, name) and never asks
        // again — so the first successful directory is the process's directory. A FAILED load is not
        // cached, which is the case that matters here: the store installing the engine mid-session
        // and a later Create picking it up still works. In production there is one store and one
        // directory, so the last-write-wins race between two Creates is theoretical; if E8.S3 ever
        // gives two instances two directories, this is the line that has to become immutable.
        Volatile.Write(ref _nativeDirectory, nativeDirectory);
        EnsureResolver();
        return new BergamotEngine(new BlockingService(configPath));
    }

    private static Func<string?>? _nativeDirectory;

    private static readonly object ResolverSync = new();

    private static bool _resolverInstalled;

    /// <summary>Install the DLL resolver, once per process. <see cref="NativeLibrary.SetDllImportResolver"/>
    /// throws <see cref="InvalidOperationException"/> if it is called twice for the same assembly,
    /// so the latch is the contract and not an optimisation — and the throw is swallowed for the one
    /// case that can still reach it: the E8.S1 spike harness installs its own resolver in the same
    /// process, and its directory is as good as ours.
    ///
    /// <para>A <c>lock</c> rather than an <see cref="Interlocked"/> latch, and the difference is not
    /// style: a latch claimed BEFORE the registration lets a second thread leave this method while
    /// no resolver is installed yet, call <c>translator_initialize</c>, and take a
    /// <see cref="DllNotFoundException"/> on a machine where the engine is correctly installed —
    /// mapped to <c>Unavailable</c>, with a gate window on top. "Once per process" has to mean
    /// "nobody proceeds until it is installed".</para>
    /// </summary>
    private static void EnsureResolver()
    {
        lock (ResolverSync)
        {
            if (_resolverInstalled) return;

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(BlockingService).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Someone in this process got there first (the spike harness). Theirs answers.
            }

            _resolverInstalled = true;
        }
    }

    /// <summary>
    /// <c>IntPtr.Zero</c> and <b>never a throw</b>, including out of the locator: returning zero
    /// lets the runtime fall through to its own probing and raise the
    /// <see cref="DllNotFoundException"/> the caller already maps to <c>Unavailable</c>. A resolver
    /// that threw would surface a raw exception from inside a static runtime callback, which is the
    /// one shape <see cref="BergamotTranslator"/> promises the chain it will never produce — and it
    /// would be misclassified on the way out, because a store failing to answer where its files are
    /// is not the engine misbehaving.
    ///
    /// <para><b>The obligation E8.S2 wrote here for E8.S3, now discharged (ruling E8-f).</b> This
    /// loads a 21.4 MB native library by absolute path out of a USER-WRITABLE directory, and
    /// <c>NativeLibrary.Load</c> on an absolute path also resolves that library's own dependencies
    /// from beside it — so anything that can write there would get code execution inside the app.
    /// The bytes therefore come from one place (a GitHub release of the owner's, through
    /// <c>UpdateService.IsTrustedDownload</c>, allow-list not widened) and are checked against a
    /// manifest that ships INSIDE the exe, twice: once by <c>OfflineModelStore</c> before the file
    /// is renamed into place, and once <b>here</b>, on the bytes about to be loaded. The second
    /// check is the one that matters — the first proves what was downloaded, this proves what is
    /// being executed — and it is a hash rather than a "verified" marker file precisely because a
    /// marker is forgeable by copying two files into the directory. It costs ~20 ms for 22 MB and
    /// runs once per process: the runtime caches a successful resolve and never asks again.</para>
    ///
    /// <para><b>The window between the hash and the load is open, on purpose, and E8.S4 looked at
    /// it.</b> A writer who can swap the file in the ~20 ms between <see cref="OfflineModelStore.IsVerifiedNative"/>
    /// and <see cref="NativeLibrary.TryLoad"/> gets the load — but that writer can already write this
    /// directory, so it is not a capability the check was ever going to remove. The cheap narrowing
    /// (hold the file open with a restrictive <c>FileShare</c> across both, so it cannot be replaced
    /// in between) was <b>considered and not taken</b>: whether Windows admits <c>LoadLibrary</c>'s
    /// execute-mapping open against such a handle cannot be established anywhere in this repo —
    /// CI-8 forbids the native load outright — and an untested change to the one path that P/Invokes
    /// 22 MB fails in the worse direction, refusing the engine on a correctly installed machine.
    /// It belongs to <b>E8.S7</b>, on the owner's machine, where the real DLL can be loaded and the
    /// mitigation actually verified rather than assumed.</para>
    /// </summary>
    private static IntPtr Resolve(string name, System.Reflection.Assembly _, DllImportSearchPath? __)
    {
        if (!string.Equals(name, NativeLibraryName, StringComparison.Ordinal)) return IntPtr.Zero;

        try
        {
            var dir = Volatile.Read(ref _nativeDirectory)?.Invoke();
            if (string.IsNullOrEmpty(dir)) return IntPtr.Zero;

            var path = Path.Combine(dir, NativeFileName);

            // Ruling E8-f. IntPtr.Zero and not a throw, like every other refusal in this method:
            // the runtime falls through to its own probing, fails to find `bergamot`, and raises the
            // DllNotFoundException BergamotTranslator already maps to Unavailable — which is the
            // honest answer for an engine whose files are not the ones this build knows about.
            if (!OfflineModelStore.IsVerifiedNative(path)) return IntPtr.Zero;

            return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    public string Translate(string text, bool html) => _service.Translate(text, html);

    public IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> lines) => _service.Translate(lines);

    /// <summary><c>translator_free</c>. The handle goes and, measured, so does the memory.</summary>
    public void Dispose() => _service.Dispose();
}

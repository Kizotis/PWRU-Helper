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
        // Assigned before the resolver can run, and on every Create: the runtime allows exactly ONE
        // resolver per assembly for the life of the process, so the callback has to read a field
        // rather than close over one instance's locator. In production there is one store and one
        // directory; a second instance with a different one still wins from here on rather than
        // being silently answered by the first instance's path.
        Volatile.Write(ref _nativeDirectory, nativeDirectory);
        EnsureResolver();
        return new BergamotEngine(new BlockingService(configPath));
    }

    private static Func<string?>? _nativeDirectory;

    private static int _resolverInstalled;

    /// <summary>Install the DLL resolver, once per process. <see cref="NativeLibrary.SetDllImportResolver"/>
    /// throws if it is called twice for the same assembly, so the latch is the contract and not an
    /// optimisation — and the throw is swallowed for the one case that can still reach it: the
    /// E8.S1 spike harness installs its own resolver in the same process, and its directory is as
    /// good as ours.</summary>
    private static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 1) return;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(BlockingService).Assembly, (name, _, _) =>
            {
                if (!string.Equals(name, NativeLibraryName, StringComparison.Ordinal)) return IntPtr.Zero;

                var dir = Volatile.Read(ref _nativeDirectory)?.Invoke();
                if (string.IsNullOrEmpty(dir)) return IntPtr.Zero;

                // IntPtr.Zero and never a throw: returning zero lets the runtime fall through to its
                // own probing and raise the DllNotFoundException the caller already maps to
                // Unavailable. A resolver that threw would surface a raw native exception from
                // inside a static callback, which is the one shape BergamotTranslator promises the
                // chain it will never produce.
                return NativeLibrary.TryLoad(Path.Combine(dir, NativeFileName), out var handle)
                    ? handle
                    : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // Someone in this process got there first (the spike harness). Theirs answers.
        }
    }

    public string Translate(string text, bool html) => _service.Translate(text, html);

    public IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> lines) => _service.Translate(lines);

    /// <summary><c>translator_free</c>. The handle goes and, measured, so does the memory.</summary>
    public void Dispose() => _service.Dispose();
}

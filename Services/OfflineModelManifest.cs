namespace PWRUHelper.Services;

/// <summary>
/// One file the offline engine needs, as the app knows it <b>before</b> anything is downloaded:
/// its name, how many bytes it must be, its SHA-256, and which directory under the model root it
/// belongs in.
/// </summary>
/// <param name="FileName">The asset's name on the release AND its name on disk. It is one string
/// for both on purpose: a rename between the two would be a second place to get the mapping wrong,
/// and the Marian config below names these files literally.</param>
/// <param name="Size">The exact byte count. Checked first because it is free — a truncated download
/// is the common failure and it costs no hashing to reject.</param>
/// <param name="Sha256">Lower-case hex, 64 characters — or <see cref="OfflineModelManifest.Todo"/>
/// while the owner has not yet published the release (see the type's remarks).</param>
/// <param name="Pair">The language pair this file belongs to (<c>"ru-en"</c>), or <c>null</c> for
/// the native library, which is shared by every pair and lives at the root.</param>
internal sealed record OfflineFile(string FileName, long Size, string Sha256, string? Pair)
{
    /// <summary>Does this row carry a real digest, or is it still the owner's to fill in? A
    /// placeholder may never be treated as "no check": <see cref="OfflineModelStore.IsInstalled"/>
    /// answers false and the install fails at verification, so a build with an unpopulated manifest
    /// cannot silently load 22 MB of unverified native code (ruling E8-f).</summary>
    internal bool IsVerifiable =>
        Size > 0 && Sha256.Length == 64
        && Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// <b>Ruling E8-f, as a table that ships inside the exe.</b> One release is one known set of bytes:
/// the store downloads only these names, from only this release, and verifies size + SHA-256 before
/// anything is renamed into place. <c>BergamotEngine.Resolve</c> then re-checks the native library's
/// digest against this same table before it is loaded, so "the store said it was fine" is not the
/// trust anchor — the bytes are.
///
/// <para><b>Why a C# table and not <c>Data/offline-manifest.json</c></b>, which was the other option
/// the ruling allowed. The three files under <c>Data/</c> are <c>EmbeddedResource</c> <b>and</b> are
/// copied next to the exe as a first-run <i>editable</i> copy — that is the whole point of them, and
/// <c>phrases.json</c>, <c>slang.json</c> and <c>squad.json</c> are all documented as user-editable.
/// A manifest with that treatment would put the trust anchor for a native code load in a file the
/// user (or anything running as the user) can rewrite, which is precisely the hole this table
/// exists to close. Keeping it in the assembly also removes a parse-failure path and a
/// resource-loading path from a security check that must have exactly one answer.</para>
///
/// <para><b>The release exists.</b> <c>offline-engine-v1</c> was published on 2026-09-09 with the
/// four assets below (beside the MPL-2.0 licence text and the notice, which the app never
/// downloads), and every size and digest here was taken from those exact bytes — the three model
/// files cross-checked against Mozilla Remote Settings' <c>decompressedSize</c> /
/// <c>decompressedHash</c> for the <c>tiny</c> ru→en <b>v3.0</b> records.</para>
///
/// <para><b>Updating the engine means republishing it</b>, in one movement: a new release tag, this
/// table repointed at it, and an app release that carries the new table. A manifest edited without a
/// release — or a release published without the matching edit — does not load different bytes, it
/// fails verification and takes the feature inert, which is the correct failure and not a reason to
/// relax the check. The procedure is <c>packaging/offline-engine-release.md</c>.</para>
///
/// <para><b><see cref="Todo"/> stays as the structural floor.</b> A row without a real digest is not
/// a row without a check: <see cref="OfflineFile.IsVerifiable"/> is false, so the store reports "not
/// installed", the install fails with the verification reason, and the native resolver refuses to
/// load. The download and verification paths are exercised end to end in the suite against a
/// manifest of the suite's own (see <see cref="Override"/>), never against the release.</para>
///
/// <para><b>I11</b>: nothing here is logged. A URL is a path, and a path is not a diagnostic.</para>
/// </summary>
internal sealed class OfflineModelManifest
{
    /// <summary>The digest of a file that has not been published yet — unused by the shipping table
    /// since <c>offline-engine-v1</c>, and kept for the next one. Deliberately not 64 hex
    /// characters, so <see cref="OfflineFile.IsVerifiable"/> rejects it structurally rather than by
    /// string comparison — a check that cannot be defeated by pasting a plausible-looking value.</summary>
    internal const string Todo = "TODO-owner";

    /// <summary>The owner's repository, and the ONLY host these bytes may come from. It is spelled
    /// here once and handed to <c>UpdateService.IsTrustedDownload</c>, which is the app's one
    /// allow-list (<c>github.com</c> / <c>*.githubusercontent.com</c>) and is <b>not widened</b> by
    /// this story (AC 6, <c>project-context.md</c>).</summary>
    private const string ReleaseBase = "https://github.com/Kizotis/PWRU-Helper/releases/download";

    /// <summary>The Marian config the engine reads, written by the store beside the model after the
    /// last file has been verified. Its NAME is <c>BergamotTranslator</c>'s const — the one contract
    /// between that class and this store — so it is referenced and never respelled.</summary>
    internal const string ConfigFileName = BergamotTranslator.ConfigFileName;

    /// <summary>The native library's file name, which is also its name on the release. Matched by
    /// <c>BergamotEngine</c>'s resolver, so a rename here is a rename there.</summary>
    internal const string NativeFileName = "bergamot.dll";

    /// <summary>The one pair release C installs (E8-a: one pair, <c>tiny</c> ru→en, is what the
    /// 150 MiB ceiling is written for). <c>BergamotTranslator.DefaultPairs</c> is the same fact on
    /// the provider side; §4.2's "[ Add a language pair ]" is out of scope for the prototype.</summary>
    internal const string RuEn = "ru-en";

    /// <summary>The release tag holding the assets below. A tag of its own rather than an app
    /// version: the engine's bytes change when Mozilla ships a new model, which has nothing to do
    /// with when this app ships.</summary>
    internal string Tag { get; }

    internal IReadOnlyList<OfflineFile> Files { get; }

    /// <summary>The download root. It defaults to <see cref="ReleaseBase"/>, which is the only value
    /// production ever uses — the parameter exists so a test can hand the store an <b>off-list</b>
    /// host and prove AC 6's refusal happens before a request is made. Without it that guard would
    /// be unreachable: every shipping URL is composed from one constant, so nothing could ever fail
    /// the check, and an assertion that cannot fail is not a guard.</summary>
    internal string Base { get; }

    internal OfflineModelManifest(string tag, IReadOnlyList<OfflineFile> files, string? baseUrl = null)
    {
        Tag = tag;
        Files = files;
        Base = baseUrl ?? ReleaseBase;
    }

    /// <summary>
    /// <b>The shipping table.</b> Sizes for the native library come from the E8.S1 spike
    /// (22,460,928 B, imports <c>KERNEL32 / SHELL32 / ole32 / dbghelp</c> only); the three model
    /// files are the <c>tiny</c> ru→en v3.0 set the spike measured at 22,530,152 B in total. All
    /// four sizes and digests are the published <c>offline-engine-v1</c> bytes.
    /// </summary>
    private static readonly OfflineModelManifest Shipping = new("offline-engine-v1", new[]
    {
        new OfflineFile(NativeFileName, 22_460_928, "c8210424785f762c91a530c741f68035f44eced4bd10ff88923cf16c42427a1e", null),
        new OfflineFile("model.ruen.intgemm.alphas.bin", 17_141_051, "b1d85c13cfbb05e1d326dd6f0fb5ef270a2011b547450260f96567a93f446c94", RuEn),
        new OfflineFile("vocab.ruen.spm", 905_257, "93bdc941b16e523695c319f74778bca9fd8b75a25ad75020cdc98aef74cdc0fc", RuEn),
        new OfflineFile("lex.50.50.ruen.s2t.bin", 4_483_844, "f654693577505fd38b1f3d220cdd4ffffbb45afb900a60cf751f0724eadc74e0", RuEn),
    });

    /// <summary>The manifest a test drives the store with (IS-2's shape, applied to a table instead
    /// of a path): the download and verification paths must be provable end to end without the
    /// owner's release existing, and without ever hashing 22 MB. Null = the shipping table.</summary>
    internal static OfflineModelManifest? Override;

    internal static OfflineModelManifest Current => Override ?? Shipping;

    /// <summary>Can this manifest verify anything at all? False while the owner's digests are
    /// outstanding, and every caller treats that as "nothing is installed and nothing can be" —
    /// never as "no check required".</summary>
    internal bool IsVerifiable => Files.Count > 0 && Files.All(f => f.IsVerifiable);

    /// <summary>The row for the native library, or null if the table has none.</summary>
    internal OfflineFile? Native =>
        Files.FirstOrDefault(f => f.Pair is null
            && string.Equals(f.FileName, NativeFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every language pair the table describes, in table order.</summary>
    internal IReadOnlyList<string> Pairs =>
        Files.Where(f => f.Pair is not null).Select(f => f.Pair!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Total bytes an install downloads — AC 4's percentage denominator, and the reason
    /// OQ-9's "the mirror does not report a total" is an exceptional path rather than the normal
    /// one: a manifest we publish always knows.</summary>
    internal long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>The download URL for one row. Composed here so the host appears in exactly one
    /// place in the app and is handed straight to the allow-list — a story that widens that list is
    /// wrong even if its own tests pass (<c>project-context.md</c>).</summary>
    internal string UrlFor(OfflineFile file) => $"{Base}/{Tag}/{file.FileName}";

    /// <summary>
    /// The Marian config, written by the store after verification. <c>relative-paths: true</c> is
    /// what makes the model directory movable and what lets the three file names below be bare; the
    /// knobs are the binding README's, at the values E8.S1 measured (a sweep moved RSS by ±1 MiB).
    /// </summary>
    internal static string ConfigText(string pair) => pair switch
    {
        RuEn => string.Join("\n", new[]
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
            "workspace: 128",
            "max-length-factor: 2.0",
            "skip-cost: true",
            "cpu-threads: 0",
            "quiet: true",
            "quiet-translation: true",
            "gemm-precision: int8shiftAlphaAll",
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(pair), pair, "no config for this pair"),
    };
}

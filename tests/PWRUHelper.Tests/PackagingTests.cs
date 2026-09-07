using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <b>E8.S6 — how <c>bergamot.dll</c> ships.</b> The decision is layout C: the native library is
/// downloaded with the models, so the portable exe does not grow, <c>%TEMP%\.net\PWRUHelper\&lt;id&gt;</c>
/// is not re-armed, and not one publish flag moves. That decision is worth exactly as much as the
/// things that keep it true, and this file pins those:
///
/// <list type="number">
/// <item>the one offline <c>PackageReference</c> still carries <c>ExcludeAssets="native"</c> — read
///   out of the project XML, not grepped, because a commented-out reference must not read as a
///   live one (the lesson <see cref="PublishFlagsTests"/> paid for with its own documentation);</item>
/// <item>no build path and no packaging document has learned about the native library, and
///   <c>IncludeNativeLibrariesForSelfExtract</c> is still <c>true</c> everywhere it appears — flag
///   <i>parity</i> is already pinned next door, but parity would survive all three files agreeing on
///   a changed value, and this story is the one that would have changed it;</item>
/// <item>the owner's release guide names every file the shipping manifest expects, and the MPL-2.0
///   text it promises is actually in the repo.</item>
/// </list>
///
/// <para>Nothing here builds, publishes or downloads. <see cref="PublishFlagsTests"/> is untouched
/// by design (CI-7): this story changed no publish flag, so it earned no right to edit that file.
/// The "<c>bergamot.dll</c> is absent from the build output" pin lives in
/// <c>BergamotTranslatorTests</c> where E8.S2 put it, and is not duplicated here.</para>
/// </summary>
public class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), $"{relative} is missing");
        var text = File.ReadAllText(path);
        Assert.NotEqual(string.Empty, text.Trim());   // an empty file passes every "does not contain"
        return text;
    }

    /// <summary>The publish flags of a build file, same parse as <see cref="PublishFlagsTests"/>'s
    /// and for the same reason: a flag named inside a comment that explains why it is <i>not</i>
    /// used must not read as a flag in use.</summary>
    private static HashSet<string> PublishFlags(string text) =>
        Regex.Matches(text, @"-p:(?<flag>[A-Za-z]+=[A-Za-z0-9.]+)")
             .Select(m => m.Groups["flag"].Value)
             .ToHashSet();

    private static readonly string[] BuildFiles =
    {
        "Build Portable EXE.bat",
        "Build MSI Installer.bat",
        ".github/workflows/release.yml",
    };

    // =============================================================================================
    //  T1 — layout C, as it appears in the project file
    // =============================================================================================

    /// <summary>
    /// The whole of layout C on the build side is four words in one attribute. Without them the
    /// 21.4 MB native asset flows into the publish output, into the single-file bundle, and — with
    /// <c>IncludeNativeLibrariesForSelfExtract</c> — into <c>%TEMP%\.net\PWRUHelper\&lt;id&gt;</c> on
    /// first launch, which is the exact mechanism P1 implicates.
    ///
    /// <para>Read from the XML rather than the text: <c>ExcludeAssets</c> is legal as an attribute
    /// <b>or</b> as a child element, and a grep for the string would also be satisfied by the long
    /// comment above the reference that explains what it does.</para>
    /// </summary>
    [Fact]
    public void The_offline_package_is_referenced_with_the_native_asset_excluded()
    {
        var project = XDocument.Parse(Read("PWRUHelper.csproj"));

        var refs = project.Descendants("PackageReference")
            .Where(e => string.Equals((string?)e.Attribute("Include"), "BergamotTranslatorSharp",
                                      StringComparison.OrdinalIgnoreCase))
            .ToList();

        var reference = Assert.Single(refs);

        // The version is pinned because every figure in the E8.S1 spike — 22,460,928 B of native
        // asset, 9,728 B of managed binding, the manifest's pinned size — was measured against it.
        Assert.Equal("0.5.1", (string?)reference.Attribute("Version"));

        var excluded = (string?)reference.Attribute("ExcludeAssets")
                       ?? (string?)reference.Element("ExcludeAssets");

        Assert.Equal("native", excluded?.Trim());
    }

    // =============================================================================================
    //  T5 / T6 — the build paths, which layout C does not touch
    // =============================================================================================

    /// <summary>
    /// <b>R-14, as a failing test rather than a paragraph.</b> Layout B — the DLL as a second file
    /// beside the exe — is the layout that would have needed a build file edited, and it is the one
    /// that breaks "one file, put it anywhere". Nothing that publishes the app may name the native
    /// library, and that includes <c>installer/Product.wxs</c> (where layout B's second
    /// <c>File</c> element would go) and <c>packaging/signpath-signing.md</c> (which carries a
    /// replacement <c>release.yml</c> and is the file people forget).
    ///
    /// <para>The offline engine's own guide is deliberately out of this list: naming the four assets
    /// is its entire job. What must not happen is a <i>build</i> learning about them.</para>
    /// </summary>
    [Fact]
    public void No_build_path_and_no_signing_document_names_the_native_library()
    {
        foreach (var file in BuildFiles.Concat(new[]
                 {
                     "packaging/signpath-signing.md",
                     "installer/Product.wxs",
                 }))
        {
            Assert.DoesNotContain("bergamot", Read(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The self-extraction flag is still on, and still on in all three paths. Its <i>parity</i> is
    /// pinned next door (<see cref="PublishFlagsTests.All_three_build_paths_publish_with_the_same_flags"/>),
    /// but parity is satisfied by three files that agree on a wrong value — and a packaging story
    /// that wanted to make a measurement convenient is exactly where all three would have been
    /// edited together. The value itself is what the WPF native DLLs need; layout C left it alone
    /// because layout C adds nothing for it to extract.
    /// </summary>
    [Fact]
    public void The_self_extract_flag_is_unchanged_in_every_build_path()
    {
        foreach (var file in BuildFiles)
            Assert.Contains("IncludeNativeLibrariesForSelfExtract=true", PublishFlags(Read(file)));
    }

    // =============================================================================================
    //  T7 — the MPL-2.0 obligation, and the guide that discharges it
    // =============================================================================================

    /// <summary>
    /// MPL-2.0 asks that the licence text accompany the distribution. The engine is distributed from
    /// a GitHub release, so the text is uploaded there — and it is carried in the repo so that
    /// uploading it is a copy and never a re-download from a URL nobody checked.
    /// </summary>
    [Fact]
    public void The_MPL_licence_text_ships_in_the_repo()
    {
        var licence = Read("packaging/LICENSE-MPL-2.0.txt");

        Assert.StartsWith("Mozilla Public License Version 2.0", licence, StringComparison.Ordinal);

        // Not merely the title: the whole text, ending where the canonical text ends. A truncated
        // licence is worse than a missing one — it looks discharged.
        Assert.Contains("2. License Grants and Conditions", licence, StringComparison.Ordinal);
        Assert.Contains("6. Disclaimer of Warranty", licence, StringComparison.Ordinal);
        Assert.Contains("7. Limitation of Liability", licence, StringComparison.Ordinal);
        Assert.Contains("Exhibit B - \"Incompatible With Secondary Licenses\" Notice",
                        licence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guide is the deliverable, and a guide that names three of four assets produces a release
    /// the app rejects at verification with no clue why. So the manifest is the source of truth and
    /// the guide is checked against it — the same direction E8.S3 pointed the store: one release is
    /// one known set of bytes.
    /// </summary>
    [Fact]
    public void The_owners_release_guide_names_every_file_the_manifest_expects()
    {
        var guide = Read("packaging/offline-engine-release.md");
        var shipping = ShippingManifest();

        Assert.Contains(shipping.Tag, guide, StringComparison.Ordinal);
        foreach (var file in shipping.Files)
            Assert.Contains(file.FileName, guide, StringComparison.Ordinal);

        // The two files the licence obligation adds to the release, and the notice that identifies
        // the covered artefacts. Both are in the repo, so uploading them is a copy.
        Assert.Contains("LICENSE-MPL-2.0.txt", guide, StringComparison.Ordinal);
        Assert.Contains("NOTICE-offline-engine.md", guide, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, Read("packaging/NOTICE-offline-engine.md").Trim());

        // The field the owner edits, named by name — "paste them into the manifest" is not an
        // instruction anybody can follow.
        Assert.Contains("OfflineModelManifest", guide, StringComparison.Ordinal);
        Assert.Contains("Shipping", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The check the guide sends the owner to run.</b> Four rows are filled in by hand from a
    /// PowerShell listing, and the dangerous outcome is not a wrong digest — the store rejects that
    /// at verification and deletes everything — but a <i>half</i>-filled table: three real digests
    /// and one leftover <c>TODO-owner</c> reads as a manifest somebody meant to finish. That state
    /// fails here, naming the row.
    ///
    /// <para>Read from the private <c>Shipping</c> field rather than from
    /// <c>OfflineModelManifest.Current</c>: <c>Current</c> honours the suite's <c>Override</c> seam,
    /// and this case is about the table that ships. Reading it directly also keeps this class out of
    /// the non-parallel <c>Gates</c> collection.</para>
    /// </summary>
    [Fact]
    public void The_shipping_manifest_is_wholly_the_owners_placeholder_or_wholly_populated()
    {
        var shipping = ShippingManifest();

        Assert.Equal("offline-engine-v1", shipping.Tag);

        var placeholders = shipping.Files
            .Where(f => string.Equals(f.Sha256, OfflineModelManifest.Todo, StringComparison.Ordinal))
            .Select(f => f.FileName)
            .ToList();

        Assert.True(placeholders.Count == 0 || placeholders.Count == shipping.Files.Count,
            "the shipping manifest is half-populated — still waiting on a digest for: "
            + string.Join(", ", placeholders)
            + ". Fill in every row or none: a table where some rows verify and others cannot is a "
            + "release somebody stopped halfway through, and the store will refuse the install "
            + "without saying which row it stopped on. See packaging/offline-engine-release.md.");

        if (placeholders.Count == 0)
        {
            // Populated: every row must actually verify. IsVerifiable is structural (size > 0, a
            // 64-character LOWER-CASE hex digest) — Get-FileHash returns upper case, which is the
            // one mistake this paste invites, and the guide says so.
            Assert.All(shipping.Files, f => Assert.True(f.IsVerifiable,
                $"{f.FileName}: size must be > 0 and the digest 64 lower-case hex characters"));
            Assert.True(shipping.IsVerifiable);
        }

        // Either way, the native row's size is the one the E8.S1 spike measured against the pinned
        // package. A different number means a different build of the DLL, and § 2 of the guide is
        // where to look.
        Assert.Equal(22_460_928, Assert.IsType<OfflineFile>(shipping.Native).Size);
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>The table that ships inside the exe, bypassing the test <c>Override</c> seam. The
    /// field name is load-bearing — the owner's guide tells him to open it by that name — so a
    /// rename failing here is the right outcome.</summary>
    private static OfflineModelManifest ShippingManifest()
    {
        var field = typeof(OfflineModelManifest)
            .GetField("Shipping", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(field != null,
            "OfflineModelManifest.Shipping is gone — packaging/offline-engine-release.md names it.");

        var shipping = Assert.IsType<OfflineModelManifest>(field!.GetValue(null));
        Assert.Equal(4, shipping.Files.Count);
        return shipping;
    }
}

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The publish flags that decide what users actually download are written out THREE times — in
/// "Build Portable EXE.bat", "Build MSI Installer.bat" and .github/workflows/release.yml — and
/// nothing made them agree. That is not hypothetical drift: packaging/signpath-signing.md already
/// described a CI wiring the workflow did not have.
///
/// The MSI is supposed to wrap the very same build the portable download gives you, so a flag that
/// exists in one file and not another silently ships two different apps.
/// </summary>
public class PublishFlagsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>Every <c>-p:Name=Value</c> in a file, whatever the line-continuation syntax around
    /// it (batch <c>^</c>, YAML <c>&gt;</c> folding). Order-insensitive.</summary>
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

    [Fact]
    public void The_single_file_bundle_is_never_compressed()
    {
        // Compressing the bundle stops Windows memory-mapping the assemblies: they are decompressed
        // into private RAM at every launch. Measured, same machine and build:
        //   compressed    74 MB exe -> 267 MB working set / 153 MB private
        //   uncompressed 179 MB exe -> 155 MB working set /  95 MB private
        // ~110 MB of RAM back to the machine also running the game, for download size only — and the
        // MSI recovers most of that size in its own CAB. If you are re-adding this flag, measure first.
        //
        // Checked on the PARSED flags, not the raw text: each of these files explains in a comment
        // why the flag is absent, and naming it there must not read as using it. (The first version
        // of this test grepped the text and failed on its own documentation.)
        foreach (var file in BuildFiles.Append("packaging/signpath-signing.md"))
            Assert.DoesNotContain(PublishFlags(Read(file)),
                flag => flag.StartsWith("EnableCompressionInSingleFile", StringComparison.Ordinal));
    }

    [Fact]
    public void All_three_build_paths_publish_with_the_same_flags()
    {
        var reference = PublishFlags(Read(BuildFiles[0]));

        Assert.NotEmpty(reference);   // a regex that matched nothing would pass every check below
        foreach (var file in BuildFiles.Skip(1))
            Assert.Equal(reference, PublishFlags(Read(file)));
    }

    [Fact]
    public void The_signing_wiring_is_applied_not_merely_drafted()
    {
        // packaging/signpath-signing.md used to carry a full replacement for release.yml, to be
        // applied once SignPath approved, and this test compared the two copies. The block has now
        // been APPLIED and deleted from the doc, so the thing worth guarding changed shape:
        //   1. the workflow must really contain the wiring the doc claims is live, and
        //   2. the doc must never grow a rival copy of the publish step again — a second set of
        //      -p: flags outside the three build files is exactly the drift this class exists for.
        var workflow = Read(".github/workflows/release.yml");
        Assert.Contains("SignPath/github-action-submit-signing-request", workflow);
        Assert.Contains("SIGNING_ENABLED", workflow);

        Assert.Empty(PublishFlags(Read("packaging/signpath-signing.md")));
    }
}

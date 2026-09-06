using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The error vocabulary every layer speaks. Nothing here changes behaviour: a failure that used to
/// carry only a sentence now carries a machine-readable <see cref="TranslationErrorKind"/> in front
/// of it, so the mapper, the breaker and the user-facing message stop being derived from a string.
/// </summary>
public class TranslationErrorsTests
{
    // architecture-cible.md §4.1, verbatim and in order. Pinned as text because three later pieces
    // are written against this exact vocabulary: the mapper's classification table, the breaker's
    // "which Kind opens the circuit" list, and the Friendly() switch. A rename or a reorder here is
    // a contract change, not a refactor.
    private static readonly string[] ExpectedKinds =
    {
        "RateLimited", "Blocked", "Unavailable", "Timeout", "Network", "BadResponse",
        "QuotaExhausted", "AuthFailed", "Cancelled", "AllProvidersPaused", "Unknown",
    };

    [Fact]
    public void The_enum_is_the_eleven_kinds_of_section_4_1_in_order()
        => Assert.Equal(ExpectedKinds, Enum.GetNames<TranslationErrorKind>());

    [Fact]
    public void Every_kind_round_trips_through_the_constructor()
    {
        // Enumerated rather than written out one by one, so Cancelled is covered without this file
        // naming it — the source scan below bans that token, and a test is source too.
        var retryAt = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
        {
            var bare = new TranslationException(kind, "boom");
            Assert.Equal(kind, bare.Kind);
            Assert.Equal("boom", bare.Message);
            Assert.Null(bare.RetryAt);
            Assert.Null(bare.ProviderId);

            var full = new TranslationException(kind, "boom", retryAt, "deepl");
            Assert.Equal(kind, full.Kind);
            Assert.Equal("boom", full.Message);
            Assert.Equal(retryAt, full.RetryAt);
            Assert.Equal("deepl", full.ProviderId);
        }
    }

    [Fact]
    public void The_only_constructor_takes_a_kind_first_and_no_message_only_overload_exists()
    {
        // Removing the message-only constructor is the point of the story: it is what forces every
        // existing throw site through the compiler and makes it state a Kind.
        var ctor = Assert.Single(typeof(TranslationException).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.Equal(
            new[] { typeof(TranslationErrorKind), typeof(string), typeof(DateTimeOffset?), typeof(string) },
            ctor.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void The_type_stays_public_and_in_PWRUHelper_Services()
    {
        // Both halves are load-bearing: the code-behind catches it (MainWindow.xaml.cs) and the
        // test project constructs it, and keeping the namespace avoids using-directive churn.
        Assert.True(typeof(TranslationException).IsPublic);
        Assert.Equal("PWRUHelper.Services", typeof(TranslationException).Namespace);
        Assert.Equal("PWRUHelper.Services", typeof(TranslationErrorKind).Namespace);
    }

    // ---- TP-MAP-17 -------------------------------------------------------------------------

    /// <summary>
    /// The I3 contract, as a test. <c>Cancelled</c> exists only so the classifier can be a total
    /// function; a provider that recognises a genuine cancellation <b>rethrows the original
    /// OperationCanceledException</b> and never wraps it. Wrapping it would turn every HttpClient
    /// timeout — an OCE whose token is NOT cancelled — into a phantom user-cancel, which is this
    /// project's most expensive past bug. Nothing in the app may construct that Kind, so nothing in
    /// the app may even name it.
    /// </summary>
    [Fact]
    public void No_production_source_names_Kind_Cancelled()
    {
        var files = ProductionSources();

        // Non-vacuity: a scan that found nothing (wrong root, wrong filter) would pass silently.
        Assert.True(files.Count > 20, $"the scan found only {files.Count} production .cs files");
        var throwers = files.Where(f => File.ReadAllText(f).Contains("throw new TranslationException("))
                            .Select(Path.GetFileName)
                            .ToList();
        Assert.Contains("TranslationService.cs", throwers);
        Assert.Contains("DeepLTranslator.cs", throwers);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("TranslationErrorKind.Cancelled", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} names TranslationErrorKind.Cancelled — rethrow the OperationCanceledException instead");
            // A `using static` would let a file write a bare `Cancelled` and slip past the scan above.
            Assert.False(Regex.IsMatch(text, @"using\s+static\s+[\w.]*TranslationErrorKind"),
                $"{Path.GetFileName(file)} imports TranslationErrorKind statically, which hides Cancelled from this scan");
        }
    }

    /// <summary>Every app .cs file: the repo root minus the test project and the build outputs.</summary>
    private static List<string> ProductionSources()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
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

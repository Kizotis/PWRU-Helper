using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// `TranslationPolicy` has no behaviour, so these are not behaviour tests: they are the guards that
/// keep it what it is meant to be — a graded table of today's numbers. Three things can go wrong
/// with such a file and all three are silent: a member arrives ungraded (AC 1), a number drifts
/// away from what the code actually does (AC 2), or a marker is written with a capital letter and
/// never matches the lower-cased body it is compared against (AC 3).
/// </summary>
public class TranslationPolicyTests
{
    /// <summary>The vocabulary the table is graded in. <c>[UNKNOWN]</c> joined it with E3.S4: a
    /// value that ships at the safe end of an open question is neither confirmed, nor measured, nor
    /// calibrated to a reported range — and grading it <c>[ASSUMED]</c> to satisfy the scan would
    /// have been the scan lying about the evidence, which is the one thing this file exists to stop.
    /// A fourth word is cheap; a mis-graded number is not.</summary>
    private static readonly string[] Grades = { "[CONFIRMED]", "[MEASURED]", "[ASSUMED]", "[UNKNOWN]" };

    // ---- AC 1: internal static, every member const or static readonly, nothing else ----------

    [Fact]
    public void The_type_is_a_static_internal_table_with_no_behaviour()
    {
        var t = typeof(TranslationPolicy);

        Assert.True(t.IsAbstract && t.IsSealed, "TranslationPolicy must be a static class");
        Assert.False(t.IsPublic, "TranslationPolicy is internal — the app reads it, InternalsVisibleTo lets the suite read it");
        Assert.Equal("PWRUHelper.Services", t.Namespace);

        // No methods, no properties, no constructor: a table of numbers cannot grow a code path
        // without someone noticing here.
        Assert.Empty(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        var fields = Fields(t);
        Assert.True(fields.Count >= 7, $"only {fields.Count} members found — the scan is looking at the wrong type");
        Assert.All(fields, f => Assert.True(f.IsLiteral || (f.IsStatic && f.IsInitOnly),
            $"{f.Name} is neither const nor static readonly"));
    }

    // ---- AC 1: every member says where its number came from ---------------------------------

    [Fact]
    public void Every_member_carries_exactly_one_evidence_grade()
    {
        var lines = File.ReadAllLines(PolicySource());
        var fields = Fields(typeof(TranslationPolicy));

        foreach (var f in fields)
        {
            // The DECLARATION line, found by name on the first line that is not itself a comment —
            // deliberately not `Name\s*=`, so splitting a declaration across two lines (name on one,
            // initializer on the next) keeps this green instead of failing on formatting. Comment
            // lines are skipped because the class summary and the section headers name members too.
            var i = Array.FindIndex(lines, l => !l.TrimStart().StartsWith("//")
                                                && Regex.IsMatch(l, $@"\b{Regex.Escape(f.Name)}\b"));
            Assert.True(i >= 0, $"{f.Name} was not found in a declaration line of TranslationPolicy.cs");

            // The grade may sit at the end of the declaration or in the comment block directly
            // above it; both read the same way to a human, so both count. The declaration runs to
            // its ';' — a member spread over several lines carries its grade on any of them.
            var block = lines[i];
            for (int j = i - 1; j >= 0 && lines[j].TrimStart().StartsWith("//"); j--) block = lines[j] + "\n" + block;
            for (int j = i; j + 1 < lines.Length && !lines[j].Contains(';'); j++) block += "\n" + lines[j + 1];

            var found = Grades.Where(g => block.Contains(g, StringComparison.Ordinal)).ToList();
            Assert.True(found.Count == 1,
                $"{f.Name} carries {found.Count} evidence grades; exactly one of {string.Join(" / ", Grades)} is required (AC 1)");
        }
    }

    // ---- AC 2: today's values, and only today's values ---------------------------------------

    [Fact]
    public void The_numbers_are_the_ones_the_code_uses_today()
    {
        // The numbers the code really runs on. The remaining §5.6 targets (the LIVE ones, the cache's
        // save debounce …) are deliberately absent until the code that reads them exists.
        Assert.Equal(12, TranslationPolicy.RequestTimeoutSeconds);
        // E2.S5 replaced MaxAttemptsToday = 3 / RetrySpacingBaseMs = 300 with §5.6's targets, in
        // the same commit that changed the loop — E1.S1 said it would. The literals are the point
        // HERE and only here: everywhere else the assertions are on relationships (one request on
        // a 429, two on a 503), so E2.S7's tuning commit touches this line and no other.
        Assert.Equal(2, TranslationPolicy.MaxAttempts);
        Assert.Equal(500, TranslationPolicy.BackoffBaseMs);
        Assert.Equal(500, TranslationPolicy.CacheCapacityToday);
        // E4.S1 split the two: 2000 is the shared TranslationCacheStore's capacity, 500 stays the
        // default of the CachingTranslator constructor that has no store (asserted by reflection
        // below). Listed here because this number is behaviour a user can feel — it decides how much
        // of a long session is still free after an hour. Unlike the rate-ceiling four (which are
        // deliberately unpinned, TranslationPolicy.cs's "§5.4" block), U8/E4.S3 is expected to move
        // this one: when it does, it edits THIS line and no other, which is the point of pinning it.
        Assert.Equal(2000, TranslationPolicy.CacheCapacity);
        Assert.Equal(1500, TranslationPolicy.MaxQueryBytes);

        // E3.S8's cap, and the literal belongs HERE and nowhere else (U9): PerLineFallbackTests
        // asserts the RELATIONSHIPS — at the cap every line is asked, one past it exactly one is
        // not — so a tuning commit that moves this number touches this line alone.
        Assert.Equal(8, TranslationPolicy.PerLineCap);

        // OQ-A's shipped answer, pinned so that turning it on is a deliberate act with a red test
        // in front of it rather than a one-character edit nobody reviews. E3.S1's capture flips
        // this line and TP-PRV-04 together, or neither.
        Assert.False(TranslationPolicy.GoogleDictBatchJoinEnabled,
            "the \\n-joined batch stays off until U1 is settled by a capture (OQ-A, architecture-cible §7.1)");
    }

    [Fact]
    public void The_call_sites_that_now_read_the_policy_still_behave_identically()
    {
        // The constants that replaced a literal, checked where they land rather than where they are
        // declared — a wrong reference would be invisible in the assertions above.
        var timeout = TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds);
        Assert.Equal(timeout, ClientOf(new GoogleGtxTranslator(new FakeHandler())).Timeout);
        Assert.Equal(timeout, ClientOf(new DeepLTranslator("k:fx", new FakeHandler())).Timeout);

        var capacity = typeof(CachingTranslator).GetConstructors().Single()
                                                .GetParameters().Single(p => p.Name == "capacity");
        Assert.Equal(500, capacity.DefaultValue);
    }

    // ---- AC 3: the HTML abuse-page markers ---------------------------------------------------

    [Fact]
    public void The_markers_are_section_4_3s_strings()
    {
        Assert.Equal(new[] { "automated queries", "unusual traffic" }, TranslationPolicy.RateLimitMarkers);
        Assert.Equal(new[] { "we're sorry", "captcha", "recaptcha" }, TranslationPolicy.BlockMarkers);
    }

    [Fact]
    public void Every_marker_is_lower_case_so_no_call_site_has_to_remember()
    {
        // E1.S4 matches these against de-tagged text that it has already lower-cased. A capital
        // letter here would simply never match, and nothing would fail loudly.
        foreach (var m in TranslationPolicy.RateLimitMarkers.Concat(TranslationPolicy.BlockMarkers))
        {
            Assert.False(string.IsNullOrWhiteSpace(m));
            Assert.Equal(m.ToLowerInvariant(), m);
            Assert.Equal(m.Trim(), m);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static List<FieldInfo> Fields(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
         .ToList();

    /// <summary>The private client a provider built for its injected handler. Asserts rather than
    /// returning null, so a renamed field reads as "the seam moved", not as a NullReferenceException
    /// at the call site.</summary>
    private static HttpClient ClientOf(object provider)
    {
        var field = provider.GetType().GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"{provider.GetType().Name} has no private _http field — the test seam moved");
        return Assert.IsType<HttpClient>(field!.GetValue(provider));
    }

    /// <summary>The policy file itself: the grades live in comments, which reflection cannot see.
    /// Walks up from the test output to the repo root, like TranslationErrorsTests' source scan.</summary>
    private static string PolicySource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");

        var path = Path.Combine(dir!.FullName, "Services", "TranslationPolicy.cs");
        Assert.True(File.Exists(path), $"TranslationPolicy.cs not found at {path}");
        return path;
    }
}

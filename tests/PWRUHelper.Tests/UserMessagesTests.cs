using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The copy deck, as a test. `UserMessages` has almost no behaviour, so most of what can go wrong
/// with it is silent: a Kind arrives with no sentence and the user reads a raw provider message; a
/// sentence grows an HTTP code; a sentence starts with "(" and a failure gets cached as a
/// translation (I4); a `System.Windows` type creeps in and the table stops being testable at all
/// (I2). Each of those has a case here.
///
/// The user-visible wording of this increment is pinned literally, on purpose: this is the story
/// that changes it, and the next edit to any of these sentences must be a deliberate one that
/// fails here first.
/// </summary>
[Collection("Gates")]
public class UserMessagesTests : GatesTestBase
{
    // The one Kind that must never reach a surface. TP-MAP-17's scan excludes tests/
    // (TranslationErrorsTests.ProductionSources), so a test file MAY name the token and
    // ProviderErrorMapperTests:25 does the same thing for the same reason — production cannot, and
    // a test that avoided the name too would be asserting on a member nobody could read. It is
    // named exactly once, here, and every use below compares against this VALUE, so no assertion
    // in this file depends on the spelling.
    private const TranslationErrorKind UserCancelled = TranslationErrorKind.Cancelled;

    // ---- AC: every Kind that can reach a player has exactly one sentence ---------------------

    /// <summary>
    /// Enumerated rather than listed, so a Kind added later fails here instead of shipping a raw
    /// provider message to a player. Two values are skipped and both are skipped for a stated
    /// reason: the cancel Kind renders nothing anywhere (§2.1), and `Unknown` is §4.4's deliberate
    /// pass-through of whatever the provider said.
    /// </summary>
    [Fact]
    public void Every_kind_a_user_can_reach_has_a_non_empty_sentence()
    {
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
        {
            if (kind == UserCancelled || kind == TranslationErrorKind.Unknown)
            {
                Assert.Null(UserMessages.Sentence(kind));
                continue;
            }

            var sentence = UserMessages.Sentence(kind);
            Assert.False(string.IsNullOrWhiteSpace(sentence), $"{kind} has no sentence in UserMessages");
        }
    }

    /// <summary>
    /// The exact copy of this increment, per Kind. `Friendly` is what the four surfaces render, so
    /// it is what is asserted — a sentence could otherwise be correct in the table and unreachable
    /// through the switch.
    /// </summary>
    [Theory]
    [InlineData(TranslationErrorKind.RateLimited,
        "The translation service asked us to slow down — try again in a moment")]
    [InlineData(TranslationErrorKind.Blocked,
        "The translation service is refusing requests from your connection right now")]
    [InlineData(TranslationErrorKind.Unavailable,
        "The translation service is down right now — try again shortly")]
    [InlineData(TranslationErrorKind.Timeout,
        "The translation service took too long to answer — try again shortly")]
    [InlineData(TranslationErrorKind.Network,
        "No internet connection — nothing can be translated until it is back")]
    [InlineData(TranslationErrorKind.BadResponse,
        "The translation service sent something we could not read — try again shortly")]
    [InlineData(TranslationErrorKind.QuotaExhausted,
        "Your free translation quota is used up for this month")]
    [InlineData(TranslationErrorKind.AuthFailed,
        "Your API key was refused — check it in About, or clear it")]
    [InlineData(TranslationErrorKind.AllProvidersPaused,
        "All engines are paused — nothing you need to do; it retries on its own")]
    public void Friendly_renders_one_fixed_sentence_per_kind(TranslationErrorKind kind, string expected)
    {
        // The provider's own message is deliberately something else: what the user reads must come
        // from the table, not from the throw site. The message stays meaningful for the log.
        var ex = new TranslationException(kind, "raw provider text (HTTP 429) — not for the user");

        Assert.Equal(expected, UserMessages.For(ex));
        Assert.Equal(expected, MainWindow.Friendly(ex));
    }

    /// <summary>
    /// §4.4's pass-through. `Unknown` is the mapper's last resort — the failure nobody has a rule
    /// for yet — and the only useful thing to show is what the provider actually said.
    /// </summary>
    [Fact]
    public void Unknown_still_renders_the_exceptions_own_message()
    {
        var ex = new TranslationException(TranslationErrorKind.Unknown,
            "Translation service error (HTTP 418). Please try again later.");

        Assert.Equal("Translation service error (HTTP 418). Please try again later.", MainWindow.Friendly(ex));
    }

    /// <summary>
    /// The price of the pass-through above, paid once: `Unknown` hands the PROVIDER's message to
    /// the player, so that message may never carry anything of the user's. They are fixed literals
    /// today (the only interpolation any of them takes is the HTTP status code), but "I read them
    /// and they looked fine" is not a guard — this drives both providers with a sentinel user text
    /// and a sentinel API key through every failure a message can come out of, and asserts neither
    /// survives into `ex.Message` or into what `Friendly` renders.
    ///
    /// Only non-retried statuses are used, so the case costs no backoff.
    /// </summary>
    [Fact]
    public async Task No_provider_message_the_player_can_read_carries_the_users_text_or_a_key()
    {
        const string sentinel = "секретное SENTINEL сообщение";
        const string sentinelKey = "SENTINEL-api-key-0000:fx";
        var body = $"{{\"error\":\"{sentinel}\",\"key\":\"{sentinelKey}\"}}";

        var raised = new List<TranslationException>();

        // Google, keyless. 400/404 are the Unknown pass-through — the one Kind whose provider
        // message the player actually reads — and 401/403 are the other non-retried statuses.
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound,
                                       HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
            raised.Add(await Assert.ThrowsAsync<TranslationException>(
                () => new TranslationService(new FakeHandler().Respond(status, body))
                          .TranslateAsync(sentinel, "ru", "en")));

        // A 200 whose body is not the provider's shape, and a transport failure: the two messages
        // that are built where the body and the exception are both in scope.
        raised.Add(await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(new FakeHandler().Respond(HttpStatusCode.OK, body, "text/plain"))
                      .TranslateAsync(sentinel, "ru", "en")));
        raised.Add(await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(new FakeHandler().Throws(new HttpRequestException(sentinel)))
                      .TranslateAsync(sentinel, "ru", "en")));

        // DeepL, with a key actually set — the provider whose messages are about the key.
        foreach (var status in new[] { HttpStatusCode.Unauthorized, (HttpStatusCode)456,
                                       HttpStatusCode.BadRequest })
            raised.Add(await Assert.ThrowsAsync<TranslationException>(
                () => new DeepLTranslator(sentinelKey, new FakeHandler().Respond(status, body))
                          .TranslateAsync(sentinel, "ru", "en")));

        Assert.Equal(9, raised.Count);   // non-vacuity: every branch above produced a message
        foreach (var ex in raised)
        {
            Assert.DoesNotContain("SENTINEL", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("секретное", ex.Message, StringComparison.Ordinal);
            // …and the same for what the surface renders, which is the thing that actually matters.
            Assert.DoesNotContain("SENTINEL", MainWindow.Friendly(ex), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The switch must not throw on a Kind it has never seen — a player mid-fight is the worst
    /// possible place to discover an unhandled enum member. Cast a value that is in no member's
    /// place; the exception's own message is the honest answer.
    /// </summary>
    [Fact]
    public void An_unmapped_kind_falls_back_instead_of_throwing()
    {
        var ex = new TranslationException((TranslationErrorKind)9999, "something new");

        Assert.Null(UserMessages.Sentence((TranslationErrorKind)9999));
        Assert.Equal("something new", MainWindow.Friendly(ex));
    }

    // ---- AC: the two raw-exception arms and the default are unchanged in shape ---------------

    /// <summary>
    /// Not everything reaches the code-behind classified: the OCR path can still surface a raw
    /// transport failure. These two arms existed before this story and are kept
    /// — what changed is that they now answer with the same sentence as their typed twins, so a
    /// dead network reads identically whether or not it was mapped on the way up.
    /// </summary>
    [Fact]
    public void The_raw_transport_exceptions_render_the_same_sentences_as_their_kinds()
    {
        Assert.Equal(UserMessages.Network, MainWindow.Friendly(new HttpRequestException("no such host is known")));
        Assert.Equal(UserMessages.Timeout, MainWindow.Friendly(new TaskCanceledException()));
    }

    /// <summary>Anything else is still shown as-is — the arm that keeps a genuinely unexpected
    /// failure legible instead of dressing it up as a translation problem.</summary>
    [Fact]
    public void Any_other_exception_still_shows_its_own_message()
        => Assert.Equal("disk on fire", MainWindow.Friendly(new InvalidOperationException("disk on fire")));

    // ---- I4: nothing here may start a sentence with "(" --------------------------------------

    /// <summary>
    /// The feed rows wrap this text in parentheses (`MainWindow.Live.cs`, `MainWindow.Ocr.cs`) and
    /// `CachingTranslator.IsCacheable` refuses to cache anything starting with "(". A sentence that
    /// already opened with one would not double the parens — it would sail through the cache guard
    /// on the OTHER call sites and store a failure as a translation.
    /// </summary>
    [Fact]
    public void No_sentence_starts_with_the_character_that_marks_a_failure()
    {
        foreach (var s in Sentences())
            Assert.False(s.StartsWith('('), $"a copy-deck sentence starts with '(' — I4: {s}");

        // The guard this protects, pinned by source rather than by call: it is private, and making
        // it internal to assert on it would widen an API for a test. If this line stops matching,
        // the rule above has moved and this case must be re-pointed, not deleted.
        Assert.Contains("!value.StartsWith('(')",
            File.ReadAllText(ServiceSource("CachingTranslator.cs")));
    }

    // ---- H10: the house rules for the copy itself --------------------------------------------

    /// <summary>
    /// UX hints H10/H11, which only became testable once the copy lived in one table (ruling
    /// GAP-4). No HTTP status code, no provider internal, no bare "error", no exclamation mark,
    /// and short enough for the 360 px compact overlay.
    /// </summary>
    [Fact]
    public void No_sentence_leaks_a_status_code_or_a_provider_internal()
    {
        var banned = new[] { "gtx", "dict-chrome-ex", "http", "error", "!", "{p}", "{t}" };

        foreach (var s in Sentences())
        {
            var lower = s.ToLowerInvariant();
            foreach (var b in banned)
                Assert.False(lower.Contains(b, StringComparison.Ordinal),
                    $"a copy-deck sentence contains \"{b}\": {s}");

            Assert.False(Regex.IsMatch(s, @"\b[45]\d\d\b"), $"a copy-deck sentence names a status code: {s}");
            Assert.True(s.Length <= 80, $"a copy-deck sentence is {s.Length} chars, over §3's 80: {s}");
            Assert.Equal(s.Trim(), s);
            // No terminal punctuation: every one of today's six call sites JOINS this text into a
            // longer line, none renders it alone. The composed cases are pinned below.
            Assert.DoesNotContain(s[^1], ".!?;:,");
        }
    }

    // ---- the sentences as the six surfaces actually render them -------------------------------

    /// <summary>
    /// The gap every layer of this story's review found: nine cases pinned the bare sentence and
    /// nothing pinned a COMPOSED string, so the deck could be correct and every surface still read
    /// badly. It did — with the deck's own full stop these came out as
    /// "Live stopped after repeated errors (…shortly.)." and "⚠ …shortly. — your text is kept".
    ///
    /// The wrappers are copied here as literals rather than invoked, because four of the six live
    /// in `async void`-ish UI paths that need a window; if a wrapper is ever edited, this test
    /// keeps saying what the old one produced and the pin at the bottom of the file is what
    /// catches the drift for the two AC-2 ones.
    /// </summary>
    [Fact]
    public void Every_surface_reads_as_a_sentence_once_the_wrapper_is_applied()
    {
        foreach (var s in Sentences())
        {
            var composed = new[]
            {
                $"Failed: {s}",                                     // Translate.cs:106
                $"({s})",                                           // Live.cs:281, Ocr.cs:298
                $"Live hiccup ({s}) — retrying…",                   // Live.cs:238
                $"Live stopped after repeated errors ({s}).",       // Live.cs:235
                $"OCR failed: {s}",                                 // Ocr.cs:248
                $"⚠ {s} — your text is kept, press Enter to retry.",// CompactOverlay.xaml.cs:142
            };

            foreach (var line in composed)
            {
                Assert.DoesNotContain("..", line);       // no doubled stop
                Assert.DoesNotContain(".)", line);       // no stop inside an inline parenthetical
                Assert.DoesNotContain(". —", line);      // no stop before a continuing clause
                Assert.DoesNotContain("((", line);       // I4's marker, not doubled by the deck
                Assert.DoesNotContain("  ", line);
            }

            // The feed row is the one surface with a hard budget of its own: it is a row in a
            // 360 px overlay list, and a batch failure stamps EVERY row in the batch with it.
            Assert.True($"({s})".Length <= 110,
                $"a feed row is {$"({s})".Length} chars, over the 110 the feed can carry: ({s})");
        }
    }

    /// <summary>H11 — each sentence exists exactly once. Two Kinds sharing a string is how the
    /// deck rots back into "an unexpected response" meaning four different things.</summary>
    [Fact]
    public void Every_sentence_is_distinct()
    {
        var all = Sentences();
        Assert.Equal(all.Count, all.Distinct().Count());
        // Non-vacuity: a reflection scan that found nothing would pass every assertion above.
        Assert.True(all.Count >= 9, $"only {all.Count} sentences found — the scan is reading the wrong type");
    }

    // ---- I2: the table stays UI-free ---------------------------------------------------------

    /// <summary>
    /// The reason this file can exist at all: `Services/` holds no UI type, so the copy is
    /// assertable without an STA host, a dispatcher or a window. It is also why the countdown is
    /// NOT rendered here — `RetryAt` is formatted by `MainWindow` at display time (§4.4).
    /// </summary>
    [Fact]
    public void The_table_is_a_static_internal_type_with_no_UI_dependency()
    {
        var t = typeof(UserMessages);

        Assert.True(t.IsAbstract && t.IsSealed, "UserMessages must be a static class");
        Assert.False(t.IsPublic, "UserMessages is internal — the app reads it, InternalsVisibleTo lets the suite read it");
        Assert.Equal("PWRUHelper.Services", t.Namespace);

        // CODE only: the file's own doc comments name the very types they promise not to use
        // ("no System.Windows type", "MainWindow formats the countdown"), which is documentation
        // doing its job — a scan that read them would fail on the explanation of why it exists.
        var code = string.Join('\n', File.ReadAllLines(ServiceSource("UserMessages.cs"))
                                         .Where(l => !l.TrimStart().StartsWith("//")));
        foreach (var forbidden in new[] { "System.Windows", "DispatcherTimer", "MainWindow", "RetryAt" })
            Assert.False(code.Contains(forbidden, StringComparison.Ordinal),
                $"UserMessages references {forbidden} — Services/ stays UI-free (I2)");
    }

    /// <summary>
    /// The point of the ruling, stated as a scan: the sentences live in the table and nowhere else.
    /// `MainWindow.xaml.cs` used to hold two of them inline; if one comes back, the two copies drift
    /// and the user reads whichever file was edited last.
    /// </summary>
    [Fact]
    public void The_code_behind_holds_no_copy_of_its_own()
    {
        var friendly = File.ReadAllText(RepoFile("MainWindow.xaml.cs"));

        Assert.Contains("UserMessages.For(ex)", friendly);
        foreach (var s in Sentences())
            Assert.False(friendly.Contains(s, StringComparison.Ordinal),
                $"MainWindow.xaml.cs holds its own copy of a copy-deck sentence: {s}");
        // The two literals this story removed, named so a revert is loud.
        Assert.DoesNotContain("\"no Internet connection\"", friendly);
        Assert.DoesNotContain("\"the request timed out\"", friendly);
    }

    // ---- AC: the parenthesised feed-row wrapper is untouched ----------------------------------

    /// <summary>
    /// The two feed-row call sites are the ones the owner's screenshot shows, and the "(" they add
    /// is the I4 marker that keeps a failure out of the cache. This story changes what goes INSIDE
    /// the parentheses and nothing else.
    /// </summary>
    [Theory]
    [InlineData("MainWindow.Live.cs")]
    [InlineData("MainWindow.Ocr.cs")]
    public void The_feed_rows_still_wrap_the_sentence_in_parentheses(string file)
        => Assert.Contains("it.TranslationBody = $\"({Friendly(ex)})\";", File.ReadAllText(RepoFile(file)));

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Every sentence in the table, read by reflection so a new one is covered by the house rules
    /// the day it is added — and cross-checked against what the LOOKUP can actually return, which
    /// is the part that was missing. `IsLiteral` sees only `const`: a sentence added as
    /// `static readonly` (the natural shape the moment one is composed rather than typed) or as a
    /// property would have escaped all five house-rule tests at once, and the old
    /// `Assert.True(count >= 9)` floor could never have noticed because the nine existing consts
    /// keep it green forever. An equality against the enum-driven set is the guard that cannot rot
    /// that way: it fails if the scan stops seeing a sentence AND if a const is ever unreachable
    /// through `Sentence`.
    /// </summary>
    private static List<string> Sentences()
    {
        var byReflection = typeof(UserMessages)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var byLookup = Enum.GetValues<TranslationErrorKind>()
            .Select(UserMessages.Sentence)
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();

        Assert.Equal(byLookup.OrderBy(x => x, StringComparer.Ordinal),
                     byReflection.OrderBy(x => x, StringComparer.Ordinal));
        return byReflection;
    }

    private static string ServiceSource(string name) => RepoFile(Path.Combine("Services", name));

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

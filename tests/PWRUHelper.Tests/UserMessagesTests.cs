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
        "The translation service asked us to slow down — paused briefly, and it retries on its own")]
    [InlineData(TranslationErrorKind.Blocked,
        "The translation service is refusing requests from your connection — paused briefly, nothing you need to do")]
    [InlineData(TranslationErrorKind.Unavailable,
        "The translation service is down right now — try again shortly")]
    [InlineData(TranslationErrorKind.Timeout,
        "The translation service took too long to answer — try again shortly")]
    [InlineData(TranslationErrorKind.Network,
        "No internet connection — nothing can be translated until it is back")]
    [InlineData(TranslationErrorKind.BadResponse,
        "The translation service sent something we could not read — try again shortly")]
    [InlineData(TranslationErrorKind.QuotaExhausted,
        "Your free translation quota is used up for this month — the free engines are used instead")]
    [InlineData(TranslationErrorKind.AuthFailed,
        "Your API key was refused — check it in About, or clear it")]
    [InlineData(TranslationErrorKind.AllProvidersPaused,
        "All engines are paused — next try shortly, nothing you need to do")]
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
                () => new GoogleGtxTranslator(new FakeHandler().Respond(status, body))
                          .TranslateAsync(sentinel, "ru", "en")));

        // A 200 whose body is not the provider's shape, and a transport failure: the two messages
        // that are built where the body and the exception are both in scope.
        raised.Add(await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(new FakeHandler().Respond(HttpStatusCode.OK, body, "text/plain"))
                      .TranslateAsync(sentinel, "ru", "en")));
        raised.Add(await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(new FakeHandler().Throws(new HttpRequestException(sentinel)))
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
            // §3's budget is "<= 90 characters RENDERED", and RENDERED is the operative word since
            // amendment A3: these consts are the {P}-LESS forms, where "The translation service"
            // stands in for a 6-character engine name and costs 17 more. §3's real 90 is asserted
            // next door, on the rendered sentence. This ceiling is the fallback's, and it exists so
            // a sentence cannot grow unbounded now that the two surfaces with a hard budget of
            // their own — the compact overlay (A3's short forms) and the feed row (A5: no sentence
            // at all) — have stopped rendering these. Blocked is the longest at 106.
            Assert.True(s.Length <= 110, $"a copy-deck sentence is {s.Length} chars, over 110: {s}");
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
    /// Every wrapper is INVOKED here since E7.S1, not copied as a literal: all of them moved into
    /// the deck (ruling GAP-4 — a wrapper is copy, and copy in a code-behind is copy no scan can
    /// read), so there is nothing left to keep in step by hand. Two of the six are gone rather than
    /// rewritten: the feed row carries no sentence at all (A5) and the compact overlay takes §3.4's
    /// short forms (A3); both have their own cases below.
    /// </summary>
    [Fact]
    public void Every_surface_reads_as_a_sentence_once_the_wrapper_is_applied()
    {
        foreach (var s in Sentences())
        {
            var raw = new TranslationException(TranslationErrorKind.Unknown, s);
            var composed = new[]
            {
                // The Translator tab, rendered ALONE and terminated — "Failed: " retired (§3.0).
                UserMessages.TranslatorTabStatus(s),
                // §3.2's auto-stop, whose parenthetical keeps the sentence's own capital (A11).
                UserMessages.LiveAutoStopped(5, s),
                // E5.S4 replaced "OCR failed: {s}" — developer-speak for a component the player
                // does not have and cannot act on (§3.3). This is the COLON join, the one §3.3's
                // "lower-cased at the join" was written for, and the reason the helper is public.
                UserMessages.ReadFailed(raw),
                // …and read-once's other two joining statuses. Composed through the deck rather
                // than copied, so a wording change to any of them still has to read here.
                UserMessages.ReadOncePartlyTranslated(4, 3, raw),
                UserMessages.ReadOnceNoneTranslated(4, raw),
            };

            foreach (var line in composed)
            {
                Assert.DoesNotContain("..", line);       // no doubled stop
                Assert.DoesNotContain(".)", line);       // no stop inside an inline parenthetical
                Assert.DoesNotContain(". —", line);      // no stop before a continuing clause
                Assert.DoesNotContain("((", line);       // I4's marker, not doubled by the deck
                Assert.DoesNotContain("  ", line);
            }
        }
    }

    /// <summary>
    /// The same, for the parameterised renders — which is where D4 can now break in a way the base
    /// forms cannot: <c>{t}</c> is substituted mid-sentence, and a countdown that arrived already
    /// terminated ("0:58.") or already spaced would produce exactly the shapes above.
    /// </summary>
    [Fact]
    public void Every_surface_still_reads_as_a_sentence_with_a_provider_and_a_countdown()
    {
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
            foreach (var provider in new string?[] { null, ProviderIds.GoogleDict, ProviderIds.GoogleGtx })
                foreach (var t in new string?[] { null, "0:58", "about 4 min", "more than 30 min" })
                    foreach (var another in new[] { false, true })
                    {
                        if (UserMessages.Sentence(kind, provider, t, another) is not { } s) continue;

                        foreach (var line in new[] { UserMessages.TranslatorTabStatus(s),
                                                     UserMessages.LiveAutoStopped(5, s) })
                        {
                            Assert.DoesNotContain("..", line);
                            Assert.DoesNotContain(".)", line);
                            Assert.DoesNotContain(". —", line);
                            Assert.DoesNotContain("  ", line);
                        }

                        // §3's real budget, measured on what a player actually reads: an engine's
                        // own name and a countdown. The longest row in the deck (Blocked, with
                        // "Google" and "0:58") is exactly 90.
                        if (provider == ProviderIds.GoogleDict && t == "0:58")
                            Assert.True(s.Length <= 90, $"{kind} renders {s.Length} chars, over §3's 90: {s}");
                    }
    }

    /// <summary>
    /// <b>The budget on what the app can ACTUALLY render, which is not the same number</b> (review).
    /// §3's 90 is measured with <c>{t}</c> = "0:58", and ruling E7-a means no §3.1 sentence ever
    /// shows an <c>m:ss</c> again: these lines are written once and never repainted, so
    /// <c>MainWindow.TryAgainIn</c> hands them <c>CountdownJoinText</c>'s COARSE band. The longest
    /// thing production can put in the hole is therefore "more than 30 min" (16 characters against
    /// four), so the two real ceilings are <b>111</b> for a NAMED row ("Google (backup)" is 15
    /// characters against "Google"'s 6) and <b>119</b> for the <c>{P}</c>-less fallback ("The
    /// translation service" is 23) — both on <c>Blocked</c>, the deck's longest row.
    ///
    /// <para>Those are budgets, not violations, and the reason is A3/A5: the two surfaces with a
    /// hard limit of their own no longer render these sentences at all — the 360 px overlay takes
    /// §3.4's short forms (pinned at 90/95 below) and a feed row takes no sentence (pinned at 110
    /// in <c>ReadOnceStatusTests</c>). What is left is the Translator tab and the LIVE status line,
    /// both of which wrap. This case exists so the numbers cannot grow again unnoticed, and so the
    /// next reader is not told "90" by a case that pins a countdown the app can no longer
    /// produce.</para>
    /// </summary>
    [Fact]
    public void The_rendered_budget_holds_for_every_countdown_production_can_actually_emit()
    {
        // Exactly the band MainWindow.TryAgainIn can hand a §3.1 sentence: null under a minute
        // (A12 takes over), "about N min" to the cap, "more than 30 min" at it.
        var coarse = new string?[] { null, MainWindow.CountdownJoinText(60),
                                     MainWindow.CountdownJoinText(200),
                                     MainWindow.CountdownJoinText(45 * 60) };
        var ids = ProviderIds.All.Select(x => (string?)x).Append(null).ToList();
        string longestNamed = "", longestFallback = "";

        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
            foreach (var id in ids)
                foreach (var t in coarse)
                    foreach (var another in new[] { false, true })
                    {
                        if (UserMessages.Sentence(kind, id, t, another) is not { } s) continue;
                        if (id is null) { if (s.Length > longestFallback.Length) longestFallback = s; }
                        else if (s.Length > longestNamed.Length) longestNamed = s;

                        // The shapes the deck's no-terminal-stop rule and A12 exist to prevent, on
                        // every render the app can reach rather than on the sample above.
                        Assert.DoesNotContain("{", s);
                        Assert.DoesNotContain("  ", s);
                        Assert.DoesNotContain("— —", s);
                        Assert.DoesNotContain(s[^1], ".!?;:,");
                    }

        Assert.True(longestNamed.Length <= 111,
            $"the worst NAMED §3.1 row is now {longestNamed.Length} chars: {longestNamed}");
        Assert.True(longestFallback.Length <= 119,
            $"the worst {{P}}-less §3.1 row is now {longestFallback.Length} chars: {longestFallback}");
        // Non-vacuity, and the two numbers in the doc comment above: Blocked, at the cap.
        Assert.Equal(UserMessages.Sentence(TranslationErrorKind.Blocked, ProviderIds.GoogleGtx,
                                           "more than 30 min"), longestNamed);
        Assert.Equal(UserMessages.Sentence(TranslationErrorKind.Blocked, null,
                                           "more than 30 min"), longestFallback);
    }

    /// <summary>
    /// <b>No rendered string carries a placeholder</b> (review). Every <c>{P}</c>, <c>{t}</c> and
    /// <c>{n}</c> of the deck is substituted by a parameter, and the one way that fails silently is
    /// a hole left in a wrapper — a template written as copy rather than as an interpolation. The
    /// nine rows have their own scan; this one walks the WRAPPERS, which is where the deck's copy
    /// actually reaches a status line.
    /// </summary>
    [Fact]
    public void No_rendered_line_leaves_a_placeholder_in_the_text()
    {
        var ex = new TranslationException(TranslationErrorKind.RateLimited, "raw", null,
                                          ProviderIds.GoogleDict);
        var rendered = new List<string>
        {
            UserMessages.TranslatorTabStatus(MainWindow.Friendly(ex)),
            UserMessages.LiveAutoStopped(5, MainWindow.Friendly(ex)),
            UserMessages.LiveStarted(), UserMessages.LiveOneReadFailed(),
            UserMessages.LivePausedNextTry("0:58"), UserMessages.LivePausedAboutToRetry(),
            UserMessages.LivePausedNoCountdown(), UserMessages.LivePausedOverlayNextTry("0:58"),
            UserMessages.LivePausedOverlayAboutToRetry(), UserMessages.LivePausedOverlayNoCountdown(),
            UserMessages.ReadingStatus(), UserMessages.ReadTranslatingStatus(4),
            UserMessages.ReadOnceAllTranslated(4), UserMessages.ReadOncePartlyTranslated(4, 3, ex),
            UserMessages.ReadOnceNoneTranslated(4, ex), UserMessages.ReadOncePaused(4, "0:58", false),
            UserMessages.ReadOncePaused(4, null, true), UserMessages.ReadFailed(ex),
            UserMessages.ReadCancelledStatus(), UserMessages.ReadCancelledRow(),
            UserMessages.PendingRetryRow(), UserMessages.RetryGaveUpRow(),
            UserMessages.DeepLNoKeyStatus(), UserMessages.AzureNoKeyStatus(),
            UserMessages.DeepLKeySetStatus(), UserMessages.AzureKeySetStatus("westeurope"),
            UserMessages.AzureKeySetForReadingStatus("westeurope"), UserMessages.AzureNeedsARegion(),
            UserMessages.AzureCredentialUnsendable(), UserMessages.AzureForReadingHint(),
            UserMessages.AzureKeySavedToast(), UserMessages.AzureKeyClearedToast(),
            UserMessages.DeepLUsage(500_000, 1_000_000), UserMessages.DeepLUsage(500_000, null),
            UserMessages.TestKeyLabel(), UserMessages.TestKeyLabelCosts(),
            UserMessages.TestKeyCostsTooltip(), UserMessages.TestingLabel(),
        };

        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
            foreach (var p in new string?[] { null, ProviderIds.DeepL, ProviderIds.Bergamot })
            {
                rendered.Add(UserMessages.OverlayReply(
                    new TranslationException(kind, "raw", null, p), "about 4 min"));
                rendered.Add(UserMessages.OverlayReply(
                    new TranslationException(kind, "raw", null, p)));
                foreach (var id in new[] { ProviderIds.DeepL, ProviderIds.Azure })
                {
                    rendered.Add(UserMessages.KeyTestSentence(id, KeyTestResult.Failed(kind),
                                                              "westeurope", "about 4 min"));
                    rendered.Add(UserMessages.KeyTestSentence(id, KeyTestResult.PausedFor(kind, 90),
                                                              "westeurope", "about 2 min"));
                }
            }
        rendered.Add(UserMessages.KeyTestSentence(ProviderIds.DeepL, KeyTestResult.Works("x"), "", null));
        rendered.Add(UserMessages.KeyTestSentence(ProviderIds.Azure, KeyTestResult.Works(), "westeurope", null));

        Assert.True(rendered.Count > 60, $"only {rendered.Count} rendered lines — the scan is vacuous");
        foreach (var line in rendered)
        {
            Assert.DoesNotContain("{", line);
            Assert.DoesNotContain("}", line);
            Assert.DoesNotContain("  ", line);
            Assert.DoesNotContain("— —", line);
            Assert.Equal(line.Trim(), line);
        }
    }

    // ---- E5.S4: the read-once statuses (§3.3) -------------------------------------------------

    /// <summary>
    /// The five sentences a read-once can end on, pinned literally like everything else in this
    /// file: this is the increment that writes them, and the next edit to any of them must be a
    /// deliberate one that fails here first. They are METHODS rather than table rows because each
    /// is parameterised on what the read actually did — which is the point of the story that added
    /// them ("Done" is a claim, and a claim has to be earned line by line).
    ///
    /// <para>The paused sentence now carries §3.3's {n} — ruling <b>E5-g</b> (E5.S3): the pre-capture
    /// check that could not know a line count is gone, so the read captures, OCRs, serves what the
    /// cache has and REPORTS the pause it was given. What is still deliberately not §3.3's is the
    /// promise that the rows "will fill in when one is back": the retry queue is drained by the LIVE
    /// loop, so a read taken with LIVE stopped has nothing coming for it. E7.S1 owns the final
    /// wording.</para>
    /// </summary>
    [Fact]
    public void The_read_once_statuses_read_as_this_increments_copy()
    {
        var offline = new TranslationException(TranslationErrorKind.Network, "raw provider text (HTTP 000)");

        Assert.Equal("Done — 3 line(s) translated.", UserMessages.ReadOnceAllTranslated(3));
        Assert.Equal("Read 5 line(s) — 3 translated, 2 could not be. No internet connection — nothing can be translated until it is back.",
                     UserMessages.ReadOncePartlyTranslated(5, 3, offline));
        Assert.Equal("Read 5 line(s) — 3 translated, 2 could not be.",
                     UserMessages.ReadOncePartlyTranslated(5, 3, null));
        Assert.Equal("Read 4 line(s) — none could be translated. No internet connection — nothing can be translated until it is back.",
                     UserMessages.ReadOnceNoneTranslated(4, offline));
        Assert.Equal("Read 4 line(s) — none could be translated.",
                     UserMessages.ReadOnceNoneTranslated(4, null));
        // Amendment A7's fork, and it is the whole of what E7.S1 changed here: §3.3's promise that
        // the rows "fill in when one is back" is true only while the LIVE loop — the thing that
        // drains E5.S3's queue — is running. Deliberate expected-string updates, per the story's
        // "write them as deliberate" note.
        Assert.Equal("Read 6 line(s) — every engine is paused, try again in about 4 min.",
                     UserMessages.ReadOncePaused(6, "about 4 min", liveIsRunning: false));
        Assert.Equal("Read 6 line(s) — every engine is paused, try again shortly.",
                     UserMessages.ReadOncePaused(6, null, liveIsRunning: false));
        Assert.Equal("Read 6 line(s) — every engine is paused, they fill in when one is back.",
                     UserMessages.ReadOncePaused(6, null, liveIsRunning: true));
        // …and the running form never promises BOTH: a drain is coming, so there is nothing for a
        // countdown to be about, and the number is dropped rather than joined into a second clause.
        Assert.Equal(UserMessages.ReadOncePaused(6, null, liveIsRunning: true),
                     UserMessages.ReadOncePaused(6, "about 4 min", liveIsRunning: true));
        Assert.Equal("Could not read the screen: no internet connection — nothing can be translated until it is back.",
                     UserMessages.ReadFailed(offline));
        Assert.Equal("Read cancelled.", UserMessages.ReadCancelledStatus());
        Assert.Equal("not translated — read cancelled", UserMessages.ReadCancelledRow());

        // E5.S3's two row texts. The pending one is the ellipsis the feed already writes, and the
        // assertion that matters about it is the negative one: it must NOT open with "(", or it
        // reads as terminal (AC 2) — and, worse, as I4's "this is a failure" marker on a row that
        // has not failed. The given-up one is the only one of the two the call site parenthesises.
        Assert.Equal("…", UserMessages.PendingRetryRow());
        Assert.False(UserMessages.PendingRetryRow().StartsWith('('),
                     "a pending row may not read as a terminal failure (AC 2)");
        Assert.Equal("not translated — the engines did not come back", UserMessages.RetryGaveUpRow());
        Assert.False(UserMessages.RetryGaveUpRow().StartsWith('('),
                     "the deck never writes the paren — the feed row call site does (I4)");
    }

    /// <summary>
    /// <b>The two joins, and the review that split them (E5.S4).</b> §3.3 writes "{reason} is the
    /// §3.1 sentence, lower-cased at the join" — true of the join it was written for, which glues
    /// the sentence on after a COLON and must not restart it in upper case. The other three
    /// read-once statuses join after a FULL STOP, where the same rule produced "…2 could not be. no
    /// internet connection", and a sentence that opens in lower case after a stop reads as a typo
    /// rather than as §1's "one sentence answering the three questions". The deck's own capital
    /// stands there, and the line is terminated instead.
    ///
    /// <para>Either way only the first character is ever in play: "Your API key was refused — check
    /// it in About" keeps the capital A of About.</para>
    /// </summary>
    [Fact]
    public void The_reason_is_lower_cased_after_a_colon_and_left_alone_after_a_full_stop()
    {
        Assert.Equal("your API key was refused — check it in About, or clear it",
                     UserMessages.LowerAtJoin(UserMessages.AuthFailed));
        Assert.Equal("", UserMessages.LowerAtJoin(""));

        foreach (var s in Sentences())
        {
            var joined = UserMessages.LowerAtJoin(s);
            Assert.Equal(s.Length, joined.Length);
            Assert.Equal(s[1..], joined[1..]);                       // only the first character moved

            var ex = new TranslationException(
                Enum.GetValues<TranslationErrorKind>().First(k => UserMessages.Sentence(k) == s), "raw");

            // After the colon: lower-cased, and terminated because the line is shown alone.
            Assert.Equal($"Could not read the screen: {joined}.", UserMessages.ReadFailed(ex));
            // After the full stop: the deck's own sentence, verbatim and terminated.
            Assert.Equal($"Read 2 line(s) — none could be translated. {s}.",
                         UserMessages.ReadOnceNoneTranslated(2, ex));
        }
    }

    /// <summary>
    /// The colon join lower-cases a sentence, not an acronym. §4.4's <c>Unknown</c> arm and the
    /// untyped-exception arm both pass a message through that this app did not write, and the
    /// framework's start with things like "GDI+" — "Could not read the screen: gDI+ …" is the
    /// developer-speak the join was supposed to remove, spelled worse. A second capital is the
    /// cheapest signal that the first one is not sentence case; every deck sentence is.
    /// </summary>
    [Fact]
    public void The_join_lower_cases_a_sentence_and_leaves_an_acronym_alone()
    {
        Assert.Equal("no internet connection — nothing can be translated until it is back",
                     UserMessages.LowerAtJoin(UserMessages.Network));
        Assert.Equal("GDI+ capture failed", UserMessages.LowerAtJoin("GDI+ capture failed"));
        Assert.Equal("DNS lookup failed", UserMessages.LowerAtJoin("DNS lookup failed"));
        Assert.Equal("a", UserMessages.LowerAtJoin("A"));
        Assert.Equal("1 thing", UserMessages.LowerAtJoin("1 thing"));

        Assert.Equal("Could not read the screen: GDI+ capture failed.",
                     UserMessages.ReadFailed(new InvalidOperationException("GDI+ capture failed")));
    }

    /// <summary>The <c>Unknown</c> pass-through is the one reason clause that can already carry its
    /// own full stop, and the join may not double it — "…try again later.." is the shape the deck's
    /// no-terminal-stop rule exists to prevent, arriving from the one arm that rule does not
    /// govern.</summary>
    [Fact]
    public void A_reason_that_is_already_terminated_is_not_given_a_second_stop()
    {
        var passthrough = new TranslationException(TranslationErrorKind.Unknown,
            "Translation service error (HTTP 418). Please try again later.");

        Assert.EndsWith("Please try again later.", UserMessages.ReadOnceNoneTranslated(2, passthrough));
        Assert.DoesNotContain("..", UserMessages.ReadOnceNoneTranslated(2, passthrough));
        Assert.DoesNotContain("..", UserMessages.ReadFailed(passthrough));
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

        Assert.Contains("UserMessages.For(ex, TryAgainIn(ex))", friendly);
        foreach (var s in Sentences())
            Assert.False(friendly.Contains(s, StringComparison.Ordinal),
                $"MainWindow.xaml.cs holds its own copy of a copy-deck sentence: {s}");
        // The two literals this story removed, named so a revert is loud.
        Assert.DoesNotContain("\"no Internet connection\"", friendly);
        Assert.DoesNotContain("\"the request timed out\"", friendly);
    }

    // ---- A5: a feed row carries no sentence at all ---------------------------------------------

    /// <summary>
    /// <b>Amendment A5, as a scan.</b> "A row never carries a §3.1 sentence, a provider name or a
    /// countdown" — there is exactly one explanation per window and it lives on the status line, so
    /// a row that repeats it is a second one, on every row of the batch, saying less than the line
    /// above it already does.
    ///
    /// <para>Both call sites used to stamp <c>$"({Friendly(ex)})"</c>, which is what the owner's
    /// screenshot shows. The "(" they add is unchanged — it is I4's marker, and it is what keeps a
    /// failure out of the cache — so this asserts the two halves separately: no row is built from
    /// <c>Friendly</c> any more, and every row that IS written comes from the three texts A5
    /// allows.</para>
    /// </summary>
    [Theory]
    [InlineData("MainWindow.Live.cs")]
    [InlineData("MainWindow.Ocr.cs")]
    public void A_feed_row_carries_no_failure_sentence(string file)
    {
        var code = Code(File.ReadAllText(RepoFile(file)));

        Assert.DoesNotContain("TranslationBody = $\"({Friendly(ex)})\"", code);
        foreach (var row in Regex.Matches(code, @"TranslationBody = \$?""[^""]*""")
                                 .Select(m => m.Value))
            Assert.True(row.Contains("UserMessages.", StringComparison.Ordinal)
                        || row.EndsWith("\"…\"", StringComparison.Ordinal),
                        $"a feed row in {file} is written from something other than the deck: {row}");
    }

    /// <summary>The three row texts A5 allows, and no fourth — including the per-line placeholders,
    /// which ruling E2-d handed to this story and which said "rate-limited" on rows that had failed
    /// for any reason at all (and, in the third, could put an HTTP status code on screen).</summary>
    [Fact]
    public void A5_Three_row_texts_exist_and_the_per_line_placeholders_are_one_of_them()
    {
        Assert.Equal("…", UserMessages.PendingRetryRow());
        Assert.Equal("not translated — the engines did not come back", UserMessages.RetryGaveUpRow());
        Assert.Equal("not translated — read cancelled", UserMessages.ReadCancelledRow());

        foreach (var placeholder in new[] { PerLineFallback.SkippedMessage,
                                            PerLineFallback.RateLimitedMessage,
                                            PerLineFallback.Failed("the request timed out (HTTP 500)") })
        {
            Assert.Equal($"({UserMessages.RetryGaveUpRow()})", placeholder);
            // I4 — these ARE a translator's return value, so the marker is what keeps them out of
            // the cache. It is the one row text the deck does not write the paren for.
            Assert.StartsWith("(", placeholder);
            Assert.DoesNotContain("rate-limited", placeholder);
            Assert.DoesNotContain("500", placeholder);
        }
    }

    // ---- T1: {P} — one provider-name table, total, keyed off ProviderIds ----------------------

    /// <summary>
    /// One name per <c>ProviderIds.All</c> member (amendment A2 / OQ-2). Six ids, six names, no
    /// gaps — enumerated rather than listed, so a provider added later fails here instead of
    /// shipping a sentence with an id in it.
    /// </summary>
    [Fact]
    public void Every_provider_id_has_exactly_one_user_facing_name()
    {
        var expected = new Dictionary<string, (string Display, string Short)>
        {
            [ProviderIds.GoogleDict] = ("Google", "Google"),
            [ProviderIds.Edge] = ("Edge", "Edge"),
            // A2 retired "Google (old)": a name should say a provider's ROLE, not its age.
            [ProviderIds.GoogleGtx] = ("Google (backup)", "Google (backup)"),
            [ProviderIds.DeepL] = ("DeepL", "DeepL"),
            [ProviderIds.Azure] = ("Azure", "Azure"),
            [ProviderIds.Bergamot] = ("Offline engine", "Offline"),
        };

        foreach (var id in ProviderIds.All)
        {
            Assert.True(expected.ContainsKey(id), $"{id} has no name in ProviderNames");
            Assert.Equal(expected[id].Display, ProviderNames.Display(id));
            Assert.Equal(expected[id].Short, ProviderNames.Short(id));
        }
        Assert.Equal(ProviderIds.All.Count, expected.Count);

        // Total, and NEVER inventing (§3.0 rule 1): an unknown id and the very common null both
        // answer null, and the sentence then renders its {P}-less form rather than a name nobody
        // chose. It must not throw — a player mid-fight is the worst place to discover an id.
        Assert.Null(ProviderNames.Display(null));
        Assert.Null(ProviderNames.Display("bergamot-v2"));
        Assert.Null(ProviderNames.Short(null));
        Assert.Null(ProviderNames.Short(""));

        // …and no name is a provider INTERNAL: §3's rule, and the reason this table exists. An id
        // is hyphenated and lower-case ("google-dict", "dict-chrome-ex"); a name never is.
        foreach (var id in ProviderIds.All)
        {
            Assert.DoesNotContain("-", ProviderNames.Display(id)!, StringComparison.Ordinal);
            Assert.True(char.IsUpper(ProviderNames.Display(id)![0]));
        }
    }

    // ---- T2: the deck gains {P} and {t}, and every parameter is optional ----------------------

    /// <summary>
    /// The eight <c>{P}</c>-bearing rows, in both forms. The provider-named one is what a player
    /// reads once <c>HttpProviderCore</c> has stamped the failure; the {P}-less one is §3.0 rule 1's
    /// fallback, reachable through every throw site that carries no provider id (a DeepL count
    /// mismatch, an untyped failure classified on the way up).
    /// </summary>
    [Theory]
    [InlineData(TranslationErrorKind.RateLimited,
        "Google asked us to slow down — paused for 0:58, and it retries on its own")]
    [InlineData(TranslationErrorKind.Blocked,
        "Google is refusing requests from your connection — paused for 0:58, nothing you need to do")]
    [InlineData(TranslationErrorKind.Unavailable, "Google is down right now — try again shortly")]
    [InlineData(TranslationErrorKind.Timeout, "Google took too long to answer — try again shortly")]
    [InlineData(TranslationErrorKind.BadResponse,
        "Google sent something we could not read — try again shortly")]
    [InlineData(TranslationErrorKind.QuotaExhausted,
        "Your Google quota is used up for this month — the free engines are used instead")]
    [InlineData(TranslationErrorKind.AuthFailed,
        "Your Google key was refused — check it in About, or clear it")]
    [InlineData(TranslationErrorKind.AllProvidersPaused,
        "All engines are paused — next try in 0:58, nothing you need to do")]
    public void A_named_provider_and_a_countdown_are_substituted_into_the_row(
        TranslationErrorKind kind, string expected)
    {
        Assert.Equal(expected, UserMessages.Sentence(kind, ProviderIds.GoogleDict, "0:58"));

        // The same failure through the display-time entry point, which is what actually renders.
        var ex = new TranslationException(kind, "raw provider text (HTTP 429) — not for the user",
                                          null, ProviderIds.GoogleDict);
        Assert.Equal(expected, UserMessages.For(ex, "0:58"));
    }

    /// <summary>
    /// <b>Amendment A12: one substitution rule, not a second sentence.</b> <c>RetryAt</c> is
    /// legitimately absent — a gate opened on a strike count rather than on a window, and
    /// <c>AuthFailed</c>'s <c>MaxValue</c> sentinel has no honest countdown at all — and the row
    /// then reads "paused briefly" / "next try shortly". A sentence rendering "paused for " with
    /// nothing after it is the defect this pins.
    /// </summary>
    [Fact]
    public void A12_A_missing_countdown_becomes_briefly_or_shortly_and_never_a_hole()
    {
        Assert.Equal("Google asked us to slow down — paused briefly, and it retries on its own",
                     UserMessages.Sentence(TranslationErrorKind.RateLimited, ProviderIds.GoogleDict));
        Assert.Equal("Google is refusing requests from your connection — paused briefly, nothing you need to do",
                     UserMessages.Sentence(TranslationErrorKind.Blocked, ProviderIds.GoogleDict));
        Assert.Equal("All engines are paused — next try shortly, nothing you need to do",
                     UserMessages.Sentence(TranslationErrorKind.AllProvidersPaused));

        // The negative that matters, across every row and every provider: no trailing preposition
        // and no double space where a number should have been.
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
            if (UserMessages.Sentence(kind, ProviderIds.DeepL) is { } s)
            {
                Assert.DoesNotContain("paused for,", s);
                Assert.DoesNotContain("paused for ,", s);
                Assert.DoesNotContain("try in,", s);
                Assert.DoesNotContain("  ", s);
                Assert.DoesNotContain("{", s);
            }
    }

    /// <summary>
    /// An id nobody knows renders the <c>{P}</c>-less row — <b>the same string the const holds</b>,
    /// which is the whole reason the parameterised form is built from the const's own fragments.
    /// </summary>
    [Fact]
    public void An_unknown_provider_id_renders_the_sentence_the_table_shipped_with()
    {
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
        {
            Assert.Equal(UserMessages.Sentence(kind), UserMessages.Sentence(kind, "no-such-engine"));
            Assert.Equal(UserMessages.Sentence(kind), UserMessages.Sentence(kind, null, null));
        }

        Assert.Equal(UserMessages.AuthFailed, UserMessages.Sentence(TranslationErrorKind.AuthFailed, ""));
        Assert.Contains("Your free translation quota",
                        UserMessages.Sentence(TranslationErrorKind.QuotaExhausted, "nope")!);
    }

    /// <summary>
    /// <b>D2, and A4's ruling on it.</b> "— another engine is being tried" is back in the deck and
    /// behind a parameter whose default is the honest one: the three forked rows promise a retry
    /// only for a caller that can prove one is coming, and no surface shipping today can — a §3.1
    /// sentence is rendered once the whole attempt has failed. The clause is therefore written once
    /// here and rendered by whoever builds the mid-chain status line (E7.S4/E7.S5).
    /// </summary>
    [Fact]
    public void D2_The_promise_of_another_engine_renders_only_when_the_caller_says_it_is_true()
    {
        foreach (var kind in new[] { TranslationErrorKind.Unavailable, TranslationErrorKind.Timeout,
                                     TranslationErrorKind.BadResponse })
        {
            var promised = UserMessages.Sentence(kind, ProviderIds.Edge, null,
                                                 anotherEngineIsBeingTried: true)!;
            var plain = UserMessages.Sentence(kind, ProviderIds.Edge)!;

            Assert.EndsWith("— another engine is being tried", promised);
            Assert.EndsWith("— try again shortly", plain);
            // One sentence with two tails, not two sentences: everything before the fork is
            // character-for-character the same string.
            Assert.Equal(plain[..plain.IndexOf("— try again", StringComparison.Ordinal)],
                         promised[..promised.IndexOf("— another", StringComparison.Ordinal)]);
        }

        // No other row grows the clause — a promise on a paused engine or a refused key would be
        // false in a way no caller could make true.
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
            if (kind is not (TranslationErrorKind.Unavailable or TranslationErrorKind.Timeout
                             or TranslationErrorKind.BadResponse))
                Assert.Equal(UserMessages.Sentence(kind, ProviderIds.Edge),
                             UserMessages.Sentence(kind, ProviderIds.Edge, null, true));

        // …and the entry point the six surfaces actually call never asks for it (A4).
        var ex = new TranslationException(TranslationErrorKind.Timeout, "raw", null, ProviderIds.Edge);
        Assert.DoesNotContain("another engine", MainWindow.Friendly(ex));
    }

    // ---- §3.4: the compact overlay takes SHORT forms, never a §3.1 sentence -------------------

    /// <summary>
    /// Amendment A3's table, verbatim. The wrapper alone costs 46 characters of a 360 px line, so
    /// this surface never renders a deck sentence — and "your text is kept", the best sentence in
    /// the app, is dropped only where there is nothing to keep and nothing to retry.
    /// </summary>
    [Fact]
    public void The_overlay_quick_reply_takes_the_short_form_for_its_kind()
    {
        static string Line(TranslationErrorKind kind, string? provider, string? t = null)
            => UserMessages.OverlayReply(new TranslationException(kind, "raw", null, provider), t);

        Assert.Equal("⚠ Google paused (0:58) — your text is kept, press Enter to retry.",
                     Line(TranslationErrorKind.RateLimited, ProviderIds.GoogleDict, "0:58"));
        Assert.Equal("⚠ Azure paused (about 4 min) — your text is kept, press Enter to retry.",
                     Line(TranslationErrorKind.Blocked, ProviderIds.Azure, "about 4 min"));
        Assert.Equal("⚠ Engines paused (0:58) — your text is kept.",
                     Line(TranslationErrorKind.AllProvidersPaused, null, "0:58"));
        // A12 with no countdown, and it is the DURATION substitution (review): the parenthesis
        // holds how long the pause LASTS, which is the "for {t}" slot, so it degrades to "briefly".
        // "Google paused (shortly)" reads as "Google pauses soon" — a different, false statement.
        Assert.Equal("⚠ Google paused (briefly) — your text is kept, press Enter to retry.",
                     Line(TranslationErrorKind.RateLimited, ProviderIds.GoogleDict));
        Assert.Equal("⚠ Engines paused (briefly) — your text is kept.",
                     Line(TranslationErrorKind.AllProvidersPaused, null));
        Assert.Equal("⚠ No internet — your text is kept, press Enter to retry.",
                     Line(TranslationErrorKind.Network, null));
        Assert.Equal("⚠ Google did not answer — your text is kept, press Enter to retry.",
                     Line(TranslationErrorKind.Timeout, ProviderIds.GoogleDict));
        Assert.Equal("⚠ Offline did not answer — your text is kept, press Enter to retry.",
                     Line(TranslationErrorKind.Unavailable, ProviderIds.Bergamot));   // §3.0's SHORT name
        Assert.Equal("⚠ Your DeepL key was refused — see About.",
                     Line(TranslationErrorKind.AuthFailed, ProviderIds.DeepL));
        Assert.Equal("⚠ Your Azure quota is used up — see About.",
                     Line(TranslationErrorKind.QuotaExhausted, ProviderIds.Azure));
        // The generic arm: a failure with no Kind to key off, and the null a null Error takes.
        Assert.Equal("⚠ Could not translate — your text is kept, press Enter to retry.",
                     UserMessages.OverlayReply(new InvalidOperationException("GDI+ went home")));
        Assert.Equal("⚠ Could not translate — your text is kept, press Enter to retry.",
                     UserMessages.OverlayReply(null));

        // …and no line carries what a 360 px window cannot hold, a §3.1 sentence, or a raw message.
        foreach (var kind in Enum.GetValues<TranslationErrorKind>())
            foreach (var p in new string?[] { null, ProviderIds.GoogleGtx, ProviderIds.Bergamot })
            {
                var line = Line(kind, p, "more than 30 min");
                // §3.4 asks for ~60 and every provider-NAMED form is 39–70; the {P}-less fallback
                // runs to ~82 because "The translation service" is 23 characters, and that one
                // wraps rather than inventing a name (§3.0 rule 1).
                // §3.4 asks for ~60 and every form a player realistically reads is 39–70 — its own
                // table's worst case is 62. The two that run past it are the ones §3.4 does not
                // measure: the {P}-less fallback (95, because "The translation service" is 23
                // characters, and §3.0 rule 1 says wrap rather than invent a name) and the two-word
                // "Google (backup)" at the 30-minute cap (86). Both wrap; neither lies.
                Assert.True(line.Length <= (p is null ? 95 : 90), $"overlay line is {line.Length}: {line}");
                Assert.DoesNotContain("raw", line);
                Assert.StartsWith("⚠ ", line);
            }
    }

    // ---- A8: the read-once button's two states, both in the deck (E7.S5) ----------------------

    /// <summary>
    /// <b>Amendment A8's copy.</b> Six strings, and the reason they are in the deck rather than in
    /// two XAML attributes is the reason every other pair in this file is: a control whose label
    /// CHANGES needs both forms written down in one place, or the restore becomes a second spelling
    /// of the idle one (UX-DR19). The idle wording is the SHIPPED one and not A8's paraphrase
    /// ("Read the area once") — the README walks a new player through this button by name, and
    /// renaming it is E7.S8's call.
    /// </summary>
    [Fact]
    public void A8_the_read_once_button_has_two_labels_and_both_are_the_decks()
    {
        Assert.Equal("Select area & read once", UserMessages.ReadOnceLabel());
        Assert.Equal("Cancel read", UserMessages.CancelReadLabel());
        Assert.Equal("👁 Read once", UserMessages.ReadOnceOverlayLabel());
        Assert.Equal("■", UserMessages.CancelReadOverlayLabel());

        // The tooltip is where the hotkey belongs — on the control it duplicates, never on a status
        // line describing a state (principle 1). That is option (a) of the story's T8: the deck
        // chose the button over "Reading… Ctrl+Alt+R to stop.", so no status sentence names it.
        Assert.Contains("Ctrl+Alt+R", UserMessages.CancelReadTooltip(), StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt+R", UserMessages.ReadOnceTooltip(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+Alt+R", UserMessages.ReadingStatus(), StringComparison.Ordinal);

        // The overlay is 360 px: its idle label and both tooltips have to be readable there, and the
        // cancel form is one glyph.
        Assert.True(UserMessages.ReadOnceOverlayLabel().Length <= 20);
        Assert.True(UserMessages.CancelReadTooltip().Length <= 60,
                    $"the cancel tooltip is {UserMessages.CancelReadTooltip().Length} chars");
    }

    // ---- ruling E7-a: the coarse {t}, and m:ss only on the lines that tick ---------------------

    /// <summary>
    /// <b>Ruling E7-a.</b> A sentence written once and never repainted may not show a stopwatch: a
    /// frozen "0:05" reads as a live clock. So under a minute the non-ticking surfaces show no
    /// number at all and A12's clause takes over, and above it they take the minute band. Only the
    /// 1 Hz LIVE status lines use <c>m:ss</c>.
    ///
    /// <para>And the cap says <b>"more than 30 min"</b>: <c>QuotaOpenMinutes</c> is 60 against a
    /// 30-minute display cap, so "about 30 min" was rounding a possible hour DOWN — the one
    /// direction §2.4's "rounded up" may not break.</para>
    /// </summary>
    [Fact]
    public void E7a_A_sentence_that_never_ticks_takes_the_coarse_countdown()
    {
        Assert.Null(MainWindow.CountdownJoinText(4));
        Assert.Null(MainWindow.CountdownJoinText(30));      // was "0:30" — E7-a
        Assert.Null(MainWindow.CountdownJoinText(59));
        Assert.Equal("about 1 min", MainWindow.CountdownJoinText(60));
        Assert.Equal("about 2 min", MainWindow.CountdownJoinText(75));   // "1:15" on a LIVE line
        Assert.Equal("about 4 min", MainWindow.CountdownJoinText(200));
        Assert.Equal("more than 30 min", MainWindow.CountdownJoinText(45 * 60));
        Assert.Null(MainWindow.CountdownJoinText(null));

        // The ticking band is unchanged below the cap and shares the minute arithmetic above it.
        Assert.Equal("0:30", MainWindow.CountdownText(30));
        Assert.Equal("1:15", MainWindow.CountdownText(75));
        Assert.Equal("about 4 min", MainWindow.CountdownText(200));
        Assert.Equal("more than 30 min", MainWindow.CountdownText(45 * 60));

        // Never m:ss on a written-once sentence, at any second of the first hour.
        for (int s = 0; s <= LiveTickPolicy.MaxCountdownSeconds; s++)
            Assert.DoesNotContain(":", MainWindow.CountdownJoinText(s) ?? "");
    }

    // ---- A11: the colon join lower-cases a sentence, never a proper noun ----------------------

    /// <summary>
    /// <b>Amendment A11, and the guard it forces now that <c>{P}</c> is back.</b> A11 keeps the
    /// lower-casing rule for the colon join and removes it from every other, on the argument that
    /// "google asked us to slow down" is a typo rather than a sentence. That argument reaches the
    /// colon join too the moment the sentence opens with a name — and the second-capital guard
    /// cannot see it, because "DeepL", "Google (backup)" and "Edge" all have a lower-case second
    /// letter. A name is left alone for exactly the reason "GDI+" is.
    /// </summary>
    [Fact]
    public void A11_The_join_leaves_an_engines_name_alone_and_still_lower_cases_a_sentence()
    {
        foreach (var id in ProviderIds.All)
        {
            var named = UserMessages.Sentence(TranslationErrorKind.Timeout, id)!;
            Assert.Equal(named, UserMessages.LowerAtJoin(named));
            Assert.StartsWith(ProviderNames.Display(id)!, UserMessages.ReadFailed(
                new TranslationException(TranslationErrorKind.Timeout, "raw", null, id))
                    ["Could not read the screen: ".Length..]);
        }

        // …and the rule itself still applies to everything that IS sentence case.
        Assert.Equal("the translation service took too long to answer — try again shortly",
                     UserMessages.LowerAtJoin(UserMessages.Timeout));
        Assert.Equal("your API key was refused — check it in About, or clear it",
                     UserMessages.LowerAtJoin(UserMessages.AuthFailed));
        Assert.Equal("GDI+ capture failed", UserMessages.LowerAtJoin("GDI+ capture failed"));
        // A name is only a name at the START of a sentence and only as a whole word.
        Assert.Equal("edgewise, this is a sentence", UserMessages.LowerAtJoin("Edgewise, this is a sentence"));
    }

    // ---- AC 4: a successful fallback produces NO error text anywhere --------------------------

    /// <summary>
    /// <b>AC 4, and it is a rule about ABSENCE</b> (UX hint 5, TP §6.3's S2 row). Tier 1 fails, tier
    /// 2 answers: the player got their translation, so nothing on any surface may say a word about
    /// the failure — only the chip changes, plus §3.5's one-time notice (E7.S5's).
    ///
    /// <para>Asserted where it can actually be proven: the chain answers, so no code path has an
    /// exception to render, and <c>LastOutcome</c> — the one thing E7 reads about a successful
    /// call — carries a provider id and no Kind. A sentence rendered from that outcome would have
    /// to be invented, and there is nothing to invent it from.</para>
    /// </summary>
    [Fact]
    public async Task AC4_A_fallback_that_answered_leaves_no_error_text_anywhere()
    {
        var chain = ChainTranslator.Of(
            (ProviderIds.GoogleDict, new Tier(_ => throw new TranslationException(
                 TranslationErrorKind.Unavailable, "raw 503", null, ProviderIds.GoogleDict))),
            (ProviderIds.Edge, new Tier(text => "T:" + text)));

        Assert.Equal("T:привет", await chain.TranslateAsync("привет", "ru", "en"));

        var outcome = chain.LastOutcome;
        Assert.NotNull(outcome);
        Assert.Equal(ProviderIds.Edge, outcome!.ProviderId);   // somebody answered…
        Assert.Null(outcome.Kind);                             // …so there is no Kind to render
        Assert.Empty(outcome.Skipped);

        // And the deck cannot produce a sentence out of that: every row is keyed by a Kind, and
        // there is none. This is the assertion that would fail the day a "helpful" status line
        // decided to narrate a fallback the player never needed to know about.
        Assert.Null(UserMessages.Sentence(TranslationErrorKind.Unknown, outcome.ProviderId));
    }

    // ---- §3.0: the Translator tab renders the sentence alone, terminated ----------------------

    /// <summary>
    /// <b>"Failed: " is retired</b> (§3.0). With <c>{P}</c> restored the sentence names the engine
    /// and says what happened; "Failed:" in front of it is the app saying "bad news" twice and
    /// demoting the sentence to a sub-clause. This is also the surface §3.1 writes its sentences
    /// FOR — rendered alone — so it is the one that adds the terminal stop the deck omits (D4).
    /// </summary>
    [Fact]
    public void The_translator_tab_renders_the_sentence_alone_and_terminated()
    {
        Assert.Equal("Google took too long to answer — try again shortly.",
                     UserMessages.TranslatorTabStatus(
                         MainWindow.Friendly(new TranslationException(
                             TranslationErrorKind.Timeout, "raw", null, ProviderIds.GoogleDict))));

        // §4.4's pass-through can already carry its own stop, and the join may not double it.
        Assert.Equal("Translation service error (HTTP 418). Please try again later.",
                     UserMessages.TranslatorTabStatus(
                         "Translation service error (HTTP 418). Please try again later."));

        // The prefix is gone from the call site too, not just from the deck.
        var code = Code(File.ReadAllText(RepoFile("MainWindow.Translate.cs")));
        Assert.DoesNotContain("\"Failed: ", code);
        Assert.Contains("TranslateStatus.Text = UserMessages.TranslatorTabStatus(Friendly(ex));", code);
    }

    // ---- AC 3 / UX-DR19: each sentence exists exactly once in production source ----------------

    /// <summary>
    /// Hint 11 as a scan, and the one this story could most easily have broken: it reopened the one
    /// file the whole deck lives in, and the failure mode UX-DR19 names is ending up with two
    /// spellings of one sentence.
    ///
    /// <para><b>Fragments and not whole sentences</b>, because the nine rows are <c>const</c>s
    /// assembled from <c>const</c> fragments (so that they stay compile-time constants AND so that
    /// the {P}-bearing and {P}-less forms cannot drift). The distinctive middle of each row is what
    /// a duplicate would have to copy. <c>Code()</c> strips comments, so the rendered forms quoted
    /// in this file's own doc comments do not count as a second occurrence.</para>
    /// </summary>
    [Fact]
    public void UXDR19_Each_deck_sentence_appears_exactly_once_in_production_source()
    {
        var fragments = new[]
        {
            // §3.1, the nine rows (A1's three replacements included).
            " asked us to slow down — paused ",
            ", and it retries on its own",
            " is refusing requests from your connection — paused ",
            ", nothing you need to do",
            " is down right now — ",
            " took too long to answer — ",
            "No internet connection — nothing can be translated until it is back",
            " sent something we could not read — ",
            " quota is used up for this month — the free engines are used instead",
            " key was refused — check it in About, or clear it",
            "All engines are paused — next try ",
            "another engine is being tried",
            // ("try again " is deliberately NOT scanned: it is a connective, not a sentence, and
            //  four providers' own exception messages — which the player never reads — end in
            //  "Please try again later." The rows that carry it have their own fragments above.)
            // §3.2, the LIVE rows this story moved out of the code-behind.
            "Live stopped after ", " failed reads in a row (", ") — press ▶ to try again.",
            "🔴 Live — one read did not translate, retrying…",
            "○ Live — paused, next try in ",
            "○ Live — paused, about to retry.",
            " It resumes on its own; nothing is lost.",
            "○ Live paused — back in ",
            "○ Live paused — about to retry",
            "○ Live paused — it resumes on its own",
            // §3.2's S6 rows and §3.5's fallback notice, added by E7.S4.
            "○ Live — paused, no internet connection.", "○ Live paused — no internet",
            "Translated by ", " is paused.",
            // §3.3 and §3.3a.
            "Done — ", " — none could be translated.", " — every engine is paused, ",
            "they fill in when one is back.", "Could not read the screen: ",
            "not translated — the engines did not come back", "not translated — read cancelled",
            "Read cancelled.", "Reading…", ". Translating…",
            // §3.4's short forms.
            " — your text is kept", ", press Enter to retry.", " paused (", " did not answer",
            "Could not translate", "\"No internet\"", "see About.",
            // §3.7's cleared row, now written for both key boxes from one string.
            " key — using the free engines (Google)",
            // A8's button copy (E7.S5). The XAML no longer carries any of it — a label that changes
            // cannot live in an attribute, and these fragments are what would catch it coming back.
            "Cancel read", "👁 Read once", "Draw a box over Russian text and read it once",
            // ("Select area & read once" is deliberately NOT scanned: the Translator tab's
            //  empty-state hint QUOTES the button by name — "Use “Select area & read once” above…" —
            //  and that paragraph is tab copy no deck section owns. E7.S8's README/About pass is
            //  where the two are reconciled; a scan here would only force the quote out of a
            //  sentence it belongs in.)
        };

        foreach (var fragment in fragments)
        {
            var hits = ProductionSources()
                .Select(f => (File: Path.GetFileName(f), Count: Occurrences(Code(File.ReadAllText(f)), fragment)))
                .Where(x => x.Count > 0)
                .ToList();

            var total = hits.Sum(x => x.Count);
            Assert.True(total == 1,
                $"\"{fragment}\" appears {total} times in production source "
                + $"({string.Join(", ", hits.Select(x => $"{x.File}×{x.Count}"))}) — UX-DR19");
            Assert.Equal("UserMessages.cs", hits[0].File);
        }
    }

    /// <summary>
    /// The other half of AC 3, and the one that actually caught things: <b>no orphan sentence is
    /// left in a code-behind</b>. A status write whose argument is a string literal ending in a
    /// full stop is copy, and copy belongs in the deck where a scan can read it.
    ///
    /// <para>The exceptions are named rather than pattern-matched, so adding one is a decision. The
    /// OCR-pack lines and the install diagnostics are Screen-OCR-tab copy about Windows capabilities
    /// — no deck section covers them, and E7 does not own them; "Live stopped." is <c>StopLive</c>'s
    /// own line, which §3.2 keeps.</para>
    /// </summary>
    [Theory]
    [InlineData("MainWindow.Live.cs")]
    [InlineData("MainWindow.Ocr.cs")]
    [InlineData("MainWindow.Translate.cs")]
    [InlineData("CompactOverlay.xaml.cs")]
    public void No_status_line_holds_a_sentence_of_its_own(string file)
    {
        var known = new[]
        {
            "Live stopped.",                                    // §3.2, StopLive's own line
            "No text detected there. Try a tighter box around the text.",
            "No text detected — the Russian OCR pack isn't installed. Install it on the Screen OCR tab (1 click).",
        };

        foreach (Match m in Regex.Matches(Code(File.ReadAllText(RepoFile(file))),
                                          @"Set(?:ScreenStatus|Status|StatusIfChanged|ReplyResult)\(\s*""([^""]*\.)""") )
        {
            var literal = m.Groups[1].Value;
            Assert.True(known.Contains(literal),
                $"{file} writes a status sentence of its own — it belongs in UserMessages: \"{literal}\"");
        }
    }

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
            .Select(k => UserMessages.Sentence(k))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();

        Assert.Equal(byLookup.OrderBy(x => x, StringComparer.Ordinal),
                     byReflection.OrderBy(x => x, StringComparer.Ordinal));
        return byReflection;
    }

    /// <summary>The smallest <c>ITranslator</c> a chain case needs: one function per call.</summary>
    private sealed class Tier : PWRUHelper.Services.ITranslator
    {
        private readonly Func<string, string> _f;
        public Tier(Func<string, string> f) { _f = f; }

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
            => Task.FromResult(_f(text));

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
            CancellationToken ct = default)
            => Task.FromResult(lines.Select(_f).ToList());
    }

    private static string ServiceSource(string name) => RepoFile(Path.Combine("Services", name));

    /// <summary>Source with its comments stripped, so a sentence QUOTED in a doc comment — which is
    /// documentation doing its job, and this file's own rendered forms are quoted all over
    /// <c>UserMessages.cs</c> — does not count as a second occurrence (E6.S5's review wrote this
    /// helper for the same scan over §3.7's rows).</summary>
    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static int Occurrences(string text, string fragment)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0) { count++; at += fragment.Length; }
        return count;
    }

    /// <summary>Every shipped source file: the app's own <c>.cs</c> and <c>.xaml</c>, with the test
    /// tree and the build outputs left out.</summary>
    private static IEnumerable<string> ProductionSources()
    {
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        foreach (var pattern in new[] { "*.cs", "*.xaml" })
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                // RELATIVE to the repo root: this repo is developed in a worktree under
                // `.claude\worktrees\…`, so an absolute-path filter would exclude every file in the
                // app and pass with nothing scanned.
                var relative = sep + Path.GetRelativePath(root, file);
                if (relative.Contains($"{sep}tests{sep}") || relative.Contains($"{sep}bin{sep}")
                    || relative.Contains($"{sep}obj{sep}")) continue;
                yield return file;
            }
    }

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

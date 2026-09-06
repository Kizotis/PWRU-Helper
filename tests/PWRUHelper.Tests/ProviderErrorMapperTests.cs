using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// One case per row of <c>architecture-cible.md</c> §4.2, in the table's order (TP-MAP-01…10, 15,
/// 16). The mapper is the single classification point: after this story no other place in the app
/// decides what a status code means, so these cases are the whole contract, and the provider-level
/// cases below prove the two existing providers really go through it.
///
/// Row 11's HTML sniffing (§4.3, TP-MAP-11…14) is the last section of the file, added by E1.S4
/// together with the recorded bodies under <c>Fixtures/</c>.
/// </summary>
public class ProviderErrorMapperTests
{
    private static HttpResponseMessage Resp(int status, string body = "", string contentType = "application/json")
        => new((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) };

    // The row-1 value, named here the way production may not (TP-MAP-17 bans the token in app
    // source only — a test is allowed to say what it is asserting).
    private const TranslationErrorKind Cancelled = TranslationErrorKind.Cancelled;

    // ---- Rows 1-3: the transport rows, where the OCE trap lives -------------------------------

    /// <summary>TP-MAP-01 — a genuine cancel, and it wins over everything else: the status and the
    /// transport exception below would each classify differently on their own.</summary>
    [Fact]
    public void A_cancelled_token_is_the_first_rule_and_beats_every_other_signal()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(Cancelled, ProviderErrorMapper.Classify(null, null, null, false, cts.Token));
        using var ok = Resp(200, "{}");
        Assert.Equal(Cancelled, ProviderErrorMapper.Classify(ok, null,
            new TaskCanceledException(), true, cts.Token));
        using var tooMany = Resp(429);
        Assert.Equal(Cancelled, ProviderErrorMapper.Classify(tooMany, null, null, false, cts.Token));
    }

    /// <summary>
    /// TP-MAP-02 — the regression guard for risk R-03, and the reason this file exists. On .NET 8
    /// an HttpClient timeout is a TaskCanceledException (a subclass of OperationCanceledException)
    /// whose token is NOT cancelled. Reading it as a cancel silently disabled the DeepL→Google
    /// fallback and left a zombie LIVE indicator; it cost this project three releases once.
    /// </summary>
    [Fact]
    public void A_timeout_is_never_a_cancel()
    {
        // The exact shape HttpClient produces (and the shape FakeHandler.TimesOut() replays).
        var timeout = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing.",
            new TimeoutException(), CancellationToken.None);

        Assert.Equal(TranslationErrorKind.Timeout,
            ProviderErrorMapper.Classify(null, null, timeout, false, CancellationToken.None));
        // The bare base class too — a decorator that rethrows loses the subclass.
        Assert.Equal(TranslationErrorKind.Timeout,
            ProviderErrorMapper.Classify(null, null, new OperationCanceledException(), true, CancellationToken.None));
    }

    /// <summary>TP-MAP-03 — DNS, TLS, connect, proxy refused.</summary>
    [Fact]
    public void A_transport_failure_is_Network()
        => Assert.Equal(TranslationErrorKind.Network, ProviderErrorMapper.Classify(
            null, null, new HttpRequestException("no such host is known"), false, CancellationToken.None));

    // ---- Rows 4-10: the status rows -----------------------------------------------------------

    [Theory]
    [InlineData(429, false, TranslationErrorKind.RateLimited)]      // TP-MAP-04, row 4
    [InlineData(429, true, TranslationErrorKind.RateLimited)]       // a key changes nothing here
    [InlineData(401, true, TranslationErrorKind.AuthFailed)]        // TP-MAP-05, row 5 — with a key
    [InlineData(401, false, TranslationErrorKind.Blocked)]          // row 5b, ruling E2-g — keyless
    [InlineData(403, false, TranslationErrorKind.Blocked)]          // TP-MAP-08, row 8
    [InlineData(403, true, TranslationErrorKind.AuthFailed)]        // TP-MAP-07, row 7
    [InlineData(456, true, TranslationErrorKind.QuotaExhausted)]    // TP-MAP-09, row 9
    [InlineData(500, false, TranslationErrorKind.Unavailable)]      // TP-MAP-10, row 10
    [InlineData(502, false, TranslationErrorKind.Unavailable)]
    [InlineData(503, true, TranslationErrorKind.Unavailable)]
    public void The_status_rows_classify_as_section_4_2_says(int status, bool keyWasSent, TranslationErrorKind expected)
    {
        using var resp = Resp(status);
        Assert.Equal(expected, ProviderErrorMapper.Classify(resp, null, null, keyWasSent, CancellationToken.None));
    }

    /// <summary>
    /// TP-MAP-06 — row 6, the only row that reads the body. Azure answers an exhausted subscription
    /// with a 403 carrying a quota envelope, which is a different user story from a rejected key
    /// (S8 "the key's allowance is used up" vs S7 "check the key in Settings").
    /// </summary>
    [Theory]
    [InlineData("""{"error":{"code":403,"message":"The request is not authorized because the quota for this subscription is exceeded."}}""")]
    [InlineData("""{"error":{"code":403,"message":"Out of credit."}}""")]
    [InlineData("""{"error":{"code":403,"message":"Monthly LIMIT EXCEEDED for this resource."}}""")]
    public void A_403_naming_a_quota_is_QuotaExhausted_when_a_key_was_sent(string envelope)
    {
        using var resp = Resp(403, envelope);
        Assert.Equal(TranslationErrorKind.QuotaExhausted,
            ProviderErrorMapper.Classify(resp, envelope, null, true, CancellationToken.None));
    }

    /// <summary>TP-MAP-07's other half: the same envelope with no key is still a block — row 8 comes
    /// first for a keyless provider, and the free Google endpoint is exactly that.</summary>
    [Fact]
    public void A_quota_envelope_without_a_key_is_still_Blocked()
    {
        const string envelope = """{"error":{"code":403,"message":"quota exceeded"}}""";
        using var resp = Resp(403, envelope);
        Assert.Equal(TranslationErrorKind.Blocked,
            ProviderErrorMapper.Classify(resp, envelope, null, false, CancellationToken.None));
    }

    /// <summary>
    /// Ruling E2-g — a 401 with no key sent is a <c>Blocked</c>, exactly like a 403 with no key.
    /// The free Google endpoint sends no credentials, so a 401 from it is never "the key is wrong":
    /// it is a captive portal, a corporate proxy or a hiccup. Mapped to <c>AuthFailed</c> it opened
    /// that provider's gate until <c>ProviderGates.ClearAuthBlock</c> — which for a keyless provider
    /// has no key-save handler and therefore <b>no reachable caller</b>. One hotel Wi-Fi login page
    /// and the provider was gone for the life of the process, with the app's own exit table saying
    /// "restart it". A block is a state the breaker's own probe can leave.
    /// </summary>
    [Fact]
    public void A_401_without_a_key_is_Blocked_and_not_the_user_only_exit()
    {
        using var resp = Resp(401, "<html><body>Sign in to continue</body></html>");
        Assert.Equal(TranslationErrorKind.Blocked,
            ProviderErrorMapper.Classify(resp, null, null, keyWasSent: false, CancellationToken.None));
    }

    /// <summary>TP-MAP-07 — a 403 with a key and no quota wording is the key being rejected.</summary>
    [Fact]
    public void A_403_with_a_key_and_no_quota_wording_is_AuthFailed()
    {
        const string envelope = """{"error":{"code":403,"message":"The request is not authorized."}}""";
        using var resp = Resp(403, envelope);
        Assert.Equal(TranslationErrorKind.AuthFailed,
            ProviderErrorMapper.Classify(resp, envelope, null, true, CancellationToken.None));
    }

    // ---- Rows 12-13: the success that does not parse, and the last resort ----------------------

    /// <summary>TP-MAP-15 — row 12. A 200 whose body is not the provider's shape is a BadResponse,
    /// not a mystery.</summary>
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    public void A_success_that_does_not_parse_is_BadResponse(int status)
    {
        using var resp = Resp(status, "not json");
        Assert.Equal(TranslationErrorKind.BadResponse,
            ProviderErrorMapper.Classify(resp, "not json", null, false, CancellationToken.None));
    }

    /// <summary>
    /// TP-MAP-16 (T14) — asserted as a pair, because the point is not that a 403 is Blocked but
    /// that a 403 and a 400 are no longer the same outcome. Today both come back as
    /// "Translation service error (HTTP {code})" and a bot block therefore reads like a bug.
    /// </summary>
    [Fact]
    public void A_403_is_no_longer_the_same_outcome_as_a_400_or_a_404()
    {
        using var forbidden = Resp(403);
        using var badRequest = Resp(400);
        using var notFound = Resp(404);

        var blocked = ProviderErrorMapper.Classify(forbidden, null, null, false, CancellationToken.None);
        var four00 = ProviderErrorMapper.Classify(badRequest, null, null, false, CancellationToken.None);
        var four04 = ProviderErrorMapper.Classify(notFound, null, null, false, CancellationToken.None);

        Assert.Equal(TranslationErrorKind.Blocked, blocked);
        Assert.Equal(TranslationErrorKind.Unknown, four00);
        Assert.Equal(TranslationErrorKind.Unknown, four04);
        Assert.NotEqual(blocked, four00);
    }

    /// <summary>
    /// AC 3 — the mapper is total. Every status from 100 to 599, with and without a key, with and
    /// without a body head, and the empty outcome (no response, no exception at all): it must
    /// answer, and it must never throw. An honest Unknown beats a wrong guess.
    /// </summary>
    [Fact]
    public void It_answers_for_every_outcome_and_never_throws()
    {
        Assert.Equal(TranslationErrorKind.Unknown,
            ProviderErrorMapper.Classify(null, null, null, false, CancellationToken.None));
        // A transport exception the table does not name is not a guess either.
        Assert.Equal(TranslationErrorKind.Unknown,
            ProviderErrorMapper.Classify(null, null, new InvalidOperationException("boom"), false, CancellationToken.None));

        foreach (var status in Enumerable.Range(100, 500))
            foreach (var key in new[] { false, true })
            {
                using var resp = Resp(status, "{}");
                var kind = ProviderErrorMapper.Classify(resp, "{}", null, key, CancellationToken.None);
                Assert.True(Enum.IsDefined(kind), $"HTTP {status} (key={key}) produced {kind}");
                Assert.NotEqual(Cancelled, kind);   // row 1 is the only door to that value
            }
    }

    // ---- Retry-After (§5.5): the server's own hint, when it sent one ---------------------------

    [Fact]
    public void Retry_After_is_read_as_delta_seconds_or_as_a_date_and_is_null_when_absent()
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        using var silent = Resp(429);
        Assert.Null(ProviderErrorMapper.RetryAfter(silent, now));
        Assert.Null(ProviderErrorMapper.RetryAfter(null, now));

        using var delta = Resp(429);
        delta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        Assert.Equal(now.AddSeconds(120), ProviderErrorMapper.RetryAfter(delta, now));

        using var dated = Resp(429);
        dated.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(5));
        Assert.Equal(now.AddMinutes(5), ProviderErrorMapper.RetryAfter(dated, now));
    }

    /// <summary>
    /// A hint the app cannot read is the same as no hint: null, never an exception and never a
    /// guessed instant. The header is whatever a proxy or an angry endpoint chose to send, so this
    /// walks the shapes a strict parser rejects — words, a negative delta, a delta that overflows,
    /// an unparseable date, an empty value. It matters because E2's gate will take this value as a
    /// "do not call again until" and a wrong instant would pause a working provider.
    /// </summary>
    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("99999999999999999999")]
    [InlineData("Tue, 99 Xxx 2026 99:99:99 GMT")]
    [InlineData("")]
    [InlineData("120, 240")]
    public void Retry_After_ignores_a_header_it_cannot_parse_and_never_throws(string raw)
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        using var resp = Resp(429);
        resp.Headers.TryAddWithoutValidation("Retry-After", raw);

        Assert.Null(ProviderErrorMapper.RetryAfter(resp, now));
    }

    // ---- The two providers really go through the mapper (AC 1, AC 2) --------------------------

    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";
    private const string GoogleBye = """[[["bye","пока",null,null,10]],null,"ru"]""";

    /// <summary>
    /// AC 2 through the real provider: the keyless Google endpoint's 403 is a Blocked, its 400 is
    /// an Unknown — and the sentence the user reads is byte-identical to the one v0.14.0 produced
    /// for both. Only the Kind changed in this increment; E1.S6 changes the wording.
    /// </summary>
    [Theory]
    [InlineData(403, TranslationErrorKind.Blocked)]
    [InlineData(400, TranslationErrorKind.Unknown)]
    [InlineData(404, TranslationErrorKind.Unknown)]
    public async Task Google_classifies_its_non_transient_statuses_through_the_mapper(
        int status, TranslationErrorKind expected)
    {
        var fake = new FakeHandler().Respond((HttpStatusCode)status, "nope");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal($"Translation service error (HTTP {status}). Please try again later.", ex.Message);
        Assert.Equal(1, fake.Requests);   // non-transient: still no retry (E2.S5 owns that decision)
    }

    /// <summary>
    /// The E1.S1 deferred item: a transport failure on the LAST attempt used to escape
    /// <c>RequestAsync</c> raw, which also made the friendly "couldn't reach" line below the loop
    /// unreachable (E1.S2's deferred item). It is now the mapper's Network, carrying that same
    /// sentence — and the retry count is unchanged at three.
    /// </summary>
    [Fact]
    public async Task Googles_last_attempt_transport_failure_is_Network_and_keeps_its_sentence()
    {
        var fake = new FakeHandler().Throws(new HttpRequestException("no route to host"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Network, ex.Kind);
        Assert.Equal("Couldn't reach the translation service. Check your Internet connection.", ex.Message);
        Assert.Equal(3, fake.Requests);
    }

    /// <summary>A Google timeout is a Timeout, and it carries the sentence it has always carried.
    /// Since E1.S6 that sentence is the LOG's account only: what the player reads is the Timeout
    /// Kind's copy-deck sentence from <c>UserMessages</c>, pinned in <c>UserMessagesTests</c>. This
    /// case pins the Kind and the no-retry decision. A timeout is not retried, exactly as before.</summary>
    [Fact]
    public async Task A_Google_timeout_is_a_Timeout_with_todays_wording()
    {
        var fake = new FakeHandler().TimesOut();

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.Equal("the request timed out", ex.Message);
        Assert.Equal(1, fake.Requests);
    }

    /// <summary>
    /// The E1.S1 deferred item this story closes: the per-line loop's unfiltered
    /// <c>catch (OperationCanceledException) { throw; }</c>. A timeout on line 2 used to rethrow as
    /// if the user had cancelled — throwing away line 1's good translation, which is the exact case
    /// the comment above that loop exists to protect. It now latches like any other provider
    /// failure and the successes survive (I3, I16 — the latch and its placeholder are unchanged).
    /// </summary>
    [Fact]
    public async Task A_timeout_mid_batch_keeps_the_lines_already_translated()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)   // the joined batch comes back as one segment: count mismatch
            .RespondJson(GoogleOk)   // per-line: line 1 translates
            .TimesOut();             // per-line: line 2 times out (the last step repeats)

        var outp = await new TranslationService(fake)
            .TranslateLinesAsync(new[] { "привет", "пока" }, "ru", "en");

        Assert.Equal(new[] { "hello", "(rate-limited — try again shortly)" }, outp);
    }

    /// <summary>A genuine cancel still propagates untouched — row 1's contract, at the provider.</summary>
    [Fact]
    public async Task A_real_cancel_still_propagates_as_an_OperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeHandler().RespondJson(GoogleOk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TranslationService(fake).TranslateLinesAsync(
                new[] { "привет", "пока" }, "ru", "en", cts.Token));
        Assert.Equal(0, fake.Requests);
    }

    /// <summary>The one-line path takes neither the batch nor the per-line loop — it goes through
    /// <c>SafeOne</c>, whose generic catch used to turn a genuine Stop into a
    /// "(translation failed: A task was canceled.)" line. I3 asks every OCE catch in the pipeline
    /// to filter, and this was the last one that did not.</summary>
    [Fact]
    public async Task A_real_cancel_propagates_from_the_one_line_path_too()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeHandler().RespondJson(GoogleOk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TranslationService(fake).TranslateLinesAsync(
                new[] { "привет" }, "ru", "en", cts.Token));
        Assert.Equal(0, fake.Requests);
    }

    // ---- The three batch exits (I5/I16 + §6.3), pinned one by one -----------------------------

    /// <summary>
    /// Exit 1 — a batch TIMEOUT propagates as ONE Timeout and must never open the per-line
    /// fallback. Falling through would multiply the 12 s request timeout by the line count inside a
    /// single LIVE tick; one honest failure beats N waits. This is the behaviour change the story
    /// names, and until now nothing pinned it.
    /// </summary>
    [Fact]
    public async Task A_batch_timeout_propagates_and_never_fans_out_per_line()
    {
        var fake = new FakeHandler().TimesOut();

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateLinesAsync(
                new[] { "привет", "пока", "спасибо" }, "ru", "en"));

        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.Equal("the request timed out", ex.Message);
        Assert.Equal(1, fake.Requests);   // the batch, and nothing else
    }

    /// <summary>
    /// Exit 2 — a count mismatch DOES fall through to per-line (§6.3, the join/split family), and
    /// the lines really are translated one by one. E2 bounds this at <c>PerLineCap</c>; today it is
    /// unbounded and I16 keeps it that way.
    /// </summary>
    [Fact]
    public async Task A_batch_count_mismatch_falls_through_to_the_per_line_loop()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)    // batch: one segment for two lines — mismatch
            .RespondJson(GoogleOk)    // per-line: line 1
            .RespondJson(GoogleBye);  // per-line: line 2

        var outp = await new TranslationService(fake)
            .TranslateLinesAsync(new[] { "привет", "пока" }, "ru", "en");

        Assert.Equal(new[] { "hello", "bye" }, outp);
        Assert.Equal(3, fake.Requests);   // batch + one per line
    }

    /// <summary>
    /// Exit 3 — a batch whose 200 does not parse propagates as ONE BadResponse. It does NOT fan out
    /// per-line, and that is deliberate: §6.3 gives the join/split family a per-line fallback for a
    /// <em>count mismatch</em>, not for a body that is not the provider's shape. Retrying such a
    /// body line by line asks the same broken endpoint N more times and ends on the
    /// <c>rateLimited</c> latch anyway, so the user would trade one honest sentence for N
    /// placeholders. Pre-existing behaviour (<c>catch (TranslationException) { throw; }</c> predates
    /// this epic) and unchanged here — pinned so the choice is on record for E1.S4, which turns most
    /// of these bodies into RateLimited/Blocked before the parser ever sees them.
    /// </summary>
    [Fact]
    public async Task A_batch_body_that_does_not_parse_propagates_as_one_BadResponse()
    {
        var fake = new FakeHandler().RespondJson("not json");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateLinesAsync(
                new[] { "привет", "пока" }, "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        Assert.Equal(1, fake.Requests);
    }

    /// <summary>
    /// The E1.S1 deferred item on the DeepL side: the timeout mapping wrapped <c>SendAsync</c> but
    /// not the body read, so a timeout while reading the response escaped as a raw
    /// TaskCanceledException — past the TranslationException contract the FallbackTranslator and
    /// the LIVE loop are written against. Both sit in the same try now.
    /// <para>What this case can actually prove is the mapping, not the position: with HttpClient's
    /// default <c>ResponseContentRead</c> the body is buffered inside <c>SendAsync</c>, so a
    /// content that refuses to materialise fails there and the post-send read cannot fail from
    /// transport at all. The fix is therefore defensive — the contract is the point, not the odds —
    /// and the position is asserted by reading the file, not by this test.</para>
    /// </summary>
    [Fact]
    public async Task A_DeepL_timeout_reading_the_response_is_a_Timeout()
    {
        var handler = new BodyFailsHandler(new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing.",
            new TimeoutException(), CancellationToken.None));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", handler).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.Equal("DeepL timed out — check your connection or try again.", ex.Message);
    }

    /// <summary>A response whose body fails to arrive is a transport failure, not a parse failure
    /// — the same catch, the other Kind.</summary>
    [Fact]
    public async Task A_DeepL_transport_failure_reading_the_response_is_Network()
    {
        var handler = new BodyFailsHandler(new HttpRequestException("connection reset"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", handler).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Network, ex.Kind);
        Assert.Equal("Couldn't reach DeepL. Check your Internet connection.", ex.Message);
    }

    /// <summary>A 200 whose body arrives whole but is not DeepL's shape stays a BadResponse — the
    /// parser detects it, §4.2 row 12 names it, and the two agree.</summary>
    [Fact]
    public async Task A_DeepL_success_that_does_not_parse_is_BadResponse()
    {
        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", new FakeHandler().RespondJson("not json"))
                .TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
    }

    // ============================================================================================
    //  Row 11 / §4.3 — the HTML abuse page, classified BEFORE it is parsed (TP-MAP-11…14)
    // ============================================================================================

    /// <summary>A recorded body (IS-9), read from the copy the csproj item group puts next to the
    /// test assembly. Asserts rather than throwing an IOException, so a missing item group reads as
    /// "the fixtures did not ship", not as a mystery.</summary>
    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path),
            $"fixture {name} not found at {path} — is the Fixtures item group still in PWRUHelper.Tests.csproj?");
        return File.ReadAllText(path);
    }

    private static HttpResponseMessage Html(int status, string body)
        => new((HttpStatusCode)status)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };

    /// <summary>A response that declares no media type at all — the case §4.3 step 1 does not
    /// name, and the one where the body sniff has to answer alone.</summary>
    private static HttpResponseMessage NoContentType(int status, string body)
    {
        var content = new StringContent(body);
        content.Headers.ContentType = null;
        return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
    }

    /// <summary>CI-6 — the fixtures ship, they are text, and the folder stays inside its budget.
    /// A silently-dropped item group would otherwise turn every case below into a file-not-found.</summary>
    [Fact]
    public void The_recorded_fixtures_ship_next_to_the_tests_and_stay_under_the_64_KB_budget()
    {
        var dir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
        Assert.True(dir.Exists, $"no Fixtures folder at {dir.FullName} — the csproj item group is gone");

        var files = dir.GetFiles("*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.Name == "google-429.html");
        Assert.Contains(files, f => f.Name == "google-captcha.html");
        Assert.True(files.Sum(f => f.Length) <= 64 * 1024,
            $"Fixtures/ is {files.Sum(f => f.Length)} bytes — CI-6 caps it at 64 KB");
    }

    /// <summary>
    /// TP-MAP-11 (T13) — the story, in one case. The body is the measured P2 page
    /// (<c>benchmark…</c> §3.1), served with a <b>200</b>: it classifies as RateLimited, and the
    /// parser is never reached. The second half is asserted with a parse-counting spy standing in
    /// for the provider's <c>JsonDocument.Parse</c>, called through §4.3's own step 4 — "only a
    /// body that survived 1–3". A Kind assertion alone would not prove the ordering.
    /// </summary>
    [Fact]
    public void TP_MAP_11_the_measured_abuse_page_on_a_200_is_RateLimited_and_the_parser_is_never_reached()
    {
        var page = Fixture("google-429.html");
        using var resp = Html(200, page);

        int parses = 0;
        string ParseLikeTheProvider(string body)
        {
            parses++;
            using var doc = System.Text.Json.JsonDocument.Parse(body);   // throws on HTML — it must never run
            return doc.RootElement.ToString();
        }

        var kind = ProviderErrorMapper.Classify(resp, page, null, false, CancellationToken.None, out var head);

        Assert.Equal(TranslationErrorKind.RateLimited, kind);
        Assert.NotEqual(TranslationErrorKind.BadResponse, kind);   // BadResponse is what "the parser saw it" looks like

        // §4.3 step 4, written exactly as the provider writes it.
        if (!ProviderErrorMapper.LooksLikeHtml(resp, page)) ParseLikeTheProvider(page);
        Assert.Equal(0, parses);

        // I11 / §10.1: what travels on is the server's own page, de-tagged and capped.
        Assert.NotNull(head);
        Assert.DoesNotContain('<', head!);
        Assert.True(head!.Length <= 120, $"the log head is {head.Length} characters, §10.1 caps it at 120");
    }

    /// <summary>
    /// AC 3 — the same measured body, on any status. 200 reaches row 11 and is RateLimited; 429
    /// never gets there because row 4 already said RateLimited, which is the same answer by a
    /// shorter road. Rows 4–10 keep their precedence: §4.2's order is the table's, and this story
    /// reorders nothing above the seam.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(429)]
    [InlineData(404)]
    public void The_measured_abuse_page_is_RateLimited_on_every_status_that_reaches_row_11(int status)
    {
        var page = Fixture("google-429.html");
        using var resp = Html(status, page);

        Assert.Equal(TranslationErrorKind.RateLimited,
            ProviderErrorMapper.Classify(resp, page, null, false, CancellationToken.None));
    }

    /// <summary>The fixture really is the recorded evidence and not a paraphrase: the sentence
    /// <c>benchmark…</c> §3.1 captured survives de-tagging word for word, and the RateLimitMarker
    /// is inside the head the matcher actually reads.</summary>
    [Fact]
    public void The_429_fixture_still_carries_the_sentence_the_benchmark_recorded()
    {
        var head = ProviderErrorMapper.DeTaggedHead(Fixture("google-429.html"));

        Assert.Contains("We're sorry", head, StringComparison.Ordinal);
        Assert.Contains("but your computer or network may be sending automated queries.", head, StringComparison.Ordinal);
        Assert.Contains("To protect our users, we can't process your request right now.", head, StringComparison.Ordinal);
        // The <style> block's CSS is not text, and if it counted the marker would fall outside the head.
        Assert.DoesNotContain("font-family", head, StringComparison.Ordinal);
    }

    /// <summary>TP-MAP-12 — a captcha interstitial is a refusal, not a throttle. It carries no
    /// RateLimitMarker, so the BlockMarkers decide.</summary>
    [Fact]
    public void TP_MAP_12_a_captcha_page_is_Blocked()
    {
        var page = Fixture("google-captcha.html");
        using var resp = Html(200, page);

        Assert.Equal(TranslationErrorKind.Blocked,
            ProviderErrorMapper.Classify(resp, page, null, false, CancellationToken.None, out var head));
        Assert.Contains("captcha", ProviderErrorMapper.DeTaggedHead(page), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(head);
    }

    /// <summary>
    /// TP-MAP-13 — an HTML page nobody has a marker for (a consent interstitial) is still Blocked,
    /// and the 120-character de-tagged head comes back with it. That head is the whole point: it is
    /// what E1.S5 puts in the log's <c>body=</c> field, and it is how the next phrasing becomes one
    /// edit in <c>TranslationPolicy</c> instead of a guess.
    /// </summary>
    [Fact]
    public void TP_MAP_13_an_HTML_page_with_no_marker_is_Blocked_and_hands_back_a_de_tagged_head()
    {
        var page = Fixture("google-consent.html");
        using var resp = Html(200, page);

        var kind = ProviderErrorMapper.Classify(resp, page, null, false, CancellationToken.None, out var head);

        Assert.Equal(TranslationErrorKind.Blocked, kind);
        Assert.NotNull(head);
        Assert.Equal(120, head!.Length);                       // §10.1: the first 120 characters, no more
        Assert.StartsWith("Before you continue to Google", head, StringComparison.Ordinal);
        Assert.Contains("cookies", head, StringComparison.Ordinal);
        Assert.DoesNotContain('<', head);                      // de-tagged
        Assert.DoesNotContain("  ", head, StringComparison.Ordinal);   // whitespace-collapsed
        Assert.Equal(head.Trim(), head);
        // The head keeps the page's own casing (§10.1's example does); only the MATCHING is lower-cased.
        Assert.Contains("Google", head, StringComparison.Ordinal);
    }

    /// <summary>
    /// TP-MAP-14 — the sniff wins over a content-type that claims JSON. This is the rule that makes
    /// "never parse-then-guess" real: a block page mislabelled <c>application/json</c> would
    /// otherwise sail straight into the parser, which is the bug §4.3 exists to kill.
    /// </summary>
    [Fact]
    public void TP_MAP_14_a_body_that_starts_with_an_angle_bracket_takes_the_HTML_path_whatever_the_content_type_claims()
    {
        var page = Fixture("google-429.html");
        using var lying = Resp(200, page);                       // Content-Type: application/json

        Assert.True(ProviderErrorMapper.LooksLikeHtml(lying, page));
        Assert.Equal(TranslationErrorKind.RateLimited,
            ProviderErrorMapper.Classify(lying, page, null, false, CancellationToken.None));

        // Leading whitespace is still whitespace, and only the first 200 characters are scanned.
        using var padded = Resp(200, "\r\n\t  <html><body>captcha</body></html>");
        Assert.True(ProviderErrorMapper.LooksLikeHtml(padded, "\r\n\t  <html><body>captcha</body></html>"));

        var late = new string(' ', 250) + "<html>we're sorry</html>";
        using var tooLate = Resp(200, late);
        Assert.False(ProviderErrorMapper.LooksLikeHtml(tooLate, late));   // §4.3 step 2: 200 characters, not more
    }

    /// <summary>
    /// The other half of the precedence rule, pinned so it is a decision and not an accident: a
    /// content-type that DECLARES a non-JSON media type takes the HTML path even when the body
    /// would have parsed. §4.3 step 1 is checked before step 2, and "never parse-then-guess" means
    /// the body's parseability is not allowed to be the tie-breaker.
    /// <para>An <b>absent</b> content-type is the one case §4.3 does not name, and it is decided
    /// the other way: nothing is declared, so the body sniff answers alone. Reading "no JSON media
    /// type" as "including none at all" would make an unlabelled healthy 200 a Blocked with an
    /// empty log head, which is exactly the wrong-guess row 13 exists to avoid.</para>
    /// </summary>
    [Fact]
    public void A_declared_non_JSON_content_type_takes_the_HTML_path_and_an_absent_one_leaves_it_to_the_body()
    {
        const string json = """[[["hello","привет",null,null,10]],null,"ru"]""";

        using var htmlCt = Html(200, json);
        Assert.True(ProviderErrorMapper.LooksLikeHtml(htmlCt, json));
        Assert.Equal(TranslationErrorKind.Blocked,
            ProviderErrorMapper.Classify(htmlCt, json, null, false, CancellationToken.None));

        using var textCt = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json) };                    // StringContent's default: text/plain
        Assert.True(ProviderErrorMapper.LooksLikeHtml(textCt, json));

        // The JSON family, all of which stay on the parser's road.
        foreach (var media in new[] { "application/json", "text/json", "application/ld+json" })
        {
            using var ok = Resp(200, json, media);
            Assert.False(ProviderErrorMapper.LooksLikeHtml(ok, json), $"{media} must not take the HTML path");
        }

        using var unlabelledJson = NoContentType(200, json);
        Assert.False(ProviderErrorMapper.LooksLikeHtml(unlabelledJson, json));

        using var unlabelledHtml = NoContentType(200, "<html><body>we're sorry</body></html>");
        Assert.True(ProviderErrorMapper.LooksLikeHtml(unlabelledHtml, "<html><body>we're sorry</body></html>"));
    }

    /// <summary>A healthy body is untouched by all of this: the recorded gtx array declares JSON,
    /// starts with '[', and never takes the HTML path — §4.3 step 4's other half.</summary>
    [Fact]
    public void A_healthy_JSON_array_survives_the_sniff_and_reaches_the_parser()
    {
        var body = Fixture("gtx-single.json");
        using var resp = Resp(200, body);

        Assert.False(ProviderErrorMapper.LooksLikeHtml(resp, body));
        using var doc = System.Text.Json.JsonDocument.Parse(body);   // it really is the provider's shape
        Assert.Equal("hello", doc.RootElement[0][0][0].GetString());
    }

    /// <summary>
    /// The sniff is the first thing that touches a response, so it may never be the thing that
    /// throws: a malformed Content-Type has to fall through to the body sniff rather than surface a
    /// parse failure past the TranslationException contract every caller is written against.
    /// <para>The second half pins what row 11 hands back when the caller read <b>no body at all</b>
    /// — which is exactly TranslationService's non-success branch (it passes <c>bodyHead: null</c>):
    /// the HTML path really was taken, so the head is <b>empty, not null</b>. E1.S5 must therefore
    /// test the head for emptiness, not just for null, before writing a <c>body=</c> field.</para>
    /// </summary>
    [Fact]
    public void The_sniff_never_throws_on_a_malformed_content_type_and_a_body_less_HTML_path_gives_an_empty_head()
    {
        const string page = "<html><body>we're sorry</body></html>";
        using var garbage = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page) };
        garbage.Content.Headers.Remove("Content-Type");
        garbage.Content.Headers.TryAddWithoutValidation("Content-Type", "not a media type;;");

        Assert.True(ProviderErrorMapper.LooksLikeHtml(garbage, page));   // no throw — the body decides
        Assert.Equal(TranslationErrorKind.Blocked,
            ProviderErrorMapper.Classify(garbage, page, null, false, CancellationToken.None));

        using var htmlNoBody = Html(404, "");
        Assert.Equal(TranslationErrorKind.Blocked,
            ProviderErrorMapper.Classify(htmlNoBody, null, null, false, CancellationToken.None, out var head));
        Assert.Equal(string.Empty, head);
    }

    // ---- The de-tagger itself (T2): bounded, single-pass, and not a regex ----------------------

    [Fact]
    public void The_de_tagger_strips_tags_collapses_whitespace_and_drops_script_and_style_content()
    {
        Assert.Equal("Hello world", ProviderErrorMapper.DeTaggedHead("<p>Hello</p>\n\n   <b>world</b>"));
        Assert.Equal("keep me", ProviderErrorMapper.DeTaggedHead(
            "<style> body { font-family: verdana; } </style><script>var a = 1 < 2;</script><p>keep me</p>"));
        Assert.Equal("", ProviderErrorMapper.DeTaggedHead("<html><head><title></title></head></html>"));
        Assert.Equal("", ProviderErrorMapper.DeTaggedHead(null));
        Assert.Equal("", ProviderErrorMapper.DeTaggedHead(""));
        // An unterminated tag ends the scan instead of running away.
        Assert.Equal("", ProviderErrorMapper.DeTaggedHead("<div class=\"never closed"));
    }

    /// <summary>
    /// The bound, asserted rather than assumed: a body far larger than any abuse page costs a
    /// bounded scan and yields at most a 400-character head. No regex means no catastrophic
    /// backtracking to worry about; this is what stands in for that worry.
    /// </summary>
    [Fact]
    public void The_de_tagger_is_bounded_in_both_directions()
    {
        var huge = new string('x', 5_000_000);
        var head = ProviderErrorMapper.DeTaggedHead("<p>" + huge + "</p>");
        Assert.Equal(400, head.Length);

        // A megabyte of markup before any text: the scan gives up rather than walking the whole body.
        var buried = string.Concat(Enumerable.Repeat("<div class=\"pad\">", 100_000)) + "needle";
        Assert.DoesNotContain("needle", ProviderErrorMapper.DeTaggedHead(buried), StringComparison.Ordinal);
    }

    // ---- The provider: the page can no longer reach JsonDocument.Parse (AC 1) ------------------

    /// <summary>
    /// AC 1 end-to-end through E1.S1's FakeHandler. Before this story the fixture below came back
    /// through the JSON parser as a BadResponse — "an unexpected response" by accident. It is now a
    /// RateLimited, and <b>the sentence the user reads is byte-identical</b>: E1.S6 owns the
    /// wording, this story owns only the Kind.
    /// </summary>
    [Fact]
    public async Task Google_classifies_an_HTML_abuse_page_served_with_a_200_before_it_parses_it()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK, Fixture("google-429.html"), "text/html");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.Equal("The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.",
                     ex.Message);
        Assert.Equal(1, fake.Requests);   // not retried — exactly as an unparseable 200 behaved before
    }

    /// <summary>A captcha page on a 200 takes the same road to the other Kind.</summary>
    [Fact]
    public async Task Google_classifies_a_captcha_page_served_with_a_200_as_Blocked()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK, Fixture("google-captcha.html"), "text/html");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Blocked, ex.Kind);
    }

    /// <summary>The healthy path is unchanged: a JSON array still parses and still comes back as a
    /// translation. The sniff is a gate, not a filter.</summary>
    [Fact]
    public async Task A_healthy_body_still_translates()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK, Fixture("gtx-single.json"));

        Assert.Equal("hello", await new TranslationService(fake).TranslateAsync("привет", "ru", "en"));
    }

    /// <summary>
    /// The ordering itself, pinned in the file rather than only in behaviour — the instrument E1.S3
    /// used for the same kind of claim. A Kind assertion says the parser did not produce the
    /// outcome; this says the parser cannot even be reached, because the sniff is written above it
    /// and the success path throws in between.
    /// </summary>
    [Fact]
    public void The_sniff_is_written_above_the_parser_in_TranslationService()
    {
        // Comments stripped first, and not for tidiness: the sniff's own comment explains what used
        // to "sail into JsonDocument.Parse below", so a plain substring scan finds the parser three
        // lines ABOVE the guard and fails on prose. E1.S3's TP-MAP-17 red was the same shape — a
        // source scan that cannot tell code from commentary is measuring the wrong thing.
        var src = string.Join("\n", File.ReadAllLines(SourceOf("TranslationService.cs"))
                                        .Where(l => !l.TrimStart().StartsWith("//")));

        int sniff = src.IndexOf("LooksLikeHtml", StringComparison.Ordinal);
        int parse = src.IndexOf("JsonDocument.Parse", StringComparison.Ordinal);

        Assert.True(sniff >= 0, "TranslationService no longer sniffs the body — §4.3 step 4 is gone");
        Assert.True(parse >= 0, "TranslationService no longer parses — this guard is looking at the wrong file");
        Assert.True(sniff < parse, "the HTML sniff must be written BEFORE JsonDocument.Parse (§4.3, never parse-then-guess)");
    }

    /// <summary>An app source file, found by walking up from the test output — the same walk
    /// <c>TranslationPolicyTests</c> uses for its grade scan.</summary>
    private static string SourceOf(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");

        var path = Path.Combine(dir!.FullName, "Services", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }

    /// <summary>Answers a 200 whose CONTENT throws when it is read. FakeHandler cannot express this
    /// (its bodies are complete strings), and a body that never materialises is the shape the
    /// send-and-read block has to map, so the double lives here until a second story needs it.</summary>
    private sealed class BodyFailsHandler : HttpMessageHandler
    {
        private readonly Exception _onRead;
        public BodyFailsHandler(Exception onRead) => _onRead = onRead;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent(_onRead) });

        private sealed class FailingContent : HttpContent
        {
            private readonly Exception _ex;
            public FailingContent(Exception ex) => _ex = ex;
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw _ex;
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
        }
    }
}

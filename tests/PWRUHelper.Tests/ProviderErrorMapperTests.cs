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
/// Rows 11 and 13's HTML sniffing (TP-MAP-11…14) belong to E1.S4 and are deliberately absent.
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
    [InlineData(401, false, TranslationErrorKind.AuthFailed)]       // TP-MAP-05, row 5
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

    // ---- The two providers really go through the mapper (AC 1, AC 2) --------------------------

    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";

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

    /// <summary>A Google timeout is a Timeout, and the sentence it carries is the one
    /// <c>Friendly()</c> renders today for a raw TaskCanceledException — so nothing the user reads
    /// changes. A timeout is not retried, exactly as before.</summary>
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

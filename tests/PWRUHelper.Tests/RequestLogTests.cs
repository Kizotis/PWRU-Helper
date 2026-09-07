using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E1.S5 — the §10.1 per-request diagnostic line.
///
/// Two halves, and the second one is the story. The first half is ordinary: the line carries the
/// fields `architecture-cible.md` §10.1 lists, a failing attempt produces exactly one of them and a
/// successful attempt produces none. The second half is <b>I11</b>, and it is what makes this
/// feature safe to ship at all: the log is pasted to Discord <i>by design</i>
/// (<c>MainWindow.xaml.cs:312-322</c>), so the user's own sentence, the <c>q=</c> that carries it,
/// the full URL and any API key must be provably absent from the file. Those assertions are written
/// as negatives over the REAL produced file, with a Cyrillic sentinel and a recognisable key, and if
/// they are weak the whole story is worse than not doing it.
///
/// <para><b>Isolation.</b> <see cref="TestLogRedirect"/> owns <c>Logging.DirectoryOverride</c> for
/// the whole assembly, so a test here takes it, restores the previous value in a <c>finally</c>
/// (<c>LoggingTests.cs:101-110</c>'s discipline) and never assumes the file is otherwise empty:
/// xUnit runs other classes in parallel and their deliberate failures land in whatever directory is
/// current. Every end-to-end case therefore fingerprints its own lines with a language pair nothing
/// else uses (<c>dir=zz-&gt;qq</c> and friends) and filters on it, instead of counting lines.</para>
/// </summary>
[Collection(LogFileCollection.Name)]
public class RequestLogTests : GatesTestBase
{
    // The Google endpoint's healthy shape, same literal HttpSeamGuardTests pins.
    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";

    // A sentence no other test writes, in the script a real user types, plus an ASCII half so a
    // "the log dropped the Cyrillic and kept the rest" bug cannot pass the sentinel assertions.
    private const string Sentinel = "привет KIZOTIS-SENTINEL-4711 как дела";

    // ---------------------------------------------------------------------------------------
    //  TP-LOG-08 — millisecond timestamps (AC 4)
    // ---------------------------------------------------------------------------------------

    /// <summary>The retry spacing this story exists to make readable is 300 ms and 600 ms; a
    /// second-resolution timestamp renders both as "the same second".</summary>
    [Fact]
    public void TP_LOG_08_the_timestamp_carries_milliseconds()
    {
        var dir = TempDir();
        try
        {
            new LogWriter(dir).Write("WARN", "marker");

            var text = File.ReadAllText(Path.Combine(dir, "log.txt"));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[WARN\] marker", text);
        }
        finally { Delete(dir); }
    }

    // ---------------------------------------------------------------------------------------
    //  The builder, tested without HTTP
    // ---------------------------------------------------------------------------------------

    /// <summary>The field set and its order, on a hand-built 429 — the same assertion TP-LOG-04
    /// makes end to end below, here where every value is known.</summary>
    [Fact]
    public void The_line_carries_every_section_10_1_field_in_order()
    {
        var call = RequestLog.ForRequest("google-gtx",
            new Uri("https://translate.googleapis.com/translate_a/single?client=gtx&q=secret"),
            "ru", "en", "one\ntwo", TranslationPolicy.MaxAttempts);

        using var resp = Resp(429, "<html><body>We're sorry...</body></html>", "text/html");
        resp.Headers.TryAddWithoutValidation("Retry-After", "30");
        resp.Headers.TryAddWithoutValidation("Via", "1.1 google");
        resp.Headers.TryAddWithoutValidation("Server", "HTTP server (unknown)");
        resp.Headers.TryAddWithoutValidation("Set-Cookie", "NID=abc");

        var line = RequestLog.Line(call, 2, RequestLog.ResponseFacts.Of(resp, BodyOf(resp)),
            TimeSpan.FromMilliseconds(142), 37);

        // The shape §10.1 prints, field for field:
        // tr provider=google-gtx ep=translate.googleapis.com/translate_a/single dir=ru->en
        // attempt=2/2 cid=0054 status=429 elapsed=142ms retry-after=30 ct=text/html len=40
        // hdrs=[via:"1.1 google" srv:"HTTP server (unknown)" xrl:- set-cookie:yes]
        // bytes=7 lines=2 burst60=37 ipv=? body="We're sorry..."
        Assert.Matches(
            @"^tr provider=google-gtx ep=translate\.googleapis\.com/translate_a/single dir=ru->en " +
            @"attempt=2/2 cid=[0-9a-f]{4} status=429 elapsed=142ms retry-after=30 ct=text/html " +
            @"len=\d+ hdrs=\[via:""1\.1 google"" srv:""HTTP server \(unknown\)"" xrl:- set-cookie:yes\] " +
            @"bytes=7 lines=2 burst60=37 ipv=\? body="".+""$",
            line);
    }

    /// <summary>Absent headers read as <c>-</c> rather than vanishing, so a parser (and a human)
    /// can tell "the server said nothing" from "the field was dropped".</summary>
    [Fact]
    public void A_bare_response_renders_every_header_field_as_a_dash()
    {
        var call = Call("ru", "en", "hi");
        using var resp = Resp(500, "{}", "application/json");

        var line = RequestLog.Line(call, 1, RequestLog.ResponseFacts.Of(resp, "{}"),
            TimeSpan.Zero, 1);

        Assert.Contains("retry-after=- ", line);
        Assert.Contains("hdrs=[via:- srv:- xrl:- set-cookie:no]", line);
        Assert.DoesNotContain("body=", line);
    }

    /// <summary>`X-RateLimit-*` is the header family that would settle whether Google is throttling
    /// by policy or by abuse detection, so it is rendered with its name AND value.</summary>
    [Fact]
    public void Rate_limit_headers_are_rendered_with_their_names()
    {
        using var resp = Resp(429, "{}");
        resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");

        var line = RequestLog.Line(Call("ru", "en", "hi"),
            1, RequestLog.ResponseFacts.Of(resp, "{}"), TimeSpan.Zero, 1);

        Assert.Contains("xrl:X-RateLimit-Remaining=0", line);
    }

    /// <summary>A transport failure has no response at all: the status field becomes the exception
    /// TYPE, which is what discriminates "no Internet" from "the endpoint refused us" without
    /// asking the user (§5.2).</summary>
    [Fact]
    public void A_transport_failure_logs_the_exception_type_as_its_status()
    {
        var network = RequestLog.Line(Call("ru", "en", "hi"), 1,
            RequestLog.ResponseFacts.OfTransport(new HttpRequestException("boom")), TimeSpan.Zero, 1);
        var timeout = RequestLog.Line(Call("ru", "en", "hi"), 1,
            RequestLog.ResponseFacts.OfTransport(new TaskCanceledException("t")), TimeSpan.Zero, 1);

        Assert.Contains("status=HttpRequestException", network);
        Assert.Contains("status=TaskCanceledException", timeout);
        // The message is the exception's, not the user's — but it is still not logged: only the type.
        Assert.DoesNotContain("boom", network);
    }

    /// <summary>Every value the SERVER controls is bounded and ASCII-folded, because the line has to
    /// stay one line: a header carrying a newline would otherwise split one event into two.</summary>
    [Fact]
    public void The_line_is_a_single_ascii_line_whatever_the_server_sends()
    {
        using var resp = Resp(503, "<p>" + new string('é', 400) + "</p>", "text/html");
        resp.Headers.TryAddWithoutValidation("Server", "a\r\nInjected: yes");

        var line = RequestLog.Line(Call("ru", "en", "hi"), 1,
            RequestLog.ResponseFacts.Of(resp, BodyOf(resp)), TimeSpan.FromSeconds(12), 1);

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        Assert.True(line.Length <= 400, $"the line is {line.Length} chars — §10.1's shape is ~300");
    }

    // ---------------------------------------------------------------------------------------
    //  I11 — the structural half: what the builder CANNOT render
    // ---------------------------------------------------------------------------------------

    /// <summary>TP-LOG-02, structurally. The endpoint travels as a <see cref="Uri"/> and the one
    /// function that turns it into text renders host + path — it cannot express a query, so no call
    /// site can leak one by passing the wrong string.</summary>
    [Fact]
    public void TP_LOG_02_the_endpoint_renderer_cannot_express_a_query_string()
    {
        var ep = RequestLog.Endpoint(new Uri(
            "https://translate.googleapis.com/translate_a/single?client=gtx&sl=ru&tl=en&dt=t&q=" + Sentinel));

        Assert.Equal("translate.googleapis.com/translate_a/single", ep);
        Assert.DoesNotContain("?", ep);
        Assert.DoesNotContain("q=", ep);
        Assert.Equal("-", RequestLog.Endpoint(null));
    }

    /// <summary>The <see cref="RequestLog.Call"/> record is where a request's identity is kept for
    /// the whole retry loop, and it holds the payload's SIZE, never the payload. A record struct
    /// prints its members, so this reads the whole thing back.</summary>
    [Fact]
    public void The_call_record_keeps_the_size_of_the_payload_and_never_the_payload()
    {
        var call = RequestLog.ForRequest("google-gtx", new Uri("https://x.test/p?q=" + Sentinel),
            "ru", "en", Sentinel, 3);

        Assert.DoesNotContain("KIZOTIS-SENTINEL", call.ToString());
        Assert.DoesNotContain("привет", call.ToString());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(Sentinel), call.RequestBytes);
        Assert.Equal(1, call.RequestLines);
    }

    /// <summary>
    /// The one gate every body passes through, and it is deliberately NARROWER than §4.3's
    /// <c>LooksLikeHtml</c>: markup only, whatever the content-type claimed. The provider's own JSON
    /// answer CONTAINS the user's text (original and translation both), so a proxy that mislabels it
    /// <c>text/plain</c> would otherwise walk that text into a report the user pastes to Discord.
    /// </summary>
    [Fact]
    public void The_body_gate_takes_markup_only_and_caps_it_at_120_characters()
    {
        // The provider's real answer, mislabelled: not markup ⇒ never logged.
        Assert.Null(RequestLog.BodyHead(GoogleOk));
        Assert.Null(RequestLog.BodyHead("""["привет KIZOTIS-SENTINEL-4711"]"""));
        Assert.Null(RequestLog.BodyHead(null));
        Assert.Null(RequestLog.BodyHead("   "));

        var head = RequestLog.BodyHead(
            "<html><head><style>body{color:red}</style></head><body><div>We're sorry</div>" +
            "<div>but your computer or network may be sending automated queries.</div>" +
            new string('x', 500) + "</body></html>");

        Assert.NotNull(head);
        Assert.True(head!.Length <= RequestLog.MaxBodyChars, $"head is {head.Length} chars");
        Assert.StartsWith("We're sorry but your computer", head);
        Assert.DoesNotContain("<", head);
        Assert.DoesNotContain("color:red", head);   // <style> content is dropped, not rendered
    }

    /// <summary>
    /// The other way a user's sentence can reach a server's page: an interstitial that quotes the
    /// URL it refused — which carries the sentence in <c>q=</c>, percent-escaped. The head keeps the
    /// prose that identifies the block and stops at the echo. Google's own /sorry/ page does not do
    /// this; "the page we have seen does not" is not a rule.
    /// </summary>
    [Theory]
    [InlineData("<p>Blocked: /translate_a/single?q=привет KIZOTIS-SENTINEL-4711</p>", "Blocked: /translate_a/single?")]
    [InlineData("<p>Blocked: %D0%BF%D1%80%D0%B8%D0%B2%D0%B5%D1%82</p>", "Blocked:")]
    [InlineData("<p>Refused https://translate.googleapis.com/x</p>", "Refused https")]
    // Prose that merely looks like it: '%' not followed by two hex digits is not an escape.
    [InlineData("<p>100% of requests were refused</p>", "100% of requests were refused")]
    public void A_page_that_echoes_the_request_is_cut_at_the_echo(string body, string expected)
    {
        Assert.Equal(expected, RequestLog.BodyHead(body));
    }

    // ---------------------------------------------------------------------------------------
    //  I11 — adversarial: the three shapes that would leak if the gate were only a content-type
    //  check. Each one is a real answer a real endpoint can return, not a hypothetical.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Adversarial 1 — the provider's own answer, relabelled.</b> Google's JSON echoes the source
    /// sentence AND the detected language back at us, and a corporate proxy is free to hand it over
    /// as <c>text/plain</c>. That is the worst case on purpose: §4.3's wider rule reads a
    /// non-JSON content-type as "a page", so the app itself decides this 200 is a block page and
    /// hands the body to the log. The body gate is narrower — markup only — so the sentence still
    /// never becomes <c>body=</c>. Driven end to end: the relabelling is a response-level fact a
    /// unit test on the gate cannot express.
    /// </summary>
    [Fact]
    public async Task I11_the_providers_own_json_relabelled_text_plain_never_becomes_a_body()
    {
        var echo = $"[[[\"hello\",\"{Sentinel}\",null,null,10]],null,\"ru\"]";
        var lines = await FailingCall("zz", "qj",
            h => h.Respond(HttpStatusCode.OK, echo, "text/plain"));

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.DoesNotContain("body=", line);
            Assert.DoesNotContain("KIZOTIS-SENTINEL", line, StringComparison.Ordinal);
            Assert.DoesNotContain("привет", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>Adversarial 2 — the query hidden in markup rather than in prose.</b> A block page that
    /// offers a "try again" form carries the sentence in an attribute, and its retry link carries it
    /// percent-encoded in an <c>href</c>. Neither is visible text, so the de-tagger drops both; the
    /// entity-encoded third form is cut by the echo rule. What survives is the page's own sentence,
    /// which is the only thing the field is for.
    /// </summary>
    [Fact]
    public void I11_a_query_hidden_in_an_attribute_or_a_link_never_reaches_the_head()
    {
        var head = RequestLog.BodyHead(
            "<html><body>" +
            $"<form><input name=\"q\" value=\"{Sentinel}\"></form>" +
            "<a href=\"/translate_a/single?q=%D0%BF%D1%80%D0%B8%D0%B2%D0%B5%D1%82+KIZOTIS-SENTINEL-4711\">try again</a>" +
            "<p>Your request was refused.</p></body></html>");

        Assert.NotNull(head);
        Assert.DoesNotContain("KIZOTIS-SENTINEL", head!, StringComparison.Ordinal);
        Assert.DoesNotContain("привет", head!, StringComparison.Ordinal);
        Assert.DoesNotContain("%D0", head!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refused", head!, StringComparison.Ordinal);

        // The same sentence as HTML entities — visible text this time, and still not the page's.
        Assert.Equal("Blocked:", RequestLog.BodyHead(
            "<p>Blocked: &#x43F;&#x440;&#x438;&#x432;&#x435;&#x442;</p>"));
    }

    /// <summary>
    /// <b>Adversarial 3 — a credential echoed by an error page.</b> No keyed provider emits a line
    /// in this story (DeepL joins at E2.S5), but the gate is what E2.S5 will inherit, and a keyed
    /// provider's HTML error page naming the parameter it rejected is exactly how a key would walk
    /// into a report pasted to Discord. The head stops at the parameter's NAME.
    /// </summary>
    [Theory]
    [InlineData("<p>Authentication failed for auth_key=KIZOTIS-DEEPL-KEY-0000-1111:fx</p>",
                "Authentication failed for")]
    [InlineData("<p>Rejected: api_key KIZOTIS-DEEPL-KEY-0000-1111</p>", "Rejected:")]
    [InlineData("<p>Bad token=KIZOTIS-DEEPL-KEY-0000-1111</p>", "Bad")]
    [InlineData("<p>Use Bearer KIZOTIS-DEEPL-KEY-0000-1111 instead</p>", "Use")]
    // E6.S2's shape: Azure names its credential header with a COLON, so none of the markers above
    // sees it. The header name is the marker, which gives the second keyed provider the same two
    // layers DeepL has (this cut, and its own ProviderOptions.Secret scrub).
    [InlineData("<p>Rejected: Ocp-Apim-Subscription-Key KIZOTIS-DEEPL-KEY-0000-1111</p>", "Rejected:")]
    [InlineData("<p>Bad ocp-apim-subscription-key: KIZOTIS-DEEPL-KEY-0000-1111</p>", "Bad")]
    // The guard the cut must not trip on: prose, not a parameter.
    [InlineData("<p>The monkey= sign is not a key</p>", "The monkey= sign is not a key")]
    public void I11_an_api_key_echoed_by_an_error_page_is_cut_at_the_parameter_name(
        string body, string expected)
    {
        var head = RequestLog.BodyHead(body);

        Assert.Equal(expected, head);
        Assert.DoesNotContain("KIZOTIS-DEEPL-KEY", head ?? "", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    //  burst60
    // ---------------------------------------------------------------------------------------

    /// <summary>The counter is what turns the log into evidence for the §2.3 volume model, so it
    /// counts successes too — and it forgets, or a long session would report a lifetime total.</summary>
    [Fact]
    public void The_burst_counter_counts_a_rolling_window_and_forgets_what_falls_out_of_it()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 13, 29, 0, TimeSpan.Zero);
        var counter = new BurstCounter();

        for (int i = 0; i < 37; i++) counter.Note(t0.AddMilliseconds(i * 10));
        Assert.Equal(37, counter.Count(t0.AddSeconds(1)));

        // 59 s later they are all still in the window; 61 s later none of them is.
        Assert.Equal(37, counter.Count(t0.AddSeconds(59)));
        Assert.Equal(1, counter.Note(t0.AddSeconds(61)));
    }

    /// <summary>A minute of hits that have since left the window must not make the next one
    /// invisible: the cap exists to bound memory, not to lose the request that follows a storm.</summary>
    [Fact]
    public void The_burst_counter_counts_the_first_request_after_a_capped_storm()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 13, 29, 0, TimeSpan.Zero);
        var counter = new BurstCounter();

        for (int i = 0; i < 5000; i++) counter.Note(t0);     // past the 4096 cap
        Assert.Equal(1, counter.Note(t0.AddSeconds(120)));   // the storm is long gone
    }

    // ---------------------------------------------------------------------------------------
    //  The storm valve
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A LIVE loop meeting a hard 429 fails every request, and three lines per logical call at LIVE
    /// rates would roll the 1 MB log in minutes — taking the beginning of the incident with it, the
    /// one part nobody can reconstruct afterwards. So an identical failure goes quiet after
    /// <see cref="LogSuppressor.Threshold"/> lines and is represented by one summary per window.
    /// </summary>
    [Fact]
    public void An_identical_failure_goes_quiet_after_the_threshold_and_reports_what_it_held()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 13, 29, 0, TimeSpan.Zero);
        var valve = new LogSuppressor();

        // The first N are evidence and are written in full — a retried call is not a storm.
        for (int i = 0; i < LogSuppressor.Threshold; i++)
        {
            var d = valve.Note("google-gtx", "429", t0.AddSeconds(i * 0.5));
            Assert.True(d.Write);
            Assert.Null(d.Summary);
        }

        // After that the run is silent, and silent means silent: no line, no summary.
        for (int i = 0; i < 200; i++)
        {
            var d = valve.Note("google-gtx", "429", t0.AddSeconds(10 + i * 0.1));
            Assert.False(d.Write);
            Assert.Null(d.Summary);
        }

        // One summary per window, carrying the count and nothing a suppressed line did not carry.
        var minute = valve.Note("google-gtx", "429", t0.AddSeconds(61));
        Assert.False(minute.Write);
        Assert.NotNull(minute.Summary);
        Assert.Matches(@"^tr provider=google-gtx status=429 suppressed=201 in=\d+s ", minute.Summary!);
    }

    /// <summary>A different failure is new information: it is written, and it flushes what the run
    /// it interrupted was still holding — so no suppressed line is ever silently lost.</summary>
    [Fact]
    public void A_different_status_ends_the_run_and_flushes_its_count()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 13, 29, 0, TimeSpan.Zero);
        var valve = new LogSuppressor();

        for (int i = 0; i < LogSuppressor.Threshold + 5; i++)
            valve.Note("google-gtx", "429", t0.AddSeconds(i * 0.1));

        var changed = valve.Note("google-gtx", "503", t0.AddSeconds(3));
        Assert.True(changed.Write);
        Assert.Contains("status=429 suppressed=5", changed.Summary);

        // …and the new run starts from zero, so it gets its own full quota of lines.
        var next = valve.Note("google-gtx", "503", t0.AddSeconds(4));
        Assert.True(next.Write);
        Assert.Null(next.Summary);
    }

    /// <summary>The valve is per provider as well as per status: one provider drowning must not
    /// silence another (E2.S5 puts every provider through this same emitter).</summary>
    [Fact]
    public void A_different_provider_is_never_silenced_by_another_ones_storm()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 13, 29, 0, TimeSpan.Zero);
        var valve = new LogSuppressor();

        for (int i = 0; i < 100; i++) valve.Note("google-gtx", "429", t0.AddSeconds(i * 0.1));

        Assert.True(valve.Note("deepl", "429", t0.AddSeconds(11)).Write);
    }

    /// <summary>The attempts of one logical call are well under the threshold, which is the property
    /// TP-LOG-04 depends on: the valve must not eat an ordinary retried failure. E2.S5 re-checked
    /// the valve's cases against the new storm shape — two attempts instead of three makes it
    /// shallower, never deeper — and changed the fixture's spacing, not the numbers.</summary>
    [Fact]
    public void One_retried_call_is_never_suppressed()
    {
        var t0 = new DateTimeOffset(2026, 9, 6, 13, 29, 0, TimeSpan.Zero);
        var valve = new LogSuppressor();

        // The widest spacing E2.S5's full jitter can put between the two attempts: [0, 500).
        Assert.All(new[] { 0, 499 },
            ms => Assert.True(valve.Note("google-gtx", "503", t0.AddMilliseconds(ms)).Write));
    }

    // ---------------------------------------------------------------------------------------
    //  End to end, through E1.S1's fake handler
    // ---------------------------------------------------------------------------------------

    /// <summary>TP-LOG-04 — the field set is complete for a real 429 taken through the real request
    /// path. Since E2.S5 a 429 is <b>not retried</b> (the gate owns the wait), so the whole episode
    /// is one line, and this case is renamed to say so: it used to promise "one line per attempt
    /// under a single correlation id" and then assert `Single(cids)` over one line, which cannot
    /// fail. The shared-cid property moved to a case that really has two attempts —
    /// <c>HttpProviderCoreTests.TP_RET_08</c>, on a 503, which is what a retry is now for.</summary>
    [Fact]
    public async Task TP_LOG_04_a_429_logs_one_complete_line_and_only_one()
    {
        var lines = await FailingCall("zz", "qa", h => h.Respond(HttpStatusCode.TooManyRequests,
            "{\"error\":\"rate limited\"}", "application/json"));

        Assert.Single(lines);
        Assert.Matches(@"^[0-9a-f]{1,8}$", Field(lines[0], "cid"));
        Assert.Equal(new[] { "1/2" }, lines.Select(l => Field(l, "attempt")).ToArray());

        foreach (var line in lines)
        {
            Assert.Equal("google-gtx", Field(line, "provider"));
            Assert.Equal("translate.googleapis.com/translate_a/single", Field(line, "ep"));
            Assert.Equal("zz->qa", Field(line, "dir"));
            Assert.Equal("429", Field(line, "status"));
            Assert.Matches(@"^\d+ms$", Field(line, "elapsed"));
            Assert.Equal("-", Field(line, "retry-after"));
            Assert.Equal("application/json", Field(line, "ct"));
            Assert.Matches(@"^\d+$", Field(line, "len"));
            Assert.Contains("hdrs=[via:- srv:- xrl:- set-cookie:no]", line);
            Assert.Matches(@"^\d+$", Field(line, "bytes"));
            Assert.Equal("1", Field(line, "lines"));
            Assert.Matches(@"^\d+$", Field(line, "burst60"));
            Assert.Equal("?", Field(line, "ipv"));
        }
    }

    /// <summary>TP-LOG-05, first half: the measured abuse page. This is the P2 shape the whole
    /// story exists for — a 429 whose body is an HTML "automated queries" page.</summary>
    [Fact]
    public async Task TP_LOG_05_the_html_fixture_is_logged_de_tagged_and_capped()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "google-429.html"));
        var lines = await FailingCall("zz", "qc",
            h => h.Respond(HttpStatusCode.TooManyRequests, html, "text/html"));

        var body = Quoted(lines[0], "body");
        Assert.NotNull(body);
        Assert.True(body!.Length <= RequestLog.MaxBodyChars, $"body= is {body.Length} chars");
        Assert.DoesNotContain("<", body);
        Assert.Contains("sorry", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("text/html", Field(lines[0], "ct"));
    }

    /// <summary>TP-LOG-05, second half: a JSON error carries no <c>body=</c> at all. Which is not a
    /// formatting preference — the provider answers JSON, and the provider's JSON is where the
    /// user's text lives.</summary>
    [Fact]
    public async Task TP_LOG_05_a_json_error_body_is_never_logged()
    {
        var lines = await FailingCall("zz", "qd", h => h.Respond(HttpStatusCode.Forbidden,
            "{\"error\":{\"message\":\"" + Sentinel + "\"}}", "application/json"));

        Assert.Single(lines);                       // 403 is not transient: one attempt, one line
        Assert.DoesNotContain("body=", lines[0]);
    }

    /// <summary>TP-LOG-06 — 50 successful requests, zero lines. The log is capped at 1 MB with one
    /// rollover (<c>Logging.cs:69,95-105</c>); a chatty success path would evict the evidence. They
    /// are still counted, which is the whole point of counting them.</summary>
    [Fact]
    public async Task TP_LOG_06_fifty_successful_requests_produce_no_lines_and_are_still_counted()
    {
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();
            int before = RequestLog.Burst.Count(DateTimeOffset.UtcNow);

            var svc = new GoogleGtxTranslator(new FakeHandler().RespondJson(GoogleOk));
            for (int i = 0; i < 50; i++) await svc.TranslateAsync("hello", "zz", "qe");

            Assert.Empty(LinesFor(dir, "zz->qe"));
            Assert.True(RequestLog.Burst.Count(DateTimeOffset.UtcNow) >= before + 50,
                "successes must be counted into burst60 even though they are not logged");
        }
        finally { Restore(previous, dir); }
    }

    /// <summary>Both transport shapes, and the OCE trap with them: a timeout is an
    /// <c>OperationCanceledException</c> whose token is NOT cancelled, and it has to read as a
    /// timeout in the log — mislabelling it as a user cancel is the bug I3 exists for.</summary>
    [Fact]
    public async Task A_network_failure_and_a_timeout_each_log_their_own_status()
    {
        // The two Kinds E2.S5 swapped round: a Network failure is no longer retried (E3.S3's chain
        // is the redundancy the retry used to fake) and a Timeout now is.
        var network = await FailingCall("zz", "qf", h => h.Throws(new HttpRequestException("no route")));
        Assert.Single(network);
        Assert.All(network, l => Assert.Equal("HttpRequestException", Field(l, "status")));
        Assert.All(network, l => Assert.Equal("-", Field(l, "ct")));

        // The first failure paused the provider (§5.3's soft cooldown), and a paused provider sends
        // nothing — which is E2's whole point and would make the second half of this case measure
        // an empty log. Two failures a minute apart in the field; one reset here.
        ResetGates();

        var timeout = await FailingCall("zz", "qg", h => h.TimesOut());
        Assert.Equal(2, timeout.Count);
        Assert.All(timeout, l => Assert.Equal("TaskCanceledException", Field(l, "status")));
    }

    /// <summary>A body that claimed to be JSON, is not markup and still does not parse: the one
    /// shape that reaches the parser after E1.S4. It is an attempt that ended exceptionally, so it
    /// gets its line — and the body that broke the parser is still not in it.</summary>
    [Fact]
    public async Task An_unparseable_success_body_is_logged_without_the_body()
    {
        var lines = await FailingCall("zz", "qh",
            h => h.Respond(HttpStatusCode.OK, "not json at all: " + Sentinel, "application/json"));

        Assert.Single(lines);
        Assert.Equal("200", Field(lines[0], "status"));
        Assert.DoesNotContain("body=", lines[0]);
        Assert.DoesNotContain("KIZOTIS-SENTINEL", lines[0]);
    }

    // ---------------------------------------------------------------------------------------
    //  I11 — the load-bearing negatives, over the file that is actually produced
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// TP-LOG-01 and TP-LOG-02 together, and this is the test the story is judged on. A real
    /// sentence is translated through a failing endpoint; then the produced FILE — and what "Copy
    /// error report" would put on the clipboard — is grepped for the sentence, for the query
    /// parameter that carries it and for the full URL.
    /// </summary>
    [Fact]
    public async Task TP_LOG_01_no_user_text_no_query_and_no_full_url_reach_the_file()
    {
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            // The failure is the measured one: a 429 whose body is Google's abuse page. So the log
            // DOES carry a body= — from the server's page — while the user's sentence does not
            // appear anywhere. Both halves matter: a log that carried nothing would pass too.
            var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "google-429.html"));
            var svc = new GoogleGtxTranslator(
                new FakeHandler().Respond(HttpStatusCode.TooManyRequests, html, "text/html"));

            await Assert.ThrowsAsync<TranslationException>(
                () => svc.TranslateAsync(Sentinel, "ru", "zx"));

            var file = File.ReadAllText(Path.Combine(dir, "log.txt"));
            var report = Logging.ReadRecent();      // what MainWindow.xaml.cs:312-322 copies
            var lines = LinesFor(dir, "ru->zx");

            Assert.NotEmpty(lines);                 // the DoD: the report is not empty any more

            // The sentence is unique to this test, so it is grepped over the WHOLE file and over
            // the report itself: nothing else in the suite can produce a false pass.
            foreach (var text in new[] { file, report })
            {
                Assert.DoesNotContain("KIZOTIS-SENTINEL", text, StringComparison.Ordinal);
                Assert.DoesNotContain("привет", text, StringComparison.Ordinal);
                Assert.DoesNotContain("%d0%bf", text, StringComparison.OrdinalIgnoreCase);   // URL-encoded "п"
            }

            // The URL rules are asserted over the lines THIS story writes. The override is
            // process-wide, so while this test holds it every other class's deliberate failure
            // lands in the same file, and one of those is free to log a URL of its own.
            foreach (var line in lines)
            {
                Assert.DoesNotContain("q=", line, StringComparison.Ordinal);
                Assert.DoesNotContain("https://", line, StringComparison.Ordinal);
                Assert.DoesNotContain("client=gtx", line, StringComparison.Ordinal);
            }
        }
        finally { Restore(previous, dir); }
    }

    /// <summary>
    /// TP-LOG-03 — a recognisable API key is set and really sent, and it appears neither in the log
    /// nor in "Copy error report". Since E2.S5 DeepL emits §10.1 lines of its own — which is what
    /// turned key redaction from a hypothetical into an obligation — so the assertion is made
    /// against the whole produced file, not against one line's shape.
    /// </summary>
    [Fact]
    public async Task TP_LOG_03_a_recognisable_api_key_reaches_neither_the_log_nor_the_error_report()
    {
        const string key = "KIZOTIS-DEEPL-KEY-0000-1111:fx";
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            var deepl = new FakeHandler().Respond(HttpStatusCode.Forbidden, "{}");
            var google = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "{}");
            var chain = ChainTranslator.Of((ProviderIds.DeepL, new DeepLTranslator(key, deepl)),
                                           (ProviderIds.GoogleGtx, new GoogleGtxTranslator(google)));

            await Assert.ThrowsAsync<TranslationException>(
                () => chain.TranslateAsync(Sentinel, "ru", "zy"));

            // The key really travelled — otherwise this test proves nothing.
            Assert.Contains(deepl.Calls, c => c.Headers.TryGetValue("Authorization", out var a)
                                              && a.Contains(key, StringComparison.Ordinal));

            var file = File.ReadAllText(Path.Combine(dir, "log.txt"));
            Assert.NotEmpty(LinesFor(dir, "ru->zy"));
            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
            Assert.DoesNotContain("KIZOTIS-DEEPL-KEY", file, StringComparison.Ordinal);
            Assert.DoesNotContain(key, Logging.ReadRecent(), StringComparison.Ordinal);
            Assert.DoesNotContain("KIZOTIS-SENTINEL", file, StringComparison.Ordinal);
        }
        finally { Restore(previous, dir); }
    }

    /// <summary>The repo's own rule for <see cref="Logging"/>, extended to the builder: a
    /// diagnostic that can break the feature it diagnoses is not a diagnostic
    /// (<c>Logging.cs:89</c>). A response whose content throws on every access is the shape that
    /// would otherwise take a request down.</summary>
    [Fact]
    public void Emitting_a_line_never_throws_however_broken_the_response_is()
    {
        var call = Call("ru", "en", "hi");

        RequestLog.Emit(call, 1, TimeSpan.Zero, 1, new HostileResponse(), "body");
        RequestLog.Emit(call, 1, TimeSpan.Zero, 1, new Exception("plain"));
        RequestLog.EmitStatus(call, 1, TimeSpan.Zero, 1, 200, null);

        // And the builder itself, handed nothing at all.
        var line = RequestLog.Line(default, 0, default, TimeSpan.Zero, 0);
        Assert.StartsWith("tr provider=", line);
    }

    // ---------------------------------------------------------------------------------------
    //  helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs one failing translation with the log pointed at a fresh directory and hands
    /// back this call's own lines, found by its unique language pair — xUnit runs other classes in
    /// parallel and their deliberate failures land in whatever directory is current.</summary>
    private static async Task<List<string>> FailingCall(string source, string target,
        Action<FakeHandler> script)
    {
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            var handler = new FakeHandler();
            script(handler);
            var svc = new GoogleGtxTranslator(handler);

            try { await svc.TranslateAsync("hello", source, target); }
            catch (TranslationException) { /* the failure is the point */ }

            return LinesFor(dir, $"{source}->{target}");
        }
        finally { Restore(previous, dir); }
    }

    private static List<string> LinesFor(string dir, string dir60)
    {
        var path = Path.Combine(dir, "log.txt");
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path)
                   .Select(l => l.Contains("] tr ") ? l[(l.IndexOf("] tr ", StringComparison.Ordinal) + 2)..] : l)
                   .Where(l => l.StartsWith("tr ", StringComparison.Ordinal) && l.Contains("dir=" + dir60))
                   .ToList();
    }

    /// <summary>One unquoted <c>key=value</c> field of a line.</summary>
    private static string Field(string line, string key)
    {
        var m = Regex.Match(line, $@"(?:^|\s){Regex.Escape(key)}=([^\s]*)");
        Assert.True(m.Success, $"field '{key}' is missing from: {line}");
        return m.Groups[1].Value;
    }

    /// <summary>One quoted field (<c>body="…"</c>), null when the field is absent.</summary>
    private static string? Quoted(string line, string key)
    {
        var m = Regex.Match(line, $@"(?:^|\s){Regex.Escape(key)}=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static RequestLog.Call Call(string source, string target, string text) =>
        RequestLog.ForRequest("google-gtx", new Uri("https://translate.googleapis.com/translate_a/single?q=x"),
            source, target, text, TranslationPolicy.MaxAttempts);

    private static HttpResponseMessage Resp(int status, string body, string contentType = "application/json")
        => new((HttpStatusCode)status)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) };

    private static string BodyOf(HttpResponseMessage resp) => resp.Content.ReadAsStringAsync().Result;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PWRUHelperReqLog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Restore(string? previous, string dir)
    {
        Logging.DirectoryOverride = previous;   // never leave the suite pointed at a deleted dir
        Delete(dir);
    }

    // Another class may still have a write in flight into this directory; failing to bin a temp
    // folder is not a test result (LoggingTests.cs:116-118 makes the same call).
    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* the OS will get it */ }
    }

    /// <summary>A response that throws on every interesting member — the "logging must not be what
    /// breaks the feature" guard needs something the builder cannot possibly render.</summary>
    private sealed class HostileResponse : HttpResponseMessage
    {
        public HostileResponse() : base(HttpStatusCode.BadGateway)
        {
            Content = new ThrowingContent();
        }

        private sealed class ThrowingContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
                => throw new IOException("nope");
            protected override bool TryComputeLength(out long length) => throw new IOException("nope");
        }
    }
}

/// <summary>
/// The classes that take <c>Logging.DirectoryOverride</c> away from <see cref="TestLogRedirect"/>
/// run one at a time. The override is a process-wide static: two classes swapping it concurrently
/// would each read the other's lines, which is a flaky suite rather than a broken feature — and a
/// flaky suite is how a real I11 regression gets re-run until it passes.
/// </summary>
/// <para><b>Not parallel with anything either, since E2.S5.</b> The classes in here now derive
/// <c>GatesTestBase</c>, so their constructors and <c>Dispose</c> reset the process-wide
/// <c>ProviderGates</c> registry and re-point its clock — the very state <c>[Collection("Gates")]</c>
/// exists to serialise, reached from a second collection. It was safe only because "Gates" happens
/// to set <c>DisableParallelization</c> and nothing else touched the registry; "safe because of a
/// property nobody asserts" is exactly risk R-08. Saying it here makes it a fact instead.</para>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LogFileCollection
{
    internal const string Name = "log-file";
}

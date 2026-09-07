using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Web;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// TP-PRV-01…05 — the new default free provider, driven entirely off <see cref="FakeHandler"/> and
/// the recorded bodies under <c>Fixtures/</c> (IS-9/IS-10/IS-11): <b>no request leaves the box in
/// this file, ever</b>. The two response shapes are written verbatim from <c>benchmark…</c> §3.2's
/// own probe, so a shape that changes under us fails here rather than in a user's chat feed.
///
/// <para><c>[Collection("Gates")]</c> because the provider consults the process-global registry
/// (IS-5): a 429 in one case opens <c>google-dict</c>'s gate for a minute, and a parallel case that
/// found it open would fail for a reason that has nothing to do with what it asserts.</para>
/// </summary>
[Collection("Gates")]
public class GoogleDictTranslatorTests : GatesTestBase
{
    // The two shapes, as constants for the cases that do not need the file — every case that IS the
    // shape assertion reads the fixture instead, because the fixture is the evidence.
    private const string ShapeA = """["Hello"]""";

    // =============================================================================================
    //  TP-PRV-01 / TP-PRV-02 — the two recorded shapes
    // =============================================================================================

    /// <summary>TP-PRV-01 — a fixed <c>sl</c> answers a flat array of one string.</summary>
    [Fact]
    public async Task TP_PRV_01_shape_A_is_the_first_element()
    {
        var fake = new FakeHandler().RespondJson(Fixture("google-dict-single.json"));

        Assert.Equal("Hello", await new GoogleDictTranslator(fake).TranslateAsync("привет", "ru", "en"));
    }

    /// <summary>TP-PRV-02 — <c>sl=auto</c> answers a NESTED array whose second element is the
    /// detected source. The translation is <c>root[0][0]</c> and the detected language is
    /// <b>discarded</b>: which source a message was sent with is the caller's decision, per message
    /// (I7, <c>MainWindow.Live.cs:304-310</c>), and a provider that reported back a different one
    /// would be answering a question nobody asked.</summary>
    [Fact]
    public async Task TP_PRV_02_shape_B_is_the_first_element_of_the_first_element()
    {
        var fake = new FakeHandler().RespondJson(Fixture("google-dict-auto.json"));

        var translated = await new GoogleDictTranslator(fake).TranslateAsync("привет", "auto", "en");

        Assert.Equal("Hello", translated);
        Assert.DoesNotContain("ru", translated);   // the detected language never reaches the caller
    }

    /// <summary>Both fixtures are the bodies <c>benchmark…</c> §3.2 recorded, byte for byte. A
    /// re-captured fixture that no longer matches the document is a shape change, and this is where
    /// it shows.</summary>
    [Fact]
    public void The_fixtures_are_the_recorded_bodies()
    {
        Assert.Equal("""["Hello"]""", Fixture("google-dict-single.json").Trim());
        Assert.Equal("""[["Hello","ru"]]""", Fixture("google-dict-auto.json").Trim());
    }

    // =============================================================================================
    //  TP-PRV-03 — anything else is a BadResponse, strictly
    // =============================================================================================

    /// <summary>TP-PRV-03. Strict on purpose: this provider will carry the LIVE loop's whole volume,
    /// so a parser that "recovered" from an unexpected shape would turn a silent endpoint change
    /// into plausible-looking wrong text. A <c>BadResponse</c> is also what §5.3 counts three of in
    /// a row before it opens the gate — a recovery would spend that budget too.</summary>
    [Theory]
    [InlineData("""{"x":1}""")]      // the test-plan's own row
    [InlineData("[]")]               // an array, but nothing in it
    [InlineData("[[]]")]             // shape B with an empty inner array
    [InlineData("[1]")]              // a number where the translation should be
    [InlineData("[[1,\"ru\"]]")]     // shape B with a number where the translation should be
    [InlineData("[null]")]
    [InlineData("[[null]]")]         // shape B whose translation slot is null
    [InlineData("""[[["Hello"]]]""")] // nested one level deeper than shape B
    [InlineData("\"Hello\"")]        // a bare string: not an array at all
    [InlineData("null")]             // the JSON null literal: a value, but not an array
    [InlineData("")]                 // an empty body served as JSON
    [InlineData("""["Hello"] and then some""")]  // trailing garbage after a valid value
    // The multi-`q=` body benchmark… §3.2 measured and declined to propose. This provider sends
    // exactly one q=, so a root of two elements is a body it cannot have asked for — and accepting
    // it would return translation 1 of 2 while telling the gate the call SUCCEEDED. Silent
    // truncation is I5's "never pad" seen from the other side, and this row is what forbids it.
    [InlineData("""["Hello","How are you"]""")]
    [InlineData("""[["Hello","ru"],["How are you","ru"]]""")]
    [InlineData("not json at all")]
    public async Task TP_PRV_03_any_other_shape_is_a_BadResponse(string body)
    {
        var fake = new FakeHandler().RespondJson(body);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        Assert.Equal(1, fake.Requests);   // a bad shape is not retried
    }

    /// <summary>
    /// The other half of the rejection table, and the reason it is a separate case: this shape is
    /// <b>accepted</b>, deliberately, and the acceptance is a decision rather than an oversight — so
    /// it is pinned here, where a future edit has to read it.
    ///
    /// <para><c>[["Hello"]]</c> is shape B with no detected-language element. §7.1 says only
    /// "<c>root[0]</c> is an array ⇒ take <c>root[0][0]</c>"; it does not require the second element,
    /// and the translation is still unambiguously <c>root[0][0]</c>. Rejecting it would mean a
    /// <c>BadResponse</c> feed — and, at three in a row, an open gate — on the day Google stops
    /// echoing the detected source back on a request whose translation arrived perfectly well.
    /// <b>Note the asymmetry with the multi-element ROOT above, which is rejected:</b> a missing
    /// element loses nothing, an extra one would be dropped, and dropping is the failure this
    /// project has a name for (I5, "never pad" — read from the other side).</para>
    /// </summary>
    [Fact]
    public async Task Shape_B_without_a_detected_language_is_accepted_on_purpose()
    {
        var fake = new FakeHandler().RespondJson("""[["Hello"]]""");

        Assert.Equal("Hello", await new GoogleDictTranslator(fake).TranslateAsync("привет", "ru", "en"));
    }

    /// <summary>The language codes are percent-encoded like <c>q</c> is. Unreachable from the app —
    /// today's callers pass combo-box <c>Tag</c> constants and the literals "ru"/"auto" — but
    /// <c>TranslateAsync</c> is public on a public class, and the one parameter that must never move
    /// is <c>client=</c>: R1's whole mitigation is that the client id lives in exactly one place.
    /// Asserted on the parsed query, so an injected parameter shows up as an extra KEY.</summary>
    [Fact]
    public async Task A_language_code_cannot_inject_a_query_parameter()
    {
        var fake = new FakeHandler().RespondJson(ShapeA);

        await new GoogleDictTranslator(fake).TranslateAsync("привет", "ru&client=evil", "en");

        var q = Query(Assert.Single(fake.Calls).Uri);
        Assert.Equal(new[] { "client", "q", "sl", "tl" }, q.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("dict-chrome-ex", q["client"]);       // still the one and only client id
        Assert.Equal("ru&client=evil", q["sl"]);           // the whole thing travelled as ONE value
    }

    // =============================================================================================
    //  TP-PRV-05 — the recorded 429 page, and AC 4's mapping generally
    // =============================================================================================

    /// <summary>TP-PRV-05 — the body this app really received on 2026-09-06, served with a 429:
    /// <c>RateLimited</c>, and <b>one</b> request. The gate owns the wait (TP-RET-01's rule holds
    /// for every provider on the core), so a second attempt would only be a second entry in
    /// Google's abuse counter.</summary>
    [Fact]
    public async Task TP_PRV_05_the_recorded_429_page_is_RateLimited_and_costs_one_request()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.TooManyRequests,
            Fixture("google-429.html"), "text/html");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.Equal(1, fake.Requests);
    }

    /// <summary>The same page on a <b>200</b> — §4.3's whole point, and the shape that made this
    /// endpoint necessary. The HTML sniff belongs to the core and the mapper; this case proves the
    /// new provider inherited it rather than growing a second classifier of its own (T5: a provider
    /// that classifies its own statuses is the bug §4.2 exists to prevent).</summary>
    [Fact]
    public async Task An_abuse_page_served_with_a_200_is_classified_before_the_parser_sees_it()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.OK, Fixture("google-429.html"), "text/html");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);   // not the BadResponse a parse-first path gives
        Assert.Equal(1, fake.Requests);
    }

    /// <summary>AC 4's other rows, proved rather than written: <c>KeyWasSent: false</c> is what
    /// makes the 403 a <c>Blocked</c> and not an <c>AuthFailed</c> (§4.2 rows 5–8), and the 5xx is
    /// the one failure a second attempt could survive.</summary>
    [Theory]
    [InlineData(403, TranslationErrorKind.Blocked, 1)]
    [InlineData(503, TranslationErrorKind.Unavailable, 2)]
    [InlineData(500, TranslationErrorKind.Unavailable, 2)]
    public async Task The_shared_mapper_supplies_every_row_of_AC_4(int status, TranslationErrorKind kind, int requests)
    {
        var fake = new FakeHandler().Respond((HttpStatusCode)status, "nope");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(requests, fake.Requests);
    }

    // =============================================================================================
    //  AC 1 — the request line itself
    // =============================================================================================

    /// <summary>AC 1, parameter by parameter. §7.1 names four and only four: a <c>dt=</c> or an
    /// <c>ie=</c>/<c>oe=</c> carried over from the gtx URL would be a guess about an endpoint nobody
    /// documents, and the cheapest way to be blocked is to look like something else.</summary>
    [Fact]
    public async Task The_request_is_exactly_section_7_1s_line()
    {
        var fake = new FakeHandler().RespondJson(ShapeA);

        await new GoogleDictTranslator(fake).TranslateAsync("привет мир", "ru", "en");

        var call = Assert.Single(fake.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("clients5.google.com", call.Uri.Host);
        Assert.Equal("/translate_a/t", call.Uri.AbsolutePath);
        Assert.Equal("https", call.Uri.Scheme);

        var q = Query(call.Uri);
        Assert.Equal(new[] { "client", "q", "sl", "tl" }, q.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("dict-chrome-ex", q["client"]);
        Assert.Equal("ru", q["sl"]);
        Assert.Equal("en", q["tl"]);
        Assert.Equal("привет мир", q["q"]);
        Assert.Null(call.Body);
    }

    /// <summary>The client id appears <b>once</b> in the source (R1's own mitigation: an endpoint
    /// that is burned must be one edit to replace, not a search through interpolated strings), and
    /// the id the gate is keyed on is never spelled a second time — a second spelling is a silently
    /// duplicated gate, which is the exact bug <c>ProviderIds</c> exists to prevent.</summary>
    [Fact]
    public void The_client_id_and_the_provider_id_are_each_spelled_once()
    {
        var source = File.ReadAllText(ProviderSource());

        Assert.Equal(1, Occurrences(source, "\"dict-chrome-ex\""));
        Assert.Equal(0, Occurrences(source, "\"google-dict\""));
        Assert.Contains("ProviderIds.GoogleDict", source);
    }

    /// <summary>The frozen Chrome User-Agent, byte-identical to the gtx provider's — asserted
    /// between the two providers rather than against a literal, because the literal is what would
    /// drift. §7.0 says keep it and never rotate it.</summary>
    [Fact]
    public async Task The_user_agent_is_byte_identical_to_the_gtx_providers()
    {
        var dict = new FakeHandler().RespondJson(ShapeA);
        var gtx = new FakeHandler().RespondJson("""[[["hello","привет",null,null,10]],null,"ru"]""");

        await new GoogleDictTranslator(dict).TranslateAsync("привет", "ru", "en");
        await new GoogleGtxTranslator(gtx).TranslateAsync("привет", "ru", "en");

        var sent = Assert.Single(dict.Calls).Headers["User-Agent"];
        Assert.Equal(Assert.Single(gtx.Calls).Headers["User-Agent"], sent);
        Assert.Contains("Chrome/120.0", sent);
    }

    // =============================================================================================
    //  I11 — the log says what happened and never what was said
    // =============================================================================================

    /// <summary>I11. The address travels to the core as a <see cref="Uri"/> and
    /// <c>RequestLog.Endpoint</c> renders host + path with no parameter that could add the rest —
    /// so the assertion is on the whole emitted line: no <c>q=</c>, and none of the user's text.
    /// Adversarial input on purpose: Cyrillic (which a naive ASCII fold would mangle rather than
    /// drop) plus an ASCII sentinel that cannot occur by accident.</summary>
    [Fact]
    public async Task TP_PRV_the_log_line_carries_no_q_and_no_user_text()
    {
        const string sentinel = "ZZQSENTINELZZ";
        var previous = Logging.DirectoryOverride;
        var dir = Directory.CreateTempSubdirectory("pwru-dictlog-").FullName;
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            var fake = new FakeHandler().Respond(HttpStatusCode.BadRequest, "nope");
            await Assert.ThrowsAsync<TranslationException>(
                () => new GoogleDictTranslator(fake).TranslateAsync($"привет {sentinel} мир", "ru", "en"));

            var lines = File.ReadAllLines(Path.Combine(dir, "log.txt"))
                            .Where(l => l.Contains(" tr provider=google-dict")).ToList();

            var line = Assert.Single(lines);
            Assert.Contains("ep=clients5.google.com/translate_a/t", line);
            Assert.DoesNotContain("q=", line);
            Assert.DoesNotContain(sentinel, line);
            Assert.DoesNotContain("привет", line);
            // …and the payload is MEASURED rather than kept: the byte count is there, the bytes are not.
            Assert.Contains("bytes=", line);
        }
        finally
        {
            Logging.DirectoryOverride = previous;
            RequestLog.ResetSuppression();
            try { Directory.Delete(dir, recursive: true); } catch { /* the case already made its point */ }
        }
    }

    // =============================================================================================
    //  TP-PRV-04 — per line until U1 says otherwise (OQ-A)
    // =============================================================================================

    /// <summary>
    /// TP-PRV-04, as the <b>negative</b> pin it has to be until E3.S1's capture exists: the join is
    /// off, a 3-line group costs 3 requests, each request carries exactly one line, and the answers
    /// come back in order. When <c>google-dict-batch.txt</c> lands, THIS is the case that changes —
    /// <c>TranslationPolicy.GoogleDictBatchJoinEnabled</c> goes true and the assertions below become
    /// "one request, split on \n".
    /// </summary>
    [Fact]
    public async Task TP_PRV_04_until_U1_a_three_line_group_costs_three_requests_one_line_each()
    {
        Assert.False(TranslationPolicy.GoogleDictBatchJoinEnabled,
            "OQ-A ships per-line; flipping this is E3.S1's, with a captured fixture behind it");

        var fake = new FakeHandler()
            .RespondJson("""["one"]""")
            .RespondJson("""["two"]""")
            .RespondJson("""["three"]""");

        var result = await new GoogleDictTranslator(fake)
            .TranslateLinesAsync(new[] { "раз", "два", "три" }, "ru", "en");

        Assert.Equal(new[] { "one", "two", "three" }, result);
        Assert.Equal(3, fake.Requests);
        Assert.Equal(new[] { "раз", "два", "три" }, fake.Calls.Select(c => Query(c.Uri)["q"]));
        // Nothing was joined: not one request carried a newline.
        Assert.All(fake.Calls, c => Assert.DoesNotContain("\n", Query(c.Uri)["q"]));
    }

    /// <summary>I16 — the latch, and only for a refusal. A 429 on line 2 stops the loop: line 3
    /// costs no request AND says it was skipped, which is what separates a latch from the gate
    /// simply refusing the next admission (both cost zero requests; only one of them is this
    /// method's doing). The successes above the refusal are KEPT — losing 29 good translations
    /// because line 30 was throttled is the bug this loop's shape exists to prevent.</summary>
    [Fact]
    public async Task A_refusal_at_line_two_latches_and_line_three_costs_no_request()
    {
        var fake = new FakeHandler()
            .RespondJson("""["one"]""")
            .Respond(HttpStatusCode.TooManyRequests, Fixture("google-429.html"), "text/html");

        var result = await new GoogleDictTranslator(fake)
            .TranslateLinesAsync(new[] { "раз", "два", "три" }, "ru", "en");

        Assert.Equal(2, fake.Requests);
        Assert.Equal("one", result[0]);
        // E7.S1 / amendment A5: one row text for all three per-line branches — the symbols are
        // what say which branch ran, and the row says the one thing it owes the player.
        Assert.Equal(PerLineFallback.RateLimitedMessage, result[1]);
        Assert.Equal(PerLineFallback.SkippedMessage, result[2]);
    }

    /// <summary>The other half of E3.S6's narrowing, inherited here: a <c>BadResponse</c> is NOT a
    /// refusal, so it fails its own line and says so. It used to turn the rest of the group into
    /// "(skipped — rate-limited…)", a sentence that was simply false.</summary>
    [Fact]
    public async Task A_bad_shape_on_one_line_does_not_latch_the_others()
    {
        var fake = new FakeHandler()
            .RespondJson("""{"x":1}""")
            .RespondJson("""["two"]""")
            .RespondJson("""["three"]""");

        var result = await new GoogleDictTranslator(fake)
            .TranslateLinesAsync(new[] { "раз", "два", "три" }, "ru", "en");

        Assert.Equal(PerLineFallback.Failed(""), result[0]);
        Assert.DoesNotContain("rate-limited", result[0]);
        Assert.Equal("two", result[1]);
        Assert.Equal("three", result[2]);
        Assert.Equal(3, fake.Requests);
    }

    /// <summary>One line is one request and no join decision at all — the single-line path the
    /// Translator tab takes on every keystroke-ish translation.</summary>
    [Fact]
    public async Task A_single_line_group_is_one_request()
    {
        var fake = new FakeHandler().RespondJson(ShapeA);

        var result = await new GoogleDictTranslator(fake).TranslateLinesAsync(new[] { "привет" }, "ru", "en");

        Assert.Equal(new[] { "Hello" }, result);
        Assert.Equal(1, fake.Requests);
    }

    // =============================================================================================
    //  The query budget — chunking, in UTF-8 bytes
    // =============================================================================================

    /// <summary>Text over <c>MaxQueryBytes</c> is chunked by <see cref="TextChunker"/> and stitched
    /// back in order. Cyrillic on purpose: 751 characters are 1502 <b>bytes</b>, so a splitter that
    /// counted characters would send one request with a query twice the size it believed.</summary>
    [Fact]
    public async Task Text_over_the_query_budget_is_chunked_on_the_byte_count()
    {
        var text = new string('я', 751);                    // 1502 bytes, one request too long
        Assert.Equal(1502, Encoding.UTF8.GetByteCount(text));

        var fake = new FakeHandler().RespondJson("""["one"]""").RespondJson("""["two"]""");

        var translated = await new GoogleDictTranslator(fake).TranslateAsync(text, "ru", "en");

        Assert.Equal("onetwo", translated);                 // stitched, in order
        Assert.Equal(2, fake.Requests);
        Assert.All(fake.Calls, c => Assert.True(
            Encoding.UTF8.GetByteCount(Query(c.Uri)["q"]) <= TranslationPolicy.MaxQueryBytes,
            "a chunk went out over the query budget"));
        Assert.Equal(text, string.Concat(fake.Calls.Select(c => Query(c.Uri)["q"])));
    }

    /// <summary>Exactly at the budget is still one request — the boundary the byte count decides.</summary>
    [Fact]
    public async Task Text_exactly_at_the_query_budget_is_one_request()
    {
        var text = new string('я', 750);                    // 1500 bytes
        var fake = new FakeHandler().RespondJson(ShapeA);

        await new GoogleDictTranslator(fake).TranslateAsync(text, "ru", "en");

        Assert.Equal(1, fake.Requests);
    }

    /// <summary>Empty in, empty out, and <b>no request</b>: an empty <c>q=</c> is a request that
    /// asks a rented endpoint to translate nothing.</summary>
    [Fact]
    public async Task Blank_text_costs_no_request()
    {
        var fake = new FakeHandler().RespondJson(ShapeA);

        Assert.Equal("", await new GoogleDictTranslator(fake).TranslateAsync("   ", "ru", "en"));
        Assert.Equal(0, fake.Requests);
    }

    // =============================================================================================
    //  The seams: the handler, the gate, and the priority E3.S7 will hand in (T6)
    // =============================================================================================

    /// <summary>T6 — the priority is per INSTANCE because <c>ITranslator</c> has no channel for it
    /// (I1), and it defaults to <c>Interactive</c> so nothing changes until E3.S7 asks. Asserted on
    /// the ctor's own default: a change to it would silently re-prioritise every existing caller.</summary>
    [Fact]
    public void The_priority_is_a_ctor_parameter_that_defaults_to_Interactive()
    {
        var ctor = typeof(GoogleDictTranslator)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpMessageHandler)));

        var priority = Assert.Single(ctor.GetParameters(), p => p.ParameterType == typeof(RequestPriority));
        Assert.True(priority.HasDefaultValue);
        Assert.Equal((int)RequestPriority.Interactive, Convert.ToInt32(priority.DefaultValue));
        Assert.True(ctor.IsAssembly, "the handler ctor is a test seam, not public API");
    }

    /// <summary>I9 — two instances of this provider (E3.S7 builds one per chain) share <b>one</b>
    /// gate, because the condition a gate mirrors is a counter on Google's side, per endpoint and
    /// not per chain. Proved end to end: a 429 taken by the first instance means the second one is
    /// refused <i>without sending anything at all</i>, which is the sentence Epic 2 is judged on.</summary>
    [Fact]
    public async Task Two_instances_share_one_gate_so_a_refusal_stops_the_other_one_too()
    {
        var first = new FakeHandler().Respond(HttpStatusCode.TooManyRequests,
            Fixture("google-429.html"), "text/html");
        await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(first).TranslateAsync("привет", "ru", "en"));

        var second = new FakeHandler().RespondJson(ShapeA);
        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(second, gate: null, priority: RequestPriority.Background)
                        .TranslateAsync("привет", "ru", "en"));

        Assert.True(ex.NotSent, "the second instance's request must never have been made");
        Assert.Equal(0, second.Requests);
    }

    /// <summary>IS-8 — no handler means the shared static client, a handler means a private one.
    /// The field NAMES are the seam <c>HttpSeamGuardTests</c> finds by reflection; this is the
    /// behavioural half.</summary>
    [Fact]
    public void The_handler_seam_builds_a_private_client_and_null_keeps_the_shared_one()
    {
        var shared = typeof(GoogleDictTranslator)
            .GetField("Http", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        Assert.NotNull(shared);

        Assert.Same(shared, ClientOf(new GoogleDictTranslator()));
        Assert.NotSame(shared, ClientOf(new GoogleDictTranslator(new FakeHandler())));
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    private static object? ClientOf(object provider) =>
        provider.GetType().GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider);

    /// <summary>A recorded body (IS-9), read from the copy the csproj item group puts next to the
    /// test assembly. Asserts rather than throwing, so a missing item group reads as "the fixtures
    /// did not ship" and not as a mystery IOException.</summary>
    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path),
            $"fixture {name} not found at {path} — is the Fixtures item group still in PWRUHelper.Tests.csproj?");
        return File.ReadAllText(path);
    }

    /// <summary>The query as a dictionary, decoded. Reading it here is exactly what the LOG may
    /// never do (I11) — which is why the assertions above can be this precise about a string no
    /// diagnostic will ever show.</summary>
    private static Dictionary<string, string> Query(Uri uri)
    {
        var parsed = HttpUtility.ParseQueryString(uri.Query);
        return parsed.AllKeys.Where(k => k != null)
                     .ToDictionary(k => k!, k => parsed[k] ?? "", StringComparer.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ProviderSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");

        var path = Path.Combine(dir!.FullName, "Services", "GoogleDictTranslator.cs");
        Assert.True(File.Exists(path), $"GoogleDictTranslator.cs not found at {path}");
        return path;
    }
}

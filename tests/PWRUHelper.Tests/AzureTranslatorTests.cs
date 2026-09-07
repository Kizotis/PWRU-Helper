using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// TP-PRV-07…10 — the second keyed provider (§7.5), driven entirely off <see cref="FakeHandler"/>
/// and the bodies under <c>Fixtures/</c> (IS-9/IS-10/IS-11): <b>no request leaves the box in this
/// file, ever</b>. The shapes are §7.5's documented ones rather than a live capture, which is why
/// this story's risk is graded low while U2 still blocks Edge — Azure is a contracted API.
///
/// <para><c>[Collection("Gates")]</c> because every failing case consults the process-global
/// registry (IS-5): a 429 here opens <c>azure</c>'s gate for a minute, and a parallel case that
/// found it open would fail for a reason that has nothing to do with what it asserts.</para>
/// </summary>
[Collection("Gates")]
public class AzureTranslatorTests : GatesTestBase
{
    private const string Key = "azure-key-0000";
    private const string Region = "westeurope";

    private static AzureTranslator Azure(FakeHandler fake) => new(Key, Region, fake);

    // =============================================================================================
    //  TP-PRV-07 — the array contract: one element per input, in order
    // =============================================================================================

    /// <summary>TP-PRV-07. The oracle is the VALUES and their order: Azure is the only tier with a
    /// true 1:1 array contract, and everything this provider is worth rests on it.</summary>
    [Fact]
    public async Task TP_PRV_07_one_element_per_input_in_input_order()
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-batch.json"));

        var outp = await Azure(fake).TranslateLinesAsync(new[] { "привет", "как дела" }, "ru", "en");

        Assert.Equal(new[] { "Hello", "How are you?" }, outp);
        Assert.Equal(1, fake.Requests);   // native batching: two lines cost ONE request
    }

    /// <summary>A single line takes the same path and reads element 0.</summary>
    [Fact]
    public async Task A_single_line_reads_the_first_element()
    {
        var fake = new FakeHandler().RespondJson("""[{"translations":[{"text":"Hello","to":"en"}]}]""");

        Assert.Equal("Hello", await Azure(fake).TranslateAsync("привет", "ru", "en"));
    }

    /// <summary>The fixtures are §7.5's documented shapes. A fixture that no longer matches the
    /// document is a contract change, and this is where it shows.</summary>
    [Fact]
    public void The_fixtures_are_the_documented_shapes()
    {
        Assert.Equal("""[{"translations":[{"text":"Hello","to":"en"}]},{"translations":[{"text":"How are you?","to":"en"}]}]""",
            Fixture("azure-batch.json").Trim());
        Assert.Contains("\"detectedLanguage\"", Fixture("azure-auto.json"));
        Assert.Contains("\"error\"", Fixture("azure-error-auth.json"));
        Assert.Contains("quota", Fixture("azure-error-quota.json"));

        // CI-6's 64 KB budget is over the WHOLE folder and is asserted once, in
        // ProviderErrorMapperTests (`The_fixtures_ship…`). This is the narrower guard that keeps
        // the Azure share of it a rounding error, so a fixture pasted from a live capture fails
        // here — next to the shapes it belongs with — rather than only when the folder budget goes.
        var bytes = Directory.EnumerateFiles(FixtureDir(), "azure-*.json").Sum(f => new FileInfo(f).Length);
        Assert.InRange(bytes, 1, 4096);
    }

    // =============================================================================================
    //  AC 1 — the request line, on the recorded request (IS-11)
    // =============================================================================================

    /// <summary>
    /// AC 1, in one case: the method, the host and path, the three query parameters, the two
    /// <c>Ocp-Apim-*</c> headers, the content type §7.5 asks for verbatim, and the body — with a
    /// capital <c>Text</c>, in input order. The key is in a HEADER and in nothing else.
    /// </summary>
    [Fact]
    public async Task The_request_is_the_one_section_7_5_documents()
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-batch.json"));

        await Azure(fake).TranslateLinesAsync(new[] { "привет", "как дела" }, "ru", "en");

        var call = Assert.Single(fake.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("api.cognitive.microsofttranslator.com", call.Uri.Host);
        Assert.Equal("/translate", call.Uri.AbsolutePath);
        Assert.Equal("?api-version=3.0&from=ru&to=en", call.Uri.Query);

        Assert.Equal(Key, call.Headers["Ocp-Apim-Subscription-Key"]);
        Assert.Equal(Region, call.Headers["Ocp-Apim-Subscription-Region"]);
        Assert.Equal("application/json; charset=utf-8", call.Headers["Content-Type"]);

        // `Text` with a capital T: Azure's request schema is case-sensitive on the property name.
        Assert.Equal("""[{"Text":"привет"},{"Text":"как дела"}]""",
            call.Body);

        // …and the same body read back as JSON, so the assertion above is about the SHAPE and not
        // about System.Text.Json's default escaping of Cyrillic.
        using var doc = JsonDocument.Parse(call.Body!);
        Assert.Equal(new[] { "привет", "как дела" },
            doc.RootElement.EnumerateArray().Select(e => e.GetProperty("Text").GetString()).ToArray());

        // A vendor path that has never sent a User-Agent must not start (the Chrome string is
        // Google's, and a shared client factory that added one here would be a behaviour change
        // smuggled in by a refactor).
        Assert.False(call.Headers.ContainsKey("User-Agent"));
        // I11's first line of defence: the key is not in the URL, so it cannot reach a proxy log,
        // a referrer or the §10.1 line's host+path rendering.
        Assert.DoesNotContain(Key, call.Uri.ToString(), StringComparison.Ordinal);
    }

    /// <summary>TP-PRV-10 — auto-detect: <c>from</c> is omitted, the response's
    /// <c>detectedLanguage</c> is read PAST (I7), and the answer is still 1:1.</summary>
    [Fact]
    public async Task TP_PRV_10_auto_detect_omits_from_and_discards_the_detection()
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-auto.json"));

        var outp = await Azure(fake).TranslateLinesAsync(new[] { "привет", "как дела" }, "auto", "en");

        Assert.Equal(new[] { "Hello", "How are you?" }, outp);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("?api-version=3.0&to=en", call.Uri.Query);
        Assert.DoesNotContain("from=", call.Uri.Query, StringComparison.Ordinal);
        // "Read past" only means something if there was something to read past: the fixture really
        // carries a detection, and what comes back is exactly the `text` values — no language, no
        // score, nothing the caller did not ask for (I7).
        Assert.Contains("""{"language":"ru","score":1.0}""", Fixture("azure-auto.json"));
    }

    /// <summary>The two code mappers, at the unit level: plain ISO codes, never DeepL's
    /// <c>EN-US</c>, and an empty/auto source means "omit <c>from</c>".</summary>
    [Theory]
    [InlineData("ru", "ru")]
    [InlineData(" RU ", "ru")]
    [InlineData("auto", null)]
    [InlineData("", null)]
    public void The_source_is_omitted_only_for_auto(string source, string? expected)
        => Assert.Equal(expected, AzureTranslator.ToAzureSource(source));

    [Theory]
    [InlineData("en", "en")]
    [InlineData("FR", "fr")]
    [InlineData("", "en")]
    [InlineData("auto", "en")]
    public void The_target_is_a_plain_iso_code(string target, string expected)
        => Assert.Equal(expected, AzureTranslator.ToAzureTarget(target));

    // =============================================================================================
    //  TP-PRV-08 — the count mismatch, and the throw that must never become a pad (I5)
    // =============================================================================================

    /// <summary>
    /// TP-PRV-08. Two inputs, one translation back: a <c>BadResponse</c>, and <b>nothing is
    /// padded</b> — no partial list, no source line echoed as if it had been translated. Padding
    /// once bypassed the fallback AND cached raw Russian source as a translation (R-12), which is
    /// the most expensive bug in this repo's history.
    /// </summary>
    [Fact]
    public async Task TP_PRV_08_a_short_answer_is_a_BadResponse_and_is_never_padded()
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-mismatch.json"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateLinesAsync(new[] { "привет", "как дела" }, "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        Assert.Equal(1, fake.Requests);                 // a bad shape is not retried
        Assert.DoesNotContain("привет", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same rule from the other side: MORE elements than inputs is a mismatch too. An
    /// accepted extra element would be silent truncation, which is I5 read backwards.</summary>
    [Fact]
    public async Task A_long_answer_is_a_BadResponse_as_well()
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-batch.json"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateLinesAsync(new[] { "привет" }, "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
    }

    /// <summary>Anything that is not <c>[{"translations":[{"text":…}]}, …]</c> is a
    /// <c>BadResponse</c>, strictly: a parser that "recovered" from an unexpected shape would turn a
    /// silent contract change into plausible-looking wrong text, and would spend the three
    /// <c>BadResponse</c> strikes §5.3 counts before it opens the gate.</summary>
    [Theory]
    [InlineData("""{"error":{"code":400000}}""")]        // an error envelope served with a 200
    [InlineData("[]")]                                    // an array, but nothing in it
    [InlineData("""[{"translations":[]}]""")]             // an element with no translation
    [InlineData("""[{"translations":[{"to":"en"}]}]""")]  // a translation with no text
    [InlineData("""[{"translations":[{"text":1}]}]""")]   // a number where the text should be
    [InlineData("""[{"translations":{"text":"Hello"}}]""")] // translations as an object
    [InlineData("""[{"text":"Hello"}]""")]                // the inner shape, flattened
    [InlineData("""["Hello"]""")]                         // a bare string array
    [InlineData("null")]
    [InlineData("")]
    [InlineData("""[{"translations":[{"text":"Hello"}]}] and then some""")]  // trailing garbage
    [InlineData("not json at all")]
    public async Task Any_other_shape_is_a_BadResponse(string body)
    {
        var fake = new FakeHandler().RespondJson(body);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        Assert.Equal(1, fake.Requests);
    }

    // =============================================================================================
    //  AC 3 — the error mapping of §7.5, through the REAL ProviderErrorMapper
    // =============================================================================================

    /// <summary>
    /// The four rows the epic names, plus the 403 split that <c>KeyWasSent: true</c> is responsible
    /// for. A <c>Theory</c> rather than one case per assert on purpose: each row opens the
    /// <c>azure</c> gate, and a fresh test instance is a fresh registry (IS-5).
    ///
    /// <para>The 403 pair is the one to read twice: with a quota envelope it is
    /// <c>QuotaExhausted</c>, without one it is <c>AuthFailed</c> — and <b>never</b> <c>Blocked</c>,
    /// which is what a <c>KeyWasSent: false</c> would have made both of them (ruling E2-g). A
    /// rejected Azure key reading as "your connection is refusing requests" would send the user to
    /// their router.</para>
    /// </summary>
    [Theory]
    [InlineData(401, "azure-error-auth.json", TranslationErrorKind.AuthFailed, 1)]
    [InlineData(403, "azure-error-quota.json", TranslationErrorKind.QuotaExhausted, 1)]
    [InlineData(403, "azure-error-auth.json", TranslationErrorKind.AuthFailed, 1)]
    [InlineData(429, "azure-error-rate.json", TranslationErrorKind.RateLimited, 1)]
    [InlineData(500, "azure-error-rate.json", TranslationErrorKind.Unavailable, 2)]
    [InlineData(503, "azure-error-rate.json", TranslationErrorKind.Unavailable, 2)]
    public async Task The_status_mapping_is_section_7_5s(int status, string fixture,
        TranslationErrorKind expected, int requests)
    {
        var fake = new FakeHandler().Respond((HttpStatusCode)status, Fixture(fixture));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal(ProviderIds.Azure, ex.ProviderId);
        Assert.Equal(requests, fake.Requests);   // only a 5xx is worth a second attempt (§5.6)
    }

    /// <summary>
    /// The status §7.5's table does <b>not</b> name, pinned because this provider can now produce
    /// one: the batch split sends a single text longer than
    /// <see cref="TranslationPolicy.AzureMaxCharsPerRequest"/> <b>alone</b> rather than cutting it,
    /// and Azure answers a request over its limits with a 400.
    ///
    /// <para>§4.2 has no row for a 400, so row 13 answers — <c>Unknown</c>, "never a guess" — and
    /// that is the right answer rather than a gap: it is deliberately <b>not</b> a
    /// <c>BadResponse</c>, which means "a 200 whose body is not our shape" and spends one of the
    /// three strikes §5.3 counts before it opens the gate. It is not retried (§5.6 retries only
    /// <c>Unavailable</c> and <c>Timeout</c>), and because <c>UserMessages.Sentence(Unknown)</c> is
    /// §4.4's deliberate pass-through, what the player reads is this provider's OWN account of the
    /// status — never the vendor's <c>message</c>, which is the half of §7.5 that is a rule.</para>
    /// </summary>
    [Fact]
    public async Task A_400_is_the_row_the_table_does_not_name()
    {
        const string vendorMessage = "The target language is not valid.";
        var fake = new FakeHandler().Respond(HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":400036,\"message\":\"" + vendorMessage + "\"}}");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Unknown, ex.Kind);
        Assert.Equal(ProviderIds.Azure, ex.ProviderId);
        Assert.Equal(1, fake.Requests);                      // a 400 is not worth a second attempt
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(vendorMessage, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(vendorMessage, MainWindow.Friendly(ex), StringComparison.Ordinal);
    }

    /// <summary>§5.5 / ruling E2-f — the server's own <c>Retry-After</c> is honoured by the core,
    /// so this provider needs no handling of its own; the pin is here so a second one is never
    /// added.</summary>
    [Fact]
    public async Task A_Retry_After_on_a_429_reaches_the_caller()
    {
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.TooManyRequests, Fixture("azure-error-rate.json"))
            .WithHeader("Retry-After", "30");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.NotNull(ex.RetryAt);
        Assert.InRange(ex.RetryAt!.Value - ProviderGates.Clock(),
            TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(35));
    }

    /// <summary>The gate this provider consults is <c>azure</c>'s and nobody else's — the id is
    /// spelled once, in <see cref="ProviderIds"/>, and a second spelling would be a silently
    /// duplicated gate rather than a typo that fails loudly.</summary>
    [Fact]
    public async Task A_failure_reports_to_the_azure_gate_and_no_other()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.Unauthorized, Fixture("azure-error-auth.json"));

        await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateAsync("привет", "ru", "en"));

        var snapshot = ProviderGates.Snapshot(ProviderIds.Azure);
        Assert.NotNull(snapshot);
        Assert.Equal(TranslationErrorKind.AuthFailed, snapshot!.LastKind);
        Assert.Null(ProviderGates.Snapshot(ProviderIds.DeepL));
        Assert.Null(ProviderGates.Snapshot(ProviderIds.GoogleGtx));
    }

    /// <summary>
    /// I3, from the outside, on a provider whose class doc makes a point of not owning an OCE
    /// catch. An <see cref="HttpClient"/> timeout arrives as a <c>TaskCanceledException</c> whose
    /// token is <b>not</b> cancelled; read as a cancel it disables the fallback and leaves a zombie
    /// LIVE indicator, which cost this project three releases. The filter lives in
    /// <see cref="HttpProviderCore"/> and this is what proves the new provider inherits it rather
    /// than merely not breaking it — and it is the only case that reaches <c>TransportMessage</c>.
    /// </summary>
    [Fact]
    public async Task A_timeout_is_a_Timeout_and_never_a_cancel()
    {
        var fake = new FakeHandler().TimesOut();

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.Equal(ProviderIds.Azure, ex.ProviderId);
        Assert.Equal(2, fake.Requests);   // unlike a 4xx, a timeout IS worth a second attempt (§5.6)
        Assert.Contains("Azure", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.4's own sentence — <i>on Open nothing leaves the machine</i> — for the new provider, and
    /// the only case that reaches <c>PausedMessage</c>. A 429 opens <c>azure</c>'s gate; the next
    /// instance is refused with <c>NotSent</c> (ruling E3-b) before a socket is touched, which is
    /// what lets <see cref="ChainTranslator"/> count it as a SKIP rather than as this tier's own
    /// failure — the distinction E6.S4 depends on when Azure enters the read chain.
    /// </summary>
    [Fact]
    public async Task An_open_gate_refuses_the_next_call_without_sending_anything()
    {
        var first = new FakeHandler().Respond(HttpStatusCode.TooManyRequests,
            Fixture("azure-error-rate.json"));
        await Assert.ThrowsAsync<TranslationException>(
            () => Azure(first).TranslateAsync("привет", "ru", "en"));

        var second = new FakeHandler().RespondJson(Fixture("azure-batch.json"));
        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => Azure(second).TranslateAsync("привет", "ru", "en"));

        Assert.True(ex.NotSent, "an open gate must refuse before anything is sent");
        Assert.Equal(0, second.Requests);
        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.Contains("Azure", ex.Message, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  TP-PRV-09 / I11 — the error envelope: the status travels, the vendor message never does
    // =============================================================================================

    /// <summary>
    /// TP-PRV-09, both halves. The §10.1 line carries <c>provider=azure</c> and the HTTP status —
    /// which is what "the code is logged" amounts to here: <see cref="RequestLog"/>'s <c>body=</c>
    /// door takes MARKUP only, so a JSON error envelope's <c>code</c> and <c>message</c> reach the
    /// log through nothing but the status field. What §7.5 forbids is the other half, and it holds
    /// whatever the body shape: the vendor's sentence never reaches a surface the player reads —
    /// what they get is the Kind's sentence (E1.S6). This case uses an HTML page precisely because
    /// it is the one body that CAN be rendered into the line, so the assertion is made where it is
    /// hardest to pass.
    ///
    /// <para>And the adversarial half (I11): an error page that quotes the key back with no
    /// parameter name in front of it — the shape <c>RequestLog</c>'s marker cut cannot see, because
    /// it stops at a parameter NAME and cannot know a VALUE. <c>ProviderOptions.Secret</c> is what
    /// closes it, and two such gaps were closed in E2.S5's review over exactly this path.</para>
    /// </summary>
    [Fact]
    public async Task TP_PRV_09_the_status_is_logged_and_neither_the_vendor_message_nor_the_key_is()
    {
        const string key = "KIZOTIS-AZURE-KEY-0000-1111";
        const string vendorMessage = "The request is not authorized because credentials are missing or invalid.";
        var previous = Logging.DirectoryOverride;
        var dir = Directory.CreateTempSubdirectory("pwru-azure-log-").FullName;
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            // An HTML page (the one body shape that CAN reach the log) that quotes both the vendor's
            // sentence and the credential, with no `key=` anywhere near it.
            var page = "<html><body><p>" + vendorMessage + " Credential " + key
                     + " was refused.</p></body></html>";
            var fake = new FakeHandler().Respond(HttpStatusCode.Unauthorized, page, "text/html");

            var ex = await Assert.ThrowsAsync<TranslationException>(
                () => new AzureTranslator(key, Region, fake).TranslateAsync("привет", "ru", "zq"));

            // The key really travelled — otherwise this case proves nothing.
            Assert.Equal(key, Assert.Single(fake.Calls).Headers["Ocp-Apim-Subscription-Key"]);

            var file = File.ReadAllText(Path.Combine(dir, "log.txt"));
            var line = Assert.Single(LinesFor(dir, "ru->zq"));
            Assert.Contains("provider=azure", line, StringComparison.Ordinal);
            Assert.Contains("status=401", line, StringComparison.Ordinal);

            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
            Assert.DoesNotContain("KIZOTIS-AZURE-KEY", file, StringComparison.Ordinal);
            Assert.DoesNotContain(key, Logging.ReadRecent(), StringComparison.Ordinal);
            Assert.DoesNotContain("привет", file, StringComparison.Ordinal);

            // The vendor's own sentence never reaches a surface the player reads.
            Assert.DoesNotContain(vendorMessage, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(vendorMessage, MainWindow.Friendly(ex), StringComparison.Ordinal);
            Assert.DoesNotContain(key, MainWindow.Friendly(ex), StringComparison.Ordinal);
        }
        finally
        {
            Logging.DirectoryOverride = previous;
            // The suppression counter is process-static: leaving it where this case left it would
            // silently thin the next class's lines.
            RequestLog.ResetSuppression();
            try { Directory.Delete(dir, recursive: true); } catch { /* the case already made its point */ }
        }
    }

    /// <summary>
    /// TP-PRV-09's <b>other</b> body shape — and the one Azure actually sends. A JSON error
    /// envelope reaches <b>no field</b> of the §10.1 line: <see cref="RequestLog.BodyHead"/> takes
    /// markup only, because the provider's own answer carries the user's text and a proxy that
    /// relabelled it would otherwise walk that text into a report pasted to Discord (I11).
    ///
    /// <para>That is also the whole of this story's accepted deviation from AC 3: the envelope's
    /// own <c>code</c> (401000, 403000, 429001 in the fixtures) cannot be logged without a provider
    /// that reads a body FOR the log, which is exactly what E1.S5 centralised away. <c>status=401</c>
    /// is what travels. This case pins both halves of what actually holds — the status is there, and
    /// neither the vendor's <c>message</c> nor the key is, on the shape where the key is adversarially
    /// placed with no parameter name in front of it.</para>
    /// </summary>
    [Fact]
    public async Task A_json_error_envelope_reaches_no_field_of_the_log_line()
    {
        const string key = "KIZOTIS-AZURE-KEY-2222-3333";
        const string vendorMessage = "The operation is not allowed because the subscription has exceeded its free quota.";
        var previous = Logging.DirectoryOverride;
        var dir = Directory.CreateTempSubdirectory("pwru-azure-json-").FullName;
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            var envelope = "{\"error\":{\"code\":403000,\"message\":\"" + vendorMessage
                         + " Credential " + key + " was refused.\"}}";
            var fake = new FakeHandler().Respond(HttpStatusCode.Forbidden, envelope);

            var ex = await Assert.ThrowsAsync<TranslationException>(
                () => new AzureTranslator(key, Region, fake).TranslateAsync("привет", "ru", "zx"));

            // The envelope really was read — it is what the quota split classifies on (§4.2 row 6).
            Assert.Equal(TranslationErrorKind.QuotaExhausted, ex.Kind);
            Assert.Equal(key, Assert.Single(fake.Calls).Headers["Ocp-Apim-Subscription-Key"]);

            var line = Assert.Single(LinesFor(dir, "ru->zx"));
            Assert.Contains("status=403", line, StringComparison.Ordinal);
            // …and none of it travelled: no `body=` at all for a JSON body, so no code, no message.
            Assert.DoesNotContain("body=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("403000", line, StringComparison.Ordinal);

            var file = File.ReadAllText(Path.Combine(dir, "log.txt"));
            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
            Assert.DoesNotContain(vendorMessage, file, StringComparison.Ordinal);
            Assert.DoesNotContain(vendorMessage, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(key, MainWindow.Friendly(ex), StringComparison.Ordinal);
        }
        finally
        {
            Logging.DirectoryOverride = previous;
            // The suppression counter is process-static: leaving it where this case left it would
            // silently thin the next class's lines.
            RequestLog.ResetSuppression();
            try { Directory.Delete(dir, recursive: true); } catch { /* the case already made its point */ }
        }
    }

    // =============================================================================================
    //  T5 — the empty-credential guard: refused before anything is sent
    // =============================================================================================

    /// <summary>A key without a region is a guaranteed 401 (§12), and a real 401 costs a gate
    /// strike and blocks the provider until the user re-saves a key. Both halves are therefore
    /// checked before the send, where the mistake costs nothing.</summary>
    [Theory]
    [InlineData("", Region)]
    [InlineData("   ", Region)]
    [InlineData(Key, "")]
    [InlineData(Key, "  ")]
    public async Task An_incomplete_credential_costs_no_request(string key, string region)
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-batch.json"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new AzureTranslator(key, region, fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.AuthFailed, ex.Kind);
        Assert.Equal(ProviderIds.Azure, ex.ProviderId);
        Assert.Equal(0, fake.Requests);
        Assert.Null(ProviderGates.Snapshot(ProviderIds.Azure));   // and no gate strike either
    }

    /// <summary>
    /// The half-entered credential <c>Trim</c> cannot reach: a control character pasted INSIDE the
    /// key or the region. It has to be refused here because it is refused nowhere else —
    /// <c>TryAddWithoutValidation</c> is deliberately the only thing that looks at these two values
    /// — and a header the transport cannot serialise throws something that is neither
    /// <c>HttpRequestException</c>, <c>IOException</c> nor <c>OperationCanceledException</c>: it
    /// escapes both of the core's filters, is reported to the gate as <c>Unknown</c> and reaches the
    /// player as a framework message that can quote the header it choked on.
    /// </summary>
    [Theory]
    [InlineData("azure-key\r\n0000", Region)]
    [InlineData("azure-key	0000", Region)]
    [InlineData(Key, "west\neurope")]
    public async Task A_credential_with_a_control_character_costs_no_request(string key, string region)
    {
        var fake = new FakeHandler().RespondJson(Fixture("azure-batch.json"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new AzureTranslator(key, region, fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.AuthFailed, ex.Kind);
        Assert.Equal(0, fake.Requests);
        Assert.Null(ProviderGates.Snapshot(ProviderIds.Azure));
        // The sentence is the log's account and names no value (I11).
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  The batch split (§7.5's documented request limits)
    // =============================================================================================

    /// <summary>The split is by the graded constants and nothing else, and it preserves input
    /// order — a batch that reordered its own groups would be I5's failure mode with extra
    /// steps.</summary>
    [Fact]
    public void A_batch_is_split_at_the_documented_limits()
    {
        // Under both caps: one request, as every real call is (a LIVE tick is ≈2.1 lines).
        Assert.Single(AzureTranslator.Batches(new[] { "a", "b", "c" }));

        var many = Enumerable.Range(0, TranslationPolicy.AzureMaxTextsPerRequest + 1)
                             .Select(i => "l" + i).ToList();
        var byCount = AzureTranslator.Batches(many);
        Assert.Equal(2, byCount.Count);
        Assert.Equal(TranslationPolicy.AzureMaxTextsPerRequest, byCount[0].Count);
        Assert.Single(byCount[1]);
        Assert.Equal(many, byCount.SelectMany(b => b).ToList());   // order preserved, nothing lost

        var big = new string('x', TranslationPolicy.AzureMaxCharsPerRequest / 2 + 1);
        var byChars = AzureTranslator.Batches(new[] { big, big, big });
        Assert.Equal(3, byChars.Count);
        Assert.All(byChars, b => Assert.Single(b));

        // A single text over the cap travels alone rather than being cut: silently cutting a line
        // is the failure I5 is about, seen from the other side. (Azure answers such a request with
        // a 400 — see A_400_is_the_row_the_table_does_not_name for what that becomes.)
        var huge = new string('y', TranslationPolicy.AzureMaxCharsPerRequest + 10);
        Assert.Equal(huge, Assert.Single(Assert.Single(AzureTranslator.Batches(new[] { huge }))));

        // …and it is still one group when the over-cap text has neighbours: the split closes the
        // group BEFORE adding, so the oversized element opens a group of its own instead of an
        // empty one being pushed in front of it.
        Assert.Equal(new[] { 1, 1, 1 },
            AzureTranslator.Batches(new[] { "a", huge, "b" }).Select(b => b.Count).ToArray());

        // No group is ever empty and no input is ever dropped — including the degenerate list a
        // caller can build out of blank OCR lines, which costs zero characters and must still be
        // one POST rather than none.
        Assert.Empty(AzureTranslator.Batches(Array.Empty<string>()));
        Assert.All(AzureTranslator.Batches(new[] { "", "", "" }), b => Assert.NotEmpty(b));
        Assert.Equal(3, AzureTranslator.Batches(new[] { "", "", "" }).Sum(b => b.Count));
    }

    /// <summary>…and the split really is one POST per group, concatenated in order.</summary>
    [Fact]
    public async Task An_oversized_batch_becomes_two_requests_whose_answers_are_concatenated()
    {
        int cap = TranslationPolicy.AzureMaxTextsPerRequest;
        var lines = Enumerable.Range(0, cap + 2).Select(i => "l" + i).ToList();

        var fake = new FakeHandler()
            .RespondJson(Body(lines.Take(cap)))
            .RespondJson(Body(lines.Skip(cap)));

        var outp = await Azure(fake).TranslateLinesAsync(lines, "ru", "en");

        Assert.Equal(2, fake.Requests);
        Assert.Equal(lines.Select(l => l + "!").ToList(), outp);
    }

    /// <summary>An empty list costs no request at all.</summary>
    [Fact]
    public async Task An_empty_list_is_answered_without_a_request()
    {
        var fake = new FakeHandler();

        Assert.Empty(await Azure(fake).TranslateLinesAsync(Array.Empty<string>(), "ru", "en"));
        Assert.Equal(0, fake.Requests);
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>A well-formed Azure answer for the given inputs, each translated to "&lt;input&gt;!"
    /// — enough to prove order and 1:1 without a fixture per size.</summary>
    private static string Body(IEnumerable<string> texts) =>
        "[" + string.Join(",", texts.Select(t =>
            $$"""{"translations":[{"text":{{JsonSerializer.Serialize(t + "!")}},"to":"en"}]}""")) + "]";

    private static string FixtureDir() => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string Fixture(string name)
    {
        var path = Path.Combine(FixtureDir(), name);
        Assert.True(File.Exists(path),
            $"fixture {name} not found at {path} — is the Fixtures item group still in PWRUHelper.Tests.csproj?");
        return File.ReadAllText(path);
    }

    /// <summary>This call's own §10.1 lines, found by a language pair nothing else uses: the log
    /// directory override is process-wide, so another class's deliberate failure can land in the
    /// same file.</summary>
    private static List<string> LinesFor(string dir, string dir60)
    {
        var path = Path.Combine(dir, "log.txt");
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path)
                   .Select(l => l.Contains("] tr ") ? l[(l.IndexOf("] tr ", StringComparison.Ordinal) + 2)..] : l)
                   .Where(l => l.StartsWith("tr ", StringComparison.Ordinal) && l.Contains("dir=" + dir60))
                   .ToList();
    }
}

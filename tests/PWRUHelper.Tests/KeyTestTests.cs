using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E6.S5 — <b>Test key</b>, everything below the button: the two probes, the outcome-to-sentence
/// mapping of <c>ux-mode-degrade.md</c> §3.7, and the two scans that keep the copy honest.
///
/// <para>Driven entirely off <see cref="FakeHandler"/> and the bodies under <c>Fixtures/</c>
/// (IS-8/IS-10/IS-11): <b>no request leaves the box in this file</b>, which matters more here than
/// anywhere else — a key test that reached a real endpoint would spend a real quota, which is the
/// very thing ruling E6-b is about.</para>
///
/// <para><c>[Collection("Gates")]</c> because both probes consult the process-global registry: a
/// 429 here opens a gate for a minute, and the "a test never clears a gate" case depends on the
/// block it just earned still being there.</para>
/// </summary>
[Collection("Gates")]
public class KeyTestTests : GatesTestBase
{
    private const string FreeKey = "0123456789abcdef-0000-1111-2222-333344445555:fx";
    private const string PaidKey = "0123456789abcdef-0000-1111-2222-333344445555";
    private const string AzureKey = "azure-key-0000";
    private const string Region = "westeurope";

    // =============================================================================================
    //  T1 / AC 3 — DeepL's probe is `GET {host}/v2/usage`, authenticated, and spends nothing
    // =============================================================================================

    /// <summary>
    /// The request line, pinned: a GET at <c>/v2/usage</c> on the host the key's own suffix
    /// selects, with the same <c>Authorization</c> header a translation would carry — and the key
    /// in that header ONLY (I11: a key in a query reaches proxies, referrers and the vendor's
    /// access logs, and §10.1 renders the path).
    /// </summary>
    [Theory]
    [InlineData(FreeKey, "https://api-free.deepl.com/v2/usage")]
    [InlineData(PaidKey, "https://api.deepl.com/v2/usage")]
    public async Task The_usage_probe_is_a_GET_on_the_host_the_key_suffix_selects(string key, string expected)
    {
        var fake = new FakeHandler().RespondJson(Fixture("deepl-usage.json"));

        var result = await new DeepLTranslator(key, fake).TestKeyAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, fake.Requests);
        var call = fake.Calls[0];
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal(expected, call.Uri.AbsoluteUri);
        Assert.Equal("DeepL-Auth-Key " + key, call.Headers["Authorization"]);
        Assert.DoesNotContain(key, call.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Null(call.Body);                       // a GET carries none, so nothing is measured
    }

    /// <summary>A healthy key: ok, plus the two numbers the endpoint volunteered — which is the
    /// whole reason this probe is worth making rather than translating one word.</summary>
    [Fact]
    public async Task A_healthy_usage_answer_is_ok_and_carries_the_character_counts()
    {
        var fake = new FakeHandler().RespondJson(Fixture("deepl-usage.json"));

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.True(result.Ok);
        Assert.Null(result.Kind);
        Assert.Equal("183,053 of 500,000 characters used.", result.UsageText);
    }

    /// <summary>
    /// §3.7's quota row, decided <b>without a translation</b>. DeepL answers 456 only on the
    /// translate path, so a key whose allowance is spent would otherwise test perfectly healthy and
    /// then fail mid-raid — the exact thing this button exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_spent_allowance_is_the_quota_row_although_the_probe_returned_200()
    {
        var fake = new FakeHandler().RespondJson(Fixture("deepl-usage-spent.json"));

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.False(result.Ok);
        Assert.Equal(TranslationErrorKind.QuotaExhausted, result.Kind);
        Assert.Equal(HttpStatusCode.OK, fake.Calls[0].Status);
    }

    /// <summary>The status mapping is E1.S3's and nobody re-decides it here (T5): a 401 is a
    /// refused key, DeepL's own 456 is a spent quota, a 5xx is the service being down.</summary>
    [Theory]
    [InlineData(401, "deepl-error-auth.json", TranslationErrorKind.AuthFailed)]
    [InlineData(403, "deepl-error-auth.json", TranslationErrorKind.AuthFailed)]
    [InlineData(456, "deepl-error-quota.json", TranslationErrorKind.QuotaExhausted)]
    [InlineData(429, "deepl-error-quota.json", TranslationErrorKind.RateLimited)]
    public async Task The_usage_probe_maps_a_failure_through_the_classifier(int status, string fixture,
        TranslationErrorKind expected)
    {
        var fake = new FakeHandler().Respond((HttpStatusCode)status, Fixture(fixture));

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.False(result.Ok);
        Assert.False(result.Paused);
        Assert.Equal(expected, result.Kind);
    }

    /// <summary>A dead network is a <c>Network</c> Kind and not a string match (T4).</summary>
    [Fact]
    public async Task A_dead_network_is_the_no_internet_row()
    {
        var fake = new FakeHandler().Throws(new HttpRequestException("no such host"));

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.Equal(TranslationErrorKind.Network, result.Kind);
        Assert.Equal("Could not check the key — no internet connection.",
            UserMessages.KeyTestSentence(ProviderIds.DeepL, result, "", null));
    }

    /// <summary>A body that is not the usage shape is a <c>BadResponse</c>, exactly as the
    /// translation parser's is — and it does not throw out of the probe.</summary>
    [Fact]
    public async Task A_body_that_is_not_the_usage_shape_is_a_BadResponse()
    {
        var fake = new FakeHandler().RespondJson("""{"hello":"world"}""");

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.Equal(TranslationErrorKind.BadResponse, result.Kind);
    }

    /// <summary>
    /// A number-shaped <c>character_count</c> the parser cannot represent is a <b>BadResponse</b>
    /// like any other body that is not the provider's shape — review: <c>GetInt64</c> raises
    /// <see cref="FormatException"/> here, not <c>InvalidOperationException</c>, so it used to
    /// escape the filter, reach the gate as <c>Unknown</c> and put a raw .NET message on the
    /// status line.
    /// </summary>
    [Theory]
    [InlineData("""{"character_count":1.5,"character_limit":500000}""")]
    [InlineData("""{"character_count":99999999999999999999,"character_limit":500000}""")]
    public async Task A_usage_number_that_is_not_an_integer_is_a_BadResponse(string body)
    {
        var fake = new FakeHandler().RespondJson(body);

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.Equal(TranslationErrorKind.BadResponse, result.Kind);
    }

    /// <summary>
    /// A limit that is not positive is not a limit (review): unmetered plans answer <c>0</c>, and
    /// the sentence must fall back to the count alone rather than promising "of 0 characters".
    /// </summary>
    [Fact]
    public async Task A_non_positive_limit_is_read_as_no_limit_at_all()
    {
        var fake = new FakeHandler().RespondJson("""{"character_count":183053,"character_limit":0}""");

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.True(result.Ok);
        Assert.Equal("183,053 characters used.", result.UsageText);
        Assert.DoesNotContain(" of ", result.UsageText!, StringComparison.Ordinal);
    }

    /// <summary>The counts survive to the row that needs them most (review): a spent allowance says
    /// HOW spent, instead of computing the numbers and dropping them.</summary>
    [Fact]
    public async Task The_quota_row_carries_the_counts_it_was_read_from()
    {
        var fake = new FakeHandler().RespondJson(Fixture("deepl-usage-spent.json"));

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.Equal("⚠ The key works, but the DeepL quota is used up — the free engines are used "
            + "until it resets. 500,000 of 500,000 characters used.",
            UserMessages.KeyTestSentence(ProviderIds.DeepL, result, "", null));
    }

    /// <summary>An empty box costs no request: a real 401 would earn a real AuthFailed block for a
    /// mistake no request can fix.</summary>
    [Fact]
    public async Task An_empty_key_is_refused_without_a_request()
    {
        var fake = new FakeHandler().RespondJson(Fixture("deepl-usage.json"));

        var result = await new DeepLTranslator("", fake).TestKeyAsync();

        Assert.Equal(TranslationErrorKind.AuthFailed, result.Kind);
        Assert.Equal(0, fake.Requests);
    }

    // =============================================================================================
    //  OQ-f / ruling E6-b — both probes go THROUGH the core, so the gate is consulted
    // =============================================================================================

    /// <summary>
    /// §5.4's own sentence applied to the button: on Open nothing leaves the machine, and ruling
    /// E6-b's third clause is what the player is told — a paused provider's test says it is paused
    /// rather than blaming a key nobody asked about.
    /// </summary>
    [Fact]
    public async Task A_paused_deepl_is_told_as_a_pause_and_costs_no_request()
    {
        var first = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, Fixture("deepl-error-quota.json"));
        await new DeepLTranslator(FreeKey, first).TestKeyAsync();

        var second = new FakeHandler().RespondJson(Fixture("deepl-usage.json"));
        var result = await new DeepLTranslator(FreeKey, second).TestKeyAsync();

        Assert.True(result.Paused);
        Assert.Equal(0, second.Requests);
        Assert.NotNull(result.SecondsUntilRetry);
        Assert.Contains("is paused right now",
            UserMessages.KeyTestSentence(ProviderIds.DeepL, result, "", "45 s"), StringComparison.Ordinal);
    }

    /// <summary>The same for Azure, whose probe is a real translation: the gate is consulted before
    /// the five characters are spent, which is the one thing Epic 2 exists to guarantee.</summary>
    [Fact]
    public async Task A_paused_azure_is_told_as_a_pause_and_costs_no_request()
    {
        var first = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, Fixture("azure-error-rate.json"));
        await new AzureTranslator(AzureKey, Region, first).TestKeyAsync();

        var second = new FakeHandler().RespondJson(AzureAnswer);
        var result = await new AzureTranslator(AzureKey, Region, second).TestKeyAsync();

        Assert.True(result.Paused);
        Assert.Equal(0, second.Requests);
    }

    /// <summary>
    /// Ruling <b>E2-i</b>, from the side that is easy to get wrong: a successful — or a failed —
    /// TEST never lifts a block. Only a key SAVE does (<c>TranslationChains.OnKeySaved</c>), which
    /// is why the way out of a refused key is the Save button and not the Test button.
    /// </summary>
    [Fact]
    public async Task A_key_test_never_clears_the_gate_it_just_closed()
    {
        var refused = new FakeHandler().Respond(HttpStatusCode.Unauthorized, Fixture("azure-error-auth.json"));
        var first = await new AzureTranslator(AzureKey, Region, refused).TestKeyAsync();
        Assert.Equal(TranslationErrorKind.AuthFailed, first.Kind);
        Assert.Equal(TranslationErrorKind.AuthFailed, ProviderGates.Snapshot(ProviderIds.Azure)!.LastKind);

        // A second press changes nothing about the block: the gate refuses it, no request is made,
        // and the AuthFailed row is still there afterwards.
        var again = new FakeHandler().RespondJson(AzureAnswer);
        var second = await new AzureTranslator(AzureKey, Region, again).TestKeyAsync();

        Assert.True(second.Paused);
        Assert.Equal(0, again.Requests);
        Assert.Equal(TranslationErrorKind.AuthFailed, ProviderGates.Snapshot(ProviderIds.Azure)!.LastKind);
    }

    /// <summary>Neither probe may reach the one facade that lifts a block (ruling E2-i), and neither
    /// may write a setting. A scan, because both are absences.</summary>
    [Fact]
    public void No_probe_names_the_key_save_facade_or_a_setting()
    {
        foreach (var file in new[] { "DeepLTranslator.cs", "AzureTranslator.cs" })
        {
            var code = Code(File.ReadAllText(RepoFile(Path.Combine("Services", file))));
            foreach (var forbidden in new[] { "OnKeySaved", "ClearAuthBlock", "AppSettings", "SettingsService" })
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal),
                    $"{file} names {forbidden} — a key TEST may neither clear a gate nor write a setting");
        }
    }

    // =============================================================================================
    //  Azure's probe — one tiny real translation, and exactly one
    // =============================================================================================

    /// <summary>
    /// The request AC 3's label is honest about: one POST, five ASCII characters, en→ru, through
    /// the normal path with both headers. "Exactly one" is half the assertion — a probe that
    /// quietly retried would spend twice what the label promises.
    /// </summary>
    [Fact]
    public async Task The_azure_probe_is_one_five_character_translation_through_the_normal_path()
    {
        var fake = new FakeHandler().RespondJson(AzureAnswer);

        var result = await new AzureTranslator(AzureKey, Region, fake).TestKeyAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, fake.Requests);
        var call = fake.Calls[0];
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=en&to=ru",
            call.Uri.AbsoluteUri);
        Assert.Equal("""[{"Text":"hello"}]""", call.Body);
        Assert.Equal(5, AzureTranslator.TestKeyProbeText.Length);
        Assert.Equal(AzureKey, call.Headers["Ocp-Apim-Subscription-Key"]);
        Assert.Equal(Region, call.Headers["Ocp-Apim-Subscription-Region"]);
    }

    /// <summary>§7.5's status mapping, reached through the probe: the sentences the player reads
    /// are keyed on these Kinds and nothing else (T5).</summary>
    [Theory]
    [InlineData(401, "azure-error-auth.json", TranslationErrorKind.AuthFailed)]
    [InlineData(403, "azure-error-quota.json", TranslationErrorKind.QuotaExhausted)]
    [InlineData(429, "azure-error-rate.json", TranslationErrorKind.RateLimited)]
    public async Task The_azure_probe_maps_a_failure_through_the_classifier(int status, string fixture,
        TranslationErrorKind expected)
    {
        var fake = new FakeHandler().Respond((HttpStatusCode)status, Fixture(fixture));

        var result = await new AzureTranslator(AzureKey, Region, fake).TestKeyAsync();

        Assert.False(result.Ok);
        Assert.Equal(expected, result.Kind);
    }

    /// <summary>A half-entered credential costs no request here either — the About tab refuses it
    /// before the button, and this is the belt to that braces.</summary>
    [Fact]
    public async Task An_azure_credential_with_no_region_is_refused_without_a_request()
    {
        var fake = new FakeHandler().RespondJson(AzureAnswer);

        var result = await new AzureTranslator(AzureKey, "", fake).TestKeyAsync();

        Assert.Equal(TranslationErrorKind.AuthFailed, result.Kind);
        Assert.Equal(0, fake.Requests);
    }

    // =============================================================================================
    //  I3 — a cancel is a cancel, and a timeout is not one
    // =============================================================================================

    /// <summary>An already-cancelled token costs no request and comes out as a cancel, not as a
    /// result: nothing renders after it because the caller asked for nothing.</summary>
    [Fact]
    public async Task A_cancelled_token_costs_no_request_and_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var deepl = new FakeHandler().RespondJson(Fixture("deepl-usage.json"));
        var azure = new FakeHandler().RespondJson(AzureAnswer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DeepLTranslator(FreeKey, deepl).TestKeyAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AzureTranslator(AzureKey, Region, azure).TestKeyAsync(cts.Token));

        Assert.Equal(0, deepl.Requests);
        Assert.Equal(0, azure.Requests);
    }

    /// <summary>
    /// I3's canonical trap on the new path: an <see cref="HttpClient"/> timeout is a
    /// <c>TaskCanceledException</c> whose token is NOT cancelled. Read as a cancel it would leave
    /// the status line silent over a button that just came back — so it must arrive as a Kind.
    /// </summary>
    [Fact]
    public async Task A_timeout_is_a_result_and_never_a_cancel()
    {
        var fake = new FakeHandler().TimesOut();

        var result = await new DeepLTranslator(FreeKey, fake).TestKeyAsync();

        Assert.Equal(TranslationErrorKind.Timeout, result.Kind);
        // Deliberate expected-string update (E7.S1 review): the joined sentence names the engine
        // now. §3.0/A3 says {P} comes from the provider that failed and is never guessed — and a
        // key test cannot guess, it was handed the id. The row sits under the DeepL key box, two
        // lines from "⚠ Not checked — DeepL is paused right now."
        Assert.Equal("Could not check the key: DeepL took too long to answer — try again shortly.",
            UserMessages.KeyTestSentence(ProviderIds.DeepL, result, "", null));
    }

    // =============================================================================================
    //  AC 4 / I11 — the key never reaches the log
    // =============================================================================================

    /// <summary>
    /// The probe DeepL's own path has never had: a <c>/usage</c> request that fails writes a §10.1
    /// line, and the adversarial body is the one E1.S5's review found — an error page that quotes
    /// the key back with no parameter name in front of it, which <see cref="RequestLog"/>'s marker
    /// cut cannot see because it stops at a NAME and cannot know a VALUE.
    /// </summary>
    [Fact]
    public async Task AC4_No_log_line_from_a_failed_test_carries_the_key()
    {
        const string key = "KIZOTIS-DEEPL-USAGE-KEY-0000-1111:fx";
        var previous = Logging.DirectoryOverride;
        var dir = Directory.CreateTempSubdirectory("pwru-keytest-log-").FullName;
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();
            var fake = new FakeHandler().Respond(HttpStatusCode.Forbidden,
                $"<html><body>Forbidden: {key} is not valid</body></html>", "text/html");

            var result = await new DeepLTranslator(key, fake).TestKeyAsync();
            Assert.Equal(TranslationErrorKind.AuthFailed, result.Kind);

            var log = Path.Combine(dir, "log.txt");
            Assert.True(File.Exists(log), "the failed probe wrote no §10.1 line at all");
            var text = File.ReadAllText(log);
            Assert.Contains("provider=deepl", text, StringComparison.Ordinal);
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
            Assert.DoesNotContain("KIZOTIS-DEEPL-USAGE-KEY", text, StringComparison.Ordinal);
            Assert.Contains(HttpProviderCore.Redacted, text, StringComparison.Ordinal);
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
    //  AC 2 — the §3.7 strings, one case each
    // =============================================================================================

    /// <summary>
    /// The deck, verbatim. Every one of these is the user-visible wording of this increment, so the
    /// next edit to any of them must be a deliberate one that fails here first.
    ///
    /// <para>The Azure region is one the app never offers — the seeded list is
    /// <c>global … swedencentral</c> — because a sentence quoting a value the user TYPED is the
    /// whole point of parameterising it.</para>
    /// </summary>
    [Fact]
    public void AC2_Every_shipped_outcome_reads_as_the_deck_writes_it()
    {
        const string typed = "brazilsouth";

        Assert.Equal("✓ Key works — DeepL is used for what you write.",
            Sentence(ProviderIds.DeepL, KeyTestResult.Works()));
        Assert.Equal("✓ Key works — DeepL is used for what you write. 183,053 of 500,000 characters used.",
            Sentence(ProviderIds.DeepL, KeyTestResult.Works(UserMessages.DeepLUsage(183_053, 500_000))));
        Assert.Equal("✕ DeepL refused this key. Check you pasted all of it (free keys end in :fx).",
            Sentence(ProviderIds.DeepL, KeyTestResult.Failed(TranslationErrorKind.AuthFailed)));
        Assert.Equal("⚠ The key works, but the DeepL quota is used up — the free engines are used until it resets.",
            Sentence(ProviderIds.DeepL, KeyTestResult.Failed(TranslationErrorKind.QuotaExhausted)));

        Assert.Equal($"✓ Key works ({typed}) — Azure is used for what you write.",
            Sentence(ProviderIds.Azure, KeyTestResult.Works(), typed));
        Assert.Equal("✕ Azure refused this key. Check the key, and that the region matches your resource.",
            Sentence(ProviderIds.Azure, KeyTestResult.Failed(TranslationErrorKind.AuthFailed), typed));
        // §3.7 goes on to promise "resets on the 1st". The reset DAY is verified nowhere and the
        // monthly allowance is still [UNKNOWN] U4 (E6.S1), so the clause is dropped rather than
        // shipped as a promise — the story's own instruction.
        Assert.Equal("⚠ Your 2 million free characters for this month are used up.",
            Sentence(ProviderIds.Azure, KeyTestResult.Failed(TranslationErrorKind.QuotaExhausted), typed));

        // The one row that is the same sentence for both providers: when nothing resolves, naming
        // an engine would be noise.
        foreach (var provider in new[] { ProviderIds.DeepL, ProviderIds.Azure })
            Assert.Equal("Could not check the key — no internet connection.",
                Sentence(provider, KeyTestResult.Failed(TranslationErrorKind.Network), typed));

        // …and ruling E6-b's own row, with and without something honest to count down to.
        Assert.Equal("⚠ Not checked — Azure is paused right now. Try again in 45 s.",
            UserMessages.KeyTestSentence(ProviderIds.Azure,
                KeyTestResult.PausedFor(TranslationErrorKind.RateLimited, 45), typed, "45 s"));
        Assert.Equal("⚠ Not checked — DeepL is paused right now.",
            UserMessages.KeyTestSentence(ProviderIds.DeepL,
                KeyTestResult.PausedFor(TranslationErrorKind.RateLimited, null), "", null));
    }

    /// <summary>
    /// The Kinds §3.7 has no row for — a 5xx, a rate limit, a body nobody can read. Rather than
    /// invent five sentences, the deck's own sentence for the Kind is joined after a colon
    /// (§3.3's join rule, the same shape <c>ReadFailed</c> uses), so there is still exactly one
    /// place that decides what the player reads about a 429.
    /// </summary>
    [Theory]
    // E7.S1 / amendment A1 rewrote the RateLimited row — §3.1 always banned "wait a minute" and the
    // shipped "try again in a moment" sat next to it only because there was no gate to count down
    // from. Deliberate expected-string update; the join itself is untouched.
    //
    // …and E7.S1's review restored {P} on this join too: the id is a parameter of KeyTestSentence,
    // so "the translation service" was the one place in the deck where a name was available and not
    // used (§3.0 rule 1 is about NEVER INVENTING one, not about declining a known one). A11's
    // proper-noun guard is what keeps "DeepL" capitalised after the colon.
    [InlineData(TranslationErrorKind.RateLimited,
        "Could not check the key: DeepL asked us to slow down — paused briefly, and it retries on its own.")]
    [InlineData(TranslationErrorKind.Unavailable,
        "Could not check the key: DeepL is down right now — try again shortly.")]
    [InlineData(TranslationErrorKind.BadResponse,
        "Could not check the key: DeepL sent something we could not read — try again shortly.")]
    public void An_outcome_the_deck_has_no_row_for_still_reads_as_one_sentence(
        TranslationErrorKind kind, string expected)
        => Assert.Equal(expected, Sentence(ProviderIds.DeepL, KeyTestResult.Failed(kind)));

    /// <summary>The untyped catch of the handler's outermost <c>catch</c>: an exception that never
    /// became a result reads like its typed twin, exactly as <c>UserMessages.For</c> does.</summary>
    [Fact]
    public void A_raw_exception_reads_like_its_typed_twin()
    {
        Assert.Equal("Could not check the key — no internet connection.",
            UserMessages.KeyTestSentence(ProviderIds.DeepL, new HttpRequestException("dns"), ""));
        Assert.Equal("✕ Azure refused this key. Check the key, and that the region matches your resource.",
            UserMessages.KeyTestSentence(ProviderIds.Azure,
                new TranslationException(TranslationErrorKind.AuthFailed, "raw"), Region));
    }

    /// <summary>AC 3, as an assertion rather than as an intention: the two labels are NOT the same
    /// string, and the one that costs quota says so.</summary>
    [Fact]
    public void AC3_The_two_labels_are_asymmetric_and_the_costly_one_says_so()
    {
        Assert.Equal("Test key", UserMessages.TestKeyLabel());
        Assert.Equal("Test key (uses a few characters)", UserMessages.TestKeyLabelCosts());
        Assert.NotEqual(UserMessages.TestKeyLabel(), UserMessages.TestKeyLabelCosts());
        Assert.Contains("quota", UserMessages.TestKeyCostsTooltip(), StringComparison.Ordinal);
        Assert.Equal("Testing…", UserMessages.TestingLabel());
    }

    // =============================================================================================
    //  UX-DR19 — each string exists exactly once in production source
    // =============================================================================================

    /// <summary>
    /// Hint 11, as a scan: the ten strings this story ships live in <c>Services/UserMessages.cs</c>
    /// and nowhere else. It is what the ten E1–E10 strings cost this project — a sentence in two
    /// files drifts, and the player reads whichever file was edited last.
    /// </summary>
    [Fact]
    public void UXDR19_Each_key_test_string_appears_exactly_once_in_production_source()
    {
        var fragments = new[]
        {
            "✓ Key works — DeepL is used for what you write.",
            "✕ DeepL refused this key.",
            "⚠ The key works, but the DeepL quota is used up",
            ") — Azure is used for what you write.",
            "✕ Azure refused this key.",
            "⚠ Your 2 million free characters for this month are used up.",
            "Could not check the key — no internet connection.",
            "is paused right now.",
            "Test key (uses a few characters)",
            "Testing…",
        };

        foreach (var fragment in fragments)
        {
            // OCCURRENCES and not files (review). Counting files could not see the likeliest
            // duplication of all — the same sentence twice inside UserMessages.cs, which is where
            // all the copy lives — and it was already true of the pause row, spelled once with a
            // countdown and once without.
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

    private static int Occurrences(string text, string fragment)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0) { count++; at += fragment.Length; }
        return count;
    }

    /// <summary>AC 1's cheap half, and the one a reviewer would otherwise have to take on trust:
    /// the code-behind that owns these buttons names no <c>MessageBox</c> at all.</summary>
    [Fact]
    public void AC1_The_handlers_name_no_MessageBox()
    {
        var code = Code(File.ReadAllText(RepoFile("Views/MainWindow.Translate.cs")));
        Assert.DoesNotContain("MessageBox", code, StringComparison.Ordinal);
        Assert.Contains("RunKeyTestAsync", code, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>Azure's answer to the probe — one element, in order, exactly as §7.5 documents.</summary>
    private const string AzureAnswer = """[{"translations":[{"text":"привет","to":"ru"}]}]""";

    private static string Sentence(string providerId, KeyTestResult result, string region = "")
        => UserMessages.KeyTestSentence(providerId, result, region, null);

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"fixture {name} not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Code only: a sentence QUOTED in a comment is prose, and the scan would then fail on
    /// the explanation of why the rule exists.</summary>
    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    /// <summary>Every shipped source file: the app's own <c>.cs</c> and <c>.xaml</c>, with the test
    /// tree and the build outputs left out.</summary>
    private static IEnumerable<string> ProductionSources()
    {
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        foreach (var pattern in new[] { "*.cs", "*.xaml" })
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                // RELATIVE to the repo root, and that is not a detail: this repo is developed in a
                // worktree under `.claude\worktrees\…`, so an absolute-path filter would exclude
                // every file in the app and pass with nothing scanned.
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

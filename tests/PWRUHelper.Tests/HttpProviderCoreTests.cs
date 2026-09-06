using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The shared request pipeline of <c>architecture-cible.md</c> §7.0 — TP-RET-01…09 plus the gate
/// wiring end to end, which is the sentence Epic 2 is judged on: <b>a refusal costs one request,
/// and the next call costs none at all.</b>
///
/// <para>No case sleeps and no case asserts elapsed time (CI-3). Every back-off claim is made on
/// the delay the core <i>requested</i>, through IS-7's injectable delay
/// (<see cref="TestBackoffRedirect"/>); every gate claim is made on a gate this file owns, or on
/// the registry's under <c>[Collection("Gates")]</c>.</para>
/// </summary>
[Collection("Gates")]
public class HttpProviderCoreTests : GatesTestBase
{
    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";

    /// <summary>A gate whose clock this file drives, so "the window has not elapsed" is a fact and
    /// not a race. <c>DrainBucketForTests</c> exists for the ceiling; this is for the breaker.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
    }

    // =============================================================================================
    //  TP-RET-01 … TP-RET-05 — what each failure costs
    // =============================================================================================

    /// <summary>TP-RET-02 — a 403 is the other refusal that must not be argued with. One request,
    /// and on the keyless endpoint it is a <c>Blocked</c> (§4.2 row 8).</summary>
    [Fact]
    public async Task TP_RET_02_a_403_costs_exactly_one_request()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.Forbidden, "nope");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(1, fake.Requests);
        Assert.Equal(TranslationErrorKind.Blocked, ex.Kind);
    }

    /// <summary>The other half of TP-RET-03: the second attempt is a real second chance, so a 503
    /// followed by a 200 comes back as a translation.</summary>
    [Fact]
    public async Task A_503_then_a_200_translates_on_the_second_attempt()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.ServiceUnavailable).RespondJson(GoogleOk);

        Assert.Equal("hello", await new TranslationService(fake).TranslateAsync("привет", "ru", "en"));
        Assert.Equal(2, fake.Requests);
    }

    /// <summary>TP-RET-09 — the two numbers the policy is, read where they land: the constructed
    /// client's timeout and the loop's bound (which §10.1's <c>attempt=n/m</c> renders, so a drift
    /// would be a log that lies).</summary>
    [Fact]
    public async Task TP_RET_09_the_client_times_out_at_12_s_and_the_bound_is_two()
    {
        Assert.Equal(2, TranslationPolicy.MaxAttempts);

        using var client = HttpProviderCore.CreateClient(ProviderIds.GoogleGtx, new FakeHandler(), null);
        Assert.Equal(TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds), client.Timeout);

        // …and the bound the LOG renders is the same number, not a second copy of it.
        var lines = await LinesOf("q1", h => h.Respond(HttpStatusCode.ServiceUnavailable));
        Assert.All(lines, l => Assert.EndsWith("/2", Field(l, "attempt")));
    }

    // =============================================================================================
    //  TP-RET-06 — full jitter
    // =============================================================================================

    /// <summary>
    /// TP-RET-06. The band is <c>[0, BackoffBaseMs &lt;&lt; attempt)</c> and the draw is real: twenty
    /// samples that are all equal would be a fixed spacing wearing a jitter's name, and a fixed
    /// spacing is what makes two instances behind one NAT retry in lockstep. Asserted on the
    /// REQUESTED delay — nothing here waits for one.
    /// </summary>
    [Fact]
    public void TP_RET_06_the_backoff_is_drawn_with_full_jitter_inside_its_band()
    {
        var samples = new List<TimeSpan>();
        for (int i = 0; i < 20; i++) samples.Add(HttpProviderCore.Backoff(0));

        Assert.All(samples, d => Assert.InRange(d.TotalMilliseconds, 0, TranslationPolicy.BackoffBaseMs - 1));
        Assert.True(samples.Distinct().Count() > 1,
            "twenty draws came back identical — that is a fixed spacing, not full jitter");

        // Exponential, not linear: the ceiling doubles per attempt.
        try
        {
            HttpProviderCore.JitterOverride = ceiling => ceiling - 1;   // the top of the band
            Assert.Equal(TranslationPolicy.BackoffBaseMs - 1, (int)HttpProviderCore.Backoff(0).TotalMilliseconds);
            Assert.Equal(2 * TranslationPolicy.BackoffBaseMs - 1, (int)HttpProviderCore.Backoff(1).TotalMilliseconds);
        }
        finally { HttpProviderCore.JitterOverride = null; }
    }

    /// <summary>IS-7 itself: the retry really goes through the injected delay, and it asks for a
    /// value inside the band. If it did not, this suite would be paying ~500 ms of production
    /// back-off per retried case again.</summary>
    [Fact]
    public async Task The_retry_waits_through_the_injectable_delay_and_nothing_sleeps()
    {
        TestBackoffRedirect.Reset();
        var fake = new FakeHandler().Respond(HttpStatusCode.ServiceUnavailable).RespondJson(GoogleOk);

        await new TranslationService(fake).TranslateAsync("привет", "ru", "en");

        var backoffs = TestBackoffRedirect.Delays
            .Where(d => d.TotalMilliseconds < TranslationPolicy.BackoffBaseMs).ToList();
        Assert.NotEmpty(backoffs);
        Assert.All(backoffs, d => Assert.InRange(d.TotalMilliseconds, 0, TranslationPolicy.BackoffBaseMs - 1));
    }

    // =============================================================================================
    //  TP-RET-07 — admission before, outcome after, exactly once
    // =============================================================================================

    /// <summary>
    /// TP-RET-07. The gate is consulted <b>before</b> anything is sent and told the outcome
    /// <b>after</b>, exactly once per logical call — a granted admission that never reports is how
    /// a half-open gate is left holding a probe nobody will ever resolve (R-01 by the back door).
    /// </summary>
    [Fact]
    public async Task TP_RET_07_the_gate_is_consulted_before_the_request_and_told_after_it()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        var fake = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "{}");

        Assert.Null(gate.Snapshot().BlockedUntil);           // nothing reported yet

        await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake, gate).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(1, fake.Requests);
        Assert.Equal(TranslationErrorKind.RateLimited, gate.Snapshot().LastKind);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds),
                     gate.Snapshot().BlockedUntil);
        Assert.Equal(1, gate.Snapshot().Strikes);            // one report, not two
    }

    /// <summary>A success reports a success, and a probe's success closes the gate — which is only
    /// true if the core quoted the probe's own token back (ruling E2-h). Without the echo the report
    /// is a stale in-flight result and the gate stays open for another full window.</summary>
    [Fact]
    public async Task A_granted_probe_reports_with_its_token_and_closes_the_gate()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        gate.ReportFailure(TranslationErrorKind.RateLimited);
        Assert.Equal(GateState.Open, gate.Snapshot().State);

        clock.Advance(TimeSpan.FromSeconds(TranslationPolicy.OpenBaseSeconds + 1));

        var fake = new FakeHandler().RespondJson(GoogleOk);
        Assert.Equal("hello", await new TranslationService(fake, gate).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(1, fake.Requests);                      // the probe, and exactly one of them
        Assert.Equal(GateState.Closed, gate.Snapshot().State);
        Assert.Null(gate.Snapshot().BlockedUntil);
        Assert.Equal(0, gate.Snapshot().Strikes);
    }

    /// <summary>A body that reaches the parser and is not the provider's shape is a
    /// <c>BadResponse</c>, and §5.3 counts those — so the parser has to run <b>inside</b> the
    /// admission. A parse that ran above the core would be a failure the gate never heard about,
    /// and an admission that never reported.</summary>
    [Fact]
    public async Task A_parse_failure_is_reported_to_the_gate_as_a_BadResponse()
    {
        var gate = new ProviderGate(new FakeClock().Read);
        var fake = new FakeHandler().RespondJson("not the provider's shape");

        await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake, gate).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, gate.Snapshot().LastKind);
    }

    // =============================================================================================
    //  The epic's own sentence: a 429 opens the gate and the next call sends nothing
    // =============================================================================================

    /// <summary>
    /// The DoD of Epic 2, end to end (L2). One 429 costs one request and opens the gate; the very
    /// next translation through the same provider issues <b>zero</b> requests and raises carrying
    /// the gate's own <c>RetryAt</c>. That second half is the whole point — everything before it is
    /// bookkeeping.
    /// </summary>
    [Fact]
    public async Task A_429_opens_the_gate_and_the_next_call_sends_nothing()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        var fake = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "{}");
        var google = new TranslationService(fake, gate);

        await Assert.ThrowsAsync<TranslationException>(() => google.TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, fake.Requests);

        var refused = await Assert.ThrowsAsync<TranslationException>(
            () => google.TranslateAsync("пока", "ru", "en"));

        Assert.Equal(1, fake.Requests);                      // NOT two: nothing left the machine
        Assert.Equal(TranslationErrorKind.RateLimited, refused.Kind);
        Assert.Equal(gate.Snapshot().BlockedUntil, refused.RetryAt);
    }

    /// <summary>The same through the registry rather than through an injected gate — I9's identity
    /// is what makes the pause apply to every chain, and the registry is how a provider finds its
    /// gate in production.</summary>
    [Fact]
    public async Task The_pause_is_the_registrys_and_applies_to_a_second_provider_instance()
    {
        var first = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "{}");
        await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(first).TranslateAsync("привет", "ru", "en"));

        var second = new FakeHandler().RespondJson(GoogleOk);
        await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(second).TranslateAsync("пока", "ru", "en"));

        Assert.Equal(0, second.Requests);
        Assert.Equal(GateState.Open, ProviderGates.Snapshot(ProviderIds.GoogleGtx)!.State);
    }

    /// <summary>A DeepL refusal must not pause Google: one gate per id, and the ids are spelled
    /// once (<see cref="ProviderIds"/>) precisely so this cannot go wrong quietly.</summary>
    [Fact]
    public async Task One_providers_pause_never_reaches_another()
    {
        var deepl = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "{}");
        await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", deepl).TranslateAsync("привет", "ru", "en"));

        var google = new FakeHandler().RespondJson(GoogleOk);
        Assert.Equal("hello", await new TranslationService(google).TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, google.Requests);
    }

    // =============================================================================================
    //  §5.4 — the rate ceiling, honoured through the injectable delay
    // =============================================================================================

    /// <summary>
    /// A <c>Wait</c> is honoured rather than raised while it is worth waiting
    /// (<c>GateDecision.WorthWaiting</c>): the bucket holds two tokens, so a third request in a row
    /// is made to wait for one — through IS-7's delay, so this case costs no time at all. The
    /// architecture's rule for a wait that is too long (move on to the next tier) is
    /// <b>E3.S3</b>'s; until the chain exists the core raises instead.
    /// </summary>
    [Fact]
    public async Task A_rate_ceiling_wait_is_honoured_through_the_injectable_delay()
    {
        TestBackoffRedirect.Reset();
        var fake = new FakeHandler().RespondJson(GoogleOk);
        var google = new TranslationService(fake);

        for (int i = 0; i < 3; i++) await google.TranslateAsync("привет", "ru", "en");

        Assert.Equal(3, fake.Requests);                      // nothing was refused
        Assert.Contains(TestBackoffRedirect.Delays,
            d => d > TimeSpan.Zero && d <= TimeSpan.FromMilliseconds(TranslationPolicy.MinSpacingMs));
    }

    // =============================================================================================
    //  §5.5 — Retry-After, parsed here and clamped by the gate
    // =============================================================================================

    /// <summary>Both forms reach the gate <b>unclamped</b> — the clamp is the gate's and is applied
    /// once (ruling E2-f; two clamps would double-apply the floor) — and the value overrides the
    /// window the gate would have computed.</summary>
    [Theory]
    [InlineData(true)]      // delta-seconds
    [InlineData(false)]     // an HTTP date
    public async Task Retry_After_reaches_the_gate_in_both_forms_and_lengthens_the_window(bool delta)
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        var when = clock.Now + TimeSpan.FromMinutes(5);

        var fake = new FakeHandler()
            .Respond(HttpStatusCode.TooManyRequests, "{}")
            .WithHeader("Retry-After", delta ? "300" : when.UtcDateTime.ToString("R"));

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake, gate).TranslateAsync("привет", "ru", "en"));

        // Longer than the 60 s first-strike window, which is what "it overrides" means.
        Assert.Equal(when, gate.Snapshot().BlockedUntil);
        Assert.Equal(when, ex.RetryAt);
    }

    /// <summary>…and it is logged verbatim: <c>RequestLog</c> renders the raw header, so the core
    /// must not normalise it on the way past.</summary>
    [Fact]
    public async Task Retry_After_is_logged_exactly_as_the_server_wrote_it()
    {
        var lines = await LinesOf("q2", h => h
            .Respond(HttpStatusCode.TooManyRequests, "{}")
            .WithHeader("Retry-After", "42"));

        Assert.Equal("42", Field(Assert.Single(lines), "retry-after"));
    }

    // =============================================================================================
    //  TP-RET-08 / §10.1 — one cid per logical call, and the key that must not be in it
    // =============================================================================================

    /// <summary>TP-RET-08 — both attempts of one logical call share one <c>cid</c>, so one call
    /// reads as one event however many lines it left.</summary>
    [Fact]
    public async Task TP_RET_08_both_attempts_of_one_call_share_one_correlation_id()
    {
        var lines = await LinesOf("q3", h => h.Respond(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(2, lines.Count);
        Assert.Single(lines.Select(l => Field(l, "cid")).Distinct());
        Assert.Equal(new[] { "1/2", "2/2" }, lines.Select(l => Field(l, "attempt")).ToArray());
    }

    /// <summary>DeepL emits §10.1 lines for the first time — E1.S5 left "routing it through the same
    /// helper" to this story, and it is what makes the next keyed incident reportable.</summary>
    [Fact]
    public async Task DeepL_now_emits_its_own_request_lines()
    {
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            await Assert.ThrowsAsync<TranslationException>(
                () => new DeepLTranslator("k:fx", new FakeHandler().Respond(HttpStatusCode.Forbidden, "{}"))
                          .TranslateAsync("привет", "ru", "q4"));

            var line = Assert.Single(LinesFor(dir, "ru->q4"));
            Assert.Equal("deepl", Field(line, "provider"));
            Assert.Equal("api-free.deepl.com/v2/translate", Field(line, "ep"));
            Assert.Equal("403", Field(line, "status"));
        }
        finally { Restore(previous, dir); }
    }

    /// <summary>
    /// I11's new surface, and the adversarial case the story asks for by name: an HTML error page
    /// that quotes the key with <b>no <c>auth_key=</c> in front of it</b>, which is exactly what
    /// <c>RequestLog</c>'s marker cut cannot see — it stops at a parameter NAME and cannot know a
    /// VALUE. The core does know it, so the key is scrubbed before the body reaches anything.
    /// </summary>
    [Fact]
    public async Task A_key_echoed_by_an_error_page_never_reaches_the_log()
    {
        const string key = "KIZOTIS-DEEPL-KEY-0000-1111:fx";
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            // No parameter name anywhere near it — just prose quoting the credential.
            var page = "<html><body><p>The credential " + key + " was rejected by this proxy.</p></body></html>";
            var fake = new FakeHandler().Respond(HttpStatusCode.Forbidden, page, "text/html");

            await Assert.ThrowsAsync<TranslationException>(
                () => new DeepLTranslator(key, fake).TranslateAsync("привет", "ru", "q5"));

            var line = Assert.Single(LinesFor(dir, "ru->q5"));
            var file = File.ReadAllText(Path.Combine(dir, "log.txt"));

            Assert.Contains("rejected by this proxy", line);      // the page's prose still survives
            Assert.DoesNotContain(key, file, StringComparison.Ordinal);
            Assert.DoesNotContain("KIZOTIS-DEEPL-KEY", file, StringComparison.Ordinal);
            Assert.DoesNotContain(key, Logging.ReadRecent(), StringComparison.Ordinal);
        }
        finally { Restore(previous, dir); }
    }

    /// <summary>The URL-encoded form too: a page that echoes the query it refused carries the key
    /// percent-escaped, and a scrub that only knew the literal would miss it.</summary>
    [Fact]
    public void The_url_encoded_form_of_a_key_is_scrubbed_as_well()
    {
        const string key = "abc def/ghi:fx";
        var body = "<p>bad " + Uri.EscapeDataString(key) + " here</p>";

        var scrubbed = HttpProviderCore.Redact(body, key);

        Assert.DoesNotContain("abc%20def", scrubbed);
        Assert.DoesNotContain(key, scrubbed);
        Assert.Contains(HttpProviderCore.Redacted, scrubbed);
        // A provider with no key changes nothing — the keyless endpoints must not pay for this.
        Assert.Equal(body, HttpProviderCore.Redact(body, null));
    }

    // =============================================================================================
    //  I3 and §10.1's ipv= field
    // =============================================================================================

    /// <summary>A genuine cancel costs zero requests and leaves as an
    /// <see cref="OperationCanceledException"/> — never as a <c>TranslationException</c>, and never
    /// as a gate report.</summary>
    [Fact]
    public async Task A_cancelled_call_sends_nothing_and_never_becomes_a_TranslationException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var gate = new ProviderGate(new FakeClock().Read);
        var fake = new FakeHandler().RespondJson(GoogleOk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TranslationService(fake, gate).TranslateAsync("привет", "ru", "en", cts.Token));

        Assert.Equal(0, fake.Requests);
        Assert.Null(gate.Snapshot().LastKind);
    }

    /// <summary>§10.1's own instruction for a value nobody can know: log <c>?</c> rather than guess.
    /// A test handler has no connect callback, so that is what a suite sees — the real value comes
    /// off the production handler's <c>ConnectCallback</c>, which cannot run offline.</summary>
    [Fact]
    public async Task The_address_family_is_a_question_mark_under_a_test_handler()
    {
        var lines = await LinesOf("q6", h => h.Respond(HttpStatusCode.BadRequest, "nope"));

        Assert.Equal(RequestLog.UnknownAddressFamily, Field(Assert.Single(lines), "ipv"));
    }

    /// <summary>
    /// The <c>ConfigureAwait(false)</c> scan E1.S5 recorded and this story owes. Callers await from
    /// UI-thread methods (<c>MainWindow.Translate.cs:37,94</c>), so without it <c>LogWriter</c>'s
    /// synchronous <c>File.AppendAllText</c> — and E2.S4's lazy state load — run on the dispatcher.
    /// A scan rather than a behaviour test because there is no headless way to observe the
    /// difference, and the one thing that can go wrong is someone adding an await without it.
    /// </summary>
    [Fact]
    public void Every_await_on_the_request_path_configures_away_the_context()
    {
        foreach (var file in new[] { "HttpProviderCore.cs", "TranslationService.cs", "DeepLTranslator.cs" })
        {
            var offenders = Statements(SourceOf(file))
                .Where(s => Regex.IsMatch(s, @"(^|[^\w.])await\s") && !s.Contains(".ConfigureAwait(false)"))
                .ToList();

            Assert.True(offenders.Count == 0,
                $"{file} awaits without ConfigureAwait(false): {string.Join(" | ", offenders)}");
        }

        // Non-vacuity: the scan must have found the awaits it is looking at.
        Assert.Contains(Statements(SourceOf("HttpProviderCore.cs")),
            s => s.Contains("await") && s.Contains("SendAsync"));
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>Statements of an app source file, comments stripped and continuation lines joined —
    /// the shape a scan needs, because an await and its <c>ConfigureAwait</c> often sit on two
    /// lines.</summary>
    private static List<string> Statements(string path)
    {
        var code = string.Join("\n", File.ReadAllLines(path)
            .Select(l => { var cut = l.IndexOf("//", StringComparison.Ordinal); return cut >= 0 ? l[..cut] : l; }));
        return code.Split(';').Select(s => s.Replace("\n", " ").Trim()).Where(s => s.Length > 0).ToList();
    }

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

    /// <summary>One failing Google call with the log pointed at a fresh directory; hands back this
    /// call's own lines, found by its unique language pair (other classes' deliberate failures land
    /// in whatever directory is current).</summary>
    private static async Task<List<string>> LinesOf(string target, Action<FakeHandler> script)
    {
        var previous = Logging.DirectoryOverride;
        var dir = TempDir();
        try
        {
            Logging.DirectoryOverride = dir;
            RequestLog.ResetSuppression();

            var handler = new FakeHandler();
            script(handler);
            try { await new TranslationService(handler).TranslateAsync("привет", "ru", target); }
            catch (TranslationException) { /* the failure is the point */ }

            return LinesFor(dir, $"ru->{target}");
        }
        finally { Restore(previous, dir); }
    }

    private static List<string> LinesFor(string dir, string pair)
    {
        var path = Path.Combine(dir, "log.txt");
        if (!File.Exists(path)) return new List<string>();
        return File.ReadAllLines(path)
                   .Select(l => l.Contains("] tr ") ? l[(l.IndexOf("] tr ", StringComparison.Ordinal) + 2)..] : l)
                   .Where(l => l.StartsWith("tr ", StringComparison.Ordinal) && l.Contains("dir=" + pair))
                   .ToList();
    }

    private static string Field(string line, string key)
    {
        var m = Regex.Match(line, $@"(?:^|\s){Regex.Escape(key)}=([^\s]*)");
        Assert.True(m.Success, $"field '{key}' is missing from: {line}");
        return m.Groups[1].Value;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PWRUHelperCore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Restore(string? previous, string dir)
    {
        Logging.DirectoryOverride = previous;
        try { Directory.Delete(dir, true); } catch { /* the OS will get it */ }
    }
}

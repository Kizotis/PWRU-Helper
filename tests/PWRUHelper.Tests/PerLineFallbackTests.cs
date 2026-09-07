using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E3.S8 — the shared per-line loop: <b>TP-CHN-10</b> (a small mismatch still fans out),
/// <b>TP-CHN-11</b> (the DoD: a big one does not), the <c>&lt;=</c>/<c>&gt;</c> boundary pair,
/// <b>TP-CHN-12</b> (the latch keeps its shape), and ruling <b>E3-f</b>'s throw as widened by
/// <b>E3-g</b> (E3.S7): a tier that translated NO line throws its last failure, whatever kind it is.
///
/// <para>Everything is driven through the REAL providers on a <see cref="FakeHandler"/> (IS-10/
/// IS-11): the assertion that matters is the handler's <b>request count</b>, because it is the only
/// one an implementation that merely <i>intends</i> to skip cannot satisfy. Two repo traps make that
/// necessary — the handler repeats its last scripted step for ever (so a per-line phase always gets
/// an answer, scripted or not), and the §5.4 ceiling paces the fan-out (so IS-7 credits every wait
/// to the virtual clock and nothing sleeps, CI-3).</para>
///
/// <para><c>[Collection("Gates")]</c> because every case here drives a provider through the
/// process-global registry (IS-5).</para>
/// </summary>
[Collection("Gates")]
public class PerLineFallbackTests : GatesTestBase
{
    // One segment, whatever it was asked to translate: as a BATCH answer for N > 1 lines it is a
    // count mismatch (1 part for N lines), and as a per-line answer it is a success. That single
    // property is what lets one scripted step drive both phases of a fallback.
    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";
    private const string DictOk = """["hello"]""";

    private static string[] Lines(int n) => Enumerable.Range(0, n).Select(i => $"line{i}").ToArray();

    // =============================================================================================
    //  AC 2 / AC 3 — the cap, on the batch → per-line fallback
    // =============================================================================================

    /// <summary>
    /// <b>TP-CHN-11, the DoD case</b> (as ruled by the architect for E3.S8: the cap bounds the
    /// fan-out rather than refusing the group). A 20-line batch comes back with the wrong count, and
    /// the fallback below it costs <b>exactly <see cref="TranslationPolicy.PerLineCap"/> requests</b>
    /// — not twenty. The lines past the cap read as skipped, cost nothing at all, and are announced
    /// once in the log.
    ///
    /// <para>Asserted on the handler's count and on the WARN line, never on the implementation's own
    /// bookkeeping. The measured amplifier this closes: <c>analyse…</c> S6/A11, a mismatch on a
    /// 14-line LIVE tick costing up to 30 requests inside one tick.</para>
    /// </summary>
    [Fact]
    public async Task TP_CHN_11_a_twenty_line_mismatch_costs_the_cap_and_not_twenty_requests()
    {
        var previous = Logging.DirectoryOverride;
        var dir = Directory.CreateTempSubdirectory("pwru-caplog-").FullName;
        try
        {
            Logging.DirectoryOverride = dir;
            var fake = new FakeHandler().RespondJson(GoogleOk);   // batch mismatch, then every line

            var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(20), "ru", "en");

            // The batch plus the cap, and nothing more. Twelve lines were never asked.
            Assert.Equal(1 + TranslationPolicy.PerLineCap, fake.Requests);
            Assert.Equal(20, outp.Count);                                   // never padded, never short
            Assert.All(outp.Take(TranslationPolicy.PerLineCap), l => Assert.Equal("hello", l));
            Assert.All(outp.Skip(TranslationPolicy.PerLineCap),
                l => Assert.Equal(PerLineFallback.SkippedMessage, l));

            // One line, counts only, no user text (I11). Read through the guard both established
            // readers of this file use (`GateLoggingTests.Lines`, `HttpProviderCoreTests.LinesFor`):
            // `Logging.DirectoryOverride` is a process-global static and `LogFileCollection` swaps
            // it too, so a missing file must fail as "no WARN was found" and not as a
            // FileNotFoundException nobody can read (E3.S8 review).
            var log = Path.Combine(dir, "log.txt");
            var capped = (File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>())
                             .Where(l => l.Contains("capped at")).ToList();
            var line = Assert.Single(capped);
            Assert.Contains(ProviderIds.GoogleGtx, line);
            Assert.Contains("of 20 lines", line);
            Assert.DoesNotContain("line0", line);
        }
        finally
        {
            Logging.DirectoryOverride = previous;
            try { Directory.Delete(dir, recursive: true); } catch { /* the case already made its point */ }
        }
    }

    /// <summary>
    /// <b>TP-CHN-10</b> — the case that proves the cap did not simply break the fallback. A mismatch
    /// on five lines (≤ the cap) fans out exactly five times, and line 4's failure costs line 4 and
    /// no other: the successes around it are kept.
    /// </summary>
    [Fact]
    public async Task TP_CHN_10_a_five_line_mismatch_still_fans_out_and_keeps_the_successes()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)      // batch: one part for five lines — mismatch
            .RespondJson(GoogleOk)      // line 1
            .RespondJson(GoogleOk)      // line 2
            .RespondJson(GoogleOk)      // line 3
            .RespondJson("not json")    // line 4 — a 200 that is not the provider's shape
            .RespondJson(GoogleOk);     // line 5 — reached because a BadResponse does not latch

        var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(5), "ru", "en");

        Assert.Equal(6, fake.Requests);                     // the batch plus five
        Assert.Equal(new[] { "hello", "hello", "hello" }, outp.Take(3));
        Assert.StartsWith("(translation failed: ", outp[3]);
        Assert.Equal("hello", outp[4]);
    }

    /// <summary>
    /// The boundary, which is where an off-by-one on a <c>&gt;=</c> would live and the cheapest
    /// defect here to pin. At the cap every line is asked; one past it, exactly one line is not.
    /// Written as a pair against <see cref="TranslationPolicy.PerLineCap"/> rather than against the
    /// literal 8, so E2.S7's tuning commit moves the constant and not this case (U9).
    /// </summary>
    [Theory]
    [InlineData(0)]     // exactly at the cap — all of them are asked
    [InlineData(1)]     // one past it — exactly one line is refused
    public async Task The_cap_is_inclusive_at_PerLineCap_and_exclusive_one_past_it(int over)
    {
        int count = TranslationPolicy.PerLineCap + over;
        var fake = new FakeHandler().RespondJson(GoogleOk);

        var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(count), "ru", "en");

        Assert.Equal(1 + TranslationPolicy.PerLineCap, fake.Requests);
        Assert.Equal(count, outp.Count);
        Assert.Equal(over, outp.Count(l => l == PerLineFallback.SkippedMessage));
    }

    /// <summary>
    /// <b>The cap counts LINES ATTEMPTED, not requests issued</b> — the half of the architect's
    /// Phase-4 instruction a later reader is most likely to take for an off-by-something, so it is
    /// pinned and not only commented (review ruling (a)). A line too long for one query is chunked
    /// by <see cref="TextChunker"/> and costs THREE requests inside one <c>translateOne</c> call,
    /// and still spends exactly one of the cap's eight: twelve lines cost 8 × 3 requests, and four
    /// lines are never asked.
    ///
    /// <para>Driven straight at the shared loop rather than through a provider, because no provider
    /// can produce this shape: a group whose <c>\n</c>-join fits <c>MaxQueryBytes</c> cannot contain
    /// a line that does not, so the only way a capped fan-out meets a chunked line is a future
    /// provider with a different budget — which is exactly the reader this case is written for.</para>
    /// </summary>
    [Fact]
    public async Task The_cap_counts_lines_attempted_even_when_one_line_costs_several_requests()
    {
        // 2000 Cyrillic characters = 4000 UTF-8 bytes against a 1500-byte budget: three chunks,
        // three requests, one line.
        var line = new string('я', 2000);
        var fake = new FakeHandler().RespondJson(GoogleOk);
        var gtx = new GoogleGtxTranslator(fake);

        var outp = await PerLineFallback.RunAsync(
            Enumerable.Repeat(line, 12).ToArray(),
            (l, token) => gtx.TranslateAsync(l, "ru", "en", token),
            ProviderIds.GoogleGtx, afterFailedBatch: true, CancellationToken.None);

        // The stitched answer is what proves the chunking really happened rather than being assumed.
        Assert.All(outp.Take(TranslationPolicy.PerLineCap), l => Assert.Equal("hellohellohello", l));
        Assert.Equal(12 - TranslationPolicy.PerLineCap,
                     outp.Count(l => l == PerLineFallback.SkippedMessage));
        // A cap on REQUESTS would have stopped after the third line. This one stopped after the
        // eighth and let the chunker spend what it had to.
        Assert.Equal(3 * TranslationPolicy.PerLineCap, fake.Requests);
    }

    /// <summary>
    /// <b>Ruling E3-e, the half that is easy to get wrong.</b> The cap bounds the fallback after a
    /// FAILED BATCH and never a provider's primary per-line path: <c>GoogleDictTranslator</c> ships
    /// one request per line under OQ-A, whose answer explicitly accepts "≈2× the LIVE request
    /// volume", so a 14-line tick costs fourteen requests and the cap does not fire. That path is
    /// bounded by the §5.4 rate ceiling and by the gate instead — and this case is what stops a
    /// later reader from "fixing" the asymmetry.
    /// </summary>
    [Fact]
    public async Task The_cap_does_not_touch_a_providers_primary_per_line_path()
    {
        Assert.False(TranslationPolicy.GoogleDictBatchJoinEnabled, "the join is off: this IS the primary path");
        Assert.True(14 > TranslationPolicy.PerLineCap, "the case is only meaningful above the cap");

        var fake = new FakeHandler().RespondJson(DictOk);

        var outp = await new GoogleDictTranslator(fake).TranslateLinesAsync(Lines(14), "ru", "en");

        Assert.Equal(14, fake.Requests);
        Assert.All(outp, l => Assert.Equal("hello", l));
    }

    /// <summary>The other unarmed path, and the reason the flag is <c>batchFailed</c> and not
    /// "this is a fallback": a group too long for one query never HAD a batch to fail, so its
    /// per-line run is a primary strategy too. Capping it would silently drop lines from a big LIVE
    /// tick that nothing had gone wrong with.</summary>
    [Fact]
    public async Task A_group_too_long_to_batch_is_not_a_failed_batch_and_is_not_capped()
    {
        // Ten lines of 200 Cyrillic characters: 400 bytes each, so the \n-joined query is far over
        // MaxQueryBytes and the batch is never attempted. Each line on its own still fits.
        var lines = Enumerable.Range(0, 10).Select(_ => new string('я', 200)).ToArray();
        var fake = new FakeHandler().RespondJson(GoogleOk);

        var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(lines, "ru", "en");

        Assert.Equal(10, fake.Requests);                    // no batch request, and no cap
        Assert.All(outp, l => Assert.Equal("hello", l));
    }

    /// <summary>
    /// <b>A blank line costs no request, so it may not spend a slot of the cap</b> (E3.S8 review).
    /// Eight blank bodies in front of eight real ones: every real line is still asked. Before the
    /// guard the blanks consumed the whole budget — <c>attempted++</c> ran before a call that
    /// returns <c>""</c> without touching the endpoint — so a tick like this one issued exactly ONE
    /// request and rendered all eight real messages as "(skipped — rate-limited…)" while nothing
    /// had rate-limited anything.
    ///
    /// <para>Not a contrived shape: <c>SplitSpeakerStrict</c> gives a truncated OCR row ("Nick:"
    /// with nothing after the colon) an empty body, LIVE never filters those out, and a blank line
    /// is never cacheable — so it is forwarded on every tick it appears in.</para>
    /// </summary>
    [Fact]
    public async Task A_blank_line_does_not_spend_a_slot_of_the_cap()
    {
        var lines = Enumerable.Repeat("", TranslationPolicy.PerLineCap)
                              .Concat(Lines(TranslationPolicy.PerLineCap)).ToArray();
        var fake = new FakeHandler().RespondJson(GoogleOk);

        var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(lines, "ru", "en");

        Assert.Equal(1 + TranslationPolicy.PerLineCap, fake.Requests);
        Assert.All(outp.Take(TranslationPolicy.PerLineCap), l => Assert.Equal("", l));
        Assert.All(outp.Skip(TranslationPolicy.PerLineCap), l => Assert.Equal("hello", l));
        Assert.DoesNotContain(PerLineFallback.SkippedMessage, outp);
    }

    // =============================================================================================
    //  Rulings E3-f / E3-g — a loop that translated NOTHING throws
    // =============================================================================================

    /// <summary>
    /// <b>E3-f.</b> A list of placeholders reads to <see cref="ChainTranslator"/> as a success — it
    /// receives a list and cannot see that every element of it is an apology — so a tier refused on
    /// every line would end the chain with a healthy tier untried. When nothing got through and
    /// every failure was a refusal, the last one is thrown instead, <c>RetryAt</c> and all.
    /// </summary>
    [Fact]
    public async Task E3_f_a_loop_rate_limited_on_every_line_throws_instead_of_returning_placeholders()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)                                          // batch: mismatch
            .Respond(HttpStatusCode.TooManyRequests, "").WithHeader("Retry-After", "30");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(4), "ru", "en"));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.NotNull(ex.RetryAt);                     // the countdown E7 renders survives the throw
        Assert.False(ex.NotSent);                       // this tier really did speak to the endpoint
        // The batch plus ONE line: the latch stopped the loop, and the throw replaced three
        // placeholder rows in the feed with one sentence.
        Assert.Equal(2, fake.Requests);
    }

    /// <summary>
    /// <b>E3-f, the <c>NotSent</c> half.</b> A gate that is already closed refuses every line at
    /// admission, costing no request and producing no translation. That is a tier that never spoke,
    /// not a batch of failures — so the refusal is rethrown <b>as it is</b>, with <c>NotSent</c>
    /// intact, which is what lets the chain record a skip rather than a failure (ruling E3-b).
    /// </summary>
    [Fact]
    public async Task E3_f_a_loop_refused_at_admission_on_every_line_throws_the_NotSent_refusal()
    {
        var gate = ProviderGates.For(ProviderIds.GoogleDict);
        gate.ReportFailure(TranslationErrorKind.RateLimited, gate.Now() + TimeSpan.FromMinutes(5));
        var fake = new FakeHandler().RespondJson(DictOk);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(fake).TranslateLinesAsync(Lines(3), "ru", "en"));

        Assert.True(ex.NotSent, "a refused tier must stay distinguishable from one that tried");
        Assert.Equal(0, fake.Requests);
    }

    /// <summary>
    /// <b>E3-g, and it is the exact case E3-f used to exclude.</b> Under E3-f a failure the next
    /// tier "cannot fix" — an unparseable body, a timeout — came back as placeholders, on the
    /// reasoning that they were this provider's answer about those lines. E3-g overturns that when
    /// there is nothing else in the list: three soft failures on three lines is a list with no
    /// translation in it, which <see cref="ChainTranslator"/> reads as a success. It now throws the
    /// last failure, so the next tier is asked.
    ///
    /// <para>The thrown failure is the provider's own — <c>BadResponse</c>, and <b>not</b>
    /// <c>NotSent</c>: this tier really did speak to the endpoint, and the chain records it as a
    /// failure rather than a skip (ruling E3-b).</para>
    /// </summary>
    [Fact]
    public async Task E3_g_soft_failures_on_every_line_now_throw_instead_of_returning_placeholders()
    {
        var fake = new FakeHandler().RespondJson("not json");    // every line: a 200 of the wrong shape

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleDictTranslator(fake).TranslateLinesAsync(Lines(3), "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        Assert.False(ex.NotSent, "the endpoint really answered — this is a failure, not a skip");
        Assert.Equal(3, fake.Requests);          // every line was still tried before the throw
    }

    /// <summary>The other side of E3-g, and the reason it is "nothing was translated" rather than
    /// "anything failed": one good line is a PARTIAL failure, and a translation the player can read
    /// is never thrown away. Two soft failures around one success still come back as a list.</summary>
    [Fact]
    public async Task E3_g_one_translated_line_is_enough_to_keep_the_placeholders()
    {
        var fake = new FakeHandler()
            .RespondJson("not json")        // line 1 fails
            .RespondJson(DictOk)            // line 2 translates
            .RespondJson("not json");       // line 3 fails

        var outp = await new GoogleDictTranslator(fake).TranslateLinesAsync(Lines(3), "ru", "en");

        Assert.StartsWith("(translation failed: ", outp[0]);
        Assert.Equal("hello", outp[1]);
        Assert.StartsWith("(translation failed: ", outp[2]);
    }

    /// <summary>
    /// <b>The hole E3-g was ruled for, now closed</b> (found by E3.S8's review, recorded for the
    /// architect rather than changed inside a review). A soft failure on line 1 is a §5.3
    /// <c>SoftCooldown</c>: the gate closes for 5 s, so lines 2-5 are refused at admission with
    /// <c>NotSent</c> and cost nothing. Under E3-f, line 1's timeout counted as "something other
    /// than a gate reason happened", the throw stayed off, and the loop returned five placeholders
    /// of which <b>none was a translation</b> — a list <see cref="ChainTranslator"/> reads as a
    /// success, leaving the healthy tier below untried. One timeout was enough to reach it.
    ///
    /// <para>E3-g's predicate is "was anything translated", so this throws. <b>WHICH failure it
    /// throws is ruling E5-e</b> (E5.S3): line 1's <c>Timeout</c>, the one that was actually SENT,
    /// and not line 5's refusal at admission. Throwing the refusal was how a tick that had burned a
    /// request and a timeout came to report as one that had cost nothing — the chain read the tier as
    /// skipped rather than tried, ended <c>AllProvidersPaused</c>, and
    /// <c>LiveTickPolicy.Classify</c> called the whole tick <c>Refused</c>, so the auto-stop never
    /// advanced and no next tier was tried.</para>
    /// </summary>
    [Fact]
    public async Task E3_g_a_soft_failure_that_closes_the_gate_now_throws_instead_of_reading_as_a_success()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)   // batch: one part for five lines — mismatch
            .TimesOut();             // line 1: a Timeout, i.e. an immediate 5 s SoftCooldown

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(5), "ru", "en"));

        Assert.Equal(TranslationErrorKind.Timeout, ex.Kind);
        Assert.False(ex.NotSent, "E5-e: the SENT failure is the informative one, not the refusals after it");
        // The batch, then line 1's attempt and its single §5.6 retry (MaxAttempts = 2). Lines 2-5
        // were refused at admission behind the cooldown — no request, and no translation either.
        Assert.Equal(1 + TranslationPolicy.MaxAttempts, fake.Requests);
    }

    /// <summary>
    /// The same shape, end to end, which is what E3-g is FOR: the tier above hands the chain a
    /// throw instead of five apologies, so the healthy tier below is actually asked — and the
    /// player gets translations rather than a feed of "(translation failed: …)" rows.
    /// </summary>
    [Fact]
    public async Task E3_g_end_to_end_a_tier_that_translated_nothing_lets_the_chain_use_the_next_one()
    {
        var gtx = new FakeHandler().RespondJson(GoogleOk).TimesOut();
        var dict = new FakeHandler().RespondJson(DictOk);

        var chain = ChainTranslator.Of(
            (ProviderIds.GoogleGtx, new GoogleGtxTranslator(gtx)),
            (ProviderIds.GoogleDict, new GoogleDictTranslator(dict)));

        var outp = await chain.TranslateLinesAsync(Lines(5), "ru", "en");

        Assert.Equal(Enumerable.Repeat("hello", 5), outp);
        Assert.Equal(5, dict.Requests);
        Assert.Equal(ProviderIds.GoogleDict, chain.LastOutcome!.ProviderId);
    }

    /// <summary>
    /// <b>E3-f's off switch, and the bug it had</b> (E3.S8 review). The ruling turns itself off as
    /// soon as one line produced something — which is right, because a translation the player can
    /// read must not be thrown away. A BLANK line produces something too: both providers answer
    /// <c>""</c> for one <i>before</i> they reach the gate, at the cost of no request whatsoever. So
    /// one truncated OCR row was enough to convince the loop that a tier refused on every real line
    /// had answered: it returned a list, <see cref="ChainTranslator"/> read the list as a success,
    /// and the healthy tier below was never asked — the precise failure E3-f exists to prevent,
    /// re-entered through the one input LIVE produces most often.
    /// </summary>
    [Fact]
    public async Task E3_f_a_blank_line_is_not_evidence_that_the_tier_answered()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)                          // batch: one part for three lines
            .Respond(HttpStatusCode.TooManyRequests, "");   // the first REAL line, and the latch

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(fake).TranslateLinesAsync(
                new[] { "", "раз", "два" }, "ru", "en"));

        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        // The batch and one refused line: the blank cost nothing, and proved nothing.
        Assert.Equal(2, fake.Requests);
    }

    /// <summary>
    /// <b>E3-g's other boundary: "nothing was translated" is not "something went wrong".</b> A group
    /// of nothing but blanks — the shape a truncated OCR row produces, and the one the guard above
    /// answers with <c>""</c> before any provider is asked — translates no line AND records no
    /// failure, so the predicate has nothing to rethrow and must not invent one. It returns the
    /// blanks, and the chain reading that as a success is the truth: nothing was asked of the
    /// provider, and nothing failed. An empty group is the same case with no rows at all.
    ///
    /// <para>Driven straight at the loop, with a <c>translateOne</c> that fails the case if it is
    /// ever called — no provider may be reached by a line the guard is supposed to answer.</para>
    /// </summary>
    [Fact]
    public async Task E3_g_a_blank_only_batch_returns_blanks_and_throws_nothing()
    {
        Task<string> NeverCalled(string line, CancellationToken token)
            => throw new InvalidOperationException("a blank line must never reach a provider");

        var outp = await PerLineFallback.RunAsync(new[] { "", "   ", "\t" }, NeverCalled,
            ProviderIds.GoogleGtx, afterFailedBatch: true, CancellationToken.None);
        Assert.Equal(new[] { "", "", "" }, outp);

        Assert.Empty(await PerLineFallback.RunAsync(Array.Empty<string>(), NeverCalled,
            ProviderIds.GoogleGtx, afterFailedBatch: true, CancellationToken.None));
    }

    /// <summary>The partial case, which is the common one: two lines fail softly, one succeeds, and
    /// nothing is thrown — throwing here would discard a translation the user can read.</summary>
    [Fact]
    public async Task A_partial_failure_returns_placeholders_and_never_throws()
    {
        var fake = new FakeHandler()
            .RespondJson(DictOk)            // line 1 translates
            .RespondJson("not json")        // line 2 fails
            .RespondJson("not json");       // line 3 fails

        var outp = await new GoogleDictTranslator(fake).TranslateLinesAsync(Lines(3), "ru", "en");

        Assert.Equal("hello", outp[0]);
        Assert.StartsWith("(translation failed: ", outp[1]);
        Assert.StartsWith("(translation failed: ", outp[2]);
    }

    /// <summary>
    /// End to end, which is what E3-f is FOR: tier 1 is rate-limited on every line of a batch it
    /// could not split, and the chain moves to tier 2 instead of handing the player four apologies.
    /// Before the ruling this returned a list, the chain read the list as a success, and tier 2 was
    /// never asked — asserted here on tier 2's own request count.
    /// </summary>
    [Fact]
    public async Task E3_f_end_to_end_a_fully_rate_limited_tier_lets_the_chain_use_the_next_one()
    {
        var gtx = new FakeHandler()
            .RespondJson(GoogleOk)                                  // batch: mismatch
            .Respond(HttpStatusCode.TooManyRequests, "");           // then every line
        var dict = new FakeHandler().RespondJson(DictOk);

        var chain = ChainTranslator.Of(
            (ProviderIds.GoogleGtx, new GoogleGtxTranslator(gtx)),
            (ProviderIds.GoogleDict, new GoogleDictTranslator(dict)));

        var outp = await chain.TranslateLinesAsync(Lines(4), "ru", "en");

        Assert.Equal(new[] { "hello", "hello", "hello", "hello" }, outp);
        Assert.Equal(4, dict.Requests);                             // tier 2 was really used
        Assert.Equal(ProviderIds.GoogleDict, chain.LastOutcome!.ProviderId);
    }

    // =============================================================================================
    //  I16 / I3 / I4 — what the extraction had to carry across unchanged
    // =============================================================================================

    /// <summary>
    /// <b>TP-CHN-12</b> — the latch keeps its shape through the extraction (I16). Line 3 is refused,
    /// lines 4 and 5 cost no request and say they were skipped, and the two translations above the
    /// refusal are kept. The successes are what make this a partial failure, so E3-f does not fire.
    /// </summary>
    [Fact]
    public async Task TP_CHN_12_a_refusal_mid_loop_still_latches_and_the_successes_survive()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)                          // batch: mismatch
            .RespondJson(GoogleOk)                          // line 1
            .RespondJson(GoogleOk)                          // line 2
            .Respond(HttpStatusCode.TooManyRequests, "");   // line 3 — the refusal

        var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(5), "ru", "en");

        Assert.Equal(new[]
        {
            "hello", "hello",
            PerLineFallback.RateLimitedMessage,
            PerLineFallback.SkippedMessage,
            PerLineFallback.SkippedMessage,
        }, outp);
        Assert.Equal(4, fake.Requests);
    }

    /// <summary>
    /// <b>I3, pinned by mutation.</b> A <see cref="System.Net.Http.HttpClient"/> timeout is an
    /// <c>OperationCanceledException</c> whose token is NOT cancelled: if the loop's catch lost its
    /// <c>when (ct.IsCancellationRequested)</c> filter, this case would throw instead of returning,
    /// and line 1's translation — already paid for — would be thrown away. That is the bug that cost
    /// this project three releases, and moving the catch into a shared file is exactly the operation
    /// that could reopen it.
    /// </summary>
    [Fact]
    public async Task I3_a_timeout_mid_loop_is_not_a_cancel_and_the_earlier_lines_survive()
    {
        var fake = new FakeHandler()
            .RespondJson(GoogleOk)   // batch: mismatch
            .RespondJson(GoogleOk)   // line 1 translates
            .TimesOut();             // line 2 times out (the step repeats)

        var outp = await new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(2), "ru", "en");

        Assert.Equal(new[] { "hello", "(translation failed: the request timed out)" }, outp);
    }

    /// <summary>I3's other side: a GENUINE cancel mid-loop propagates untouched and stops the loop
    /// where it stands — the lines below it cost nothing.</summary>
    [Fact]
    public async Task I3_a_real_cancel_mid_loop_propagates_and_stops_the_fan_out()
    {
        var cts = new CancellationTokenSource();
        var fake = new FakeHandler().RespondJson(GoogleOk);

        // Cancelled before the call: the batch's own guard is what fires, and it is an OCE.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GoogleGtxTranslator(fake).TranslateLinesAsync(Lines(5), "ru", "en", cts.Token));
        Assert.Equal(0, fake.Requests);
    }

    /// <summary>I3, the one-line path: <c>SafeOneAsync</c> has its own filtered catch, and a group of
    /// one takes neither the batch nor the loop.</summary>
    [Fact]
    public async Task I3_a_real_cancel_propagates_from_the_one_line_path_too()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeHandler().RespondJson(DictOk);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GoogleDictTranslator(fake).TranslateLinesAsync(new[] { "привет" }, "ru", "en", cts.Token));
        Assert.Equal(0, fake.Requests);
    }

    /// <summary>
    /// <b>I4 / I5 — the cap's placeholders are FAILURES, and the cache must refuse them.</b> All
    /// three strings start with <c>(</c>, which is the whole of <c>CachingTranslator.IsCacheable</c>'s
    /// rule; a reworded placeholder that lost the parenthesis would store "(skipped — …)" as if it
    /// were a translation of the line nobody translated. Proved through the real cache rather than
    /// by re-reading the rule: the second call asks the backend again.
    /// </summary>
    [Fact]
    public async Task I4_the_placeholders_start_with_a_parenthesis_and_are_never_cached()
    {
        foreach (var s in new[] { PerLineFallback.SkippedMessage, PerLineFallback.RateLimitedMessage,
                                  PerLineFallback.Failed("whatever") })
            Assert.StartsWith("(", s);

        var fake = new FakeHandler().RespondJson(GoogleOk);
        var cache = new CachingTranslator(new GoogleGtxTranslator(fake));

        var first = await cache.TranslateLinesAsync(Lines(9), "ru", "en");
        Assert.Equal(PerLineFallback.SkippedMessage, first[^1]);

        var before = fake.Requests;
        var second = await cache.TranslateLinesAsync(new[] { Lines(9)[^1] }, "ru", "en");

        // The skipped line was not stored: it is asked again, and this time it answers.
        Assert.True(fake.Requests > before, "a '(' placeholder was cached — I4 is broken");
        Assert.Equal("hello", second[0]);
    }
}

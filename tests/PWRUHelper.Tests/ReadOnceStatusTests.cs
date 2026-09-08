using System.IO;
using System.Linq;
using System.Reflection;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E5.S4 — <b>read-once tells the truth, and can be stopped.</b> Two defects lived in one method and
/// one of them was an amplifier: <c>TranslateSentencesInto</c> swallowed its failure and returned, so
/// <c>ReadRegionOnceAsync</c> printed <c>Done — N line(s) translated.</c> over a list of
/// "(no internet connection)" rows — and a player told "Done" over an empty result presses the button
/// again, and again (A7). The second was quieter: no cancellation token at all, i.e. up to ≈36.9 s of
/// greyed buttons with no way out but the window.
///
/// <para>What is pinned here, in the three ways this story can be got wrong:</para>
/// <list type="number">
/// <item>The status is composed from what the read PRODUCED, and <b>"Done" is unreachable</b> unless
///       every line has a translation (TP-ONCE-01/02/03, UX hint 4). The negative assert is the
///       point — a test for the partial string is not a test that "Done" is absent.</item>
/// <item>A fully paused chain costs a read-once <b>nothing at all</b>: no request, and no rows
///       (TP-ONCE-04, AC 3 / ruling OQ-B).</item>
/// <item>The token is real, budgeted and cancelled by the three things AC 2 names — and a budget
///       expiry is told from a person's Stop by a FLAG, never by the exception, because the two
///       raise the identical one (TP-ONCE-05/06, I3).</item>
/// </list>
///
/// <para>The status branch is a pure function (<see cref="ReadOnceSummary"/>), so most of this file
/// is L1 and needs no window. The gates are LOCAL — <c>new ProviderGate(clock)</c> over this file's
/// own clock — so nothing joins the registry, nothing reads <c>provider-state.json</c>, and nothing
/// sleeps (CI-3, IS-6). The loop-level facts a headless suite cannot execute are pinned in the
/// source, the shape <c>LivePauseTests</c> and <c>ChainCompositionTests</c> already use.</para>
/// </summary>
[Collection("Gates")]
public class ReadOnceStatusTests
{
    /// <summary>Enough settings to build a <c>MainWindow</c> without touching the developer's own
    /// %AppData% (the trap <c>TempSettings</c> exists for). A8's half of this file renders real
    /// controls, so the class joins the WPF collection — one STA thread, never in parallel.</summary>
    private const string NoSettings = """{ "SettingsVersion": 3 }""";

    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    /// <summary>A translator that would notice if it were ever called. Every case that uses it is
    /// about it not being.</summary>
    private sealed class CountingTranslator : ITranslator
    {
        public int Calls;

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
        { Calls++; return Task.FromResult(text); }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
                                                      CancellationToken ct = default)
        { Calls++; return Task.FromResult(lines.ToList()); }
    }

    private static TranslationException Offline() =>
        new(TranslationErrorKind.Network, "raw provider text (HTTP 000) — not for the user");

    // The deck's Network sentence as a read-once status JOINS it: after the full stop that ends the
    // counts, so it keeps its own capital and the line is terminated (E5.S4 review — §3.3's
    // "lower-cased at the join" belongs to the colon join of ReadFailed, not to this one).
    private const string OfflineReason = "No internet connection — nothing can be translated until it is back.";

    // =============================================================================================
    //  TP-ONCE-01/02/03 — the three status shapes (§3.3)
    // =============================================================================================

    /// <summary>TP-ONCE-01 — the only branch that may say "Done", and it says it exactly as it always
    /// did. The wording is unchanged on purpose: this story changes when it is REACHED.</summary>
    [Fact]
    public void TP_ONCE_01_every_line_translated_still_reads_Done()
        => Assert.Equal("Done — 3 line(s) translated.", ReadOnceSummary.Status(3, 3, null));

    /// <summary>
    /// <b>TP-ONCE-02, and it carries the negative assert (UX hint 4 / R-11).</b> A partial read names
    /// both counts and the reason — and the word "Done" is ABSENT, which is a different claim from
    /// "the partial string is right" and the one that actually protects the player.
    /// </summary>
    [Fact]
    public void TP_ONCE_02_a_partial_read_says_so_and_never_says_Done()
    {
        var status = ReadOnceSummary.Status(5, 3, Offline());

        Assert.Equal($"Read 5 line(s) — 3 translated, 2 could not be. {OfflineReason}", status);
        Assert.DoesNotContain("Done", status, StringComparison.Ordinal);
    }

    /// <summary>TP-ONCE-03 — nothing came back. This is the sentence the false "Done" used to cover,
    /// and the reason is the deck's own sentence, verbatim after the stop (§3.3, E5.S4 review).</summary>
    [Fact]
    public void TP_ONCE_03_a_total_failure_names_the_reason_and_never_says_Done()
    {
        var status = ReadOnceSummary.Status(4, 0, Offline());

        Assert.Equal($"Read 4 line(s) — none could be translated. {OfflineReason}", status);
        Assert.DoesNotContain("Done", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The rule itself, swept rather than sampled.</b> Every combination a read can produce, with
    /// and without a failure behind it: "Done" appears if and only if every line has a translation
    /// AND nothing threw. The `error is null` half guards a shape today's caller cannot quite
    /// produce — <c>TranslateBodiesAsync</c> awaits its two source groups in sequence, so the first
    /// throw aborts the batch and the count comes back 0 — and is swept anyway, because a batch that
    /// tolerates a partial failure is E5.S3's subject and the guard has to be in place before it is.
    /// </summary>
    [Fact]
    public void Done_appears_if_and_only_if_every_line_has_a_translation()
    {
        for (int lines = 0; lines <= 5; lines++)
            for (int translated = 0; translated <= lines; translated++)
                foreach (var error in new Exception?[] { null, Offline() })
                {
                    var status = ReadOnceSummary.Status(lines, translated, error);
                    bool claimsDone = status.Contains("Done", StringComparison.Ordinal);
                    Assert.Equal(lines > 0 && translated == lines && error is null, claimsDone);
                }
    }

    /// <summary>A count bigger than the read cannot buy a "Done" — the clamp, which exists because
    /// the caller counts over the translator's list and renders over the rows.</summary>
    [Fact]
    public void A_count_larger_than_the_read_cannot_claim_more_than_was_read()
        => Assert.Equal("Done — 2 line(s) translated.", ReadOnceSummary.Status(2, 7, null));

    /// <summary>
    /// <b>What counts as a translation, and why the number cannot be taken from the list's length.</b>
    /// <c>TranslateBodiesAsync</c> never returns a null (a gap falls back to the source text) and a
    /// provider's per-line fallback returns "(…)" placeholders for the lines it could not do, so
    /// <c>results.Count</c> is always the batch size whatever happened. The test is I4's marker —
    /// the same one <c>CachingTranslator.IsCacheable</c> applies before it stores anything.
    /// </summary>
    [Fact]
    public void Only_results_the_cache_would_keep_count_as_translations()
    {
        var results = new List<string>
        {
            "need a healer",                                  // a translation
            PerLineFallback.SkippedMessage,                   // E3.S8's per-line placeholder
            "(no internet connection)",                        // the feed-row failure stamp
            "",                                                // nothing at all
            "go to the entrance",                              // a translation
        };

        Assert.Equal(2, ReadOnceSummary.CountTranslated(results));
        Assert.True(ReadOnceSummary.IsTranslation("need a healer"));
        Assert.False(ReadOnceSummary.IsTranslation("(anything at all)"));
        Assert.False(ReadOnceSummary.IsTranslation(""));
        Assert.False(ReadOnceSummary.IsTranslation(null));

        // …and it is the same predicate the cache uses, pinned by source because it is private there
        // and widening it for a test would be the wrong trade.
        Assert.Contains("!value.StartsWith('(')",
            File.ReadAllText(RepoFile(Path.Combine("Services", "CachingTranslator.cs"))));
    }

    /// <summary>A partial read with no exception behind it — the shape E3.S8's per-line fallback
    /// produces — stops after the counts instead of inventing a reason it does not have.</summary>
    [Fact]
    public void A_partial_read_with_nothing_thrown_states_the_counts_and_stops()
    {
        var status = ReadOnceSummary.Status(3, 2, null);

        Assert.Equal("Read 3 line(s) — 2 translated, 1 could not be.", status);
        Assert.DoesNotContain("Done", status, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  TP-ONCE-04 — a paused read-once costs nothing at all (AC 3 / OQ-B)
    // =============================================================================================

    /// <summary>
    /// <b>TP-ONCE-04, behaviourally — and rewritten by ruling E5-g (E5.S3).</b> Every read tier is
    /// inside a block window. What the player gets is §3.3's sentence WITH a line count in it, and
    /// what it costs is still <b>zero requests</b> — which is the only half of AC 3 that was ever
    /// about the provider. The capture and the OCR happen, because they are local and free, and
    /// because refusing them is what made the count unknowable.
    ///
    /// <para>The pause is no longer predicted before the capture; it is REPORTED by the chain, which
    /// skips every blocked tier and raises <c>AllProvidersPaused</c> without sending anything. The
    /// chain is driven here for real over real gates, so "zero requests" is asserted on the
    /// translator that would have been called and not on a branch that was not taken.</para>
    /// </summary>
    [Fact]
    public async Task TP_ONCE_04_a_fully_paused_read_once_issues_no_request_and_says_how_many_lines()
    {
        var clock = new FakeClock();
        var dict = new ProviderGate(clock.Read);
        var gtx = new ProviderGate(clock.Read);
        var translator = new CountingTranslator();
        var chain = new ChainTranslator(new[]
        {
            new ChainTier("google-dict", dict, translator),
            new ChainTier("google-gtx", gtx, translator),
        });

        // The cable is out: Network classifies to a soft window on every tier (GAP-3), which is the
        // commonest way a chain becomes fully paused.
        dict.ReportFailure(TranslationErrorKind.Network);
        gtx.ReportFailure(TranslationErrorKind.Network);

        // The read captures and OCRs — three lines are on the screen — and asks the chain, which
        // refuses without sending. TranslateSentencesInto's catch turns that into (0, error).
        const int lines = 3;
        var error = await Assert.ThrowsAsync<TranslationException>(
            () => chain.TranslateLinesAsync(new[] { "а", "б", "в" }, "ru", "en"));

        Assert.Equal(TranslationErrorKind.AllProvidersPaused, error.Kind);
        Assert.Equal(0, translator.Calls);          // zero requests — the half AC 3 is really about

        var pause = chain.PauseNow();
        var status = ReadOnceSummary.Status(lines, 0, error,
            MainWindow.CountdownJoinText(LiveTickPolicy.CountdownSeconds(pause.RetryAt, pause.Now)),
            liveIsRunning: false);

        // The "{t}" moved twice, both times deliberately. E7.S2 gave it §2.4's bands; E7.S1 / ruling
        // E7-a then took the STOPWATCH away from every sentence that is written once and never
        // repainted — a frozen "0:05" reads as a live clock — so a five-second cooldown now has no
        // number to join at all and amendment A12's "in {t}" → "shortly" is what the player reads.
        //
        // Through CountdownJoinText, which is what PausedTryAgainIn (MainWindow.Ocr.cs) actually
        // calls (review): composing through CountdownText would pin a path that does not ship.
        var softCooldown = MainWindow.CountdownJoinText(TranslationPolicy.SoftCooldownSecs);
        Assert.Null(softCooldown);
        // …and the sentence itself is amendment A7's: §3.3's promise that the rows "fill in when one
        // is back" is only true while the LIVE loop is running, so a read taken with it stopped is
        // told to try again instead.
        Assert.Equal($"Read {lines} line(s) — every engine is paused, try again shortly.", status);
        Assert.Equal("Read 3 line(s) — every engine is paused, they fill in when one is back.",
                     ReadOnceSummary.Status(lines, 0, error, null, liveIsRunning: true));
        Assert.DoesNotContain("Done", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ruling E5-g's other half: a read whose answers are all in the cache is served while every
    /// engine is paused.</b> This is the case the pre-capture check refused outright — a player
    /// re-reading the same chat box during an outage got "nothing was read" although not one request
    /// was needed. The cache decorator only asks its inner translator for the MISSES, so a full hit
    /// never reaches the paused chain at all, and the read ends on "Done".
    /// </summary>
    [Fact]
    public async Task A_fully_cached_read_is_served_while_every_engine_is_paused()
    {
        var clock = new FakeClock();
        var gate = new ProviderGate(clock.Read);
        var translator = new CountingTranslator();
        var chain = new ChainTranslator(new[] { new ChainTier("google-gtx", gate, translator) });
        var cached = new CachingTranslator(chain, capacity: 8);

        // Warm the cache while the chain is healthy…
        var warm = await cached.TranslateLinesAsync(new[] { "привет", "го в лк" }, "ru", "en");
        Assert.Equal(1, translator.Calls);

        // …then the cable comes out.
        gate.ReportFailure(TranslationErrorKind.Network);
        Assert.True(chain.PauseNow().AllPaused);

        var again = await cached.TranslateLinesAsync(new[] { "привет", "го в лк" }, "ru", "en");

        Assert.Equal(warm, again);
        Assert.Equal(1, translator.Calls);          // not one new request
        Assert.Equal("Done — 2 line(s) translated.",
                     ReadOnceSummary.Status(2, ReadOnceSummary.CountTranslated(again), null));
    }

    /// <summary>
    /// <b>TP-ONCE-04, in the source</b> — the half a headless suite cannot execute. Ruling E5-g turns
    /// it inside out: what has to be true now is that read-once does <b>not</b> decide anything
    /// before the capture. The pause check is gone, the read captures and OCRs, and the only place
    /// the chain is asked is when the status needs a countdown — after the fact, side-effect free
    /// (R-2), and on the CHAIN rather than the registry (TP-START-02).
    /// </summary>
    [Fact]
    public void TP_ONCE_04_nothing_is_decided_before_the_capture_any_more()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));

        int method = ocr.IndexOf("private async Task ReadRegionOnceAsync(", StringComparison.Ordinal);
        int flag = ocr.IndexOf("_readingOnce = true;", method, StringComparison.Ordinal);
        var beforeTheRead = ocr[method..flag];

        // The pre-capture refusal is gone — this is the ruling, stated as the absence it is.
        Assert.DoesNotContain("PauseNow", beforeTheRead);
        Assert.DoesNotContain("AllPaused", beforeTheRead);
        Assert.DoesNotContain("ReadOncePaused", beforeTheRead);

        // …and the flag still comes before the capture, as it always did.
        Assert.True(flag < ocr.IndexOf("ScreenCapture.Capture", StringComparison.Ordinal));

        // The pause is reported instead: the status is composed from what the read produced, and the
        // countdown is asked of the CHAIN, once, in the helper named for it.
        Assert.Contains("ReadOnceSummary.Status(lines, translated, error, PausedTryAgainIn(error),",
                        ocr, StringComparison.Ordinal);
        var helper = BracedBlock(ocr, ocr.IndexOf("private string? PausedTryAgainIn(", StringComparison.Ordinal));
        Assert.Contains("_readChain.PauseNow()", helper, StringComparison.Ordinal);
        Assert.Contains("ReadOnceSummary.IsAllPaused(error)", helper, StringComparison.Ordinal);
        // IS-6: the countdown subtracts the gates' own clock, never DateTimeOffset.UtcNow.
        Assert.Contains("pause.RetryAt, pause.Now", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("UtcNow", helper);
    }

    // =============================================================================================
    //  TP-ONCE-05 — the token is real, and it is budgeted
    // =============================================================================================

    /// <summary>
    /// The budget, asserted on the REQUESTED value and never by waiting (CI-3). Thirty seconds is
    /// the ceiling the story chose because the uncancellable worst case it replaces was measured at
    /// ≈36.9 s; a "fix" that set the budget above that would leave the defect in place while looking
    /// like the cure.
    /// </summary>
    [Fact]
    public void TP_ONCE_05_the_budget_is_thirty_seconds_and_below_the_worst_case_it_replaces()
    {
        Assert.Equal(30, TranslationPolicy.ReadOnceBudgetSeconds);
        Assert.True(TranslationPolicy.ReadOnceBudgetSeconds < 36,
            "the budget must be under the ≈36.9 s worst case it exists to bound");
        Assert.True(TranslationPolicy.ReadOnceBudgetSeconds > TranslationPolicy.RequestTimeoutSeconds,
            "…and above one request's timeout, or a single slow-but-working call could never land");
    }

    /// <summary>
    /// The token reaches the place that spends the money: a cancelled one stops the chain at its
    /// first <c>ThrowIfCancellationRequested</c> and <b>no provider is entered</b>. That is what
    /// <c>default</c> could never do, and it is the whole of AC 2 one layer below the code-behind.
    /// </summary>
    [Fact]
    public async Task TP_ONCE_05_a_cancelled_token_stops_the_read_before_any_provider_is_asked()
    {
        var clock = new FakeClock();
        var translator = new CountingTranslator();
        var chain = new ChainTranslator(new[] { new ChainTier("google-dict", new ProviderGate(clock.Read), translator) });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chain.TranslateLinesAsync(new[] { "привет" }, "ru", "en", cts.Token));
        Assert.Equal(0, translator.Calls);
    }

    /// <summary>
    /// <b>TP-ONCE-05 in the source: the four wires.</b> The read builds one CTS from the budget and
    /// hands its token to the translation; a second press, <c>StopLive</c> and <c>OnClosing</c> all
    /// cancel it. The <c>default</c> that was the defect is gone from the file.
    /// </summary>
    [Fact]
    public void TP_ONCE_05_the_token_is_created_from_the_budget_and_cancelled_by_the_three_things_AC2_names()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var main = Code(File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")));

        Assert.Contains("new CancellationTokenSource(", ocr, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(TranslationPolicy.ReadOnceBudgetSeconds)", ocr, StringComparison.Ordinal);
        Assert.Contains("_readOnceTranslator, ct)", ocr, StringComparison.Ordinal);
        Assert.DoesNotContain("_readOnceTranslator, default)", ocr, StringComparison.Ordinal);

        // The field and the reason-for-cancelling live together on MainWindow, and the cancel is one
        // helper rather than four copies of Cancel/Dispose/null.
        Assert.Contains("private CancellationTokenSource? _readOnceCts;", main, StringComparison.Ordinal);
        Assert.Contains("private bool _readOnceStopped;", main, StringComparison.Ordinal);

        // (1) ■ Stop, (2) closing the window, (3) a second press — from both entry points, because
        // the Ctrl+Alt+R path returns at its own guard and never reaches ReadRegionOnceAsync's.
        Assert.Contains("CancelReadOnce();", BracedBlock(live, live.IndexOf("private void StopLive()", StringComparison.Ordinal)));
        Assert.Contains("CancelReadOnce();", BracedBlock(main, main.IndexOf("protected override void OnClosing(", StringComparison.Ordinal)));
        // THREE now, not two: amendment A8 (E7.S5) put the same guard at the top of
        // SelectAreaAndReadOnceAsync, because the button that reaches it says "Cancel read" while a
        // read is in flight and the press has to mean cancel BEFORE StopLive()/SelectRegionAsync().
        Assert.Equal(3, Occurrences(ocr, "if (_readingOnce) { CancelReadOnce(); return; }"));
    }

    /// <summary>
    /// <b>I3, and this is the story where it is easiest to get wrong.</b> A budget expiry and a
    /// person's Stop are the same <c>OperationCanceledException</c> on the same token, so the catch
    /// may not tell them apart by type — it reads the flag recorded at the cancel site. And the
    /// filter is on the token's actual state, so an <c>HttpClient</c> timeout (an OCE whose token is
    /// NOT cancelled) falls through to be classified as the failure it is rather than masquerading
    /// as a user cancel — the trap that cost this project three releases.
    ///
    /// <para><b>Both</b> branches render, which the review changed (E5.S4). The story shipped a
    /// person's Stop rendering nothing at all, on §2.1's "Cancelled is not a state" — true of the
    /// eight-state model and of the feed rows, and not true of the status line, which would have
    /// been left reading "Reading…" over a read that had stopped. A stale "Reading…" is the same lie
    /// as "Done" over an empty result, told the other way round; §1's first principle (one message
    /// per state) and fourth (honest status only) decide it.</para>
    /// </summary>
    [Fact]
    public void TP_ONCE_05_a_budget_expiry_is_a_failure_and_a_persons_stop_says_so()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));

        Assert.Contains("catch (OperationCanceledException) when (cts.IsCancellationRequested)",
                        ocr, StringComparison.Ordinal);
        // The flag chooses between the two sentences — it no longer chooses whether to speak.
        Assert.Contains("SetScreenStatus(_readOnceStopped", ocr, StringComparison.Ordinal);
        Assert.Contains("? UserMessages.ReadCancelledStatus()", ocr, StringComparison.Ordinal);
        Assert.Contains(": ReadOnceSummary.Status(lines, 0, BudgetExpired()));", ocr, StringComparison.Ordinal);
        // The filtered catch must come BEFORE the generic one, or every cancel would render
        // "Could not read the screen: …" and a Stop would look like a crash. Searched from the
        // method's own offset: `catch (Exception ex)` appears earlier in the file, in the OCR-pack
        // installer, and a whole-file IndexOf would compare two unrelated catches.
        int method = ocr.IndexOf("private async Task ReadRegionOnceAsync(", StringComparison.Ordinal);
        Assert.True(method > 0);
        Assert.True(ocr.IndexOf("catch (OperationCanceledException) when (cts.IsCancellationRequested)", method, StringComparison.Ordinal)
                    < ocr.IndexOf("catch (Exception ex)", method, StringComparison.Ordinal));

        // …and the inner method rethrows OUR cancel instead of classifying it: the Kind a mapper
        // would answer with on a cancelled token is the one nothing in this app may construct.
        var inner = BracedBlock(ocr, ocr.IndexOf(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)", StringComparison.Ordinal));
        Assert.Contains("throw;", inner, StringComparison.Ordinal);
        // And it does NOT leave its rows on "…" (E5.S4 review). Nothing is coming for them, and a
        // row that stays pending for ever is the amplifier this whole story exists to remove.
        Assert.Contains("UserMessages.ReadCancelledRow()", inner, StringComparison.Ordinal);

        // The sentence a budget expiry produces, composed here rather than run: it is a timeout, and
        // it reads as one.
        Assert.Equal("Read 4 line(s) — none could be translated. The translation service took too long to answer — try again shortly.",
                     ReadOnceSummary.Status(4, 0, new TranslationException(TranslationErrorKind.Timeout, "budget")));
    }

    /// <summary>
    /// <b>The flag has to be right, or I3 is only half-kept.</b> The budget cancels the same token
    /// from a timer thread, so a press landing between the budget firing and the read's continuation
    /// reaching its catch would relabel a 30 s timeout as a person's Stop — and the player would
    /// never learn the app gave up. Only a cancel that had something left to cancel claims it.
    /// </summary>
    [Fact]
    public void A_press_after_the_budget_already_fired_does_not_relabel_the_timeout()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var cancel = BracedBlock(ocr, ocr.IndexOf("private void CancelReadOnce()", StringComparison.Ordinal));

        Assert.Contains("if (!cts.IsCancellationRequested) _readOnceStopped = true;", cancel, StringComparison.Ordinal);
        // …and it is still the flag, never the exception type, that tells the two apart.
        Assert.DoesNotContain("TaskCanceledException", ocr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The capture and the OCR take no token of their own</b> — <c>ScreenCapture</c> is
    /// synchronous GDI and <c>OcrService.ReadLinesAsync</c> has no <c>ct</c> parameter — so a Stop or
    /// a budget expiry arriving during them is observed nowhere unless the read looks. It must look
    /// BEFORE the two things that would otherwise happen for a read already over: diagnosing an
    /// empty result ("Try a tighter box"), and creating the rows AC 3 exists to prevent.
    /// </summary>
    [Fact]
    public void A_cancel_during_the_capture_or_the_ocr_is_observed_before_any_row_is_created()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));

        int look = ocr.IndexOf("cts.Token.ThrowIfCancellationRequested();", StringComparison.Ordinal);
        int empty = ocr.IndexOf("if (sentences.Count == 0)", StringComparison.Ordinal);
        int translate = ocr.IndexOf("await TranslateSentencesInto(", StringComparison.Ordinal);

        Assert.True(look > 0, "a cancel arriving during the untokened capture/OCR must be observed after them");
        Assert.True(look < empty, "…before the empty-result diagnosis, which is not true of a cancelled read");
        Assert.True(look < translate, "…and before anything that creates a row (AC 3)");
    }

    /// <summary>
    /// The two sentences a cancel produces, and the one property that keeps the row half honest: a
    /// cancelled row carries I4's "(" marker, so nothing downstream — the cache, the counter behind
    /// "Done", E5.S3's retry pass — can mistake it for a translation. It is the same guard the feed
    /// row of a failure gets, for a row that is finished rather than failed.
    /// </summary>
    [Fact]
    public void A_cancelled_read_says_so_and_its_rows_are_not_left_pending()
    {
        Assert.Equal("Read cancelled.", UserMessages.ReadCancelledStatus());

        var row = $"({UserMessages.ReadCancelledRow()})";
        Assert.False(ReadOnceSummary.IsTranslation(row));
        Assert.False(UserMessages.ReadCancelledRow().StartsWith('('), "the call site adds the marker (I4)");
        Assert.True(row.Length <= 110, $"a feed row is {row.Length} chars, over the 110 the feed can carry");
        // Not "…": the read that owned these rows is over and nothing will fill them, which is the
        // pending-for-ever state that makes a player press the button again (A7).
        Assert.NotEqual("…", row);
    }

    // =============================================================================================
    //  TP-ONCE-06 — the re-entrancy guard is not replaced by cancellation
    // =============================================================================================

    /// <summary>
    /// <b>TP-ONCE-06.</b> The OCR engine is shared and non-reentrant, so a second read-once still
    /// starts nothing — cancellation protects the WAIT, not the engine. The guard is the first thing
    /// the method does, it is what the three other entry points test, and the <c>finally</c> clears
    /// it on every path (including the two new ones, or read-once would be dead until restart).
    /// </summary>
    [Fact]
    public void TP_ONCE_06_a_second_read_once_cancels_the_first_and_starts_nothing()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        var body = BracedBlock(ocr, ocr.IndexOf("private async Task ReadRegionOnceAsync(", StringComparison.Ordinal));
        // The FIRST statement of the method — first, because everything else it does (the OCR-pack
        // check, the pause check, the capture) would already be racing the read in flight.
        var first = body.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && l != "{");
        Assert.Equal("if (_readingOnce) { CancelReadOnce(); return; }", first);

        // The flag still gates the three entry points that share the engine…
        Assert.Contains("if (_selectingRegion || _readingOnce) return;", live, StringComparison.Ordinal);   // ToggleLive
        Assert.Contains("if (_readingOnce)", ocr, StringComparison.Ordinal);                                 // ReadLastAreaOnce
        // …and is cleared on every exit, in the READ's finally rather than at the end of its try:
        // a flag left set on the cancel path would leave both buttons greyed until restart.
        int method = ocr.IndexOf("private async Task ReadRegionOnceAsync(", StringComparison.Ordinal);
        Assert.Contains("_readingOnce = false;",
                        BracedBlock(ocr, ocr.IndexOf("finally", method, StringComparison.Ordinal)));
    }

    // =============================================================================================
    //  TP-ONCE-07 — the signature, and the branch that consumes it
    // =============================================================================================

    /// <summary>
    /// <b>TP-ONCE-07.</b> The shape of the fix, asserted by reflection so that a later refactor back
    /// to <c>void</c> — the exact shape of the bug — fails here instead of shipping. A tuple is not
    /// decoration: it is what makes "how many lines actually came back" a value the caller has to
    /// look at rather than a number it can assume.
    /// </summary>
    [Fact]
    public void TP_ONCE_07_TranslateSentencesInto_returns_the_outcome_and_takes_a_token()
    {
        var m = typeof(MainWindow).GetMethod("TranslateSentencesInto",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);

        Assert.Equal(typeof(Task<(int, TranslationException?)>), m!.ReturnType);
        Assert.Equal(new[] { typeof(List<string>), typeof(string), typeof(CancellationToken) },
                     m.GetParameters().Select(p => p.ParameterType).ToArray());

        // The tuple element names, which are the readable half of the contract.
        var names = m.ReturnParameter.GetCustomAttributesData()
            .SelectMany(a => a.ConstructorArguments)
            .SelectMany(a => a.Value as IEnumerable<System.Reflection.CustomAttributeTypedArgument>
                             ?? Enumerable.Empty<System.Reflection.CustomAttributeTypedArgument>())
            .Select(a => a.Value as string)
            .ToList();
        Assert.Contains("Translated", names);
        Assert.Contains("Error", names);

        // …and the caller really branches on it, rather than awaiting and then printing Done.
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        Assert.Contains("var (translated, error) = await TranslateSentencesInto(sentences, target, cts.Token);",
                        ocr, StringComparison.Ordinal);
        // E5-g added the fourth argument: the "{t}" of a paused read, rendered by the code-behind
        // because formatting a countdown stays out of Services/ (I2). E7.S1 / amendment A7 added the
        // fifth: the LIVE loop's state, which is what makes "they fill in when one is back" either
        // a promise or a lie.
        Assert.Contains("ReadOnceSummary.Status(lines, translated, error, PausedTryAgainIn(error),",
                        ocr, StringComparison.Ordinal);
        Assert.Contains("liveIsRunning: _liveCts != null));", ocr, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>GAP-4 for this story's copy.</b> "Done" is a claim, and there is exactly one place in the
    /// app allowed to make it — the copy deck. A second literal in the code-behind is how the branch
    /// grows a shortcut back, and it is what the scan below would catch.
    /// </summary>
    [Fact]
    public void The_word_Done_lives_in_the_copy_deck_and_nowhere_else()
    {
        var holders = ProductionSources()
            .Where(f => Code(File.ReadAllText(f)).Contains("Done —", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Equal(new[] { "UserMessages.cs" }, holders);
    }

    // =============================================================================================
    //  A8 / ruling E5-g — the cancel affordance: the read-once button IS the cancel (E7.S5)
    // =============================================================================================

    /// <summary>
    /// <b>The gesture, on both surfaces.</b> E5.S4 wired three cancel routes and shipped none of them
    /// reachable from the UI: <c>SetReadOnceEnabled(false)</c> greyed both read-once buttons and a
    /// disabled WPF button raises no <c>Click</c>, so "only Ctrl+Alt+R can stop a read" was literally
    /// true and the story's own manual check could not be performed. Amendment <b>A8</b> is the
    /// answer and it adds no control: the button stays enabled and becomes the cancel.
    ///
    /// <para>Asserted on the real controls, because the whole defect was a property nobody looked
    /// at: <c>IsEnabled</c> must be true in BOTH states, or the press this affordance is made of
    /// never happens.</para>
    /// </summary>
    [Fact]
    public void A8_the_read_once_button_becomes_the_cancel_and_is_never_disabled()
    {
        using var temp = new TempSettings(NoSettings);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            // Idle — and the copy comes from the deck, not from the XAML that no longer holds it.
            Assert.Equal(UserMessages.ReadOnceLabel(), window.SelectAreaButton.Content);
            Assert.Equal(UserMessages.ReadOnceTooltip(), window.SelectAreaButton.ToolTip);
            Assert.True(window.SelectAreaButton.IsEnabled);

            window.SetReadOnceCancelMode(reading: true);
            Assert.Equal("Cancel read", window.SelectAreaButton.Content);
            Assert.Equal(UserMessages.CancelReadTooltip(), window.SelectAreaButton.ToolTip);
            Assert.True(window.SelectAreaButton.IsEnabled);      // the whole point of A8

            window.SetReadOnceCancelMode(reading: false);
            Assert.Equal(UserMessages.ReadOnceLabel(), window.SelectAreaButton.Content);
            Assert.True(window.SelectAreaButton.IsEnabled);

            // The overlay's column of A8's table: the glyph becomes ■ and the tooltip becomes the
            // label, because at 360 px the button is icon-only and the tooltip is all it can say.
            // Same window as its owner (review): a second MainWindow builds a second copy of every
            // control this thread then keeps alive, and this one already exists.
            var overlay = new CompactOverlay(window);
            Assert.Equal(UserMessages.ReadOnceOverlayLabel(), overlay.ReadOnceButton.Content);
            // The automation name follows the LABEL in both states, which on an icon-only button is
            // the only thing a screen reader has (review): idle it is the main window's sentence,
            // never the glyph.
            Assert.Equal(UserMessages.ReadOnceLabel(),
                         System.Windows.Automation.AutomationProperties.GetName(overlay.ReadOnceButton));

            overlay.SetReadOnceCancelMode(reading: true);
            Assert.Equal("■", overlay.ReadOnceButton.Content);
            Assert.Equal("Cancel read", overlay.ReadOnceButton.ToolTip);
            Assert.Equal("Cancel read",
                         System.Windows.Automation.AutomationProperties.GetName(overlay.ReadOnceButton));
            Assert.True(overlay.ReadOnceButton.IsEnabled);

            overlay.SetReadOnceCancelMode(reading: false);
            Assert.Equal(UserMessages.ReadOnceOverlayLabel(), overlay.ReadOnceButton.Content);
            Assert.Equal(UserMessages.ReadOnceOverlayTooltip(), overlay.ReadOnceButton.ToolTip);
            Assert.True(overlay.ReadOnceButton.IsEnabled);
        });
    }

    /// <summary>
    /// <b>A8's first mandatory condition</b> (deck §3.3, from the E5.S4 review): the
    /// <c>_readingOnce</c> guard is the FIRST line of <c>SelectAreaAndReadOnceAsync</c> — ahead of
    /// <c>StopLive()</c> and <c>SelectRegionAsync()</c> — or a press meant as "cancel" starts a
    /// region drag over the game instead. The button path is the one that changed; the hotkey path
    /// (<c>ReadLastAreaOnce</c>) and the engine guard inside <c>ReadRegionOnceAsync</c> keep theirs,
    /// which is why the guard is written three times and not moved.
    /// </summary>
    [Fact]
    public void A8_the_cancel_guard_is_the_first_line_of_the_button_path()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var body = BracedBlock(ocr, ocr.IndexOf("internal async Task SelectAreaAndReadOnceAsync()",
                                                StringComparison.Ordinal));

        var first = body.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && l != "{");
        Assert.Equal("if (_readingOnce) { CancelReadOnce(); return; }", first);

        // …and it really is ahead of the two calls that would otherwise run for a cancel.
        int guard = body.IndexOf("CancelReadOnce();", StringComparison.Ordinal);
        Assert.True(guard < body.IndexOf("StopLive();", StringComparison.Ordinal));
        Assert.True(guard < body.IndexOf("SelectRegionAsync()", StringComparison.Ordinal));

        // The overlay's button reaches the same method, so ■ cancels a read from compact mode too —
        // one entry point for both surfaces, which is what stops them drifting apart.
        var overlay = Code(File.ReadAllText(RepoFile("Views/CompactOverlay.xaml.cs")));
        Assert.Contains("await _owner.SelectAreaAndReadOnceAsync();", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A8's second mandatory condition: it never disables.</b> <c>SetReadOnceEnabled</c> is gone
    /// from the app — the name as much as the behaviour, so a later edit cannot bring the grey back
    /// by calling something that sounds harmless — and neither writer touches <c>IsEnabled</c>. The
    /// genuine disable paths (the OCR-pack install) are untouched and are not these buttons.
    ///
    /// <para>The copy is the deck's on both surfaces (GAP-4): neither XAML file gives these buttons
    /// a <c>Content</c>, because a control whose label CHANGES cannot keep it in an attribute — the
    /// restore would be a second spelling of it, which is UX-DR19's failure exactly.</para>
    /// </summary>
    [Fact]
    public void A8_the_cancel_mode_never_disables_and_the_copy_lives_in_the_deck()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var overlay = Code(File.ReadAllText(RepoFile("Views/CompactOverlay.xaml.cs")));

        foreach (var (name, source) in new[] { ("MainWindow.Ocr.cs", ocr), ("CompactOverlay.xaml.cs", overlay) })
        {
            var body = BracedBlock(source, source.IndexOf("SetReadOnceCancelMode(bool reading)",
                                                          StringComparison.Ordinal));
            Assert.DoesNotContain("IsEnabled", body, StringComparison.Ordinal);
            Assert.False(body.Contains("SetReadOnceEnabled", StringComparison.Ordinal), name);
        }

        Assert.Empty(ProductionSources()
            .Where(f => Code(File.ReadAllText(f)).Contains("SetReadOnceEnabled(", StringComparison.Ordinal))
            .Select(Path.GetFileName));

        foreach (var xaml in new[] { "Views/MainWindow.xaml", "Views/CompactOverlay.xaml" })
        {
            var text = File.ReadAllText(RepoFile(xaml));
            int button = text.IndexOf(xaml == "Views/MainWindow.xaml" ? "x:Name=\"SelectAreaButton\""
                                                                : "x:Name=\"ReadOnceButton\"",
                                      StringComparison.Ordinal);
            Assert.True(button > 0, $"{xaml} no longer declares the read-once button");
            var element = text[button..text.IndexOf("/>", button, StringComparison.Ordinal)];
            Assert.DoesNotContain("Content=", element, StringComparison.Ordinal);
            Assert.DoesNotContain("ToolTip=", element, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>The race A8 had to leave impossible: a press during the cancel window starts nothing.</b>
    /// The guard that used to swallow the second press now cancels — so the review question is
    /// whether the press that cancels can also, on the way back, start a second read on the OCR
    /// engine, which is shared and non-reentrant. It cannot, and these are the four reasons, each
    /// pinned rather than argued:
    ///
    /// <list type="number">
    /// <item>every <c>_readingOnce</c> guard in the file has the same body — <c>CancelReadOnce();
    ///       return;</c> — so a press that means "cancel" never falls through to a start;</item>
    /// <item><c>CancelReadOnce</c> does not touch <c>_readingOnce</c>: it cancels the token and
    ///       leaves the guard ARMED, so a third and a fourth press cancel nothing and start nothing
    ///       either;</item>
    /// <item>the flag is cleared in exactly one place, the <c>finally</c>, so it is still true for
    ///       the whole of the unwind the cancelled read is doing;</item>
    /// <item>and that <c>finally</c> is reached only after the awaited OCR call has returned — the
    ///       capture and <c>ReadLinesAsync</c> take no token, so cancellation is observed after
    ///       them, never during. The engine is free before the next read can be started.</item>
    /// </list>
    ///
    /// <para>Source-level, because the read a headless suite would have to drive is the one thing
    /// this repo cannot drive: it captures the screen and runs the Windows OCR engine (CI-3) — the
    /// shape this file's other loop-level facts already use.</para>
    /// </summary>
    [Fact]
    public void A8_a_press_during_the_cancel_window_cannot_start_a_second_read()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var read = BracedBlock(ocr, ocr.IndexOf("private async Task ReadRegionOnceAsync(",
                                                StringComparison.Ordinal));

        // (1) every guard cancels AND returns — there is no other form of it in the file.
        Assert.Equal(Occurrences(ocr, "if (_readingOnce)"),
                     Occurrences(ocr, "if (_readingOnce) { CancelReadOnce(); return; }"));

        // (2) the cancel leaves the flag alone, so the guard stays armed for the whole unwind.
        var cancel = BracedBlock(ocr, ocr.IndexOf("private void CancelReadOnce()", StringComparison.Ordinal));
        Assert.DoesNotContain("_readingOnce", cancel, StringComparison.Ordinal);

        // (3) one clear, and it is the finally's.
        Assert.Equal(1, Occurrences(ocr, "_readingOnce = false;"));
        var unwind = BracedBlock(read, read.IndexOf("finally", StringComparison.Ordinal));
        Assert.Contains("_readingOnce = false;", unwind, StringComparison.Ordinal);

        // (4) …and that finally cannot run before the OCR call has returned: it is awaited inside
        // the try, and it takes no token of its own that could abandon it.
        Assert.Contains("await _ocr.ReadLinesAsync(", read, StringComparison.Ordinal);
        Assert.True(read.IndexOf("_ocr.ReadLinesAsync(", StringComparison.Ordinal)
                    < read.IndexOf("_readingOnce = false;", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The label may never disagree with the flag it describes</b> (review). Since A8 the button
    /// is not greyed but re-labelled, and the guard reads <c>_readingOnce</c> rather than the label
    /// — so a window in which the button says "Cancel read" while the flag is still false is a
    /// window in which that press STARTS a read instead of cancelling one. The two writes are
    /// therefore adjacent, with no statement between them, and both are restored by the
    /// <c>finally</c> that ends every path.
    ///
    /// <para>The same truth is pushed through the one door that does not go through the read:
    /// <c>EnterCompactMode</c> hands the overlay <c>_readingOnce</c> itself — not a hardcoded idle
    /// — because Ctrl+Alt+R can be reading when the player goes compact. With the STA case above
    /// proving what the two states render, this is what proves the overlay is told the right
    /// one.</para>
    /// </summary>
    [Fact]
    public void A8_the_label_is_written_with_the_flag_and_pushed_when_the_overlay_opens()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var read = BracedBlock(ocr, ocr.IndexOf("private async Task ReadRegionOnceAsync(",
                                                StringComparison.Ordinal));

        const string label = "SetReadOnceCancelMode(reading: true);";
        int at = read.IndexOf(label, StringComparison.Ordinal);
        int flag = read.IndexOf("_readingOnce = true;", StringComparison.Ordinal);
        Assert.True(at > 0 && at < flag, "the cancel label is written before the flag it describes");
        Assert.DoesNotContain(";", read[(at + label.Length)..flag], StringComparison.Ordinal);

        var unwind = BracedBlock(read, read.IndexOf("finally", StringComparison.Ordinal));
        Assert.Contains("SetReadOnceCancelMode(reading: false);", unwind, StringComparison.Ordinal);

        var compact = Code(File.ReadAllText(RepoFile("Views/MainWindow.Compact.cs")));
        var enter = BracedBlock(compact, compact.IndexOf("internal void EnterCompactMode()",
                                                         StringComparison.Ordinal));
        Assert.Contains("SetReadOnceCancelMode(_readingOnce);", enter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetReadOnceCancelMode(reading:", enter, StringComparison.Ordinal);
        Assert.True(enter.IndexOf("_overlay.Show();", StringComparison.Ordinal)
                    < enter.IndexOf("SetReadOnceCancelMode(_readingOnce);", StringComparison.Ordinal),
                    "the overlay has to exist and be shown before it is told anything");
    }

    /// <summary>
    /// <b>§3.3 as a copy pass (AC 2, AC 5).</b> Every read-once status the deck writes, composed
    /// through the seam that actually ships and compared with the deck verbatim — the five outcomes,
    /// the paused fork amendment A7 added, the two in-flight rows and the cancel. E5.S4 shipped the
    /// shapes; what E7.S5 owes is that they still say what §3.3 says, character for character.
    ///
    /// <para>AC 5's join rule is visible in the last two: the COLON join lower-cases the §3.1
    /// sentence, and a second capital ("GDI+") is left alone (A11).</para>
    /// </summary>
    [Fact]
    public void The_read_once_statuses_are_the_decks_own_words()
    {
        Assert.Equal("Done — 12 line(s) translated.", ReadOnceSummary.Status(12, 12, null));
        Assert.Equal("Read 12 line(s) — 8 translated, 4 could not be. " + OfflineReason,
                     ReadOnceSummary.Status(12, 8, Offline()));
        Assert.Equal("Read 12 line(s) — none could be translated. " + OfflineReason,
                     ReadOnceSummary.Status(12, 0, Offline()));

        // The paused fork (A7), with the count ruling E5-g restored — and a coarse {t} (E7-a): this
        // sentence is written once and never repainted, so it may not show a stopwatch.
        var paused = new TranslationException(TranslationErrorKind.AllProvidersPaused, "raw");
        Assert.Equal("Read 12 line(s) — every engine is paused, they fill in when one is back.",
                     ReadOnceSummary.Status(12, 0, paused, "about 4 min", liveIsRunning: true));
        Assert.Equal("Read 12 line(s) — every engine is paused, try again in about 4 min.",
                     ReadOnceSummary.Status(12, 0, paused, "about 4 min", liveIsRunning: false));
        Assert.Equal("Read 12 line(s) — every engine is paused, try again shortly.",
                     ReadOnceSummary.Status(12, 0, paused, null, liveIsRunning: false));

        // In flight, and the end of a read a person stopped — which A8 has just made pressable.
        Assert.Equal("Reading…", UserMessages.ReadingStatus());
        Assert.Equal("Read 12 line(s). Translating…", UserMessages.ReadTranslatingStatus(12));
        Assert.Equal("Read cancelled.", UserMessages.ReadCancelledStatus());

        // AC 5: the colon join lower-cases (the one join that does), and "GDI+" keeps its capital
        // because the guard looks at the SECOND letter — the argument E5.S4's review settled.
        Assert.Equal("Could not read the screen: no internet connection — nothing can be translated "
                   + "until it is back.", UserMessages.ReadFailed(Offline()));
        Assert.Equal("Could not read the screen: GDI+ capture failed.",
                     UserMessages.ReadFailed(new InvalidOperationException("GDI+ capture failed")));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>The braced block that starts at the first "{" at or after <paramref name="from"/>,
    /// matched to its close. Same shape as <c>LivePauseTests.BracedBlock</c>.</summary>
    private static string BracedBlock(string source, int from)
    {
        Assert.True(from >= 0, "the anchor for the block was not found in the source");
        int open = source.IndexOf('{', from);
        Assert.True(open > 0, "no block opens after the anchor");
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail("the block never closes");
        return "";
    }

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static IEnumerable<string> ProductionSources()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f)
                            .Split('/', '\\')
                            .SkipLast(1)
                            .All(seg => !seg.StartsWith('.')
                                        && !seg.Equals("tests", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                        && !seg.Equals("obj", StringComparison.OrdinalIgnoreCase)));
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

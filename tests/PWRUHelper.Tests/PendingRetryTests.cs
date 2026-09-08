using System.IO;
using System.Linq;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E5.S3 — <b>a row that failed during a blip fills in afterwards, where it is.</b> The epic calls
/// this the riskiest story of the release, and not for the failure it fixes: <i>"a duplicated feed
/// row would be worse than the failure"</i>. A row that fails today stays parenthesised for the
/// session — annoying and honest. A row appended twice is the app lying about what was said in the
/// game chat.
///
/// <para>So the first case in this file is the one that catches the duplicate: same object, same
/// index, unchanged row count across a drain. The rest are the three quieter ways the story silently
/// becomes a no-op that passes its own happy path — a queue that a throw discards, a bound that
/// grows without limit, and a row retried for ever on "…".</para>
///
/// <list type="number">
/// <item><b>TP-LIVE-10</b> — re-translated in place: <c>ReferenceEquals</c>, position, 🔑 line,
///       <c>Count</c>.</item>
/// <item><b>TP-LIVE-11</b> — bounded at the feed's own 50, oldest dropped; an evicted row is
///       dropped from the queue <i>without</i> being translated.</item>
/// <item><b>TP-LIVE-12</b> — ■ Stop clears it.</item>
/// <item><b>TP-LIVE-13</b> — two failed drains ⇒ the terminal <c>(…)</c> row.</item>
/// <item><b>TP-LIVE-14</b> — the pending body is not "("-prefixed <b>and</b> nothing is stored in
///       the cache on the failure path.</item>
/// <item>and the one the plan did not have: <b>a drain that throws does not lose the queue</b>.</item>
/// </list>
///
/// <para>The queue itself is a <c>Services/</c> type over a row parameter, so all of the above are
/// L1 unit tests: no window, no dispatcher, no <c>%AppData%</c>, no network and nothing that sleeps
/// (CI-3). The loop facts a headless suite cannot execute — the drain's POSITION in the tick, the
/// in-place write, the absence of an <c>Add</c> — are pinned in the source, the shape
/// <c>LivePauseTests</c> and <c>ReadOnceStatusTests</c> already use.</para>
/// </summary>
public class PendingRetryTests
{
    // The row type the app queues. Using the real OcrResultItem keeps the identity assertions
    // honest: it is a class with no Equals override, so Contains and ReferenceEquals mean the same
    // thing — which is exactly the property the drain relies on.
    private static OcrResultItem Row(string body, string glossary = "") => new()
    {
        Speaker = "kotik",
        OriginalBody = body,
        TranslationBody = UserMessages.PendingRetryRow(),
        Glossary = glossary,
    };

    private static TranslationException Offline()
        => new(TranslationErrorKind.Network, "raw provider text (HTTP 000) — not for the user");

    /// <summary><c>MainWindow.RequeueOrGiveUp</c>, mirrored. The window cannot be built headlessly, so
    /// the decision is reproduced here and held against the source by
    /// <see cref="The_requeue_decision_in_the_window_is_the_one_mirrored_here"/> — a copy nobody
    /// checks is worse than no copy, because it goes on passing after the original changes.</summary>
    private static void RequeueOrGiveUp(PendingRetryQueue<OcrResultItem> queue,
                                        PendingRetryEntry<OcrResultItem> entry, Exception ex)
    {
        bool costARequest = LiveTickPolicy.Classify(ex) != LiveTickOutcome.Refused;

        if (!PendingRetryQueue<OcrResultItem>.IsRetryable(ex)
            || (costARequest && PendingRetryQueue<OcrResultItem>.GivesUpAfter(entry)))
            entry.Row.TranslationBody = $"({UserMessages.RetryGaveUpRow()})";
        else
            queue.Enqueue(entry.Row, entry.Body, entry.Target,
                          costARequest ? entry.Attempts + 1 : entry.Attempts);
    }

    // =============================================================================================
    //  Which failures are worth keeping
    // =============================================================================================

    /// <summary>
    /// The retryable table, kind by kind, because it is the decision the whole story rests on: too
    /// narrow and the rows this story exists to save stay burned; too wide and a recovery becomes a
    /// batch of certain failures fired at a provider that has only just stopped refusing.
    /// </summary>
    [Theory]
    [InlineData(TranslationErrorKind.Network, true)]
    [InlineData(TranslationErrorKind.Timeout, true)]
    [InlineData(TranslationErrorKind.Unavailable, true)]
    [InlineData(TranslationErrorKind.RateLimited, true)]
    [InlineData(TranslationErrorKind.Blocked, true)]
    [InlineData(TranslationErrorKind.AllProvidersPaused, true)]
    // A body we could not read is not a gap that "recovery" closes: the retry re-sends the identical
    // request for the identical answer. Three in a row open the gate, and the rows that fail AFTER
    // that are NotSent/AllProvidersPaused and are queued — so the recovered session still fills in.
    [InlineData(TranslationErrorKind.BadResponse, false)]
    // Neither of these recovers inside a session — a billing period and a key box — and neither can
    // reach the keyless READ chain at all (I8). Excluded so that the day a keyed tier joins it,
    // nothing starts re-sending into a spent quota.
    [InlineData(TranslationErrorKind.QuotaExhausted, false)]
    [InlineData(TranslationErrorKind.AuthFailed, false)]
    // §4.4's honest last resort: nothing says a retry would help, so the safe answer ships.
    [InlineData(TranslationErrorKind.Unknown, false)]
    public void The_retryable_kinds_are_the_ones_a_later_attempt_could_answer_differently(
        TranslationErrorKind kind, bool retryable)
        => Assert.Equal(retryable,
                        PendingRetryQueue<OcrResultItem>.IsRetryable(new TranslationException(kind, "x")));

    /// <summary>A refusal that never left the machine is retryable whatever kind it carries — the
    /// gate's LAST kind rides on a <c>NotSent</c> exception (a 429 that was never re-sent still reads
    /// as <c>RateLimited</c>), so the FLAG is what decides, exactly as
    /// <c>LiveTickPolicy.Classify</c> does. A <c>BadResponse</c> that was never sent is a refusal and
    /// not a bad body.</summary>
    [Fact]
    public void A_refusal_that_cost_no_request_is_always_retryable_whatever_kind_it_carries()
    {
        Assert.True(PendingRetryQueue<OcrResultItem>.IsRetryable(
            new TranslationException(TranslationErrorKind.BadResponse, "refused") { NotSent = true }));
        Assert.False(PendingRetryQueue<OcrResultItem>.IsRetryable(
            new TranslationException(TranslationErrorKind.BadResponse, "sent")));
    }

    /// <summary>An untyped throw is not queued. It is the shape a broken screen capture or a dead
    /// OCR engine arrives in, and nothing about it says a translator would answer differently.</summary>
    [Fact]
    public void An_untyped_failure_is_not_queued()
        => Assert.False(PendingRetryQueue<OcrResultItem>.IsRetryable(new InvalidOperationException("GDI+")));

    /// <summary>
    /// <b>A cancelled read's rows are FINISHED, not failed</b> (E5.S4 review, assigned here). The
    /// player refused the read; re-sending it would spend the request they just declined. It holds
    /// structurally rather than by a filter — the enqueue lives in the GENERIC catch, while a cancel
    /// is taken by the filtered <c>OperationCanceledException</c> catch above it (I3) — and this case
    /// pins both halves: the row text those rows carry is the cancelled one, and a cancel is not a
    /// retryable failure even if one ever reached the queue.
    /// </summary>
    [Fact]
    public void A_cancelled_read_is_finished_and_never_enters_the_queue()
    {
        Assert.False(PendingRetryQueue<OcrResultItem>.IsRetryable(new OperationCanceledException()));
        Assert.False(PendingRetryQueue<OcrResultItem>.IsRetryable(new TaskCanceledException()));

        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        int cancelCatch = ocr.IndexOf(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            ocr.IndexOf("private async Task<(int Translated, TranslationException? Error)> TranslateSentencesInto(",
                        StringComparison.Ordinal), StringComparison.Ordinal);
        var body = BracedBlock(ocr, cancelCatch);

        Assert.Contains("UserMessages.ReadCancelledRow()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingRetry", body);
    }

    // =============================================================================================
    //  TP-LIVE-10 — in place, and the count does not move
    // =============================================================================================

    /// <summary>
    /// <b>TP-LIVE-10, and the case this story is dangerous without.</b> Three rows fail during a
    /// blip; the gate closes; the next tick drains. What the feed must show afterwards is <i>the same
    /// three rows</i> — same objects, same order, same 🔑 lines — carrying translations. Not four
    /// rows, not six, and not the same three re-appended at the bottom.
    ///
    /// <para>The drain is reproduced here exactly as <c>DrainPendingRetryAsync</c> writes it: take
    /// the entries whose row is still on screen, translate the bodies in one batch, and assign
    /// <c>Row.TranslationBody</c>. The feed is a real <c>ObservableCollection</c> with a
    /// <c>CollectionChanged</c> counter on it — because "no duplicate row" and "no scroll jump" are
    /// the same assertion seen from either end, and the overlay's feed scrolls on exactly that
    /// event.</para>
    /// </summary>
    [Fact]
    public void TP_LIVE_10_a_drain_updates_the_same_rows_in_place_and_appends_nothing()
    {
        var feed = new System.Collections.ObjectModel.ObservableCollection<OcrResultItem>();
        var queue = new PendingRetryQueue<OcrResultItem>();
        var bodies = new[] { "го в лк, нужен хил", "ТС ЛЕГА 2 ДД", "кто на ПП?" };

        foreach (var b in bodies)
        {
            var row = Row(b, glossary: "🔑 ПП = Full Moon Pavilion");
            feed.Add(row);
            queue.Enqueue(row, b, "en");
        }
        int changes = 0;
        feed.CollectionChanged += (_, _) => changes++;
        var identities = feed.ToList();

        // …the gate closes and the next non-skipped tick drains.
        var due = queue.TakeAll(feed.Contains);
        Assert.Equal(3, due.Count);
        foreach (var entry in due)
            entry.Row.TranslationBody = "[" + entry.Body + "]";     // stands in for the translator

        Assert.Equal(3, feed.Count);                                 // no row was appended…
        Assert.Equal(0, changes);                                    // …and the collection never fired
        for (int i = 0; i < 3; i++)
        {
            Assert.Same(identities[i], feed[i]);                     // the SAME object, where it was
            Assert.Equal("[" + bodies[i] + "]", feed[i].TranslationBody);
            Assert.Equal("🔑 ПП = Full Moon Pavilion", feed[i].Glossary);   // the 🔑 line is untouched
        }
        Assert.True(queue.IsEmpty);
    }

    /// <summary>
    /// The mechanism the case above relies on, asserted directly: writing <c>TranslationBody</c>
    /// raises <c>PropertyChanged</c> for it <b>and</b> for <c>Translation</c>, which is what repaints
    /// the main feed's two-tone line and the overlay's single string in place. It is why this story
    /// needs no new binding and no new <c>Run.Text</c> (I15) — and it is the property that would
    /// silently rot if <c>OcrResultItem</c> ever became a plain auto-property, leaving a drain that
    /// updates rows nobody sees update.
    /// </summary>
    [Fact]
    public void A_row_repaints_both_feeds_when_the_drain_writes_it()
    {
        var row = Row("го в лк");
        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        row.TranslationBody = "let's go to lk";

        Assert.Contains(nameof(OcrResultItem.TranslationBody), seen);
        Assert.Contains(nameof(OcrResultItem.Translation), seen);
        Assert.Equal("kotik: let's go to lk", row.Translation);
    }

    // =============================================================================================
    //  TP-LIVE-11 — the bound, and the row that scrolled away
    // =============================================================================================

    /// <summary>
    /// <b>TP-LIVE-11, first half.</b> More failures than the feed keeps ⇒ the queue holds the last
    /// <see cref="TranslationPolicy.PendingRetryCapacity"/> and drops the oldest, in FIFO order. A
    /// 30-minute outage on a busy chat produces far more failed rows than a feed keeps, and an
    /// unbounded queue would come back from it with a drain nobody asked for.
    /// </summary>
    [Fact]
    public void TP_LIVE_11_the_queue_is_bounded_and_drops_the_oldest()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        var rows = Enumerable.Range(0, TranslationPolicy.PendingRetryCapacity + 20)
                             .Select(i => Row($"line {i}")).ToList();

        foreach (var (row, i) in rows.Select((r, i) => (r, i))) queue.Enqueue(row, $"line {i}", "en");

        Assert.Equal(TranslationPolicy.PendingRetryCapacity, queue.Count);
        var kept = queue.TakeAll(_ => true);
        Assert.Equal("line 20", kept[0].Body);                       // the oldest 20 fell off the front
        Assert.Equal($"line {rows.Count - 1}", kept[^1].Body);       // …and the newest is still last
    }

    /// <summary>
    /// <b>TP-LIVE-11, second half, and the assertion is the negative one</b>: a queued row that the
    /// <c>MaxHistory</c> trim has evicted from the feed is dropped <i>before</i> anything is sent.
    /// Nobody can see that row, so a request for it is spent on nothing — and the check has to happen
    /// at drain time, because a row is evicted long after it was queued (by the LIVE loop's trim, or
    /// by read-once's).
    /// </summary>
    [Fact]
    public void TP_LIVE_11_a_row_that_scrolled_off_the_feed_is_dropped_without_being_translated()
    {
        var feed = new System.Collections.ObjectModel.ObservableCollection<OcrResultItem>();
        var queue = new PendingRetryQueue<OcrResultItem>();
        var stillThere = Row("нужен хил");
        var evicted = Row("старое сообщение");
        feed.Add(stillThere);
        feed.Add(evicted);
        queue.Enqueue(stillThere, "нужен хил", "en");
        queue.Enqueue(evicted, "старое сообщение", "en");

        feed.Remove(evicted);                                        // the MaxHistory trim, in one line

        var due = queue.TakeAll(feed.Contains);

        var asked = due.Select(e => e.Body).ToList();
        Assert.Equal(new[] { "нужен хил" }, asked);
        Assert.DoesNotContain("старое сообщение", asked);            // never sent, never even asked for
    }

    /// <summary>The bound is the feed's own 50 and not a number of its own, so the queue can never
    /// hold a row the feed has already forgotten. The two live in different files — one is a
    /// code-behind <c>const</c>, the other a graded constant in <c>Services/</c> — so the agreement
    /// is pinned rather than assumed.
    ///
    /// <para>Read out of the source with the terminator, not with <c>Contains</c> (review): raising
    /// the feed to <c>MaxHistory = 500</c> still CONTAINS "MaxHistory = 50", so the loose form stayed
    /// green on the exact divergence the case exists to catch — a queue a tenth of the feed.</para>
    /// </summary>
    [Fact]
    public void The_queue_is_bounded_by_the_feed_s_own_MaxHistory()
    {
        Assert.Equal(50, TranslationPolicy.PendingRetryCapacity);

        var declared = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")), @"MaxHistory\s*=\s*(\d+)\s*;");
        Assert.True(declared.Success, "MainWindow must still declare MaxHistory");
        Assert.Equal(TranslationPolicy.PendingRetryCapacity.ToString(), declared.Groups[1].Value);
    }

    /// <summary>
    /// <b>The bound's other end, and it is a row the player can still see</b> (review). The queue and
    /// the feed are bounded by the same 50, so they normally forget the same row on the same tick —
    /// but they are two lists trimmed by three call sites, and a read-once landing between a tick's
    /// append and its failure puts their ORDER out of step. When it does, what falls off the front of
    /// the queue can be a row that is still on screen, and it would keep the pending "…" for the rest
    /// of the session with nothing left to fill it in. <c>Enqueue</c> hands the drop back so the
    /// window can give it §2.2's given-up sentence; <c>MainWindow.EnqueueForRetry</c> is the one
    /// caller and the scan below pins that it is the only one.
    /// </summary>
    [Fact]
    public void A_row_the_bound_drops_while_it_is_still_on_screen_is_told_so()
    {
        var feed = new System.Collections.ObjectModel.ObservableCollection<OcrResultItem>();
        var queue = new PendingRetryQueue<OcrResultItem>(capacity: 2);
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        foreach (var r in rows) feed.Add(r);

        var dropped = new List<PendingRetryEntry<OcrResultItem>>();
        foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            dropped.AddRange(queue.Enqueue(row, $"body {i}", "en"));

        // EnqueueForRetry's two lines, and the row is still on screen.
        foreach (var d in dropped)
            if (feed.Contains(d.Row)) d.Row.TranslationBody = $"({UserMessages.RetryGaveUpRow()})";

        Assert.Equal(2, queue.Count);
        Assert.Single(dropped);
        Assert.Same(rows[0], dropped[0].Row);
        Assert.Equal($"({UserMessages.RetryGaveUpRow()})", rows[0].TranslationBody);
        Assert.Equal(3, feed.Count);                                  // in place: nothing was added
        Assert.All(rows.Skip(1), r => Assert.Equal("…", r.TranslationBody));
    }

    /// <summary>Every enqueue in the code-behind goes through <c>EnqueueForRetry</c>, which is where
    /// "a dropped row that is still on screen is told so" lives. A bare <c>_pendingRetry.Enqueue</c>
    /// at a call site would bypass it silently — the drop is invisible by construction, since it is
    /// the entry nothing points at any more.</summary>
    [Fact]
    public void Nothing_enqueues_past_the_wrapper_that_answers_the_dropped_row()
    {
        foreach (var file in new[] { "Views/MainWindow.Ocr.cs", "Views/MainWindow.xaml.cs" })
            Assert.DoesNotContain("_pendingRetry.Enqueue(", Code(File.ReadAllText(RepoFile(file))),
                                  StringComparison.Ordinal);

        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var wrapper = BracedBlock(live, live.IndexOf("private void EnqueueForRetry(", StringComparison.Ordinal));
        Assert.Contains("_pendingRetry.Enqueue(row, body, target, attempts)", wrapper, StringComparison.Ordinal);
        Assert.Contains("_ocrItems.Contains(dropped.Row)", wrapper, StringComparison.Ordinal);
        Assert.Contains("GiveUpRow(dropped.Row)", wrapper, StringComparison.Ordinal);

        // …and the wrapper is the ONLY place that calls the queue's Enqueue in the whole window.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(live, @"_pendingRetry\.Enqueue\("));
    }

    /// <summary>A row cannot be queued twice. Nothing in the loop can do it today — each row is
    /// enqueued once, in the catch of the call that created it — but a row present twice would be
    /// translated twice in one drain, and "at most once" is cheaper to guarantee than to argue
    /// about.</summary>
    [Fact]
    public void Enqueueing_the_same_row_again_replaces_it_rather_than_doubling_it()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        var row = Row("го в лк");

        queue.Enqueue(row, "го в лк", "en");
        queue.Enqueue(row, "го в лк", "fr", attempts: 1);

        Assert.Equal(1, queue.Count);
        var due = queue.TakeAll(_ => true);
        Assert.Equal("fr", due[0].Target);
        Assert.Equal(1, due[0].Attempts);
    }

    // =============================================================================================
    //  TP-LIVE-12 / TP-LIVE-13 — Stop, and giving up
    // =============================================================================================

    /// <summary>TP-LIVE-12 — ■ Stop clears the queue, so pressing ▶ afterwards cannot resurrect rows
    /// from the session that ended (the story's manual step 8).</summary>
    [Fact]
    public void TP_LIVE_12_clearing_empties_the_queue()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        queue.Enqueue(Row("a"), "a", "en");
        queue.Enqueue(Row("b"), "b", "en");

        queue.Clear();

        Assert.True(queue.IsEmpty);
        Assert.Empty(queue.TakeAll(_ => true));
    }

    /// <summary>
    /// <b>TP-LIVE-13.</b> Two failed drains and the row stops being pending: it becomes the terminal
    /// "(not translated — …)" of <c>ux-mode-degrade.md</c> §2.2, which is the <b>only</b> one of the
    /// two forms that is parenthesised. A row that stayed on "…" for ever would be the pending row
    /// that never resolves — the thing that makes a player press the button again (A7).
    ///
    /// <para>The loop's <c>RequeueOrGiveUp</c> is reproduced here exactly, so the arithmetic of
    /// "attempts made" is asserted rather than described: an entry enters at 0, so with
    /// <see cref="TranslationPolicy.PendingRetryMaxAttempts"/> = 2 the second failure is the last.</para>
    /// </summary>
    [Fact]
    public void TP_LIVE_13_a_row_that_fails_its_retries_becomes_a_terminal_row()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        var row = Row("нужен хил");
        queue.Enqueue(row, "нужен хил", "en");

        for (int drain = 1; drain <= TranslationPolicy.PendingRetryMaxAttempts; drain++)
        {
            var entry = Assert.Single(queue.TakeAll(_ => true));
            Assert.Equal(drain - 1, entry.Attempts);
            RequeueOrGiveUp(queue, entry, Offline());     // the failing drain, as the loop writes it

            if (drain < TranslationPolicy.PendingRetryMaxAttempts)
            {
                Assert.Equal(1, queue.Count);                        // still pending…
                Assert.Equal("…", row.TranslationBody);              // …and still on the ellipsis
            }
        }

        Assert.True(queue.IsEmpty);                                  // it is not retried again
        Assert.Equal("(not translated — the engines did not come back)", row.TranslationBody);
        Assert.True(row.TranslationBody.StartsWith('('),
                    "only the GIVEN-UP form is parenthesised — that distinction is the whole of AC 2");
    }

    /// <summary>A failure a retry cannot help ends the wait immediately, whatever attempts are left:
    /// the outage the row was waiting out has been replaced by something else, and continuing to wait
    /// would be a pending row with nothing behind it. Asserted on the ROW (review — the case used to
    /// check two predicates side by side and never composed them, so the behaviour it is named for
    /// was not covered).</summary>
    [Fact]
    public void A_row_whose_failure_stops_being_retryable_gives_up_at_once()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        var entry = new PendingRetryEntry<OcrResultItem>(Row("го"), "го", "en", Attempts: 0);
        Assert.False(PendingRetryQueue<OcrResultItem>.GivesUpAfter(entry));   // it has an attempt left…

        RequeueOrGiveUp(queue, entry, new TranslationException(TranslationErrorKind.QuotaExhausted, "spent"));

        Assert.True(queue.IsEmpty, "…and no reason to use it: the row does not go back on the queue");
        Assert.Equal($"({UserMessages.RetryGaveUpRow()})", entry.Row.TranslationBody);
    }

    /// <summary>
    /// <b>A drain that was REFUSED at the gate spends no attempt</b> (review, and it is the rule that
    /// makes the two attempts mean what AC 5 says). Nothing left the machine, so the row was never
    /// actually asked about — and the drain runs on every non-skipped tick, so charging refusals
    /// would burn both of a row's attempts inside two ticks (≈1.4 s) and hand it the given-up
    /// sentence in the first seconds of the very outage the queue exists to survive. It is
    /// <c>LiveTickPolicy.Classify</c>'s definition of "this cost a request" and not a second one, so
    /// the queue and the tick counters cannot come to disagree.
    /// </summary>
    [Fact]
    public void A_drain_refused_at_the_gate_does_not_spend_an_attempt()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        var row = Row("нужен хил");
        queue.Enqueue(row, "нужен хил", "en");

        var refusal = new TranslationException(TranslationErrorKind.AllProvidersPaused, "every rung is shut");
        for (int drain = 0; drain < 5; drain++)
        {
            var entry = Assert.Single(queue.TakeAll(_ => true));
            Assert.Equal(0, entry.Attempts);                  // …however many refusals it survives
            RequeueOrGiveUp(queue, entry, refusal);
        }

        Assert.Equal(1, queue.Count);
        Assert.Equal("…", row.TranslationBody);               // still pending, never given up

        // …and the first drain that really reaches a provider does spend one.
        RequeueOrGiveUp(queue, Assert.Single(queue.TakeAll(_ => true)), Offline());
        Assert.Equal(1, Assert.Single(queue.TakeAll(_ => true)).Attempts);
    }

    /// <summary>The mirror below is only worth having if it still matches the window. This pins the
    /// three decisions <c>MainWindow.RequeueOrGiveUp</c> is made of — the cost test, the retryable
    /// test, the attempt arithmetic — against the source, so the copy cannot drift silently.</summary>
    [Fact]
    public void The_requeue_decision_in_the_window_is_the_one_mirrored_here()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var body = BracedBlock(live, live.IndexOf("private void RequeueOrGiveUp(", StringComparison.Ordinal));

        Assert.Contains("LiveTickPolicy.Classify(ex) != LiveTickOutcome.Refused", body, StringComparison.Ordinal);
        Assert.Contains("!PendingRetryQueue<OcrResultItem>.IsRetryable(ex)", body, StringComparison.Ordinal);
        Assert.Contains("costARequest && PendingRetryQueue<OcrResultItem>.GivesUpAfter(entry)",
                        body, StringComparison.Ordinal);
        Assert.Contains("GiveUpRow(entry.Row)", body, StringComparison.Ordinal);
        Assert.Contains("costARequest ? entry.Attempts + 1 : entry.Attempts", body, StringComparison.Ordinal);
        Assert.Contains("EnqueueForRetry(", body, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  TP-LIVE-14 — pending is not terminal, and it never reaches the cache
    // =============================================================================================

    /// <summary>
    /// <b>TP-LIVE-14, and it has two halves that must both hold.</b> The pending text is deliberately
    /// not "("-prefixed, so it reads as pending rather than terminal (AC 2) — but that also makes it
    /// <i>cacheable</i> by <c>CachingTranslator.IsCacheable</c>. It is safe only because the row's
    /// text never goes near the cache: the cache stores a TRANSLATOR'S OUTPUT, and this string is
    /// written by the UI onto a row. The second half is asserted on a store spy rather than on the
    /// string, because the string alone says nothing about where it went.
    /// </summary>
    [Fact]
    public async Task TP_LIVE_14_the_pending_row_is_not_terminal_and_nothing_is_cached_when_a_batch_fails()
    {
        Assert.False(UserMessages.PendingRetryRow().StartsWith('('));
        // Read back through the app's ONE definition of "this is a translation", which is the very
        // test the cache applies: a pending row would pass it, which is the trap this case names.
        Assert.True(ReadOnceSummary.IsTranslation(UserMessages.PendingRetryRow()));

        var inner = new FlakyTranslator(Offline());
        var cache = new CachingTranslator(inner, capacity: 8);

        await Assert.ThrowsAsync<TranslationException>(
            () => cache.TranslateLinesAsync(new[] { "нужен хил" }, "ru", "en"));
        Assert.Equal(1, inner.Calls);

        // The store is spied where a store can be seen: the SAME line is asked of the inner
        // translator a second time, which it would not be if the failed batch had cached anything.
        var second = await cache.TranslateLinesAsync(new[] { "нужен хил" }, "ru", "en");
        Assert.Equal(2, inner.Calls);
        Assert.Equal(new[] { "[нужен хил]" }, second);
        // …and a SUCCESS is kept, so the assertion above is about the failure and not about a cache
        // that never stores anything (the way this case could quietly become vacuous).
        Assert.Equal(second, await cache.TranslateLinesAsync(new[] { "нужен хил" }, "ru", "en"));
        Assert.Equal(2, inner.Calls);

        // …and the UI's pending text is never handed to the cache at all: the enqueue writes it onto
        // the ROW, and the only string that reaches a Store is a translator's return value.
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        int enqueue = live.IndexOf("EnqueueForRetry(items[i]", StringComparison.Ordinal);
        Assert.True(enqueue > 0, "the failure branch must enqueue the row it just marked pending");
    }

    // =============================================================================================
    //  The case the plan did not have: a drain that throws must not lose the queue
    // =============================================================================================

    /// <summary>
    /// <b>The way this story silently does nothing.</b> A drain takes its entries off the queue
    /// before it sends anything — it has to, or a throw would leave the same rows queued <i>and</i>
    /// in flight. If the throw then simply propagates, up to fifty rows are discarded by one
    /// exception and every happy-path test in this file stays green.
    ///
    /// <para>Reproduced with the loop's own <c>catch</c>: the entries go back with one more attempt
    /// spent, and the exception still reaches the loop, which owes the counter its increment and the
    /// status line its sentence.</para>
    /// </summary>
    [Fact]
    public void A_drain_that_throws_puts_its_entries_back_on_the_queue()
    {
        var feed = new System.Collections.ObjectModel.ObservableCollection<OcrResultItem>();
        var queue = new PendingRetryQueue<OcrResultItem>();
        foreach (var b in new[] { "a", "b", "c" })
        {
            var row = Row(b);
            feed.Add(row);
            queue.Enqueue(row, b, "en");
        }

        var due = queue.TakeAll(feed.Contains);
        Assert.True(queue.IsEmpty, "a drain takes its entries OFF the queue before it sends anything");

        // Explicitly an Action: a lambda whose only exit is a `throw` is convertible to Func<Task>
        // too, and xUnit's async overload is [Obsolete] for the good reason that it would not run.
        Assert.Throws<TranslationException>((Action)(() =>
        {
            try { throw Offline(); }
            catch (Exception ex)
            {
                foreach (var entry in due) RequeueOrGiveUp(queue, entry, ex);
                throw;
            }
        }));

        Assert.Equal(3, queue.Count);
        Assert.All(queue.TakeAll(_ => true), e => Assert.Equal(1, e.Attempts));
        Assert.All(feed, r => Assert.Equal("…", r.TranslationBody));   // still pending, not given up
    }

    // =============================================================================================
    //  The loop, in the source — where the drain runs and what it may not do
    // =============================================================================================

    /// <summary>
    /// <b>AC 4 / AC 6, as a scan.</b> The drain runs inside the NON-skipped branch, before the
    /// capture — §8.3's diagram drains at <c>tick N+k</c> before anything else, and a drain that ran
    /// after the capture would make a recovered row wait for an OCR pass it does not need. And the
    /// method that performs it must never touch the collection: an <c>Add</c> is the duplicated row,
    /// an <c>Insert</c> or a re-sort is the feed reordering under the player's eyes.
    /// </summary>
    [Fact]
    public void The_drain_runs_before_the_capture_and_never_touches_the_collection()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        int drain = live.IndexOf("await DrainPendingRetryAsync(ct)", StringComparison.Ordinal);
        int capture = live.IndexOf("ScreenCapture.Capture", StringComparison.Ordinal);
        int paused = live.IndexOf("if (pause.AllPaused)", StringComparison.Ordinal);
        Assert.True(drain > 0, "the tick must drain the pending-retry queue");
        Assert.True(drain > paused, "the drain belongs to the tick that RUNS — a paused tick does nothing");
        Assert.True(drain < capture, "the drain comes before the capture (AC 4, §8.3)");

        // …and the skipped branch still reaches none of it: nothing would succeed, and OQ-B says a
        // paused tick does nothing at all.
        Assert.DoesNotContain("DrainPendingRetryAsync", BracedBlock(live, paused));

        var body = BracedBlock(live, live.IndexOf("private async Task<bool> DrainPendingRetryAsync(",
                                                  StringComparison.Ordinal));
        Assert.Contains("entries[i].Row.TranslationBody = translations[i];", body, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "_ocrItems.Add", "_ocrItems.Insert", "_ocrItems.RemoveAt",
                                          "new OcrResultItem", "Sort(", ".Glossary =" })
            Assert.False(body.Contains(forbidden, StringComparison.Ordinal),
                $"the drain must not {forbidden} — it updates rows IN PLACE (AC 6)");

        // One translation path for a retried line and a fresh one: same method, so slang expansion
        // (I6) and the per-message ru/auto choice (I7) cannot come to have an exception.
        Assert.Contains("TranslateBodiesAsync(", body, StringComparison.Ordinal);
        // I3: no unfiltered OperationCanceledException catch anywhere on the drain's path.
        Assert.DoesNotContain("catch (OperationCanceledException)", body);
    }

    /// <summary>
    /// <b>The drain owes an ending to every entry it took off the queue</b> (review, and it is this
    /// story's second named risk: "a drain that throws and drops the queue, which makes the whole
    /// story a no-op that passes its own happy-path test"). <c>TakeAll</c> empties the queue before
    /// the first request, so from then on each entry must be translated, put back, or given up on
    /// EVERY exit — including the two the first version missed: the target groups after the one that
    /// threw (reachable exactly when the player changed the target combo during the outage, which is
    /// the case the grouping exists for) and the ones left when a Stop lands mid-drain.
    ///
    /// <para>Scanned rather than driven because the loop lives on a <c>MainWindow</c> the headless
    /// suite cannot build; the behavioural half is <see cref="A_drain_that_throws_puts_its_entries_back_on_the_queue"/>.</para>
    /// </summary>
    [Fact]
    public void The_drain_leaves_no_entry_behind_on_any_exit()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var body = BracedBlock(live, live.IndexOf("private async Task<bool> DrainPendingRetryAsync(",
                                                  StringComparison.Ordinal));

        // The groups are materialised — a lazy GroupBy cannot be revisited after the throw.
        Assert.Contains(".Select(g => g.ToList()).ToList()", body, StringComparison.Ordinal);
        Assert.Contains("for (int g = 0; g < groups.Count; g++)", body, StringComparison.Ordinal);

        // Both leftovers are walked, in the catch and on the cancel, and nothing else is.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            body, @"for \(int rest = g \+ 1; rest < groups\.Count; rest\+\+\)").Count);

        var failed = BracedBlock(body, body.IndexOf("catch (Exception ex)", StringComparison.Ordinal));
        Assert.Contains("RequeueOrGiveUp(entry, ex)", failed, StringComparison.Ordinal);
        Assert.Contains("EnqueueForRetry(entry.Row, entry.Body, entry.Target, entry.Attempts)",
                        failed, StringComparison.Ordinal);   // untried: the SAME attempt count
        Assert.Contains("GiveUpRow(entry.Row)", failed, StringComparison.Ordinal);
        Assert.Contains("throw;", failed, StringComparison.Ordinal);

        // The rows a completed batch answered are written BEFORE the cancel is observed: the answer
        // was paid for, and writing it in place cannot duplicate anything.
        Assert.True(body.IndexOf("entries[i].Row.TranslationBody = translations[i];", StringComparison.Ordinal)
                    < body.IndexOf("if (ct.IsCancellationRequested)", StringComparison.Ordinal),
                    "a cancel must not throw away a translation that has already come back");
    }

    /// <summary>
    /// <b>AC 1's real content.</b> <c>LiveDedup</c> is not touched at all — the strongest possible
    /// guarantee that it cannot swallow anything — and the RAW body is what is queued, because
    /// <c>TranslateBodiesAsync</c> expands the slang itself (I6) and a pre-expanded body would be
    /// expanded twice on retry.
    /// </summary>
    [Fact]
    public void The_queue_stores_the_raw_body_and_the_dedup_is_not_touched()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        int enqueue = live.IndexOf("EnqueueForRetry(items[i]", StringComparison.Ordinal);

        Assert.Contains("EnqueueForRetry(items[i], parts[i].Body, target);",
                        live, StringComparison.Ordinal);
        Assert.False(live[..enqueue].Contains("_slang.Expand", StringComparison.Ordinal)
                     && live.IndexOf("_slang.Expand", StringComparison.Ordinal) < enqueue,
                     "the queue stores the RAW body — TranslateBodiesAsync expands (I6)");

        // The dedup is named nowhere near the queue, and the queue is named nowhere in LiveDedup.
        Assert.DoesNotContain("_pendingRetry", File.ReadAllText(ServiceSource("LiveDedup.cs")));
        Assert.DoesNotContain("PendingRetry", File.ReadAllText(ServiceSource("LiveDedup.cs")));
    }

    /// <summary>■ Stop clears the queue, and it does so unconditionally — before the guard that
    /// returns when no loop is running — so the promise holds however the session ended.
    ///
    /// <para><b>And it tells the rows</b> (review). Stop does not clear the feed the way
    /// <c>StartLive</c> does, so every row that was waiting is still on both surfaces with the loop
    /// that owed it a translation now stopped: nothing is coming, by construction. §2.2 gives a
    /// pending row the "…" only WHILE something is coming and a given-up row the parenthesised
    /// sentence, so this is the moment one becomes the other — including on the auto-stop path,
    /// which ends a long outage by calling <c>StopLive()</c> for the player.</para></summary>
    [Fact]
    public void TP_LIVE_12_StopLive_clears_the_queue_and_gives_up_the_rows_it_held()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var stop = BracedBlock(live, live.IndexOf("private void StopLive()", StringComparison.Ordinal));

        Assert.Contains("foreach (var entry in _pendingRetry.Clear()) GiveUpRow(entry.Row);",
                        stop, StringComparison.Ordinal);
        Assert.True(stop.IndexOf("_pendingRetry.Clear()", StringComparison.Ordinal)
                    < stop.IndexOf("if (_liveCts == null) return;", StringComparison.Ordinal),
                    "Stop must clear the queue even when no loop is running");

        // The sentence itself is written in exactly one place — GiveUpRow — so "given up" cannot come
        // to mean two different things in two of the five branches that reach it.
        Assert.Contains("private static void GiveUpRow(OcrResultItem row)", live, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(live, @"RetryGaveUpRow\(\)"));
    }

    /// <summary>
    /// <b>Restarting LIVE on the SAME area does not wipe the chat</b> (the owner's report: LIVE
    /// running, Read once, start again — and every line he had was gone, with no way to carry on).
    ///
    /// <para>The rule is the one the UI already draws. <c>ToggleLive</c> is the RESUME door — ↻ Resume
    /// last area, ▶ Live on the overlay, Ctrl+Alt+L — and it continues the session; the picker door
    /// (<c>LiveButton_Click</c>) has just been handed a new rectangle, and that is a new one.</para>
    ///
    /// <para><b>And the wipe is one arm, not two statements.</b> A feed kept while the dedup is
    /// replaced is the worse bug in the other direction: every message still visible in the game
    /// chat reads as new and is appended a SECOND time. So both halves are asserted to live inside
    /// <c>if (freshSession)</c>, and to appear exactly once in the method — the pair may not drift
    /// apart.</para>
    /// </summary>
    [Fact]
    public void Resuming_the_saved_area_keeps_the_feed_and_the_dedup_together()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        Assert.Contains("StartLive(rect, freshSession: false);", live, StringComparison.Ordinal);
        Assert.Contains("StartLive(rect, freshSession: true);", live, StringComparison.Ordinal);

        var start = BracedBlock(live, live.IndexOf("private void StartLive(", StringComparison.Ordinal));
        var fresh = BracedBlock(start, start.IndexOf("if (freshSession)", StringComparison.Ordinal));

        foreach (var wipe in new[] { "_ocrItems.Clear();", "_dedup = new();" })
        {
            Assert.Contains(wipe, fresh, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                start, System.Text.RegularExpressions.Regex.Escape(wipe)));
        }

        // The queue's exit changed with the feed surviving: a row that is still on screen is GIVEN
        // UP rather than dropped, or it would sit on "…" for ever with no loop left to fill it in.
        // On a fresh session the feed was emptied a few lines above, so nothing is found and the
        // behaviour is what it always was.
        Assert.Contains("foreach (var entry in _pendingRetry.Clear())", start, StringComparison.Ordinal);
        Assert.Contains("if (_ocrItems.Contains(entry.Row)) GiveUpRow(entry.Row);", start,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The other half of the resume rule, and its invariant is "registered ⟸ TRANSLATED".</b>
    /// The resumed loop keeps its dedup, which is what stops it re-translating the chat it already
    /// did — but a read-once appends rows that dedup has never heard of, so without a registration
    /// the owner's own flow (LIVE, Read once, resume) shows those messages twice.
    ///
    /// <para><b>Registered is not appended</b>, and the difference is the whole test. A remembered
    /// line makes the resumed loop stay SILENT about a later frame, so it may only ever stand for a
    /// message the player can read. An earlier version registered before the <c>await</c>, on
    /// "registered ⟺ appended": one network blip mid-read stamped every row
    /// <c>(not translated — the engines did not come back)</c> AND registered every line, and the
    /// resumed loop then never translated any of them for as long as they stayed on screen — the
    /// invisible failure, which is the one direction this may not fail in.</para>
    ///
    /// <para>So the site is BELOW the write that gives the rows their translations, and below both
    /// catches — positionally and structurally, because "after" alone would still be reachable from
    /// a catch that fell through. A failed, cancelled or fully paused read registers nothing at all
    /// and the player gets the visible duplicate.</para>
    /// </summary>
    [Fact]
    public void A_read_once_registers_only_the_lines_it_really_translated()
    {
        var ocr = Code(File.ReadAllText(RepoFile("Views/MainWindow.Ocr.cs")));
        var body = BracedBlock(ocr, ocr.IndexOf(
            "private async Task<(int Translated, TranslationException? Error)> TranslateSentencesInto(",
            StringComparison.Ordinal));

        const string register = "_dedup.RememberAlreadyShown(";
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            ocr, System.Text.RegularExpressions.Regex.Escape(register)));
        Assert.Contains("if (ReadOverlapsTheLiveArea(region))", body, StringComparison.Ordinal);

        int at = body.IndexOf(register, StringComparison.Ordinal);
        Assert.True(at > body.IndexOf("items[i].TranslationBody = translations[i];", StringComparison.Ordinal),
                    "a line may only be registered once its row carries the translation");

        // Neither failure exit can reach it: they stand above it AND it is not inside them.
        foreach (var (name, mark) in new[]
                 {
                     ("the cancelled read", "foreach (var it in items) it.TranslationBody = $\"({UserMessages.ReadCancelledRow()})\";"),
                     ("the failed read",    "foreach (var it in items) GiveUpRow(it);"),
                 })
            Assert.True(at > body.IndexOf(mark, StringComparison.Ordinal),
                        $"{name} must reach its own exit before anything is registered");

        foreach (var handler in new[] { "catch (OperationCanceledException)", "catch (Exception ex)" })
            Assert.DoesNotContain(register,
                BracedBlock(body, body.IndexOf(handler, StringComparison.Ordinal)));

        // …and what is handed over is narrowed to what is READABLE ON THE FEED: a real translation
        // (the app's one definition of that), a row the MaxHistory trim did not evict, and a line
        // the LIVE filter itself would have kept — LIVE feeds _dedup.Next through LooksLikeText, and
        // registering what it cannot match only spends the 200-entry memory on fuzzy accidents.
        var narrow = BracedBlock(ocr, ocr.IndexOf("private List<string> LinesToRememberAsShown(",
                                                  StringComparison.Ordinal));
        Assert.Contains("ReadOnceSummary.IsTranslation(translations[i])", narrow, StringComparison.Ordinal);
        Assert.Contains("_ocrItems.Contains(items[i])", narrow, StringComparison.Ordinal);
        Assert.Contains("TextMatching.LooksLikeText(sentences[i], minLetters)", narrow, StringComparison.Ordinal);

        // The region rule: overlap with the area a RESUME will use, read without a side effect.
        int rule = ocr.IndexOf("private bool ReadOverlapsTheLiveArea(", StringComparison.Ordinal);
        Assert.True(rule >= 0, "the overlap rule was not found in MainWindow.Ocr.cs");
        var overlap = ocr[rule..(ocr.IndexOf(';', rule) + 1)];
        Assert.Contains("_settings.LastLiveRegion", overlap, StringComparison.Ordinal);
        Assert.Contains("IntersectsWith", overlap, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsService.Save", overlap);
        Assert.DoesNotContain("TryGetSavedRegion", overlap);
    }

    /// <summary>
    /// <b>Amplifier A7, at the one place the resume rule re-opened it.</b> A batch whose translation
    /// ARRIVED and whose player pressed ■ in the same breath used to return before writing it: the
    /// rows kept their "…", they were never on <c>_pendingRetry</c>, and nothing else in the app
    /// touches a row. <c>StartLive</c>'s unconditional <c>_ocrItems.Clear()</c> swept them on the
    /// next start — and the resume path deliberately does not clear the feed any more, while the
    /// dedup now remembers the line, so nothing would ever ask for it again. A "…" that never
    /// resolves, with no way in the UI to clear the feed, is exactly what makes a player press the
    /// button again and again.
    ///
    /// <para>The window is not theoretical: ■ Stop, closing the window and the auto-stop all cancel
    /// the token, and a fully cached batch returns between two frames. The rule now matches the
    /// drain's — the answer was paid for, so it is written wherever the row already is — and a row
    /// with no answer to write is given up rather than left pending.</para>
    /// </summary>
    [Fact]
    public void A_stop_landing_on_an_answered_batch_still_finishes_every_row()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var body = BracedBlock(live, live.IndexOf("private async Task AppendLinesToHistory(",
                                                  StringComparison.Ordinal));

        const string write = "if (i < translations.Count) items[i].TranslationBody = translations[i];";
        int at = body.IndexOf(write, StringComparison.Ordinal);
        Assert.True(at >= 0, "the write that gives every row its ending was not found");
        Assert.Contains("else GiveUpRow(items[i]);", body, StringComparison.Ordinal);

        // Nothing returns between the answer arriving and the rows it belongs to.
        Assert.DoesNotContain("if (ct.IsCancellationRequested) return;", body[..at]);
        // The cancel still skips the SCROLL — finishing a row is not the same as moving the view
        // under the eyes of someone who has just stopped.
        int cancel = body.IndexOf("if (ct.IsCancellationRequested) return;", StringComparison.Ordinal);
        Assert.True(cancel > at, "the cancel check must stand after the rows are finished");
        Assert.True(body.IndexOf("ResultsScroller?.ScrollToEnd();", cancel, StringComparison.Ordinal) > cancel,
                    "the only thing after the cancel check is the scroll");
    }

    /// <summary><c>Clear</c> answers what it was holding, which is what lets ■ Stop give those rows
    /// §2.2's given-up sentence instead of leaving them on a "…" nothing will ever resolve.</summary>
    [Fact]
    public void Clearing_hands_back_the_rows_that_were_waiting()
    {
        var queue = new PendingRetryQueue<OcrResultItem>();
        var rows = new[] { Row("a"), Row("b") };
        foreach (var r in rows) queue.Enqueue(r, r.OriginalBody, "en");

        var held = queue.Clear();

        Assert.True(queue.IsEmpty);
        Assert.Equal(rows, held.Select(e => e.Row));

        foreach (var e in held) e.Row.TranslationBody = $"({UserMessages.RetryGaveUpRow()})";
        Assert.All(rows, r => Assert.StartsWith("(", r.TranslationBody));
    }

    /// <summary>The story adds no <c>Run.Text</c> and therefore no new render case (I15) — the retry
    /// badge is E7.S6's, with its own <c>Mode=OneWay</c> and its own <c>TemplateRenderTests</c> entry.
    /// Pinned because "while we're here, let's show a little ⟳ on the pending rows" is the plausible
    /// edit, and <c>Run.Text</c> binds TwoWay by default and throws once per rendered item on a
    /// get-only property.</summary>
    [Fact]
    public void This_story_adds_no_new_binding_to_either_feed()
    {
        foreach (var file in new[] { "Views/MainWindow.xaml", "Views/CompactOverlay.xaml" })
        {
            var xaml = File.ReadAllText(RepoFile(file));
            Assert.DoesNotContain("PendingRetry", xaml);
            Assert.DoesNotContain("Attempts", xaml);

            // …and the trap itself, not just this story's name for it (review): EVERY bound Run in
            // both feeds says Mode=OneWay. Run.Text binds TwoWay by default and throws once per
            // rendered item on a get-only property — the badge edit this case exists to stop would
            // have passed a scan for the word "PendingRetry".
            foreach (System.Text.RegularExpressions.Match run in
                     System.Text.RegularExpressions.Regex.Matches(xaml, @"<Run\b[^>]*>"))
            {
                if (!run.Value.Contains("{Binding", StringComparison.Ordinal)) continue;
                Assert.True(run.Value.Contains("Mode=OneWay", StringComparison.Ordinal),
                            $"{file}: {run.Value} — Run.Text binds TwoWay by default (I15)");
            }
        }
    }

    // =============================================================================================
    //  Ruling E5-f — a refused tick backs off, and still counts as no error
    // =============================================================================================

    /// <summary>
    /// <b>E5-f.</b> Nothing bounded a <c>Refused</c> streak: a tier refusing from inside the tick left
    /// the loop capturing and OCR-ing a full frame every ~700 ms, which is the exact cost OQ-B's full
    /// pause removes, reached through the branch that throws instead of the one that skips. A refusal
    /// now advances the same curve a pause does — and still reaches no counter at all (E5-c), because
    /// backing off is not the same as blaming.
    /// </summary>
    [Fact]
    public void E5_f_a_refused_tick_backs_off_exactly_like_a_paused_one_and_counts_as_no_error()
    {
        Assert.Equal(1, LiveTickPolicy.NextBackoffSteps(0, LiveTickOutcome.Refused));
        Assert.Equal(LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.Paused),
                     LiveTickPolicy.NextBackoffSteps(3, LiveTickOutcome.Refused));
        // …and the two other outcomes are unchanged: a sent failure belongs to the error counter,
        // an empty tick leaves the curve exactly where it was (E5.S1 AC 4).
        Assert.Equal(2, LiveTickPolicy.NextBackoffSteps(2, LiveTickOutcome.SentFailure));
        Assert.Equal(2, LiveTickPolicy.NextBackoffSteps(2, LiveTickOutcome.Empty));
        Assert.Equal(0, LiveTickPolicy.NextBackoffSteps(9, LiveTickOutcome.Translated));

        // Five refusals in a row do not stop LIVE, however long the streak backs off for.
        var errors = new LiveErrorTracker();
        for (int i = 0; i < TranslationPolicy.LiveAutoStopThreshold + 3; i++)
            Assert.False(errors.Record(LiveTickOutcome.Refused));
        Assert.Equal(0, errors.ConsecutiveFailures);
    }

    /// <summary>The loop half of E5-f: the catch reads the outcome it already classified and applies
    /// the back-off wait from it, with the wait computed from the CURRENT step count and the counter
    /// advanced after it — the same order the skipped branch uses, so the first refused tick still
    /// waits the plain interval.</summary>
    [Fact]
    public void E5_f_the_catch_applies_the_back_off_it_classified()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));

        Assert.Contains("var outcome = LiveTickPolicy.Classify(ex);", live, StringComparison.Ordinal);
        Assert.Contains("if (outcome == LiveTickOutcome.Refused)\n                    pausedWait = "
                        + "LiveTickPolicy.BackoffWaitMs(CurrentLiveIntervalMs(), backoffSteps);",
                        live.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("backoffSteps = LiveTickPolicy.NextBackoffSteps(backoffSteps, outcome);",
                        live, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  Ruling E5-e — the SENT failure is the one that is rethrown
    // =============================================================================================

    /// <summary>
    /// <b>E5-e, and the shape it is about.</b> Line 1 is sent and times out; the gate closes behind
    /// it; lines 2..N come back as <c>NotSent</c> refusals. Throwing the LAST failure handed the chain
    /// a refusal — so the tier read as "skipped, not tried", the chain ended
    /// <c>AllProvidersPaused</c>, and <c>Classify</c> called a tick that had burned a request and a
    /// timeout <i>Refused</i>: no error counted, and no next tier tried. The SENT failure is the
    /// informative one, and it is what the loop now rethrows.
    /// </summary>
    [Fact]
    public async Task E5_e_a_sent_failure_is_preferred_over_the_refusals_that_followed_it()
    {
        var sent = new TranslationException(TranslationErrorKind.Timeout, "the endpoint did not answer");
        var refused = new TranslationException(TranslationErrorKind.RateLimited, "refused at admission")
        { NotSent = true };

        int line = 0;
        var thrown = await Assert.ThrowsAsync<TranslationException>(() =>
            PerLineFallback.RunAsync(new[] { "one", "two", "three" },
                (_, _) => throw (++line == 1 ? sent : refused),
                "google-gtx", afterFailedBatch: false, CancellationToken.None));

        Assert.Same(sent, thrown);
        Assert.False(thrown.NotSent);
        // …which is the whole point: the tick counts, and the chain treats the tier as tried.
        Assert.Equal(LiveTickOutcome.SentFailure, LiveTickPolicy.Classify(thrown));
    }

    /// <summary>The fallback half: when nothing left the machine at all, the last refusal is thrown
    /// as it always was — <c>NotSent</c> intact, so the chain still tells "this tier was skipped"
    /// from "this tier tried and failed" and the tick still counts as no error (E3-b / E5-c).</summary>
    [Fact]
    public async Task E5_e_with_no_sent_failure_the_last_refusal_is_still_what_is_thrown()
    {
        // Neither kind latches the loop (only RateLimited/Blocked do, rule 2), so both lines really
        // are asked and "the LAST refusal" is a claim with two candidates behind it.
        var first = new TranslationException(TranslationErrorKind.Unavailable, "first") { NotSent = true };
        var last = new TranslationException(TranslationErrorKind.Timeout, "last") { NotSent = true };

        int line = 0;
        var thrown = await Assert.ThrowsAsync<TranslationException>(() =>
            PerLineFallback.RunAsync(new[] { "one", "two" },
                (_, _) => throw (++line == 1 ? first : last),
                "google-gtx", afterFailedBatch: false, CancellationToken.None));

        Assert.Same(last, thrown);
        Assert.True(thrown.NotSent);
        Assert.Equal(LiveTickOutcome.Refused, LiveTickPolicy.Classify(thrown));
    }

    /// <summary>A line that DID come back still switches the throw off entirely (E3-g): placeholders
    /// are for a partial failure, and a partial result is not the provider's failure.</summary>
    [Fact]
    public async Task E5_e_does_not_disturb_the_partial_result_rule()
    {
        var sent = new TranslationException(TranslationErrorKind.Timeout, "one line died");

        int line = 0;
        var result = await PerLineFallback.RunAsync(new[] { "one", "two" },
            (t, _) => ++line == 1 ? Task.FromResult("[" + t + "]") : throw sent,
            "google-gtx", afterFailedBatch: false, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("[one]", result[0]);
        Assert.StartsWith("(", result[1]);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Fails once, then answers — and counts how often it was asked. TP-LIVE-14's second
    /// half is about where a string WENT, and a cache is only observable through what it stops
    /// asking for.</summary>
    private sealed class FlakyTranslator : ITranslator
    {
        private readonly Exception _ex;
        public int Calls;
        public FlakyTranslator(Exception ex) => _ex = ex;

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
            => TranslateLinesAsync(new[] { text }, s, t, ct).ContinueWith(x => x.Result[0], ct);

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
                                                      CancellationToken ct = default)
        {
            if (++Calls == 1) throw _ex;
            return Task.FromResult(lines.Select(l => "[" + l + "]").ToList());
        }
    }

    /// <summary>The braced block that starts at the first <c>{</c> at or after <paramref name="from"/>,
    /// brace-matched. Same shape as <c>LivePauseTests.BracedBlock</c>.</summary>
    private static string BracedBlock(string source, int from)
    {
        Assert.True(from >= 0, "the construct this scan is about was not found");
        int open = source.IndexOf('{', from);
        Assert.True(open >= 0, "no block after the construct");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail("the block is not closed");
        return "";
    }

    /// <summary>Code only — a <c>//</c> mention is prose, and every branch this file scans is heavily
    /// commented about the very things it must not do.</summary>
    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static string ServiceSource(string name) => RepoFile(Path.Combine("Services", name));

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");

        var path = Path.Combine(dir!.FullName, relative);
        Assert.True(File.Exists(path), $"expected {relative} at the repo root");
        return path;
    }
}

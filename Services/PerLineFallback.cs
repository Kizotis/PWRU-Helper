using System.Runtime.ExceptionServices;

namespace PWRUHelper.Services;

/// <summary>
/// The per-line loop the join/split providers share — <b>one</b> copy of it, extracted by E3.S8 when
/// the Rule of Three was reached: <see cref="GoogleGtxTranslator"/> had it, E3.S4's
/// <see cref="GoogleDictTranslator"/> copied it byte for byte (~57 lines including <c>SafeOne</c>),
/// and this story adds a third rule to it. Three copies of a loop whose whole job is to be careful
/// about cancellation, latching and caps is three places for one of those to rot — which is exactly
/// how this project lost three releases to an unfiltered <c>OperationCanceledException</c> once.
///
/// <para><b>What it is not.</b> It is not a batch strategy and it never joins anything: the join, the
/// split and the count comparison stay in the provider, because they are the provider's own body
/// shape (§6.3). This file receives a list of lines and a way to translate ONE of them, and owns
/// only what all of them share — the order of the answers, which failure latches, which failure is
/// thrown, and the cap.</para>
///
/// <para><b>The three rules, in the order a reader meets them:</b>
/// <list type="number">
/// <item><b>Keep the successes.</b> Translating line 30 of 40 and hitting a 429 must not throw away
/// the 29 good translations above it — the reason this is a loop with a result list and not a
/// <c>Task.WhenAll</c>.</item>
/// <item><b>Only a refusal latches</b> (I16, narrowed by E3.S6's AC 4). Asking a provider that just
/// said "stop" for thirteen more lines is how a soft block becomes a hard one; that reasoning holds
/// for <see cref="TranslationErrorKind.RateLimited"/> and <see cref="TranslationErrorKind.Blocked"/>
/// and for nothing else. A <c>BadResponse</c> or a timeout fails its own line and no other.</item>
/// <item><b>The cap</b> (<see cref="TranslationPolicy.PerLineCap"/>, E3.S8) — see below.</item>
/// </list></para>
///
/// <para><b>Where the cap applies, and where it deliberately does not (ruling E3-e).</b> It bounds
/// the fan-out that follows a <i>failed batch</i>: one bad response then turns into at most
/// <c>PerLineCap</c> requests instead of one per line — the measured amplifier
/// (<c>analyse…</c> S6/A11: a mismatch on a 14-line LIVE tick cost up to 30 requests inside one
/// tick). It does <b>not</b> bound a provider whose PRIMARY strategy is per line: that is
/// <see cref="GoogleDictTranslator"/> today under OQ-A, whose answer explicitly accepts "≈2× the
/// LIVE request volume", so refusing a 14-line tick would refuse the behaviour the owner approved.
/// That path is paced by the §5.4 rate ceiling and stopped by the gate. Callers say which they are
/// with <c>afterFailedBatch</c>, and the two are pinned by separate tests.</para>
///
/// <para><b>And a tier that translated NO line throws</b> (ruling E3-f, <b>widened by E3-g</b>). A
/// list of placeholders reads to <see cref="ChainTranslator"/> as a SUCCESS — the chain receives a
/// list and has no way to see that every element of it is an apology — so a tier that failed on
/// every line would end the chain with a healthy tier untried. If nothing was translated, the last
/// failure is rethrown <i>as it is</i> — carrying its <c>RetryAt</c> and the <c>NotSent</c> flag
/// only <see cref="HttpProviderCore"/> may set, so the chain can still tell a tier that was refused
/// at admission from one that really spoke to the endpoint and failed.</para>
///
/// <para><b>And WHICH failure is rethrown is ruling E5-e</b> (E5.S3): the last one that was actually
/// <i>sent</i> if there is one, and only otherwise the last refusal. "The last failure" was the
/// wrong answer for the commonest shape of a dying tick — one request sent and timed out, the gate
/// closed behind it, every line after it refused — where it made a tick that had burned a request
/// report as one that had cost nothing.</para>
///
/// <para><b>Why the predicate is "nothing was translated" and not "every failure was a gate
/// reason".</b> E3-f named three kinds (<c>RateLimited</c>, <c>Blocked</c>, a <c>NotSent</c>
/// refusal), and E3.S8's review found the hole that leaves: a SOFT failure on line 1 is a §5.3
/// <c>SoftCooldown</c>, so the gate closes for 5 s and lines 2..N come back as <c>NotSent</c>
/// refusals — but line 1's timeout had already ticked "something other than a gate reason
/// happened", the throw stayed off, and the loop returned N placeholders of which <b>none was a
/// translation</b>. One timeout was enough to hand the chain a "success" with nothing in it. Ruling
/// E3-g widened the predicate to the only thing that actually distinguishes the two cases: <b>was
/// anything translated?</b> Placeholders are for a PARTIAL failure — a list that still contains
/// something the player can read — and nothing else.</para>
///
/// <para><b>What that costs, stated plainly.</b> A tier whose every line failed softly (an
/// unparseable body on all of them) now throws instead of returning placeholders, so the chain asks
/// the next tier — one more provider tried on a page that was going to be unreadable anyway. When
/// no tier is left, the chain rethrows the last failure and the player reads that provider's own
/// sentence rather than a column of "(translation failed: …)" rows, which is the better message of
/// the two.</para>
///
/// <para><b>The placeholder strings are E7.S1's copy and this file's policy</b> (ruling E2-d): they
/// are byte-for-byte what they were in the two providers, and they are not reworded here. All of
/// them start with <c>(</c> and MUST keep doing so — <c>CachingTranslator.IsCacheable</c> refuses to
/// store a value that starts with <c>(</c>, and that is I4. A reworded placeholder that lost the
/// parenthesis would poison the cache with failure text.</para>
/// </summary>
internal static class PerLineFallback
{
    /// <summary>
    /// <b>All three per-line placeholders are now one row text, and that is amendment A5</b>
    /// (E7.S1, ruling E2-d gave this file's copy to it). "A row never carries a §3.1 sentence, a
    /// provider name or a countdown", and three row texts exist in the whole app — the pending
    /// "…", the finished "(not translated — the engines did not come back)" and the cancelled one.
    /// A per-line placeholder is the second of those: this line was not translated and nothing is
    /// coming for it, which is the one fact a row owes the player. The reason belongs to the status
    /// line, once, which is §1's first principle.
    ///
    /// <para>What that fixes, beyond tidiness. The two "rate-limited" spellings said so on rows
    /// that may have failed for any reason at all — the cap's, in particular, borrowed a sentence
    /// about a rate limit for a line nobody had asked — and <c>Failed(ex.Message)</c> could put an
    /// HTTP status code on screen, which is §3's first rule. Both were recorded in E1.S6's review
    /// and both are gone. The diagnostic text is not lost: it is what E1.S5's per-request log
    /// records, and it stops being what the player reads.</para>
    ///
    /// <para>The three names survive because the CALL SITES mean three different things and the
    /// tests read them as three branches. They all render the same row, deliberately.</para>
    ///
    /// <para>The "(" is still added HERE and not by the deck (I4): these are a translator's return
    /// values, so they are the one place <c>CachingTranslator.IsCacheable</c>'s marker really
    /// matters — a placeholder that lost the parenthesis would be stored as a translation.</para>
    /// </summary>
    internal static readonly string SkippedMessage = NotTranslatedRow;

    /// <summary>The line the provider actually refused.</summary>
    internal static readonly string RateLimitedMessage = NotTranslatedRow;

    /// <summary>Every other per-line failure, typed or not. <paramref name="message"/> is kept in
    /// the signature because the call sites have it and the log wants it; it is deliberately not
    /// rendered (§3: no HTTP status code in a user string).</summary>
    internal static string Failed(string message) => NotTranslatedRow;

    private static string NotTranslatedRow => $"({UserMessages.RetryGaveUpRow()})";

    /// <summary>
    /// Translate <paramref name="lines"/> one at a time, in order, returning a list of exactly the
    /// same length — <b>never</b> padded and never reordered (I5).
    /// </summary>
    /// <param name="translateOne">Translates one line. The providers hand in their own
    /// <c>TranslateAsync</c> closed over the source and target languages, so this file needs to know
    /// nothing about either.</param>
    /// <param name="providerId">For the one WARN line the cap emits. Counts only, never text (I11).</param>
    /// <param name="afterFailedBatch">True when this loop is the fallback after a batch that failed
    /// or came back with the wrong count — the ONLY case <see cref="TranslationPolicy.PerLineCap"/>
    /// bounds (ruling E3-e). False for a provider whose primary strategy is per line.</param>
    internal static async Task<List<string>> RunAsync(IReadOnlyList<string> lines,
        Func<string, CancellationToken, Task<string>> translateOne, string providerId,
        bool afterFailedBatch, CancellationToken ct)
    {
        var result = new List<string>(lines.Count);
        bool rateLimited = false;
        int attempted = 0;
        bool capReported = false;
        // The E3-f/E3-g bookkeeping, and it is deliberately only three things: the last failure that
        // COST A REQUEST, the last one that did not, and whether ANY line was actually translated.
        // E3-f used to track "was every failure a gate reason", which is what let one timeout switch
        // the throw off (see the class comment). Captured rather than stored so a
        // non-TranslationException — the shape the generic catch below renders — can be rethrown with
        // its own stack intact too.
        //
        // Two slots and not one is ruling E5-e (E5.S2 review, landed in E5.S3): see the throw below.
        // Named for what they hold rather than for the flag they are keyed on: `NotSent =` has
        // exactly one writer in this app (HttpProviderCore.Paused, ruling E3-b) and
        // ChainTranslatorTests scans production source for it, so a local called lastNotSent would
        // read to that scan as a second writer of a flag no provider may set.
        ExceptionDispatchInfo? lastSentFailure = null;
        ExceptionDispatchInfo? lastRefusal = null;
        bool translatedSomething = false;

        // Which of the two slots a failure belongs in, in the one place that knows: NotSent is set
        // only by HttpProviderCore, on a call the gate refused before anything left the machine
        // (ruling E3-b). Everything else — a typed failure from the endpoint, an untyped throw —
        // was a request that was sent.
        void Remember(Exception failure)
        {
            var captured = ExceptionDispatchInfo.Capture(failure);
            if (failure is TranslationException { NotSent: true }) lastRefusal = captured;
            else lastSentFailure = captured;
        }

        foreach (var line in lines)
        {
            if (rateLimited) { result.Add(SkippedMessage); continue; }

            // A BLANK line is not evidence about anything, and this guard is why (E3.S8 review).
            // Both providers answer "" for one before they reach the gate — TranslateAsync trims
            // and returns early — so it costs no request. Passed to the loop it used to buy two
            // things it had not earned: a slot of the cap it cannot spend, and, worse, a "this tier
            // answered" tick that switched OFF ruling E3-f's throw. One truncated OCR row ("Nick:"
            // with nothing after the colon — LIVE never filters those, and a blank line is never
            // cacheable so it is forwarded on EVERY tick) was therefore enough to make a tier that
            // had been refused on every real line read to ChainTranslator as a success, leaving a
            // healthy tier untried. The loop answers "" itself now, so the answer no longer depends
            // on which provider was handed in.
            if (string.IsNullOrWhiteSpace(line)) { result.Add(""); continue; }

            // The cap counts LINES ATTEMPTED, not requests issued: a line can cost zero requests (the
            // gate refused it) or several (a very long line is chunked), and a bound the reader can
            // check against the input is worth more than one that is exact about a number nobody can
            // see. The comparison is `>=` on the count of lines already attempted, so PerLineCap = 8
            // means eight lines are asked and the ninth is not.
            if (afterFailedBatch && attempted >= TranslationPolicy.PerLineCap)
            {
                if (!capReported)
                {
                    capReported = true;
                    // ONE line per capped loop, and it names counts only (I11). Logging.Warn swallows
                    // its own errors, so a failed log line can never fail a translation.
                    Logging.Warn($"per-line: {providerId} capped at {TranslationPolicy.PerLineCap} "
                                 + $"of {lines.Count} lines after a failed batch");
                }
                result.Add(SkippedMessage);
                continue;
            }

            attempted++;
            try { result.Add(await translateOne(line, ct).ConfigureAwait(false)); translatedSomething = true; }
            // A real cancel must propagate — and ONLY a real one (I3). Unfiltered, this catch rethrew
            // an HttpClient timeout (an OCE whose token is NOT cancelled) as if the user had pressed
            // Stop, throwing away every line already translated above it: exactly what rule 1 exists
            // to prevent. Timeouts do not arrive here as an OCE any more — the core hands them over
            // as a Timeout-kind TranslationException — and the filter stays anyway, because it is the
            // filter and not the core that makes this catch correct.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // Rule 2: only a refusal latches (I16 / E3.S6 AC 4). Unfiltered, this also fired on a
            // BadResponse or a timeout on line 3 of 14 and turned lines 4-14 into
            // "(skipped — rate-limited…)", a sentence that was simply false: nobody was rate-limiting
            // anything.
            catch (TranslationException tex) when (tex.Kind is TranslationErrorKind.RateLimited
                                                            or TranslationErrorKind.Blocked)
            { rateLimited = true; Remember(tex); result.Add(RateLimitedMessage); }
            // Every other typed failure is this line's problem and no other line's. Written out
            // rather than left to the generic catch below (which renders it identically) so the
            // intent survives an edit to that catch: this arm exists to NOT latch.
            catch (TranslationException tex)
            {
                Remember(tex);
                result.Add(Failed(tex.Message));
            }
            catch (Exception ex) { Remember(ex); result.Add(Failed(ex.Message)); }
        }

        // Rulings E3-f and E3-g. Not one line came back translated, so the list below would be
        // nothing but apologies: that is one failure of the PROVIDER, not N failures of N lines, and
        // it is thrown so the chain moves on to a tier that might work. Rethrown as the instance it
        // is — RetryAt, ProviderId and NotSent included — rather than rebuilt, because
        // ChainTranslator reads all three (and classifies anything untyped through the mapper).
        // Through ExceptionDispatchInfo and not `throw`, which would reset the stack to THIS line
        // and lose the throw site inside the core — on the one path the epic exists to make
        // reportable (E3.S8 review).
        //
        // RULING E5-e — WHICH failure, and it is not "the last one" (E5.S2 review). The common shape
        // of a tick that dies is: line 1 is sent and times out, the gate closes behind it, and lines
        // 2..N come back as NotSent refusals. Throwing the LAST of those handed the chain a refusal,
        // so the tier read as "skipped, not tried", the chain ended AllProvidersPaused, and
        // LiveTickPolicy.Classify called the whole tick Refused — a tick that had burned a request
        // and a timeout counted as nothing at all, twice over: the auto-stop never advanced and the
        // next tier was never tried on a chain that thought it had nothing left. The SENT failure is
        // the informative one and it is preferred whenever there is one; the NotSent slot is the
        // fallback for the genuine case where nothing left the machine at all.
        var lastFailure = lastSentFailure ?? lastRefusal;
        if (!translatedSomething && lastFailure is not null) lastFailure.Throw();

        return result;
    }

    /// <summary>
    /// The one-line path: a group of exactly one line takes neither the batch nor the loop. It
    /// differs from the loop on purpose and the difference IS the contract (E3.S3/S4 reviews): a
    /// typed failure THROWS here, because with one line there is no partial result to protect and a
    /// single placeholder returned as a success is precisely what would end the chain early.
    /// </summary>
    internal static async Task<string> SafeOneAsync(string line,
        Func<string, CancellationToken, Task<string>> translateOne, CancellationToken ct)
    {
        try { return await translateOne(line, ct).ConfigureAwait(false); }
        catch (TranslationException) { throw; }
        // I3 asks every OCE catch in the pipeline to say what it filters on: the generic catch below
        // is where a genuine Stop would land, and it would turn the cancel into a translation-failed
        // line instead of propagating.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Failed(ex.Message); }
    }
}

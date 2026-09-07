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
    /// <summary>What a line that was never asked reads as: the loop latched above it, or the cap
    /// stopped it. Copy is E7.S1's (E2-d) — and E7.S1 should know that the cap borrows a sentence
    /// which says "rate-limited" for a reason that is not always a rate limit.</summary>
    internal const string SkippedMessage = "(skipped — rate-limited, try again shortly)";

    /// <summary>The line the provider actually refused.</summary>
    internal const string RateLimitedMessage = "(rate-limited — try again shortly)";

    /// <summary>Every other per-line failure, typed or not.</summary>
    internal static string Failed(string message) => $"(translation failed: {message})";

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
        // The E3-f/E3-g bookkeeping, and it is deliberately only two things: the last failure of
        // ANY kind, and whether ANY line was actually translated. E3-f used to track "was every
        // failure a gate reason", which is what let one timeout switch the throw off (see the class
        // comment). Captured rather than stored so a non-TranslationException — the shape the
        // generic catch below renders — can be rethrown with its own stack intact too.
        ExceptionDispatchInfo? lastFailure = null;
        bool translatedSomething = false;

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
            { rateLimited = true; lastFailure = ExceptionDispatchInfo.Capture(tex); result.Add(RateLimitedMessage); }
            // Every other typed failure is this line's problem and no other line's. Written out
            // rather than left to the generic catch below (which renders it identically) so the
            // intent survives an edit to that catch: this arm exists to NOT latch.
            catch (TranslationException tex)
            {
                lastFailure = ExceptionDispatchInfo.Capture(tex);
                result.Add(Failed(tex.Message));
            }
            catch (Exception ex) { lastFailure = ExceptionDispatchInfo.Capture(ex); result.Add(Failed(ex.Message)); }
        }

        // Rulings E3-f and E3-g. Not one line came back translated, so the list below would be
        // nothing but apologies: that is one failure of the PROVIDER, not N failures of N lines, and
        // it is thrown so the chain moves on to a tier that might work. Rethrown as the instance it
        // is — RetryAt, ProviderId and NotSent included — rather than rebuilt, because
        // ChainTranslator reads all three (and classifies anything untyped through the mapper).
        // Through ExceptionDispatchInfo and not `throw`, which would reset the stack to THIS line
        // and lose the throw site inside the core — on the one path the epic exists to make
        // reportable (E3.S8 review).
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

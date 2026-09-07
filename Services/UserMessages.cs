using System.Globalization;

namespace PWRUHelper.Services;

/// <summary>
/// Every sentence a player reads <b>through <c>MainWindow.Friendly</c></b> when a translation
/// fails, in one UI-free table keyed by <see cref="TranslationErrorKind"/>. It exists because the
/// copy was scattered across two providers and a code-behind switch: fifteen literals, three of
/// which contradicted each other (DeepL derived the Kind and the sentence from two independent
/// switches over the same status), and one of which sent the user to a "Settings" tab this app
/// does not have.
///
/// <b>What this table does NOT yet own</b>, so nobody reads the paragraph above as a finished job:
/// the per-line fallback in <c>GoogleGtxTranslator.cs</c> writes three placeholder strings straight
/// into a feed row as if they were translations — "(skipped — rate-limited, try again shortly)",
/// "(rate-limited — try again shortly)" and "(translation failed: {ex.Message})". The middle one
/// used to be produced by a <c>catch (TranslationException)</c> that fired for <i>every</i> Kind,
/// so a dead network read "rate-limited" on the most common OCR path; E3.S6 narrowed that catch to
/// <c>RateLimited</c>/<c>Blocked</c>, which is what the sentence claims — the wording is still
/// unowned here. They start with "(" by
/// design (I4) and they are results, not statuses, so they are not <c>Friendly</c>'s to render.
/// <b>E2.S5's <c>HttpProviderCore</c> owns folding them into this table.</b>
///
/// Ruling GAP-4 (<c>docs/investigations/README.md</c>): the copy deck is a single table so that
/// "no HTTP code in any user string" and "every string exists exactly once" can be *tested*
/// (test-plan H10/H11) instead of hoped for. Wording is Sally's
/// (<c>02-traduction/ux-mode-degrade.md</c> §3.1); the shape — one sentence per Kind — is
/// Winston's (<c>02-traduction/architecture-cible.md</c> §4.4, §16).
///
/// Three rules keep this file honest, and all three are enforced by <c>UserMessagesTests</c>:
///
/// <list type="number">
/// <item><b>No UI, no formatting (I2).</b> No <c>System.Windows</c> type, no timer, no settings
///       read, and above all no countdown: <c>{t}</c> is rendered from
///       <see cref="TranslationException.RetryAt"/> by <c>MainWindow</c> at display time, never
///       here. The sentences below are therefore the no-countdown form; E7.S1 adds the
///       countdown-bearing variants together with the provider chip.</item>
/// <item><b>Never start a sentence with "(" (I4's marker).</b> The two feed-row call sites wrap
///       this text in parentheses (<c>MainWindow.Live.cs</c>, <c>MainWindow.Ocr.cs</c>), so a
///       leading paren here renders "((…))". The deeper reason is that "(" is the app's marker
///       for "this is a failure, not a translation" — it is what
///       <c>CachingTranslator.IsCacheable</c> refuses to store — and a copy-deck sentence must
///       never be mistakable for one of those placeholders. To be exact about the blast radius:
///       nothing this table returns is ever a translator's return value, so a leading paren here
///       could not itself poison the cache; the strings that pass that guard live in
///       <c>GoogleGtxTranslator</c>'s per-line fallback.</item>
/// <item><b>No terminal full stop.</b> Every one of today's call sites <i>joins</i> this text
///       into a longer line — "Failed: {s}", "({s})", "Live hiccup ({s}) — retrying…",
///       "Live stopped after repeated errors ({s}).", read-once's four §3.3 statuses (E5.S4
///       replaced "OCR failed: {s}" with them),
///       "⚠ {s} — your text is kept, press Enter to retry." — and none of them renders it alone.
///       With the deck's own full stop those read ".)." , "(… shortly.)" and ". — your text is
///       kept", so the stop is dropped here rather than at the joins: AC 2 freezes the two
///       feed-row joins, which makes the sentence the only place the fix can live. §3.1 writes
///       these sentences terminated because it writes them for the standalone status lines of
///       E7.S1 — that story re-terminates them and applies §3.3's "lower-cased at the join" rule
///       to the joins it rewrites.</item>
/// </list>
///
/// Sally's deck writes <c>{P}</c> for the provider's user-facing name ("Google", "DeepL", …).
/// Naming the answering provider needs <c>ChainTranslator.LastOutcome</c> (E4/E6/E7); until then
/// every sentence says "the translation service" and none of them lies about which engine failed.
/// Sally's trailing "— trying another engine" is likewise held back: these sentences are rendered
/// only once the whole attempt has already failed, and principle 4 of §1 is "honest status only".
/// </summary>
internal static class UserMessages
{
    // ---- one sentence per typed error kind (ux-mode-degrade.md §3.1) --------------------------
    // Sentence case, no terminal full stop (see the rule above), no HTTP code, no provider
    // internal ("gtx", "dict-chrome-ex", "429"), no exclamation mark, <= 80 characters — §3's
    // general rule. That 80 is NOT the compact overlay's budget: the overlay's quick-reply line
    // adds 46 characters of its own chrome around this text (CompactOverlay.xaml.cs), and §3.4
    // asks for ~60 in total there. Meeting that needs the per-surface short forms of §3.4, which
    // arrive with E7.S1; until then the overlay wraps.

    /// <summary>429, or a Google "automated queries" page. Sally: "{P} asked us to slow down —
    /// paused for {t}." The pause is E2's gate; until it exists there is no {t} to promise.</summary>
    public const string RateLimited = "The translation service asked us to slow down — try again in a moment";

    /// <summary>403 with no key, a captcha or an abuse interstitial — the honest wording for a
    /// block that is about this connection, not about anything the user typed.</summary>
    public const string Blocked = "The translation service is refusing requests from your connection right now";

    /// <summary>5xx.</summary>
    public const string Unavailable = "The translation service is down right now — try again shortly";

    /// <summary>The 12 s HttpClient timeout — an OCE whose token is NOT cancelled.</summary>
    public const string Timeout = "The translation service took too long to answer — try again shortly";

    /// <summary>DNS, TLS, connect, proxy. The one kind with no provider in it: when nothing
    /// resolves, naming an engine would be noise. Verbatim from §3.1.</summary>
    public const string Network = "No internet connection — nothing can be translated until it is back";

    /// <summary>A body that is not the provider's shape, or a batch count mismatch.</summary>
    public const string BadResponse = "The translation service sent something we could not read — try again shortly";

    /// <summary>DeepL 456, Azure's out-of-quota envelope. Keys only. "for this month" is what both
    /// free tiers actually do; the About tab is where the key that ran out lives.</summary>
    public const string QuotaExhausted = "Your free translation quota is used up for this month";

    /// <summary>401, or 403 while a key was sent. "About" — not "Settings": the key box is on the
    /// About tab, and the sentence this replaces sent the user to a tab that has never existed in
    /// this app.
    /// <para><b>"Keys only" is now what the mapper guarantees too.</b> Row 5 used to be
    /// <c>if (code == 401) return AuthFailed;</c>, unconditional on <c>keyWasSent</c> unlike rows
    /// 6/7/8 for 403, so a 401 on the keyless Google path (an authenticating proxy, a captive
    /// portal) sent a user who has never typed a key to an empty key box. Ruling <b>E2-g</b> closed
    /// it in E2.S2: <c>ProviderErrorMapper</c> guards 401 on <c>keyWasSent</c> and a keyless 401 is
    /// <c>Blocked</c>. The item E1.S6's review recorded for E6 is done — nothing to reopen.</para></summary>
    public const string AuthFailed = "Your API key was refused — check it in About, or clear it";

    /// <summary>Not reachable yet: nothing raises this Kind until the chain has gates (E2.S3).
    /// Present so the lookup below is total the day it does. E7.S1 replaces this with §3.1's
    /// countdown form ("All engines are paused — next try in {t}. Nothing you need to do."), which
    /// cannot be written here: {t} is formatting, and formatting stays out of Services/ (I2).</summary>
    public const string AllProvidersPaused = "All engines are paused — nothing you need to do; it retries on its own";

    /// <summary>
    /// The sentence for a <paramref name="kind"/>, or <c>null</c> when this table deliberately has
    /// none and the caller should fall back to the exception's own message.
    ///
    /// Two kinds answer null, and the <c>_</c> arm is what covers both without naming either.
    /// <c>Unknown</c> is §4.4's pass-through — the mapper's honest last resort carries whatever the
    /// provider said, and inventing a sentence for it would hide the one case worth reading a log
    /// over. The genuine-user-cancel kind must never reach a surface at all
    /// (<c>ux-mode-degrade.md</c> §2.1, and the contract in <c>TranslationErrors.cs</c>), and it
    /// may not even be *named* in production source (TP-MAP-17), which is the second reason this
    /// is a default arm and not an explicit one.
    ///
    /// The arm is also what keeps the switch total: a Kind added later gets the exception's message
    /// instead of an <c>ArgumentOutOfRangeException</c> thrown at a player mid-fight.
    /// </summary>
    public static string? Sentence(TranslationErrorKind kind) => kind switch
    {
        TranslationErrorKind.RateLimited => RateLimited,
        TranslationErrorKind.Blocked => Blocked,
        TranslationErrorKind.Unavailable => Unavailable,
        TranslationErrorKind.Timeout => Timeout,
        TranslationErrorKind.Network => Network,
        TranslationErrorKind.BadResponse => BadResponse,
        TranslationErrorKind.QuotaExhausted => QuotaExhausted,
        TranslationErrorKind.AuthFailed => AuthFailed,
        TranslationErrorKind.AllProvidersPaused => AllProvidersPaused,
        _ => null,
    };

    /// <summary>
    /// The whole of what <c>MainWindow.Friendly</c> renders, as a pure function — no window, no
    /// dispatcher, no STA, so the copy can be asserted headlessly (GAP-4: "tests assert on it").
    /// The two raw-exception arms are the ones the pipeline can still let through untyped, and
    /// they answer with the same sentences as their typed twins, which is the whole point of the
    /// table: a dead network reads the same whether or not it was classified on the way up.
    /// </summary>
    public static string For(Exception ex) => ex switch
    {
        TranslationException te => Sentence(te.Kind) ?? te.Message,
        System.Net.Http.HttpRequestException => Network,
        TaskCanceledException => Timeout,
        _ => ex.Message,
    };

    // ---- what a read-once says when it is over (ux-mode-degrade.md §3.3) ----------------------
    //
    // METHODS, not consts, and the difference is not cosmetic. Every one of these is parameterised
    // on what the read actually DID — how many lines were read, how many carry a translation, and
    // why the rest do not — which is the whole of the story that added them: "Done" is a claim, and
    // a claim has to be earned line by line. They are deliberately outside the Sentence(kind) table
    // above: that table is keyed by error kind, its house-rule tests read every public const in
    // this type as one of its rows, and none of these is a row — each one JOINS a row of it.
    //
    // The countdown is still not formatted here (I2): ReadOncePaused takes the "{t}" text already
    // rendered by MainWindow, exactly as LivePausedStatus renders it for the LIVE loop.

    /// <summary>The one sentence that may say "Done", and the caller may only reach it when every
    /// line read has a translation (UX hint 4 / TP-ONCE-02). Unchanged wording — what changed is
    /// that it is now one branch of four instead of the only thing a read ever said.</summary>
    public static string ReadOnceAllTranslated(int lines)
        => $"Done — {lines} line(s) translated.";

    /// <summary>Some lines came back and some did not. The count is of ROWS that carry a real
    /// translation, never of lines sent, and <paramref name="error"/> may legitimately be null: a
    /// provider's per-line fallback fills the gaps it could not do with its own placeholders and
    /// throws nothing, so there is a partial result with no exception behind it. The sentence then
    /// stops after the counts rather than inventing a reason it does not have.</summary>
    public static string ReadOncePartlyTranslated(int lines, int translated, Exception? error)
        => $"Read {lines} line(s) — {translated} translated, {lines - translated} could not be." + Because(error);

    /// <summary>A person ended the read — a second press, ■ Stop, or closing the window. §2.1 says
    /// "Cancelled" is not a STATE, and it is not: nothing is degraded, nothing is retrying, no chip
    /// and no countdown. But §1's first principle is one message per state and its fourth is honest
    /// status, and a status line left reading "Reading…" over a read that has stopped is neither —
    /// it is the same lie as "Done" over an empty result, told the other way round.
    ///
    /// <para>Terminated, unlike the §3.1 table above, because this one is rendered ALONE on the
    /// status line and joins nothing (the E1.S6 no-terminal-stop rule is about the joins).</para></summary>
    public static string ReadCancelledStatus() => "Read cancelled.";

    /// <summary>What the rows of a cancelled read say, wrapped in I4's "(" by the call site like
    /// every other non-translation. They may not be left on "…": a row that stays pending for ever
    /// is exactly what makes a player press the button again (amplifier A7), which is the thing this
    /// story exists to stop — and the read that owned them is over, so nothing will ever fill them.
    ///
    /// <para><b>E5.S3:</b> a row carrying this is FINISHED, not failed. A cancelled read is one the
    /// player refused; re-sending it would spend the request they just declined, so the retry pass
    /// must not pick these up.</para></summary>
    public static string ReadCancelledRow() => "not translated — read cancelled";

    // ---- what a row says between two attempts, and when there is no attempt left (§9.3, §2.2) ----
    //
    // Methods for the same reason the read-once statuses are: they are ROW text, not rows of the
    // Sentence(kind) table, and the house-rule tests read every public const in this type as one of
    // those rows. Here under ruling GAP-4 all the same — the copy deck is one file, so E7.S1 opens
    // one file.

    /// <summary>
    /// <b>A row waiting for the next drain, and it is the ellipsis it already was</b> (E5.S3, T2).
    ///
    /// <para>§9.3 sketches «Sally: retrying…» and <c>ux-mode-degrade.md</c> §2.2's S5 row says
    /// pending rows keep the existing "…". The two are reconciled in favour of §2.2, and the reason
    /// is UX principle 5 rather than economy: <b>a row never carries a countdown</b> and, by the same
    /// argument, never carries a status — there is exactly one explanation per window and it lives on
    /// the status line. A row that says "retrying…" is a second one, on every row, saying less than
    /// the line above it already does.
    /// </para>
    ///
    /// <para>What AC 2 actually requires of it is the half that matters: it is deliberately <b>not</b>
    /// "("-prefixed, so it reads as pending rather than terminal — a "(" here would be I4's failure
    /// marker on a row that has not failed yet. It is safe for the identical reason the marker exists:
    /// this string is written by the UI onto a row and is never a translator's return value, so it
    /// cannot reach <c>CachingTranslator.IsCacheable</c>. <b>E7.S1</b> owns the final copy, together
    /// with the retry badge (E7.S6) that is the honest place for "retrying".</para>
    /// </summary>
    public static string PendingRetryRow() => "…";

    /// <summary>The row §2.2 calls a "given-up" one: the drain has spent
    /// <c>TranslationPolicy.PendingRetryMaxAttempts</c> on it and there is nothing left to wait for.
    /// Wrapped in I4's "(" by the call site like every other non-translation, which is the whole
    /// distinction AC 2 draws — <b>only the given-up form is parenthesised</b>.
    ///
    /// <para>It names no engine and no reason on purpose. The reason belongs to the status line,
    /// which said it while the row was pending; what the row owes the player is the one fact the
    /// status line cannot carry once it has moved on — <i>this</i> message was never
    /// translated.</para></summary>
    public static string RetryGaveUpRow() => "not translated — the engines did not come back";

    /// <summary>Nothing came back. This is the sentence the false "Done" used to cover
    /// (amplifier A7: a player told "Done" over an empty result presses the button again).</summary>
    public static string ReadOnceNoneTranslated(int lines, Exception? error)
        => $"Read {lines} line(s) — none could be translated." + Because(error);

    /// <summary>Every engine was inside a block window, so not one line could be translated — at a
    /// cost of <b>zero requests</b>, which is what the pause is actually about.
    ///
    /// <para><b>{n} is now known, and that is ruling E5-g</b> (E5.S4 review). E5.S4 shipped this
    /// sentence from a check that ran BEFORE the capture, so there was no line count to give and
    /// §3.3's "Read {n} line(s) — all engines are paused" could not be written; worse, a read whose
    /// every line was already in the cache was refused although it needed no provider at all. The
    /// check is gone: read-once captures and OCRs (both local), the cache serves what it can, and
    /// the pause is now <i>reported</i> — it is the chain's own
    /// <see cref="TranslationErrorKind.AllProvidersPaused"/>, raised without sending anything.</para>
    ///
    /// <para>§3.3's row goes on to promise the rows "will fill in when one is back". That promise is
    /// deliberately NOT made here: the E5.S3 retry queue is drained by the LIVE loop, and no
    /// read-once can run while that loop does (the OCR engine is shared and non-reentrant, so one
    /// entry point stops LIVE first and the other refuses) — so a read-once row never has a drain
    /// coming for it and says what went wrong instead of waiting on "…". Final wording is
    /// E7.S1's.</para></summary>
    public static string ReadOncePaused(int lines, string? tryAgainIn)
        => tryAgainIn is null
            ? $"Read {lines} line(s) — all engines are paused. Try again shortly."
            : $"Read {lines} line(s) — all engines are paused. Try again in {tryAgainIn}.";

    /// <summary>The screen itself could not be read — a capture or an OCR failure, not a
    /// translation one. Replaces "OCR failed: …", which named a component the player does not have
    /// and cannot act on (§3.3).</summary>
    public static string ReadFailed(Exception error)
        => Terminated($"Could not read the screen: {LowerAtJoin(For(error))}");

    // ---- the About tab's key boxes (ux-mode-degrade.md §3.7, ruling GAP-4) ---------------------
    //
    // METHODS for the same reason as everything above them, and it is not a style choice: the
    // house-rule tests read every public CONST of this type as a row of the Sentence(kind) table
    // and assert that set equals what the lookup can return. None of these is a row — they are the
    // About tab's own copy — so a const here would fail five tests at once. (E6.S3.)

    /// <summary>The half-entered credential the Save button refuses (E6.S3 AC 5). It names the
    /// missing FIELD and never the value: I11 applies to a status line as much as to a log.</summary>
    public static string AzureNeedsARegion()
        => "Azure also needs the region your resource is in — pick or type it, then Save";

    // [There is no "Azure also needs your key" sentence any more — ruling E6-e removed the case it
    //  answered. An empty key is no longer half a pair: it is the gesture that removes Azure, and
    //  it clears the region with it. E6.S3's review recorded the refusal as a dead end ("paste it
    //  above" is an answer to a question the user did not ask) and referred the AC change to
    //  Winston; this is his answer. The remaining refusal is the one with no other reading — a key
    //  with no region, which cannot be sent.]

    /// <summary>A key or region pasted with a control character inside it (a line break picked up
    /// from the portal). Trim only reaches the ends, an HTTP header may carry neither, and the
    /// provider would refuse it as a failed TRANSLATION — so the Save button refuses it as a bad
    /// FIELD, which is the thing the player can actually act on.</summary>
    public static string AzureCredentialUnsendable()
        => "That key or region contains characters that cannot be sent — re-paste it";

    /// <summary>§3.7's "cleared" row, and the About tab's resting state. It names Google alone:
    /// Edge is not in the chain (ruling E3-d), and §1's fourth principle is honest status.</summary>
    public static string AzureNoKeyStatus()
        => "○ No Azure key — using the free engines (Google)";

    /// <summary>Configured, and precise about the half that is easy to get wrong: what a key buys
    /// is what the user WRITES. Reading the screen is E6.S4's opt-in and is not implied here.</summary>
    public static string AzureKeySetStatus(string region)
        => $"● Azure key set ({region}) — used for what you write. Screen reading stays on the free engines";

    /// <summary>The same line once the user has opted the key into the screen reader (E6.S4). It
    /// exists as a second sentence rather than a suffix because it is a different STATEMENT: the
    /// one above promises the free engines will keep reading the screen, and a status line that
    /// keeps saying so while the LIVE loop spends the user's quota is precisely the dishonest
    /// status §1's fourth principle forbids. Which of the two shows is decided by
    /// <c>TranslationChains.AzureReadsTheScreen</c> — the same predicate the read chain is built
    /// from, so the line cannot claim a tier the builder did not construct.</summary>
    public static string AzureKeySetForReadingStatus(string region)
        => $"● Azure key set ({region}) — used for what you write AND for screen reading";

    /// <summary>
    /// <b>The cost of the opt-in, in the one place the user decides</b> (UX-DR15, <c>ux</c> §4.2's
    /// mockup, ruling GAP-4 — it lives here and not in the XAML so it exists exactly once and E6.S1's
    /// verdict on the free tier changes ONE literal).
    ///
    /// <para><b>The "20 to 40 hours" was re-derived for E6.S4, not inherited</b> — <c>ux</c> §4.3
    /// derives the figure and then says in as many words that if the caches are merged the
    /// multiplier drops and the sentence must be re-derived rather than left to rot. E4.S4 shipped
    /// the shared cache, so here is the arithmetic with its inputs:</para>
    /// <list type="bullet">
    /// <item>a heavy LIVE user is 5.8 M characters over 120 h (<c>benchmark…</c> §8.1) ⇒ ≈48 k
    /// characters per hour of busy chat;</item>
    /// <item>Azure F0 is 2 M characters a month ⇒ <b>41 h</b> at a billing multiplier of ×1.0;</item>
    /// <item>§8.1's multiplier was ×1.3–2.0 from three terms: two batches per tick (<c>ru</c> and
    /// <c>auto</c>), retries plus uncached failure placeholders, and the read and write paths
    /// holding TWO caches that bill the same string twice. E4.S4 removed the third — one store
    /// behind all three chains — and the <c>ru</c>/<c>auto</c> merge was evaluated and <b>rejected</b>
    /// with arithmetic (README, Phase 2 closure), so the dominant term stands. The double-billing
    /// term was the smallest of the three anyway: it only ever billed twice what BOTH paths
    /// translated, and a LIVE-heavy user writes a small fraction of what they read. Low end
    /// ×1.3 → ≈×1.2; high end unchanged at ×2.0.</item>
    /// <item>41 h ÷ 2.0 … 41 h ÷ 1.2 = <b>≈21–35 hours</b>, inside the 20–40 the copy promises. The
    /// sentence therefore ships unchanged — which is the conclusion of the re-derivation, not a
    /// reason to have skipped it.</item>
    /// </list>
    /// <para>Still subject to <b>E6.S1 (U4)</b> for its first half: if F0 turns out to be
    /// trial-limited rather than a standing monthly allowance, this literal is what changes.</para>
    /// </summary>
    public static string AzureForReadingHint()
        => "Azure gives you 2 million characters a month for free — roughly 20 to 40 hours of busy "
         + "chat. Screen reading is off by default because live mode reads every new line and can "
         + "use it up in a few evenings.";

    /// <summary>ux flow (c).1, on the toast.</summary>
    public static string AzureKeySavedToast() => "Azure key saved — used when you write";

    /// <summary>…and the same event the other way round: an emptied key is not an error. Since
    /// ruling <b>E6-e</b> it is the whole gesture — one box cleared removes Azure, region and all —
    /// so the toast speaks for the pair.</summary>
    public static string AzureKeyClearedToast() => "Azure key cleared — using the free engines";

    // ---- "Test key" (ux-mode-degrade.md §3.7, rulings GAP-4 / E6-b) ---------------------------
    //
    // METHODS, like every About-tab sentence above them, and for the same reason: the house-rule
    // tests read every public CONST of this type as a row of the Sentence(kind) table, and none of
    // these is a row — they are the About tab's own copy, terminated because each is rendered ALONE
    // on a status line and joins nothing. They also carry §3.7's glyphs (✓ ✕ ⚠ ○), which are
    // glyphs and not status codes.
    //
    // Ruling E6-b is why there are TWO labels. DeepL can be checked for free — `GET /v2/usage` is
    // authenticated, spends no quota and answers the character count — while Azure has no
    // authenticated free endpoint at all (`/languages` is PUBLIC and takes no subscription key, so
    // a 200 from it proves the internet works and nothing about the user's key). Azure's check is
    // therefore one tiny real translation, and AC 3's rule is that the copy must not claim a free
    // check that is not free: the label says so, and the tooltip says why.

    /// <summary>DeepL's button. No qualifier, because there is nothing to qualify.</summary>
    public static string TestKeyLabel() => "Test key";

    /// <summary>Azure's button (AC 3). The cost is in the LABEL and not only in a tooltip: the
    /// tooltip is what a user reads after deciding to press.</summary>
    public static string TestKeyLabelCosts() => "Test key (uses a few characters)";

    /// <summary>…and the why, for the user who wonders what is different about this button.</summary>
    public static string TestKeyCostsTooltip()
        => "Azure has no free way to check a key, so this sends one tiny translation — it uses a "
         + "few characters of your quota.";

    /// <summary>The in-flight label. The button is disabled while it shows, so it is also the
    /// re-entrancy flag; the two labels differ, so the caller restores what was there rather than
    /// a shared constant.</summary>
    public static string TestingLabel() => "Testing…";

    /// <summary>
    /// The one place an outcome becomes a sentence: <c>(provider, result, region) → string</c>,
    /// pure, so all of §3.7 is asserted headlessly (GAP-4) and the code-behind decides nothing.
    ///
    /// <para><paramref name="tryAgainIn"/> is the "{t}" the caller has already rendered, exactly as
    /// <see cref="ReadOncePaused"/> takes it — formatting a duration is not <c>Services/</c>' job
    /// (I2), counting the seconds is.</para>
    ///
    /// <para><b>The wrong-region row of §3.7 is not shipped, and that is deliberate</b> (see the
    /// story's Completion Notes). Azure answers a wrong region with the same 401 and the same
    /// envelope as a wrong key, so the typed pipeline — which is the only thing this function may
    /// read (T5) — genuinely cannot tell them apart. Inventing a distinction the response does not
    /// carry is worse than the merged sentence, which already names the region as the thing to
    /// check.</para>
    /// </summary>
    public static string KeyTestSentence(string providerId, KeyTestResult result, string region,
        string? tryAgainIn)
    {
        // The gate refused it, so nothing was sent and the key was never judged. Ruling E6-b: a
        // paused provider's test says it is paused — reporting a refused key here would send the
        // player to re-paste a key that is probably fine.
        if (result.Paused) return KeyTestPaused(providerId, tryAgainIn);

        if (result.Ok)
            return IsAzure(providerId)
                ? $"✓ Key works ({region}) — Azure is used for what you write."
                : Joined("✓ Key works — DeepL is used for what you write.", result.UsageText);

        return result.Kind switch
        {
            TranslationErrorKind.AuthFailed => IsAzure(providerId)
                ? "✕ Azure refused this key. Check the key, and that the region matches your resource."
                : "✕ DeepL refused this key. Check you pasted all of it (free keys end in :fx).",

            // Azure's row drops §3.7's "resets on the 1st": the reset DAY is not verified anywhere,
            // and even the monthly allowance is still [UNKNOWN] U4 (E6.S1 has never been run against
            // a real resource). The sentence keeps what is known and promises no date.
            TranslationErrorKind.QuotaExhausted => IsAzure(providerId)
                ? "⚠ Your 2 million free characters for this month are used up."
                : "⚠ The key works, but the DeepL quota is used up — the free engines are used until it resets.",

            // §3.7's own row, and the reason T4 says this keys off the classifier's Kind and never
            // off a string match: "no internet" is a transport fact, not a word in a body.
            TranslationErrorKind.Network => "Could not check the key — no internet connection.",

            // Everything else — a timeout, a 5xx, a rate limit, a body nobody can read. §3.7 has no
            // row for these, so rather than invent five the deck's own sentence for the Kind is
            // joined after a colon, exactly as ReadFailed joins it (§3.3's join rule).
            var kind => Reason(kind),
        };
    }

    /// <summary>The same, for a failure that never became a <see cref="KeyTestResult"/> — the
    /// handler's outermost catch. Same shape as <see cref="For"/>, so an untyped transport failure
    /// reads exactly like its typed twin.</summary>
    public static string KeyTestSentence(string providerId, Exception error, string region) => error switch
    {
        TranslationException te => KeyTestSentence(providerId, KeyTestResult.Failed(te.Kind), region, null),
        System.Net.Http.HttpRequestException =>
            KeyTestSentence(providerId, KeyTestResult.Failed(TranslationErrorKind.Network), region, null),
        TaskCanceledException =>
            KeyTestSentence(providerId, KeyTestResult.Failed(TranslationErrorKind.Timeout), region, null),
        _ => Terminated($"Could not check the key: {LowerAtJoin(error.Message)}"),
    };

    /// <summary>What DeepL's <c>/v2/usage</c> answered, and the reason that probe is worth making at
    /// all: it validates the key AND settles the quota row without translating a character.
    ///
    /// <para><b>It deliberately does not say "this month".</b> DeepL's current free plan is
    /// <b>1,000,000 characters in total, non-resetting</b> (<c>benchmark-fournisseurs.md</c> §5.3),
    /// so a monthly claim would be false for the very users this button exists for. The sentence
    /// states the two numbers the endpoint actually returned and no period at all.</para></summary>
    public static string DeepLUsage(long used, long? limit)
        => limit is { } l
            ? $"{Number(used)} of {Number(l)} characters used."
            : $"{Number(used)} characters used.";

    /// <summary>Group separators, culture-independently: the UI is English-only by decision, and a
    /// French runtime would otherwise render "500 000" in an English sentence.</summary>
    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Ruling E6-b's third clause. It names no failure, because there was none: the call
    /// was refused before it left the machine.</summary>
    private static string KeyTestPaused(string providerId, string? tryAgainIn)
        => tryAgainIn is null
            ? $"⚠ Not checked — {Display(providerId)} is paused right now."
            : $"⚠ Not checked — {Display(providerId)} is paused right now. Try again in {tryAgainIn}.";

    /// <summary>The §3.7-less kinds, as the deck's own sentence after a colon.</summary>
    private static string Reason(TranslationErrorKind? kind)
    {
        var sentence = kind is { } k ? Sentence(k) : null;
        return sentence is null
            ? "Could not check the key."
            : Terminated($"Could not check the key: {LowerAtJoin(sentence)}");
    }

    /// <summary>The two keyed engines by the name the player knows them by. The ids themselves are
    /// <c>ProviderIds</c>' and never appear in copy (§3's "no provider internal").</summary>
    private static string Display(string providerId) => IsAzure(providerId) ? "Azure" : "DeepL";

    private static bool IsAzure(string providerId)
        => string.Equals(providerId, ProviderIds.Azure, StringComparison.Ordinal);

    /// <summary>A second sentence after the first, when there is one.</summary>
    private static string Joined(string sentence, string? extra)
        => string.IsNullOrEmpty(extra) ? sentence : sentence + " " + extra;

    /// <summary>§3.3's join rule, and it applies to <b>one</b> of the two joins in this file.
    /// A deck sentence continues the clause it is glued to after a COLON — "Could not read the
    /// screen: no internet connection …" — and there it must not restart in upper case.
    ///
    /// <para>After a full stop it is the opposite: "…2 could not be. no internet connection" reads
    /// as a typo, not as a sentence, so <see cref="Because"/> keeps the deck's own capital and
    /// terminates the result instead. §3.3 writes the rule as "lower-cased at the join" because it
    /// writes only the join; §1's second principle — every message answers its three questions <i>in
    /// one sentence</i> — is what decides which join gets it (E5.S4 review).</para>
    ///
    /// <para>Only the first character ever changes: "Your API key was refused — check it in About"
    /// keeps the capital A of About. And a text that OPENS in upper case twice is left alone
    /// entirely — §4.4's <c>Unknown</c> arm passes a provider's or the framework's own message
    /// through, and "GDI+ capture failed" must not be joined as "gDI+ capture failed". Every
    /// sentence in the deck is ordinary sentence case, so the guard costs the rule nothing
    /// (review, E5.S4).</para></summary>
    public static string LowerAtJoin(string sentence)
        => string.IsNullOrEmpty(sentence) || (sentence.Length > 1 && char.IsUpper(sentence[1]))
            ? sentence
            : char.ToLowerInvariant(sentence[0]) + sentence[1..];

    /// <summary>A full stop for a line that is rendered alone, added only if there is not one
    /// already: §4.4's <c>Unknown</c> arm passes a provider's own message through verbatim, and some
    /// of those are already terminated ("… Please try again later."). The deck's own sentences never
    /// are — that is the E1.S6 rule, and it is why the stop belongs here, at the join.</summary>
    private static string Terminated(string line)
        => line.Length == 0 || ".!?".Contains(line[^1]) ? line : line + ".";

    /// <summary>The reason clause of §3.3's partial and total-failure statuses: the deck's sentence
    /// as it is written, after the full stop that ends the counts, terminated so the status line
    /// does not trail off.</summary>
    private static string Because(Exception? error)
        => error is null ? "" : " " + Terminated(For(error));
}

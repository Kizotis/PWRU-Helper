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
/// <b>What this table now owns and did not</b> (E7.S1, ruling E2-d): the three per-line
/// placeholders <c>PerLineFallback</c> wrote straight into a feed row as if they were translations
/// — "(skipped — rate-limited, try again shortly)", "(rate-limited — try again shortly)" and
/// "(translation failed: {ex.Message})". All three were wrong in the same way and each in its own:
/// the first two say "rate-limited" on a row that may have failed for any reason at all, and the
/// third can put an HTTP status code on screen. Amendment <b>A5</b> settles them — a row carries no
/// §3.1 sentence, no provider name and no countdown, and <b>three</b> row texts exist in the whole
/// app — so all three now render <see cref="RetryGaveUpRow"/>: this row was not translated and
/// nothing is coming for it, which is the one fact a row owes the player. They still start with "("
/// (I4), added by <c>PerLineFallback</c> exactly as the feed-row call sites add it.
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
///       <see cref="TranslationException.RetryAt"/> by <c>MainWindow</c> at display time and
///       arrives here as text, never as a <c>TimeSpan</c>. The consts below are the no-countdown
///       form; the parameterised renders substitute what the caller already formatted.</item>
/// <item><b>Never start a sentence with "(" (I4's marker).</b> The two feed-row call sites wrap
///       this text in parentheses (<c>MainWindow.Live.cs</c>, <c>MainWindow.Ocr.cs</c>), so a
///       leading paren here renders "((…))". The deeper reason is that "(" is the app's marker
///       for "this is a failure, not a translation" — it is what
///       <c>CachingTranslator.IsCacheable</c> refuses to store — and a copy-deck sentence must
///       never be mistakable for one of those placeholders. To be exact about the blast radius:
///       nothing this table returns is ever a translator's return value, so a leading paren here
///       could not itself poison the cache; the strings that pass that guard live in
///       <c>GoogleGtxTranslator</c>'s per-line fallback.</item>
/// <item><b>No terminal full stop</b> — and since the A-amendments that is a <i>rule of the deck</i>
///       rather than a deviation from it (§3, D4). Two surfaces render a §3.1 sentence alone and
///       both add the stop themselves (<see cref="TranslatorTabStatus"/>, <see cref="ReadFailed"/>);
///       the rest join it — <see cref="LiveAutoStopped"/>'s parenthetical, read-once's §3.3
///       statuses — and with the deck's own stop those read ".)." and ". — ". The stop is dropped
///       here rather than stripped at five joins.</item>
/// </list>
///
/// Sally's deck writes <c>{P}</c> for the provider's user-facing name ("Google", "DeepL", …), and
/// <b>E7.S1 restored it</b>: the name comes from <see cref="ProviderNames"/> keyed on the failing
/// <see cref="TranslationException.ProviderId"/>, never from a guess, and a failure that carries no
/// provider still renders the <c>{P}</c>-less form this table shipped with (§3.0 rule 1).
/// Sally's trailing "— trying another engine" came back as <b>"another engine is being tried"</b>,
/// and only where it is true: amendment <b>A4</b> refuses the promise on a sentence rendered after
/// the whole attempt has already failed, which is every surface that exists today, so the clause is
/// behind a parameter and its default is the honest one.
/// </summary>
internal static class UserMessages
{
    // ---- one sentence per typed error kind (ux-mode-degrade.md §3.1, as amended) --------------
    //
    // Sentence case, no terminal full stop, no HTTP code, no provider internal ("gtx",
    // "dict-chrome-ex", "429"), no exclamation mark — §3's general rules, all four still enforced
    // by UserMessagesTests. §3's length rule is "<= 90 characters RENDERED", and rendered is the
    // operative word since amendment A3: with {P} = Google and {t} = 0:58 the longest row in the
    // deck is exactly 90. The {P}-LESS forms below run longer (Blocked is 106) because "The
    // translation service" is 23 characters where an engine's name is 6, and that is deliberate —
    // A3/A5 moved the tight budget off these sentences entirely: the compact overlay now takes the
    // short forms of §3.4 and a feed row takes no sentence at all, so the two surfaces that could
    // not wrap no longer render this text.
    //
    // THE SHAPE, and it is not decoration. Every row is a `public const` assembled from `private
    // const` fragments, for two reasons that pull the same way:
    //
    //   * the nine rows must stay compile-time constants. UserMessagesTests' house rules read every
    //     public const of this type reflectively as one row of Sentence(kind) and assert that set
    //     equals what the lookup can return; a `static readonly` — the natural shape the moment a
    //     sentence is composed rather than typed — would escape all five of them at once.
    //   * every fragment then exists EXACTLY ONCE (UX-DR19). The parameterised render below is
    //     built from the same fragments as the const, so "asked us to slow down" cannot come to be
    //     spelled two ways: the {P}-bearing form and the {P}-less form are the same string with a
    //     different subject, not two sentences that have to be kept in step by hand.
    //
    // The const is therefore the FULLY DEGRADED render — no provider name, no countdown — and it is
    // a real sentence rather than a template with holes in it because of two rulings: §3.0 rule 1
    // ("no name is ever invented": {P} → "The translation service") and amendment A12 (one
    // substitution, not a second sentence: "for {t}" → "briefly", "in {t}" → "shortly").

    // -- the pieces every row is built from ------------------------------------------------------

    /// <summary>§3.0 rule 1's subject for a failure that carries no provider id — a
    /// <see cref="TranslationException"/> raised outside <c>HttpProviderCore</c>, or one classified
    /// on the way up. It is the A.0 wording this table shipped with: it stops being the default, it
    /// does not stop existing.</summary>
    private const string SomeEngine = "The translation service";

    /// <summary>…and the two subjects that cannot degrade to the one above, because "Your The
    /// translation service quota" is not English. Both are the A.0 wording too.</summary>
    private const string Your = "Your ";
    private const string SomeQuotaOwner = "free translation";
    private const string SomeKeyOwner = "API";

    /// <summary>The <c>{P}</c>-less form for a CHIP, where <see cref="SomeEngine"/>'s sentence
    /// subject ("The translation service") neither fits the line nor reads as a name.
    /// <b>Unreachable through <see cref="EngineStatus"/></b> — every <see cref="ProviderIds.All"/>
    /// member has a name in <see cref="ProviderNames"/> — and written anyway, because "never invent
    /// a name" (§3.0 rule 1) is not the same as "throw at a player mid-fight".</summary>
    private const string SomeEngineShort = "Engine";

    /// <summary><b>Amendment A12, once, for all three rows that carry a <c>{t}</c>.</b>
    /// <c>RetryAt</c> can legitimately be absent — a gate opened on a strike count rather than on a
    /// window, and <c>AuthFailed</c>'s <see cref="DateTimeOffset.MaxValue"/> sentinel has no honest
    /// countdown at all — and there is no second sentence for that case: "for {t}" renders
    /// "briefly" and "in {t}" renders "shortly". A row that rendered "paused for " with nothing
    /// after it is the defect this pair prevents.</summary>
    private const string Briefly = "briefly";
    private const string Shortly = "shortly";

    /// <summary>The tail A4 puts where "— trying another engine" used to be, on the three rows that
    /// have no countdown to offer: it answers §1 principle 2's third question ("must you act?")
    /// instead of promising a retry that is not happening.</summary>
    private const string TryAgain = "try again ";
    private const string TryAgainShortly = TryAgain + Shortly;

    /// <summary><b>D2, restored — and only where it is true</b> (§3.1's fork). It is rendered when
    /// the caller can show that a rung below the failing one is still going to be tried; every
    /// surface shipping today renders a §3.1 sentence <i>after</i> the whole attempt has failed, so
    /// every surface shipping today gets <see cref="TryAgainShortly"/>. See
    /// <see cref="Sentence"/>'s <c>anotherEngineIsBeingTried</c>.</summary>
    private const string AnotherEngineIsBeingTried = "another engine is being tried";

    /// <summary>Shared by <c>Blocked</c> and <c>AllProvidersPaused</c>: §1 principle 2's third
    /// answer, written out instead of implied. One spelling, so the two rows cannot drift.</summary>
    private const string NothingYouNeedToDo = ", nothing you need to do";

    // -- the nine rows ---------------------------------------------------------------------------

    /// <summary>429, or a Google "automated queries" page.
    /// <para>Renders "Google asked us to slow down — paused for 0:58, and it retries on its own"
    /// (amendment <b>A1</b>, which replaced the shipped "try again in a moment": §3.1 always banned
    /// "wait a minute", and the hedge only sat next to it because there was no gate to count down
    /// from. There is one now, so the number is real and the tail answers "must you act?" — no).
    /// </para></summary>
    public const string RateLimited = SomeEngine + RateLimitedSaid + Briefly + RateLimitedTail;
    private const string RateLimitedSaid = " asked us to slow down — paused ";
    private const string RateLimitedTail = ", and it retries on its own";

    /// <summary>403 with no key, a captcha or an abuse interstitial.
    /// <para>Renders "Google is refusing requests from your connection — paused for 0:58, nothing
    /// you need to do" (amendment <b>A1</b>, which replaced the shipped sentence because it
    /// answered none of §1 principle 2's three questions). What happened — refused. What the app
    /// does — pauses for {t}. What the user does — nothing. The block is about the connection and
    /// not about anything they typed, and saying so is what stops them re-pressing.</para></summary>
    public const string Blocked = SomeEngine + BlockedSaid + Briefly + NothingYouNeedToDo;
    private const string BlockedSaid = " is refusing requests from your connection — paused ";

    /// <summary>5xx. Renders "Google is down right now — another engine is being tried" where that
    /// is true and "— try again shortly" where it is not (§3.1's fork, A4).</summary>
    public const string Unavailable = SomeEngine + UnavailableSaid + TryAgainShortly;
    private const string UnavailableSaid = " is down right now — ";

    /// <summary>The 12 s HttpClient timeout — an OCE whose token is NOT cancelled. Same fork.</summary>
    public const string Timeout = SomeEngine + TimeoutSaid + TryAgainShortly;
    private const string TimeoutSaid = " took too long to answer — ";

    /// <summary>DNS, TLS, connect, proxy. The one kind with no provider in it: when nothing
    /// resolves, naming an engine would be noise. Verbatim from §3.1.</summary>
    public const string Network = "No internet connection — nothing can be translated until it is back";

    /// <summary>A body that is not the provider's shape, or a batch count mismatch. Same fork.</summary>
    public const string BadResponse = SomeEngine + BadResponseSaid + TryAgainShortly;
    private const string BadResponseSaid = " sent something we could not read — ";

    /// <summary>DeepL 456, Azure's out-of-quota envelope. Keys only, so <c>{P}</c> is DeepL or Azure
    /// and is never ambiguous.
    /// <para>Renders "Your DeepL quota is used up for this month — the free engines are used
    /// instead" (amendment <b>A1</b>). The tail is a statement about the CHAIN, true whether or not
    /// this particular call then succeeded — which is why it survives A4 where "trying another
    /// engine" does not. "for this month" is Sally's ruling and it stands: it is what both free
    /// tiers actually do. (E6.S6 found DeepL's current Developer allowance is one-time and
    /// non-resetting; that is stated where the numbers are — <see cref="DeepLUsage"/>, which
    /// deliberately promises no period at all — rather than by weakening the row A1 just
    /// wrote.)</para></summary>
    public const string QuotaExhausted = Your + SomeQuotaOwner + QuotaSaid;
    private const string QuotaSaid = " quota is used up for this month — the free engines are used instead";

    /// <summary>401, or 403 while a key was sent. "About" — not "Settings": the key box is on the
    /// About tab, and the sentence this replaces sent the user to a tab that has never existed in
    /// this app.
    /// <para><b>"Keys only" is now what the mapper guarantees too.</b> Row 5 used to be
    /// <c>if (code == 401) return AuthFailed;</c>, unconditional on <c>keyWasSent</c> unlike rows
    /// 6/7/8 for 403, so a 401 on the keyless Google path (an authenticating proxy, a captive
    /// portal) sent a user who has never typed a key to an empty key box. Ruling <b>E2-g</b> closed
    /// it in E2.S2: <c>ProviderErrorMapper</c> guards 401 on <c>keyWasSent</c> and a keyless 401 is
    /// <c>Blocked</c>. The item E1.S6's review recorded for E6 is done — nothing to reopen.</para>
    /// <para>Renders "Your DeepL key was refused — check it in About, or clear it". It is the one
    /// row in the deck whose third answer is "yes, act", and the only row with no <c>{t}</c> on
    /// purpose: the gate stores <see cref="DateTimeOffset.MaxValue"/> for it, a permanent pause
    /// whose exit is fixing the key and not a timer, so an invitation to "try again shortly" here
    /// would be the A7 amplifier with no way to act on it (E5.S4's review).</para></summary>
    public const string AuthFailed = Your + SomeKeyOwner + AuthFailedSaid;
    private const string AuthFailedSaid = " key was refused — check it in About, or clear it";

    /// <summary>Every rung of the chain is inside a block window — §2.1's one STATE that is not an
    /// error, raised by <c>ChainTranslator</c> without sending anything.
    /// <para>Renders "All engines are paused — next try in 0:58, nothing you need to do", which
    /// §3.1 calls the single most important line in the document, and "next try shortly, …" when
    /// there is no window to count down (A12).</para></summary>
    public const string AllProvidersPaused = AllPausedSaid + Shortly + NothingYouNeedToDo;
    private const string AllPausedSaid = "All engines are paused — next try ";

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
    ///
    /// <para><b>Every parameter is optional and every row degrades honestly</b>, which is what lets
    /// <c>Sentence(kind)</c> keep answering exactly the <c>const</c> above it (the equality
    /// <c>UserMessagesTests</c> asserts between the reflection scan and this lookup).</para>
    /// </summary>
    /// <param name="providerId">The engine that failed — <see cref="TranslationException.ProviderId"/>,
    /// never a guess (§3.0/A3). An id <see cref="ProviderNames"/> does not know, and the very common
    /// <c>null</c>, render the <c>{P}</c>-less form rather than inventing a name.</param>
    /// <param name="tryAgainIn">The <c>{t}</c> <b>already rendered</b> by <c>MainWindow</c> from
    /// <see cref="TranslationException.RetryAt"/> — I2, and it is the shape
    /// <see cref="ReadOncePaused"/> and <see cref="KeyTestSentence"/> already ship. There is no
    /// <c>TimeSpan</c>, no <c>DateTimeOffset</c> and no format string anywhere in this file.</param>
    /// <param name="anotherEngineIsBeingTried"><b>D2's fork, and its default is the honest one.</b>
    /// E1.S6 removed "— trying another engine" because <c>Friendly</c> is reached only once the
    /// whole attempt has failed, and amendment <b>A4</b> upholds that: a promise of another engine
    /// would be false at the one moment it is read. The clause therefore renders only for a caller
    /// that can prove a rung below this one is still going to be tried — a status line above a
    /// chain that is still walking, which is E7.S4/E7.S5's surface and does not exist yet.
    /// <c>ChainTranslator.LastOutcome</c> is published only after a FINISHED attempt (ProviderId
    /// names who answered, or null when every rung was walked), so it can never be the evidence for
    /// this flag; whoever builds that surface passes it from its own position in the chain.</param>
    public static string? Sentence(TranslationErrorKind kind, string? providerId = null,
        string? tryAgainIn = null, bool anotherEngineIsBeingTried = false)
    {
        var p = ProviderNames.Display(providerId);
        var next = anotherEngineIsBeingTried ? AnotherEngineIsBeingTried : TryAgainShortly;
        return kind switch
        {
            TranslationErrorKind.RateLimited =>
                (p ?? SomeEngine) + RateLimitedSaid + PausedFor(tryAgainIn) + RateLimitedTail,
            TranslationErrorKind.Blocked =>
                (p ?? SomeEngine) + BlockedSaid + PausedFor(tryAgainIn) + NothingYouNeedToDo,
            TranslationErrorKind.Unavailable => (p ?? SomeEngine) + UnavailableSaid + next,
            TranslationErrorKind.Timeout => (p ?? SomeEngine) + TimeoutSaid + next,
            TranslationErrorKind.Network => Network,
            TranslationErrorKind.BadResponse => (p ?? SomeEngine) + BadResponseSaid + next,
            TranslationErrorKind.QuotaExhausted => Your + (p ?? SomeQuotaOwner) + QuotaSaid,
            TranslationErrorKind.AuthFailed => Your + (p ?? SomeKeyOwner) + AuthFailedSaid,
            TranslationErrorKind.AllProvidersPaused => AllPausedSaid + NextTryIn(tryAgainIn) + NothingYouNeedToDo,
            _ => null,
        };
    }

    /// <summary>Amendment A12's two substitutions, and the only two places this file writes them.
    /// Each takes the countdown a surface already rendered and answers the clause the sentence
    /// carries — never a bare number, and never an empty space where one should have been.</summary>
    private static string PausedFor(string? tryAgainIn) => tryAgainIn is null ? Briefly : "for " + tryAgainIn;

    private static string NextTryIn(string? tryAgainIn) => tryAgainIn is null ? Shortly : "in " + tryAgainIn;

    /// <summary>
    /// The whole of what <c>MainWindow.Friendly</c> renders, as a pure function — no window, no
    /// dispatcher, no STA, so the copy can be asserted headlessly (GAP-4: "tests assert on it").
    /// The two raw-exception arms are the ones the pipeline can still let through untyped, and
    /// they answer with the same sentences as their typed twins, which is the whole point of the
    /// table: a dead network reads the same whether or not it was classified on the way up.
    ///
    /// <para><b>Where <c>{P}</c> comes from</b>: the typed arm reads
    /// <see cref="TranslationException.ProviderId"/>, which <c>HttpProviderCore</c> stamps on every
    /// failure it raises. The two untyped arms name no engine, and neither should — an
    /// <c>HttpRequestException</c> is the Network row, which has no <c>{P}</c> by design, and a
    /// bare timeout arrived with nothing to attribute it to.</para>
    /// </summary>
    public static string For(Exception ex, string? tryAgainIn = null,
        bool anotherEngineIsBeingTried = false) => ex switch
    {
        TranslationException te =>
            Sentence(te.Kind, te.ProviderId, tryAgainIn, anotherEngineIsBeingTried) ?? te.Message,
        System.Net.Http.HttpRequestException => Network,
        TaskCanceledException => Timeout,
        _ => ex.Message,
    };

    // ---- §3.0's per-surface templates, and §3.2's LIVE statuses --------------------------------
    //
    // The wrappers used to live at their call sites, which is how the deck's own sentence came to
    // be read six different ways — "Failed: {s}", "({s})", "Live hiccup ({s}) — retrying…". Every
    // one of them is here now, for the reason GAP-4 gives: a wrapper IS copy, and copy that lives in
    // a code-behind is copy no test can scan. The code-behind keeps exactly what is formatting —
    // which countdown band a number falls in — and calls one of these with the result.

    /// <summary><b>The Translator tab, and "Failed: " is retired</b> (§3.0). With <c>{P}</c>
    /// restored the sentence names the engine and says what happened; "Failed:" in front of it is
    /// the app saying "bad news" twice and demoting the sentence to a sub-clause. The chip beside it
    /// (E7.S3) carries the who at a glance.
    ///
    /// <para>This is the surface §3.1 writes its sentences FOR — rendered alone — so it is the one
    /// that adds the terminal stop the deck deliberately omits (D4, now a rule of the deck rather
    /// than a deviation from it: five other joins would otherwise have to strip one).</para></summary>
    public static string TranslatorTabStatus(string sentence) => Terminated(sentence);

    /// <summary><b>§3.2's auto-stop row</b> (amendment A6, ruling E5-d). It replaces "Live stopped
    /// after repeated errors ({reason})." rather than keeping it: "repeated" is the app declining to
    /// say how many, and the way back — ▶ — was not in the sentence at all. The count is
    /// <c>LiveErrorTracker.ConsecutiveFailures</c> and E5-c guarantees it is five requests that were
    /// really sent and really failed, never a pause or a gate refusal.
    ///
    /// <para><paramref name="reason"/> is a §3.1 sentence with <b>its capital kept</b> (A11) and its
    /// full stop absent (D4), so the wrapper's own stop is the only one in the line.</para></summary>
    public static string LiveAutoStopped(int failures, string reason)
        => $"Live stopped after {failures} failed reads in a row ({reason}) — press ▶ to try again.";

    /// <summary>What the loop says the moment it starts, before the first tick has anything to
    /// report. §3.2 has no row for it — its "running" rows are the per-tick ones, marked unchanged —
    /// so its wording is unchanged too; what moved is where it lives (GAP-4).</summary>
    public static string LiveStarted()
        => "🔴 Live — watching the area. Translations appear when new text shows up.";

    /// <summary>§3.2's "one failed read, still trying" row, which replaces "Live hiccup ({reason}) —
    /// retrying…". The reason is deliberately gone: a hiccup the loop is already retrying is a
    /// STATE, and §1's first principle is one message per state — the sentence that names a failing
    /// engine belongs to the line the player reads when the loop stops, not to the one it paints
    /// over on the next tick.</summary>
    public static string LiveOneReadFailed() => "🔴 Live — one read did not translate, retrying…";

    // [§3.2's overlay column for this row — "⚠ one read is retrying" — is deliberately NOT here.
    //  SetScreenStatus writes ONE string to both surfaces, so a second spelling would be copy that
    //  never renders, which is UX-DR19's failure the other way round. The per-surface split for the
    //  LIVE rows is E7.S5's (the paused rows already have theirs, because E7.S2 needed a second
    //  writer for the 1 Hz repaint).]

    /// <summary>§3.2's paused rows, main window. Three of them and not one with a substitution,
    /// because §2.4's floor renders a CLAUSE and not a duration: "next try in about to retry" is not
    /// a sentence. Which one a given number selects is <c>MainWindow.LivePausedStatus</c>'s — that
    /// is the band table, which is formatting (I2).</summary>
    public static string LivePausedNextTry(string countdown)
        => $"○ Live — paused, next try in {countdown}." + ResumesOnItsOwn;

    public static string LivePausedAboutToRetry() => "○ Live — paused, about to retry.";

    public static string LivePausedNoCountdown() => "○ Live — paused." + ResumesOnItsOwn;

    /// <summary>§3.2's <b>no internet</b> row — §2.1's <b>S6</b>, which ruling <b>GAP-3</b> made a
    /// full pause exactly like S5 (the loop stops capturing too; <c>ux</c> flow (e).3's "LIVE keeps
    /// reading the screen" is superseded). It carries <b>no countdown</b>: a connection comes back
    /// when it comes back, and the gate's soft-cooldown window is not a promise about the cable —
    /// so the sentence says the cause instead, which is the one thing here the player can act on.
    ///
    /// <para>It shares <see cref="ResumesOnItsOwn"/> with the other paused rows (UX-DR19: the same
    /// promise is written once), and it deliberately does not reuse §3.1's <c>Network</c> sentence:
    /// that one is about a translation that failed, this one is about a loop that is waiting.</para></summary>
    public static string LivePausedNoNetwork()
        => "○ Live — paused, no internet connection." + ResumesOnItsOwn;

    /// <summary>The half of the paused line that is the same promise every time it is made, so it is
    /// written once — and it is the whole reason the row exists: nothing is lost while the app
    /// waits, and the player does not have to do anything for it to come back.</summary>
    private const string ResumesOnItsOwn = " It resumes on its own; nothing is lost.";

    /// <summary>§3.2's paused rows for the overlay, whose budget is <b>40 characters</b>: the long
    /// forms would wrap the one status line that window has.</summary>
    public static string LivePausedOverlayNextTry(string countdown) => $"○ Live paused — back in {countdown}";

    public static string LivePausedOverlayAboutToRetry() => "○ Live paused — about to retry";

    public static string LivePausedOverlayNoCountdown() => "○ Live paused — it resumes on its own";

    /// <summary>S6's overlay column (E7.S4). 27 characters, inside the 40 the one status line on a
    /// 360 px window allows.</summary>
    public static string LivePausedOverlayNoNetwork() => "○ Live paused — no internet";

    /// <summary>
    /// <b>§3.5's "fallback active" notice, and it is amendment A4's settlement of D2.</b> "— trying
    /// another engine" was not restored as a tail of §3.1's sentences, because those are rendered
    /// once the whole attempt has already failed and the promise would be false at the one moment
    /// it is read. What the deck kept instead is this: a line written <b>once per switch</b>, after
    /// a lower tier really did answer, naming both engines. The evidence is
    /// <c>ChainTranslator.LastOutcome.Skipped</c> being non-empty with a <c>ProviderId</c> that
    /// answered — <c>EngineStatus.FellBack</c>, ruling <b>E3-b</b> — so the app never promises a
    /// fallback, it reports one.
    ///
    /// <para>Null when either name is unknown (§3.0 rule 1: no name is ever invented) — the caller
    /// then writes nothing at all, exactly as it does for <see cref="BackOn"/>.</para></summary>
    public static string? TranslatedBy(string? servingId, string? pausedId)
        => ProviderNames.Display(servingId) is { } serving
           && ProviderNames.Display(pausedId) is { } paused
            ? $"Translated by {serving} — {paused} is paused." : null;

    // ---- §3.4: the compact overlay's quick reply ------------------------------------------------
    //
    // Amendment A3. The overlay defaults to 360 px and the wrapper "⚠ … — your text is kept, press
    // Enter to retry." costs 46 characters on its own, so this surface takes a SHORT FORM chosen by
    // Kind and never a §3.1 sentence — which is why the whole line is composed here rather than the
    // deck's sentence being handed to the overlay to wrap. Every provider-named form below is
    // 39–63 characters, inside §3.4's ~60; the {P}-less fallbacks run to ~82 because "The
    // translation service" is 23 characters, and that line wraps rather than lying about a name.
    //
    // "your text is kept" is the best sentence in the current app and it is kept verbatim. It is
    // dropped in exactly the two places where there is nothing to keep and nothing to retry — the
    // key rows, which send the player to About instead.

    /// <summary>The whole quick-reply failure line, per §3.4's table.</summary>
    /// <param name="error">The failure, or <c>null</c> for the generic arm — a reply that failed
    /// with nothing typed to attribute it to.</param>
    /// <param name="tryAgainIn">The <c>{t}</c> already rendered by <c>MainWindow</c> (I2), coarse
    /// per ruling E7-a: this line is written once and never ticks, so it may not show <c>m:ss</c>.</param>
    public static string OverlayReply(Exception? error, string? tryAgainIn = null)
    {
        var kind = (error as TranslationException)?.Kind;
        var p = ProviderNames.Short((error as TranslationException)?.ProviderId);
        return kind switch
        {
            TranslationErrorKind.RateLimited or TranslationErrorKind.Blocked =>
                KeptText(PausedShort(p ?? SomeEngine, tryAgainIn)),
            // The one row that drops "press Enter to retry": there is no engine left to retry ON.
            TranslationErrorKind.AllProvidersPaused =>
                Warn(PausedShort("Engines", tryAgainIn) + TextIsKept + "."),
            TranslationErrorKind.Network => KeptText(NoInternetShort),
            TranslationErrorKind.Timeout or TranslationErrorKind.Unavailable
                or TranslationErrorKind.BadResponse => KeptText($"{p ?? SomeEngine} did not answer"),
            TranslationErrorKind.AuthFailed => Warn($"{Your}{p ?? SomeKeyOwner} key was refused — {SeeAbout}"),
            TranslationErrorKind.QuotaExhausted => Warn($"{Your}{p ?? SomeQuotaOwner} quota is used up — {SeeAbout}"),
            _ => KeptText("Could not translate"),
        };
    }

    /// <summary>§3.4's shape, and the promise that makes it worth showing at all — the best
    /// sentence in the current app, kept verbatim, and written once for the two arms that make
    /// it.</summary>
    private static string KeptText(string what) => Warn(what + TextIsKept + ", press Enter to retry.");

    private const string TextIsKept = " — your text is kept";

    /// <summary>"{subject} paused ({t})", for the two arms that say it — an engine, or all of
    /// them.
    ///
    /// <para><b>A12's substitution, and it is the DURATION one</b> (review). The parenthesis here
    /// holds how long the pause lasts — "0:58", "about 4 min" — which is the "for {t}" slot, so the
    /// no-countdown form is <see cref="Briefly"/> and not <see cref="Shortly"/>. "Google paused
    /// (shortly)" reads as "Google pauses soon", which is a different and false statement; "Google
    /// paused (briefly)" is the one the amendment writes.</para></summary>
    private static string PausedShort(string subject, string? tryAgainIn)
        => $"{subject} paused ({tryAgainIn ?? Briefly})";

    private static string Warn(string line) => "⚠ " + line;

    /// <summary>Where a key problem is fixed. Not "Settings": this app has never had that tab.</summary>
    private const string SeeAbout = "see About.";

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
        => ReadLines(lines) + $" — {translated} translated, {lines - translated} could not be." + Because(error);

    /// <summary>The half every §3.3 status opens with, written once — the count is the claim these
    /// sentences exist to make honest, and four spellings of it is four places to get it wrong.</summary>
    private static string ReadLines(int lines) => $"Read {lines} line(s)";

    /// <summary>§3.3's two in-flight rows. They are here and not in <c>MainWindow.Ocr.cs</c> for the
    /// reason GAP-4 gives, and they are the rows a player reads for most of a read's life.</summary>
    public static string ReadingStatus() => "Reading…";

    public static string ReadTranslatingStatus(int lines) => ReadLines(lines) + ". Translating…";

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

    // ---- A8: the read-once button IS the cancel (ux-mode-degrade.md §3.3, §4.3) ------------------
    //
    // Six strings, and they are in the deck rather than in the two XAML files for the reason the
    // "Test key" labels are (E6.S5, GAP-4): a control whose copy CHANGES cannot keep it in a XAML
    // attribute — restoring the idle form would be a second spelling of it, which is UX-DR19's
    // failure exactly. MainWindow.SetReadOnceCancelMode writes both surfaces from here.
    //
    // Why a label swap at all: E5.S4 shipped three cancel routes and none of them reachable, because
    // a DISABLED WPF button raises no Click. Greying the button for the length of a 30 s read is
    // also what makes a player press it again (amplifier A7), so the press is given a meaning
    // instead — one label, one tooltip, zero new controls.

    /// <summary>The main window's read-once button while nothing is reading. The shipped wording is
    /// kept rather than taking A8's paraphrase ("Read the area once"): it is the phrase the README
    /// walks a new player through by name, and renaming it is E7.S8's call, not this pass's.</summary>
    public static string ReadOnceLabel() => "Select area & read once";

    public static string ReadOnceTooltip()
        => DrawABox + ". Ctrl+Alt+R re-reads the same area while in game.";

    /// <summary>What both read-once tooltips open with, written once: the two surfaces differ in
    /// what they add (the hotkey here, "stays in compact mode" there), not in what the gesture
    /// is.</summary>
    private const string DrawABox = "Draw a box over Russian text and read it once";

    /// <summary>The same button while a read is in flight — <b>still enabled</b>, and the press
    /// cancels (A8). It is the affordance E5-g asked for: a gesture that exists only in a status
    /// sentence is one nobody makes mid-raid.</summary>
    public static string CancelReadLabel() => "Cancel read";

    /// <summary>…and the tooltip names the second way, which is where the hotkey belongs — on the
    /// control it duplicates, not on a status line describing a state (principle 1).</summary>
    public static string CancelReadTooltip() => "Stop this read — Ctrl+Alt+R does the same.";

    /// <summary>The compact overlay's read-once button, which is icon-first: 360 px has no room for
    /// a sentence, so the tooltip carries what the main window's label says.</summary>
    public static string ReadOnceOverlayLabel() => "👁 Read once";

    public static string ReadOnceOverlayTooltip()
        => DrawABox + " — the result appears framed in the feed below. Stays in compact mode.";

    /// <summary>A8's overlay column, verbatim: the glyph becomes <c>■</c> and the tooltip becomes
    /// <see cref="CancelReadLabel"/>, which on an icon-only button is its label.</summary>
    public static string CancelReadOverlayLabel() => "■";

    // ---- what a row says between two attempts, and when there is no attempt left (§9.3, §2.2) ----
    //
    // Methods for the same reason the read-once statuses are: they are ROW text, not rows of the
    // Sentence(kind) table, and the house-rule tests read every public const in this type as one of
    // those rows. Here under ruling GAP-4 all the same — the copy deck is one file, so E7.S1 opens
    // one file.

    /// <summary>
    /// <b>A row waiting for the next drain, and it is the ellipsis it already was</b> (E5.S3, T2).
    ///
    /// <para>§9.3 sketched an unwritten "retrying…" row and <c>ux-mode-degrade.md</c> §2.2's S5 row says
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
    /// cannot reach <c>CachingTranslator.IsCacheable</c>. The copy pass has since been made and the
    /// row is confirmed as written (amendment <b>A5</b>, E7.S1/E7.S5); the retry badge (E7.S6) is
    /// still the honest place for "retrying".</para>
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
    /// translated.</para>
    ///
    /// <para><b>E7.S6/T1 — this sentence stands, and the alternative was considered.</b> E7.S6's
    /// AC 2 quotes an older draft, <c>(not translated — all engines were paused)</c>. That version
    /// states a <i>cause</i> which can be false: <c>TranslationPolicy.PendingRetryMaxAttempts</c>
    /// (2) is reachable by two <i>sent</i> failures with nothing paused at all, and by an E3-h
    /// per-line cap overflow. The shipped sentence is true in every case AC 2 can reach, and
    /// <c>ux-mode-degrade.md</c> §3.3a — amendment <b>A5</b>, which is binding for E7 — lists this
    /// exact string as one of the three row texts that exist. One spelling, not two (UX-DR19).</para></summary>
    public static string RetryGaveUpRow() => "not translated — the engines did not come back";

    /// <summary>Nothing came back. This is the sentence the false "Done" used to cover
    /// (amplifier A7: a player told "Done" over an empty result presses the button again).</summary>
    public static string ReadOnceNoneTranslated(int lines, Exception? error)
        => ReadLines(lines) + " — none could be translated." + Because(error);

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
    /// <para><b>Amendment A7 — the row forks, and neither half promises what the other delivers.</b>
    /// §3.3 used to promise the rows "will fill in when one is back". E5.S3's retry queue is drained
    /// by the LIVE LOOP, so that promise is true for a read taken while LIVE is running and false
    /// for one taken with LIVE stopped, where nothing is ever coming for those rows. One sentence
    /// cannot be honest in both states and §1 principle 4 does not allow picking the friendlier one,
    /// so the loop's own state is the fork and the code-behind — which can see it — passes it in.
    /// (Today only the second branch is reachable: no read-once can run while the loop does, because
    /// the OCR engine is shared and non-reentrant. The fork is written from the state and not from
    /// that fact, so the day a read is allowed alongside the loop the sentence is already right.)
    /// </para></summary>
    /// <param name="liveIsRunning">Whether a LIVE loop is behind these rows to drain them.</param>
    public static string ReadOncePaused(int lines, string? tryAgainIn, bool liveIsRunning)
        => ReadLines(lines) + EveryEnginePaused
         + (liveIsRunning ? "they fill in when one is back." : TryAgain + NextTryIn(tryAgainIn) + ".");

    /// <summary>The middle of both paused rows, and the reason it is one string: what happened is
    /// the same in both, only what happens next differs.</summary>
    private const string EveryEnginePaused = " — every engine is paused, ";

    /// <summary>The screen itself could not be read — a capture or an OCR failure, not a
    /// translation one. Replaces "OCR failed: …", which named a component the player does not have
    /// and cannot act on (§3.3).</summary>
    public static string ReadFailed(Exception error)
        => Terminated($"Could not read the screen: {LowerAtJoin(For(error))}");

    // ---- the About tab's "Translation engines" block (ux-mode-degrade.md §4.2, E7.S7) ----------
    //
    // T5's RULE, written down because the tension is real: the About tab's page prose is XAML
    // literals with Hyperlinks in them, and moving all of it into a code table would be silly. So —
    // the SENTENCES §3 and §4 specify live here, where the UX-DR19 scan can see them and E7.S8's
    // README can quote the same words; the static page prose (the two key paragraphs and their
    // links, the shortcut list, the author links) stays in the XAML. Headings and row labels are
    // labels, not sentences, and stay in the XAML with the rest of the block's layout.

    /// <summary>§4.2's opening sentence, and AC 3's first literal. It is the block's whole promise
    /// to the majority of players — principle 2's "many users will never open this tab" is only
    /// acceptable because nothing here has to be done.</summary>
    public static string AboutEnginesIntro()
        => "By default everything runs on free engines — no key, no signup, nothing to set up.";

    /// <summary>§4.2's keys-block sentence, AC 3's second literal. Both halves matter: what a key
    /// buys (quota of your own) and where it lives (this PC, and nowhere else).</summary>
    public static string AboutKeysIntro()
        => "A key gives you better translations and your own quota, instead of sharing a free door "
         + "with everyone else. Keys are stored only on your PC.";

    /// <summary>§4.2's offline block, as the placeholder T6 recommends: the line ships, the
    /// <c>[ Download the offline engine ]</c> button does not. <b>E8.S3 owns the behaviour</b> and
    /// E8 is gated on U6/U7 and R-12, so a visible Download button that does nothing would be worse
    /// than an absent one. Ruling <b>R-4</b> governs the setting when it arrives:
    /// <c>OfflineFallbackEnabled</c> is written by Download and Remove — there is no checkbox.</summary>
    public static string AboutOfflineNotInstalled()
        => "○ Not installed — about 50 MB to download, works with no internet at all. Used only "
         + "when every online engine is unavailable.";

    /// <summary>
    /// <b>Amendment A10 — the cache privacy sentence</b> (ruling E4-c). The technical half was
    /// already true: <c>translation-cache.json</c> is never logged and never reaches
    /// <c>CopyErrorReport_Click</c>. What was missing is that <b>nobody told the user the file
    /// exists</b>, and it holds other players' chat.
    ///
    /// <para>Verbatim in the About tab and, from E7.S8, as a README bullet — one string, so the two
    /// cannot drift.</para>
    /// </summary>
    public static string CachePrivacyLine()
        => @"Translations you have already seen are saved in %AppData%\PWRUHelper\ so the same chat "
         + "line is never translated twice — they hold chat text, they never leave your PC, and "
         + "they are never included in the error report.";

    /// <summary>A10's one control. A statement that the app stores your chat with no way to remove
    /// it is the exact shape principle 2 forbids — "what can you do about it" must be written, not
    /// implied.</summary>
    public static string ClearCacheLabel() => "Clear cache";

    /// <summary>…and its answer, on a status line and never a <c>MessageBox</c> (§4.3). There is no
    /// confirmation dialog either: unlike removing the offline engine, clearing the cache destroys
    /// nothing the app cannot rebuild — the cost is a few extra requests.</summary>
    public static string CacheClearedStatus(int removed)
        => $"Cache cleared — {removed.ToString(CultureInfo.InvariantCulture)} saved translation(s) removed.";

    /// <summary>
    /// §4.2's <b>Chain</b> line: the tiers the app really built, in chain order, in §3.0's names.
    ///
    /// <para><b>It lists what exists and nothing else.</b> §4.2's mockup reads
    /// <c>Google → Edge → Google (backup) → Offline engine (not installed)</c>, and two of those are
    /// not shipped — Edge (ruling <b>E3-d</b>, U2 owner-blocked) and Bergamot (E8). An arrow
    /// pointing at an engine the app cannot call is precisely "a chain the app does not have", so
    /// they are omitted; the offline engine's absence is stated in full by its own block on this
    /// same tab, and the chip's tooltip lists every id in <see cref="ProviderIds.All"/> with
    /// <c>— not available</c> / <c>— not installed</c>. No surface claims a tier the builders did
    /// not construct, which is the half of UX-DR19 that matters here.</para>
    ///
    /// <para><b>Two lines, not one.</b> §4.2 shows a single Chain row; the app has two chains and
    /// <b>I8</b> makes them structurally different (DeepL can never be on the read one). The labels
    /// are the block's, in the XAML; this composes either.</para>
    /// </summary>
    public static string EngineChainLine(IReadOnlyList<string> tierIds)
    {
        ArgumentNullException.ThrowIfNull(tierIds);

        var names = new List<string>(tierIds.Count);
        foreach (var id in tierIds) names.Add(ProviderNames.Display(id) ?? id);
        return string.Join(" → ", names);
    }

    /// <summary>
    /// §4.2's reason column — <c>Google is paused, retries in 0:58</c>. It renders beside the
    /// <c>In use now</c> chip and only when the chip is not itself about the pause: in §2.1's
    /// <b>S2</b> the chip names the backup that answered, and this names the engine that was
    /// skipped, which is the half the player cannot otherwise see.
    ///
    /// <para><paramref name="countdown"/> is handed in already rendered (<b>I2</b>) and is null
    /// when the surface may not show a clock — §2.4's one countdown per window — in which case the
    /// clause states the pause and stops, exactly as the chip's own S3 arm does.</para></summary>
    public static string EnginePausedReason(string? providerId, string? countdown)
        => (ProviderNames.Display(providerId) ?? SomeEngine) + " is paused"
           + (countdown is null ? "" : ", retries in " + countdown);

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
    /// Edge is not in the chain (ruling E3-d), and §1's fourth principle is honest status.
    ///
    /// <para><b>Per provider and not §3.7's single "○ No key" row</b> (E7.S1). There are two key
    /// boxes with a status line each, and a shared sentence would tell a player with an Azure key
    /// and no DeepL key "No key" under a box whose engine is configured. The half that IS shared —
    /// what happens instead — is one string.</para></summary>
    public static string AzureNoKeyStatus() => NoKeyStatus(ProviderIds.Azure);

    /// <summary>The same row for the other key box (E6.S5's review found §3.7's cleared row was not
    /// in the codebase at all for DeepL: it said "○ Using Google (free, no key needed)", which
    /// names the fallback without naming what is missing).</summary>
    public static string DeepLNoKeyStatus() => NoKeyStatus(ProviderIds.DeepL);

    private static string NoKeyStatus(string providerId)
        => $"○ No {Display(providerId)} key — using the free engines (Google)";

    /// <summary>The DeepL box's configured line, the counterpart of
    /// <see cref="AzureKeySetStatus"/>. Its wording is unchanged by E7.S1 — §3.7 has no row for it,
    /// and rewording copy the deck does not cover is how a "copy pass" becomes an opinion — but it
    /// moved here from <c>MainWindow.Translate.cs</c>, where it was the last user-facing sentence
    /// still living in a code-behind (ruling GAP-4).</summary>
    public static string DeepLKeySetStatus()
        => "● DeepL for what you write (Translator + quick reply) — falls back to Google if it "
         + "errors. Screen reading uses Google.";

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
            // …and this row JOINS the usage text where the provider volunteered one, exactly as the
            // ok row above does. DeepL's `/v2/usage` answers the counts on the spent path too, and
            // dropping them there (review) withheld the numbers on the one row where a player most
            // wants to see them.
            TranslationErrorKind.QuotaExhausted => Joined(IsAzure(providerId)
                ? "⚠ Your 2 million free characters for this month are used up."
                : "⚠ The key works, but the DeepL quota is used up — the free engines are used until it resets.",
                result.UsageText),

            // §3.7's own row, and the reason T4 says this keys off the classifier's Kind and never
            // off a string match: "no internet" is a transport fact, not a word in a body.
            TranslationErrorKind.Network => "Could not check the key — no internet connection.",

            // Everything else — a timeout, a 5xx, a rate limit, a body nobody can read. §3.7 has no
            // row for these, so rather than invent five the deck's own sentence for the Kind is
            // joined after a colon, exactly as ReadFailed joins it (§3.3's join rule).
            // …and the engine IS named here (review, §3.0/A3): this row is rendered under the key
            // box of one specific provider, the id is a parameter of this very method, and the
            // paused row two lines up already says "Azure is paused right now". Leaving the joined
            // sentence on "the translation service" was the one place in the deck where {P} was
            // available and not used.
            var kind => Reason(kind, providerId, tryAgainIn),
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
    /// was refused before it left the machine.
    ///
    /// <para>The sentence is written ONCE and the countdown joined to it, rather than spelled in
    /// both arms of a conditional — UX-DR19 says exactly once, and the review's occurrence-counting
    /// scan is what stopped it being twice. The <c>{t}</c> is already rendered by
    /// <c>MainWindow.CountdownText</c>, the band formatter the LIVE loop and read-once share
    /// (I2 — <c>Services/</c> counts seconds and never formats them).</para></summary>
    private static string KeyTestPaused(string providerId, string? tryAgainIn)
        => Joined($"⚠ Not checked — {Display(providerId)} is paused right now.",
                  tryAgainIn is null ? null : $"Try again in {tryAgainIn}.");

    /// <summary>The §3.7-less kinds, as the deck's own sentence after a colon — with the engine
    /// named, because a key test knows which one it was testing. <see cref="LowerAtJoin"/>'s
    /// proper-noun guard is what keeps "DeepL took too long…" from becoming "deepL …" here.</summary>
    private static string Reason(TranslationErrorKind? kind, string? providerId = null,
        string? tryAgainIn = null)
    {
        var sentence = kind is { } k ? Sentence(k, providerId, tryAgainIn) : null;
        return sentence is null
            ? "Could not check the key."
            : Terminated($"Could not check the key: {LowerAtJoin(sentence)}");
    }

    /// <summary>An engine by the name the player knows it by. The ids themselves are
    /// <c>ProviderIds</c>' and never appear in copy (§3's "no provider internal").
    ///
    /// <para>It delegates to <see cref="ProviderNames"/> since E7.S1 rather than spelling "Azure"
    /// and "DeepL" a second time — that is UX-DR19 applied to a noun, and the chip (E7.S3) is about
    /// to read the same table.</para></summary>
    private static string Display(string providerId) => ProviderNames.Display(providerId) ?? SomeEngine;

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
    /// (review, E5.S4).</para>
    ///
    /// <para><b>Amendment A11, and the second guard it forces.</b> A11 confirms this rule for the
    /// colon join and removes it from every other — with <c>{P}</c> restored the first word of a
    /// deck sentence is usually an engine's name, and "google asked us to slow down" is a typo
    /// rather than a sentence. That argument does not stop at the parenthetical joins: it applies
    /// to THIS join too the moment the sentence starts with a name, and the second-capital guard
    /// above cannot see it ("DeepL", "Google (backup)", "Edge" all have a lower-case second
    /// letter). So a sentence opening with a name from <see cref="ProviderNames"/> is left alone
    /// for exactly the reason "GDI+" is: it is not sentence case, it is a proper noun.</para>
    /// </summary>
    public static string LowerAtJoin(string sentence)
        => string.IsNullOrEmpty(sentence)
           || (sentence.Length > 1 && char.IsUpper(sentence[1]))
           || OpensWithAProviderName(sentence)
            ? sentence
            : char.ToLowerInvariant(sentence[0]) + sentence[1..];

    /// <summary>Whether the first word of <paramref name="sentence"/> is an engine's user-facing
    /// name. Read from the one table (<see cref="ProviderNames"/>) so a name added later is covered
    /// the day it is added, and matched with the trailing space so "Edgewise" is not "Edge".</summary>
    private static bool OpensWithAProviderName(string sentence)
    {
        foreach (var id in ProviderIds.All)
            if (ProviderNames.Display(id) is { } name
                && sentence.StartsWith(name + " ", StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- §2.3 / §3.5: the provider chip, its tooltip, and the one line on recovery (E7.S3) ------
    //
    // Words only. Every glyph the chip renders and every countdown it carries is MainWindow's
    // (I2 — Services/ counts seconds and never formats them, and a brush key is a UI fact), and
    // that split is why these can be asserted headlessly. The GLYPHS the tooltip carries are the
    // exception, and a deliberate one: §2.3 writes those rows as whole strings ("● in use"), the
    // vocabulary is fixed (● ○ ⚠ and nothing new — §6), and splitting a three-word row across two
    // files would buy nothing and cost the one property this table exists for, that the copy can be
    // read in one place.

    /// <summary>§2.3's <c>checking…</c> — what the chip says for the first moments of a session,
    /// before ruling <b>E6-a</b>'s warm-up has read <c>provider-state.json</c>. It is not a state of
    /// any engine; it is the app declining to claim a health it has not verified yet.</summary>
    public static string EngineChipChecking() => "checking…";

    /// <summary>§2.1's <b>S5</b>. The subject is "all", not an engine, so it takes no name.</summary>
    public static string EngineChipAllPaused() => "All paused";

    /// <summary>§2.1's <b>S6</b> — the one chip that asks the player to do something they can
    /// actually do. Ruling <b>GAP-3</b>: it behaves like S5 (the full pause is universal).</summary>
    public static string EngineChipNoInternet() => NoInternetShort;

    /// <summary><b>UX-DR19 applied to two words.</b> §3.4's overlay quick reply and §2.3's S6 chip
    /// say the same thing about the same state, so they say it with the same string — one spelling,
    /// one place to change it. <c>UserMessagesTests.UXDR19_…</c> is what caught them drifting apart
    /// the moment the chip was written.</summary>
    private const string NoInternetShort = "No internet";

    /// <summary>§2.1's <b>S7</b>. No countdown, ever: an <c>AuthFailed</c> block has the
    /// <c>MaxValue</c> sentinel behind it and its exit is re-saving the key, so a clock here would
    /// be a promise nothing keeps.</summary>
    public static string EngineChipKeyRefused(string? providerId)
        => (ProviderNames.Short(providerId) ?? SomeEngineShort) + " key refused";

    /// <summary>§2.1's <b>S8</b> — the free engines are still serving, and the sentence says both
    /// halves: who is answering, and whose quota ran out.</summary>
    public static string EngineChipQuotaOut(string? servingId, string? quotaId)
        => (ProviderNames.Short(servingId) ?? SomeEngineShort) + " · "
           + (ProviderNames.Short(quotaId) ?? "a key") + " quota out";

    /// <summary>§2.1's <b>S3</b>: this engine is inside a window, something below it still serves.
    /// <paramref name="countdown"/> is already rendered by <c>MainWindow</c> (I2) and is null when
    /// there is no honest one — in which case the chip says "paused" and stops there rather than
    /// leaving a hole where a number should have been (amendment A12's spirit, in two words).</summary>
    public static string EngineChipPaused(string? providerId, string? countdown)
        => (ProviderNames.Short(providerId) ?? SomeEngineShort) + " paused"
           + (countdown is null ? "" : " " + countdown);

    /// <summary>§2.1's <b>S2</b>, with §3.0 rule 2 applied: the <c>· backup</c> suffix is dropped
    /// when the name already carries it, so a fallback onto <c>google-gtx</c> reads
    /// <c>Google (backup)</c> and never <c>Google (backup) · backup</c>.</summary>
    public static string EngineChipBackup(string? providerId)
    {
        var name = ProviderNames.Short(providerId) ?? SomeEngineShort;
        return name.Contains("backup", StringComparison.OrdinalIgnoreCase) ? name : name + " · backup";
    }

    /// <summary>§2.1's <b>S1</b> / <b>S4</b> — the healthy chip is a name and nothing else.</summary>
    public static string EngineChipServing(string? providerId)
        => ProviderNames.Short(providerId) ?? SomeEngineShort;

    /// <summary>§2.3's tooltip rows for a provider that is working. "In use" is the one that
    /// answered the last call; "ready" is a tier that would be asked if the one above it stopped
    /// answering.</summary>
    public static string EngineLineInUse() => "● in use";

    public static string EngineLineReady() => "● ready";

    /// <summary>§2.3's paused row. The countdown is handed in already rendered (I2); with none, the
    /// row says the state and no more.</summary>
    public static string EngineLinePaused(string? countdown)
        => countdown is null ? "○ paused" : "○ paused — retries in " + countdown;

    /// <summary>The three ways a tier can be absent, and none of them is an error (§2.3). Keyed off
    /// <see cref="EngineStatus"/>' reasons rather than off a provider id, so the tooltip cannot come
    /// to call Edge "not installed" and the offline engine "not available".</summary>
    public static string EngineLineOff(string? reason) => reason switch
    {
        EngineStatus.NotAvailable => "— not available",   // Edge: E3-d, no capture designated (U2)
        EngineStatus.NotInstalled => "— not installed",   // the offline engine: E8 has not shipped
        _ => "— not set",                                 // a key nobody has entered
    };

    /// <summary>The tooltip's parenthetical — <b>why</b> an engine is paused, in the player's words.
    /// §2.3: "no jargon, no HTTP codes"; the input is a <see cref="TranslationErrorKind"/> and never
    /// a status line, a header or a provider's own message (I11).
    ///
    /// <para>Null for the kinds that have nothing to add: a window with no recorded reason, the
    /// pass-through <c>Unknown</c>, and the kind that may not even be named in production source
    /// (TP-MAP-17) — which is why the last arm is a default and not a list.</para></summary>
    public static string? EngineLineReason(TranslationErrorKind? kind) => kind switch
    {
        TranslationErrorKind.RateLimited => "asked us to slow down",
        TranslationErrorKind.Blocked => "is refusing requests",
        TranslationErrorKind.Network => "no connection",
        TranslationErrorKind.Timeout => "did not answer in time",
        TranslationErrorKind.Unavailable => "is having trouble",
        TranslationErrorKind.BadResponse => "sent something unreadable",
        TranslationErrorKind.QuotaExhausted => "quota used up",
        TranslationErrorKind.AuthFailed => "key refused",
        _ => null,
    };

    /// <summary>§3.5's third line, and the only one E7.S3 owns: <c>Back on Google.</c> — shown
    /// <b>once</b> per recovery, on the status line, then left alone, so the player knows the chip
    /// changed for a reason.
    ///
    /// <para>Null when no name is known, and the caller then writes nothing at all. §3.0 rule 1 —
    /// nothing here is ever invented — and "Back on the translation service." is a sentence with no
    /// information in it: the whole point of the notice is the NAME.</para></summary>
    public static string? BackOn(string? providerId)
        => ProviderNames.Display(providerId) is { } p ? "Back on " + p + "." : null;

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

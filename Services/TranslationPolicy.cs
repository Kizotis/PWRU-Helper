namespace PWRUHelper.Services;

/// <summary>
/// Every tunable number of the translation path, in one place, each with the evidence behind it.
/// It exists so the constants that will be argued about in the field are already in the place the
/// argument can be settled — a number with no provenance is exactly what this file prevents.
///
/// Evidence grades, taken from the investigation documents rather than from an opinion:
/// <list type="bullet">
/// <item><c>[CONFIRMED]</c> — read in the code at this commit, with the <c>file:line</c> that holds it.</item>
/// <item><c>[MEASURED]</c> — a value this project measured; the comment says where and when.</item>
/// <item><c>[ASSUMED]</c> — calibrated to a reported range and never measured; the comment says what
///       would settle it.</item>
/// <item><c>[UNKNOWN]</c> — the value ships at the SAFE end of an open question, and the comment
///       names the open question and the capture that would close it. Different from
///       <c>[ASSUMED]</c> on purpose: an assumption is calibrated to something, this is not
///       calibrated to anything — it is the answer that is correct whether the question is ever
///       settled or not.</item>
/// </list>
///
/// A table and nothing else: no methods, no state, no I/O, and no dependency — not even on
/// <c>Logging</c> (I2). It holds today's values plus the §5.6 target numbers whose code has
/// landed — the six breaker numbers arrived with <c>ProviderGate</c> (E2.S1), the four rate-ceiling
/// numbers with its token bucket (E2.S3), and the two retry numbers with <c>HttpProviderCore</c>
/// (E2.S5), which is also where the two "…Today" retry constants stopped describing today and were
/// retired, and <c>PerLineCap</c> arrived with E3.S8's shared per-line loop. <c>CacheCapacity</c>
/// arrived with E4.S1's <c>TranslationCacheStore</c>, whose default it is, and
/// <c>CacheSaveDebounceMs</c> with E4.S2's cache file. The rest (the LIVE numbers …) still arrive
/// with the code that reads them — E5 for LIVE — because an unused constant is a constant nobody
/// grades.
/// Source: <c>docs/investigations/02-traduction/architecture-cible.md</c> §5.6 (the target table),
/// §4.3 (the HTML markers).
/// </summary>
internal static class TranslationPolicy
{
    // ---- what the providers do today ---------------------------------------------------------
    // Behaviour-neutral by construction: each one is the literal that was already in the code,
    // moved here and referenced from the same place. If one of them changes value, the change
    // belongs to the story that changes the behaviour with it.

    /// <summary>HttpClient timeout for every provider request, Google and DeepL alike.</summary>
    public const int RequestTimeoutSeconds = 12;    // [CONFIRMED] now read once, at HttpProviderCore.CreateClient

    /// <summary>The <b>legacy decorator default</b>: what a <c>CachingTranslator</c> built without a
    /// store gives its own private one. No longer "today's cache" and, since E4.S4, no longer what
    /// ships either — all three chains are decorators over the shared store, whose capacity is
    /// <see cref="CacheCapacity"/>. This number survives only as the parameter default of the
    /// constructor that has no store to read a capacity from, which nothing in production calls.
    /// Kept rather than deleted because that constructor is public API of an assembly the tests
    /// exercise, and because a story that ever needs a private cache should get 500 and not 2000 of
    /// them.</summary>
    public const int CacheCapacityToday = 500;      // [CONFIRMED] now the ctor default at CachingTranslator.cs:25

    /// <summary>Entries kept by the shared LRU translation cache — §5.6's number, the default of
    /// <see cref="TranslationCacheStore"/>, and since E4.S4 what the one store the app builds is
    /// built with (<c>TranslationChains.Cache</c>, pinned by <c>ChainCompositionTests</c>). §8.2's
    /// ~150 B an entry ⇒ ≈300 KB was the estimate for the JSON FILE, and U8 found it three times
    /// short (see below); in memory an entry also carries two string objects, a list node and a
    /// dictionary slot, which is the half U8 had to measure because this app has a memory budget it
    /// has been bitten by.</summary>
    // [MEASURED] E4.S3 / U8 on the dev box, 2026-09-07 (docs/investigations/03-stories/spikes/
    // U8-cache-load.md): a full 2000-entry file of realistic Cyrillic chat lines is 981 KB and
    // loads in 17.8 ms (median of 7, warm) on the calling thread of the first miss, for +960 KB
    // managed / +892 KB working set — against §8.2's go criterion of 50 ms and G6's ~150 MB budget,
    // both with an order of magnitude to spare. The capacity is the knob and it did not need
    // turning; what the same measurement DID turn is TranslationCacheStore.MaxBytes, which the
    // 981 KB had come within 4% of. The remaining open half is the cold, Defender-scanned number
    // from a personal machine (the owner's hand-off), which does not gate A.2.
    public const int CacheCapacity = 2000;

    /// <summary>How long <see cref="TranslationCacheStore"/> waits after a store before it writes
    /// <c>translation-cache.json</c>, coalescing every store inside the window into one write
    /// (§8.2). Five seconds and not one: a LIVE tick stores several entries a second, and the file
    /// is two orders of magnitude larger than <c>provider-state.json</c> — whose 1 s window
    /// (<c>ProviderGates.SaveDebounceMs</c>) covers a handful of bytes on a rare transition, not
    /// 300 KB on a hot path. The window is fixed from the FIRST pending store rather than restarted
    /// by each one, so a busy minute cannot postpone the write for ever; a close inside the window
    /// is covered by <c>SaveNow()</c> on the <c>OnClosing</c> path.</summary>
    // [ASSUMED] architecture-cible.md §8.2 ("save debounced ~5 s, plus one on exit"); the WINDOW is
    // still unmeasured. What U8 (E4.S3) settled is only its cost: a full 2000-entry write measured
    // 15.8 ms on the dev box (2026-09-07), so the five seconds buy coalescing and not headroom for
    // a slow write. What is left to settle is the window itself — field reports of the app being
    // closed mid-window, and the storage a real user has.
    public const int CacheSaveDebounceMs = 5000;

    /// <summary>The text travels in a GET query string, so it is chunked to stay well under
    /// typical URL limits.</summary>
    public const int MaxQueryBytes = 1500;          // [CONFIRMED] now read at GoogleGtxTranslator.cs:111, :116, :135
                                                    // (:116 passes it on to Services/TextChunker.cs, E3.S6)

    /// <summary>Whether <c>GoogleDictTranslator</c> may translate a multi-line group as ONE
    /// <c>\n</c>-joined <c>q</c>, splitting the answer back on <c>\n</c>. <b>False</b>, which is
    /// OQ-A's settled answer: <c>dict-chrome-ex</c> returns one string rather than gtx's segments,
    /// and whether the newlines survive the round trip is U1 — the single most important [UNKNOWN]
    /// of <c>architecture-cible.md</c> §7.1. Joining on a guess would silently glue a squad's
    /// thirteen chat lines into one sentence.
    ///
    /// <para>It lives here rather than in the provider so the flip is a one-line change to a graded
    /// table with a test on it, not an edit inside a request path. E3.S1's capture
    /// (<c>google-dict-batch.txt</c>) is what flips it, in the same commit that turns TP-PRV-04 from
    /// the negative pin ("3 lines cost 3 requests") into the positive one. Multi-<c>q=</c> is not
    /// the alternative — it is a declined non-feature (<c>project-context.md</c>).</para>
    ///
    /// <para><c>static readonly</c> rather than <c>const</c>: a <c>const false</c> would make the
    /// dormant join path unreachable code, and the compiler would report the very branch this value
    /// exists to keep compiled, tested-adjacent and one edit from live.</para></summary>
    // [UNKNOWN] until U1 — architecture-cible.md §7.1; settled by E3.S1's capture, test-plan TP-PRV-04
    public static readonly bool GoogleDictBatchJoinEnabled = false;

    /// <summary>How many lines a per-line fallback may ask for after a batch that failed or came
    /// back with the wrong count. Read by <see cref="PerLineFallback"/> (E3.S8), which is the one
    /// loop the join/split providers share; beyond it the remaining lines get the skipped
    /// placeholder and cost <b>no request at all</b>.
    ///
    /// <para>What the number buys: <c>analyse…</c> S6/A11 measured a mismatch on a 14-line LIVE tick
    /// turning one logical translation into up to 30 requests inside that tick, on a connection that
    /// was already being throttled. At 8 the worst case is <b>8 lines asked</b> instead of thirty —
    /// lines and not requests, because one line can cost several (a line over
    /// <see cref="MaxQueryBytes"/> is chunked) or none at all (the gate refused it, or it was
    /// blank). The measured LIVE batch size is ≈2.1 lines, so on a healthy tick this constant never
    /// fires at all.</para>
    ///
    /// <para><b>It bounds the FALLBACK and never a primary per-line path</b> (ruling E3-e):
    /// <c>GoogleDictTranslator</c> ships per line by design under OQ-A, whose answer accepts "≈2× the
    /// LIVE request volume", and capping that would refuse the behaviour the owner approved. That
    /// path is bounded by the §5.4 rate ceiling and by the gate instead.</para></summary>
    // [ASSUMED] architecture-cible.md §6.3 / §5.6; calibrated to analyse-implementation-actuelle.md
    // S6/A11 (the 14-line → 30-request amplifier) and never measured. Field logs settle it (U9/E2.S7).
    public const int PerLineCap = 8;

    // ---- the retry policy (§5.6) ---------------------------------------------------------------
    // Read by Services/HttpProviderCore.cs (E2.S5), which replaced the three-attempt / 300 ms-linear
    // loop these two numbers describe the successor of. Both are [ASSUMED] and both are revisited
    // from field logs after the A.1 release (U9 / E2.S7).

    /// <summary>Requests per logical call: one try plus at most one retry, and only for a failure a
    /// second attempt could survive (<c>Unavailable</c>, <c>Timeout</c>). benchmark-fournisseurs.md
    /// §11.4 item 3: three attempts into a hard block triple the abuse signal for no benefit — and
    /// §10.1's line renders <c>attempt=n/m</c>, so the bound is read from here rather than written
    /// twice.</summary>
    public const int MaxAttempts = 2;               // [ASSUMED] architecture-cible.md §5.6; benchmark-fournisseurs.md §11.4 item 3

    /// <summary>Base of the exponential back-off, drawn with <b>full jitter</b>:
    /// <c>Random(0, BackoffBaseMs &lt;&lt; attempt)</c>. Jittered rather than fixed because two
    /// instances behind one NAT retrying in lockstep is what a fixed spacing guarantees.</summary>
    public const int BackoffBaseMs = 500;           // [ASSUMED] architecture-cible.md §5.6

    // ---- circuit breaker (§5.6) --------------------------------------------------------------
    // Read by Services/ProviderGate.cs (E2.S1). Every one of the six is [ASSUMED]: they are
    // calibrated to a REPORTED range (mecanismes-de-blocage-google.md Q3: "a few minutes" ..
    // "12-24 h") and this project has never measured one. They ship instrumented and are tuned
    // from >= 3 field reports after the A.1 release (U9 / E2.S7) — instrument first, tune from the
    // logs, never from an opinion. A field experiment can override all six at runtime through
    // GatePolicy.Parse, which is why the gate reads them through GatePolicy rather than directly.

    /// <summary>First strike's open window. Also the window three consecutive
    /// <c>BadResponse</c>s open, and the floor the strikes reset to after a clean run.</summary>
    public const int OpenBaseSeconds = 60;      // [ASSUMED] architecture-cible.md §5.6; matches the app's own "wait a minute"

    /// <summary>Ceiling on the doubling — 60 s, 2, 4, 8, 16, then 30 min for ever. Also the clamp
    /// on a server-sent <c>Retry-After</c> (§5.5): beyond this a user restarts rather than waits,
    /// so a 24-hour hint would simply read as a broken app.</summary>
    public const int OpenCapMinutes = 30;       // [ASSUMED] architecture-cible.md §5.6

    /// <summary>How long a provider must behave before its strike count is forgiven, so the next
    /// failure opens for 60 s and not for the escalated window.</summary>
    public const int CleanResetMinutes = 10;    // [ASSUMED] architecture-cible.md §5.6

    /// <summary>A quota is refilled by a billing period, not by a back-off, so the gate waits an
    /// hour instead of escalating. Re-saving the key clears it (§5.3).</summary>
    public const int QuotaOpenMinutes = 60;     // [ASSUMED] architecture-cible.md §5.6, §15 R9

    /// <summary>The no-strike cooldown after a 5xx, a timeout, a DNS blip or an unclassifiable
    /// failure. Small, and not optional: without it the next LIVE tick re-hits the same dead
    /// provider 700 ms later.</summary>
    public const int SoftCooldownSecs = 5;      // [ASSUMED] architecture-cible.md §5.6

    /// <summary>Consecutive <c>BadResponse</c>s that open the gate. One is a hiccup; three in a row
    /// is a provider whose shape has changed.</summary>
    public const int BadResponseStrikesToOpen = 3;  // [ASSUMED] architecture-cible.md §5.6

    // ---- rate ceiling (§5.4) -----------------------------------------------------------------
    // Read by Services/ProviderGate.cs's token bucket (E2.S3). All four are [ASSUMED]: 500 ms is
    // the value the ecosystem converged on (mecanismes-de-blocage-google.md Q2) and this project
    // has measured none of them. They ship instrumented and are tuned from >= 3 field reports
    // after the A.1 release (U9 / E2.S7) — instrument first, tune from the logs, never from an
    // opinion. Deliberately NOT pinned by TranslationPolicyTests' today's-values case: a literal
    // there would make E2.S7's tuning commit rewrite the suite.

    /// <summary>Minimum spacing between two requests to the same provider, expressed as the token
    /// bucket's refill period: one token per <c>MinSpacingMs</c>.</summary>
    public const int MinSpacingMs = 500;        // [ASSUMED] architecture-cible.md §5.4/§5.6; mecanismes-de-blocage-google.md Q2

    /// <summary>Tokens the bucket holds at rest. Two, so an interactive keypress after a quiet
    /// minute is never made to wait — and so exactly one of them can be reserved: with a capacity
    /// of 2, "<c>Background</c> may not take the last token" is the whole reserve (§5.4).</summary>
    public const int BucketCapacity = 2;        // [ASSUMED] architecture-cible.md §5.4

    /// <summary>How long a caller may sit on a <c>Wait</c> before the tier counts as unavailable.
    /// It is the <b>caller's</b> rule, not the gate's: <c>ChainTranslator</c> (E3.S3) moves on past
    /// a longer wait and the LIVE loop (E5.S1) backs off — <c>ProviderGate.TryEnter</c> never waits
    /// for anybody (§5.4: it is not a scheduler).</summary>
    public const int MaxSpacingWaitMs = 2000;   // [ASSUMED] architecture-cible.md §5.4

    /// <summary>How long a <c>Background</c> caller that finds the gate probe-eligible stands aside
    /// before taking the half-open probe itself, so a user pressing Enter inside that second gets it
    /// instead (§5.4, architect's concern #1). One second: long enough to cover a keystroke, short
    /// enough that a paused provider is re-tried promptly when nobody is typing.</summary>
    public const int ProbeDeferMs = 1000;       // [ASSUMED] architecture-cible.md §5.4

    // ---- the paused LIVE loop (§9.1) ---------------------------------------------------------
    // Read by Services/LiveTickPolicy.cs (E5.S1), which is where the arithmetic lives.

    /// <summary>Longest wait between two <b>skipped</b> LIVE ticks. While every read tier is inside
    /// a block window the loop does nothing at all — no capture, no OCR, no request (OQ-B) — and the
    /// wait doubles per skipped tick until it reaches this ceiling: 0.7 s → 1.4 → 2.8 → 5 → 5…
    ///
    /// <para>Five seconds is the compromise the two halves of the requirement meet at. Longer and a
    /// gate that reopens (a 5 s <see cref="SoftCooldownSecs"/> window, the common case for a dropped
    /// connection) would leave the feed frozen for a visible extra beat after the network is back;
    /// shorter and a long block — a 30-minute <see cref="OpenCapMinutes"/> window — would cost
    /// hundreds of pointless wake-ups a minute in a loop whose entire purpose is to be free while
    /// paused. A skipped tick costs a <c>Snapshot()</c> and a string, so the ceiling is about the
    /// wake-up and not about the work.</para></summary>
    // [ASSUMED] architecture-cible.md §9.1 (the back-off sketch); the ceiling is calibrated to
    // SoftCooldownSecs = 5 and has never been measured. Field logs settle it (U9 / E2.S7).
    public const int LiveBackoffCapMs = 5000;

    // ---- the honest auto-stop (§9.2) ----------------------------------------------------------
    // Read by Services/LiveTickPolicy.cs's LiveErrorTracker (E5.S2). ONE number and one rule: LIVE
    // stops itself after LiveAutoStopThreshold consecutive failures that COST A REQUEST, counted
    // since the last tick that translated — no time window, whatever the elapsed time (architect's
    // ruling, E5.S2 review). What does not count is as important as what does: an EMPTY tick asked
    // the providers nothing and forgives nothing (that was the bug), and a pause or a gate refusal
    // is the system working correctly and never reaches the counter at all (ruling E5-c).

    /// <summary>Sent failures that stop LIVE. <b>Five is not a new number</b>: it is the literal
    /// that shipped inside the loop (<c>if (++consecutiveErrors >= 5)</c>), moved here so that the
    /// rule around it could change without the number changing with it — which is exactly what
    /// happened: what the five now counts is five <i>sent</i> failures since the last translated
    /// tick, where it used to count five non-empty ticks in a row.
    ///
    /// <para>It is pinned by <c>TranslationPolicyTests</c> — unlike the rate-ceiling four, which are
    /// deliberately unpinned — because it decides <b>when LIVE stops</b>, which is behaviour a
    /// player watches and already knows.</para></summary>
    // [CONFIRMED] the literal this app shipped with: `if (++consecutiveErrors >= 5)` in
    // MainWindow.Live.cs' generic catch, up to and including baseline 9c8e935. Cited by the
    // expression and not by a line number, which the same commit moved.
    public const int LiveAutoStopThreshold = 5;

    // ---- HTML abuse-page markers (§4.3) ------------------------------------------------------
    // Matched lower-cased against DE-TAGGED text — E1.S4 does the de-tagging and lower-casing, so
    // the literals are kept lower-case here and no call site has to remember. Order matters at the
    // call site, not here: §4.3 step 3 checks RateLimitMarkers BEFORE BlockMarkers, because the
    // "automated queries" page also says "we're sorry" and is a throttle, not a permanent block.

    /// <summary>The body this app really received on 2026-09-06: "Sorry... We're sorry... but your
    /// computer or network may be sending automated queries. To protect our users, we can't process
    /// your request right now." — a 429 with <c>Content-Type: text/html</c> and no
    /// <c>Retry-After</c>.</summary>
    // [MEASURED] benchmark-fournisseurs.md §3.1 (own probe, 2026-09-06 11:29:02 GMT)
    public static readonly string[] RateLimitMarkers = { "automated queries", "unusual traffic" };

    /// <summary>A captcha or interstitial page: refusal rather than throttling. A new phrasing is
    /// one edit away; what would settle these is a captured body from a blocked machine.</summary>
    // [ASSUMED] architecture-cible.md §4.3, never seen by this project
    public static readonly string[] BlockMarkers = { "we're sorry", "captcha", "recaptcha" };
}

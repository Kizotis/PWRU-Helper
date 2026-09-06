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
/// </list>
///
/// A table and nothing else: no methods, no state, no I/O, and no dependency — not even on
/// <c>Logging</c> (I2). It holds today's values plus the §5.6 target numbers whose code has
/// landed — the six breaker numbers arrived with <c>ProviderGate</c> (E2.S1), the four rate-ceiling
/// numbers with its token bucket (E2.S3), and the two retry numbers with <c>HttpProviderCore</c>
/// (E2.S5), which is also where the two "…Today" retry constants stopped describing today and were
/// retired. The rest (<c>PerLineCap</c>, <c>CacheCapacity = 2000</c> …) still arrive with the code
/// that reads them — E3.S8 for the batch cap, E4 for the cache, E5 for LIVE — because an unused
/// constant is a constant nobody grades.
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

    /// <summary>Entries kept by the in-memory LRU translation cache. §5.6 raises it to 2000 and
    /// persists it (E4); today it is memory-only and dies with the process.</summary>
    public const int CacheCapacityToday = 500;      // [CONFIRMED] now the ctor default at CachingTranslator.cs:24

    /// <summary>The text travels in a GET query string, so it is chunked to stay well under
    /// typical URL limits.</summary>
    public const int MaxQueryBytes = 1500;          // [CONFIRMED] now read at TranslationService.cs:83, :88, :107

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

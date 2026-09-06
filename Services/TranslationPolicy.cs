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
/// <c>Logging</c> (I2). It holds <b>today's</b> values only. The §5.6 target numbers
/// (<c>OpenBaseSeconds</c>, <c>MinSpacingMs</c>, <c>MaxAttempts = 2</c>, <c>PerLineCap</c>,
/// <c>CacheCapacity = 2000</c> …) arrive with the code that reads them — E2.S1/E2.S3/E2.S5 for the
/// gate and the retry, E3.S8 for the batch cap, E4 for the cache, E5 for LIVE — because an unused
/// constant is a constant nobody grades.
/// Source: <c>docs/investigations/02-traduction/architecture-cible.md</c> §5.6 (the target table),
/// §4.3 (the HTML markers).
/// </summary>
internal static class TranslationPolicy
{
    // ---- what the providers do today ---------------------------------------------------------
    // These five are behaviour-neutral by construction: each one is the literal that was already
    // in the code, moved here and referenced from the same place. If one of them changes value,
    // the change belongs to the story that changes the behaviour with it.

    /// <summary>HttpClient timeout for every provider request, Google and DeepL alike.</summary>
    public const int RequestTimeoutSeconds = 12;    // [CONFIRMED] now read at TranslationService.cs:57 and DeepLTranslator.cs:48

    /// <summary>Requests per translated line today: one try plus two retries. Named "…Today" so it
    /// cannot be confused with §5.6's target <c>MaxAttempts = 2</c>, which E2.S5 introduces —
    /// benchmark-fournisseurs.md §11.4 item 3: three attempts into a hard block triple the abuse
    /// signal for no benefit.</summary>
    public const int MaxAttemptsToday = 3;          // [CONFIRMED] TranslationService.cs:158 (`attempt < 3`), still a literal there

    /// <summary>Base of the linear back-off between those attempts: <c>300 * (attempt + 1)</c>, so
    /// 300 ms then 600 ms. §5.6's target replaces it with exponential + full jitter.</summary>
    public const int RetrySpacingBaseMs = 300;      // [CONFIRMED] TranslationService.cs:229, still a literal there

    /// <summary>Entries kept by the in-memory LRU translation cache. §5.6 raises it to 2000 and
    /// persists it (E4); today it is memory-only and dies with the process.</summary>
    public const int CacheCapacityToday = 500;      // [CONFIRMED] now the ctor default at CachingTranslator.cs:24

    /// <summary>The text travels in a GET query string, so it is chunked to stay well under
    /// typical URL limits.</summary>
    public const int MaxQueryBytes = 1500;          // [CONFIRMED] now read at TranslationService.cs:76, :81, :100

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

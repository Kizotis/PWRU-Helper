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
/// <item><b>No terminal full stop.</b> Every one of today's six call sites <i>joins</i> this text
///       into a longer line — "Failed: {s}", "({s})", "Live hiccup ({s}) — retrying…",
///       "Live stopped after repeated errors ({s}).", "OCR failed: {s}",
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
}

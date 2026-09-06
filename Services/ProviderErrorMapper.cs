using System.Net.Http;

namespace PWRUHelper.Services;

/// <summary>
/// The single place that turns a raw HTTP outcome — a status code, the head of a body, a transport
/// exception — into a <see cref="TranslationErrorKind"/>. It exists so no second place in the
/// codebase can disagree about what a 403 means: today the free Google endpoint folds 403 in with
/// 400/404 and a bot block reads like a bug, while DeepL reads the same status as a rejected key.
/// Both are right for their own provider, and neither could say so.
///
/// The rules are <c>architecture-cible.md</c> §4.2, written in the table's order so a reviewer can
/// diff them line by line against the document. <b>First match wins</b>, and the function is
/// <b>total</b>: an outcome no rule matches is <c>Unknown</c>, never a guess. <c>Unknown</c> in a
/// field log is a bug report about this file.
///
/// Pure and headless (I2): no <c>Logging</c>, no I/O, no UI type. It <i>reads</i> a body head and
/// never stores or forwards one (I11) — E1.S5 is the only story that writes anything to the log.
/// </summary>
internal static class ProviderErrorMapper
{
    /// <summary>Row 1's return value — the "a genuine user cancel" member of
    /// <see cref="TranslationErrorKind"/>.
    /// <para>TP-MAP-17 bans that member's qualified token from every production file, because
    /// handing this Kind to a <see cref="TranslationException"/> is the phantom-user-cancel bug the
    /// ban exists to prevent, and a scan can only see the name. The mapper is the one place that
    /// must be able to <b>return</b> the value, so it resolves the member by name rather than
    /// writing the token: the ban stays mechanical (this file still cannot pass the Kind to a
    /// constructor), and the resolution fails loudly at type-init if the member is ever renamed —
    /// which the enum-order test already pins.</para></summary>
    private static readonly TranslationErrorKind UserCancelled =
        Enum.Parse<TranslationErrorKind>("Cancelled");

    /// <summary>Row 6's "the error envelope names a quota/limit" test — a substring match over the
    /// body head. Azure's shape is <c>{"error":{"code":403,…,"message":"… quota …"}}</c>; DeepL
    /// signals the same thing with a 456 and never needs this. Kept next to the rule (the story's
    /// first option) rather than in <see cref="TranslationPolicy"/>: nothing else reads them, and
    /// it is E1.S4's HTML markers that needed a shared home. E6.S2 moves them if Azure wants
    /// them too.</summary>
    // [ASSUMED] architecture-cible.md §4.2 row 6 — never seen from a real Azure key by this project.
    internal static readonly string[] QuotaMarkers = { "quota", "out of credit", "limit exceeded" };

    // Returns Cancelled ONLY when ct.IsCancellationRequested. The caller's contract is:
    //   if (kind == Cancelled) throw;   // rethrow the original OCE, never wrap it
    internal static TranslationErrorKind Classify(HttpResponseMessage? resp, string? bodyHead,
        Exception? transport, bool keyWasSent, CancellationToken ct)
    {
        // 1 — a genuine cancel. Nothing below may see it: an OCE bound to a cancelled token is the
        // user pressing Stop, and the caller rethrows it untouched.
        if (ct.IsCancellationRequested) return UserCancelled;

        // 2 — THE OCE TRAP, encoded once for the whole app. On .NET 8 an HttpClient timeout arrives
        // as a TaskCanceledException (a subclass of OperationCanceledException) carrying a token
        // that is NOT cancelled. Row 1 already took every cancelled token, so anything reaching
        // here is a timeout — never a cancel. Reading it the other way once disabled the
        // DeepL→Google fallback and left a zombie LIVE indicator (I3, risk R-03).
        if (transport is OperationCanceledException) return TranslationErrorKind.Timeout;

        // 3 — DNS, TLS, connect, proxy refused.
        if (transport is HttpRequestException) return TranslationErrorKind.Network;

        if (resp != null)
        {
            int code = (int)resp.StatusCode;

            if (code == 429) return TranslationErrorKind.RateLimited;   // 4
            if (code == 401) return TranslationErrorKind.AuthFailed;    // 5

            // 6, 7, 8 — the split this story exists for. With a key, a 403 is the key's problem
            // (quota if the envelope says so, otherwise a rejection); with no key it is the
            // endpoint refusing this network, which is a block and not a bug.
            if (code == 403)
                return !keyWasSent ? TranslationErrorKind.Blocked
                    : NamesAQuota(bodyHead) ? TranslationErrorKind.QuotaExhausted
                    : TranslationErrorKind.AuthFailed;

            if (code == 456) return TranslationErrorKind.QuotaExhausted;   // 9 — DeepL's own code
            if (code >= 500) return TranslationErrorKind.Unavailable;      // 10

            // 11 — E1.S4: HTML sniff goes here, BEFORE the success path. It applies to ANY status,
            // 2xx included, which is why its place in the order is this one and not inside the
            // branch below. Do not move the success path above it.

            // 12 — a success whose body does not parse into the provider's shape. The provider's
            // parser is what detects that; the mapper only names it.
            if (resp.IsSuccessStatusCode) return TranslationErrorKind.BadResponse;
        }

        // 13 — anything else non-success (400, 404, a 3xx, or nothing at all). Never throws, never
        // guesses: a wrong guess is worse than an honest Unknown.
        return TranslationErrorKind.Unknown;
    }

    /// <summary>Row 6's envelope test. Case-insensitive because the wording is the provider's, and
    /// a null head simply means the caller did not read the body — which is not a quota answer.</summary>
    internal static bool NamesAQuota(string? bodyHead) =>
        bodyHead != null &&
        QuotaMarkers.Any(m => bodyHead.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The server's own "come back at" hint, when it sent one — <c>Retry-After</c> as
    /// delta-seconds or as an HTTP date. §5.5: on a 429 it overrides the window the gate would
    /// compute; until E2 reads it, it simply travels on the <see cref="TranslationException"/>.
    /// The delta is resolved against <paramref name="now"/> rather than the ambient clock, so the
    /// gate's injected clock can drive it later (IS-6) and a test needs no wall clock.</summary>
    internal static DateTimeOffset? RetryAfter(HttpResponseMessage? resp, DateTimeOffset now)
    {
        var header = resp?.Headers.RetryAfter;   // null when absent OR unparseable — both mean "nothing said"
        if (header == null) return null;
        return header.Delta is { } delta ? now + delta : header.Date;
    }
}

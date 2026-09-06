using System.Net.Http;
using System.Text;

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
/// Pure and headless (I2): no <c>Logging</c>, no I/O, no UI type. It <i>reads</i> a body and stores
/// nothing (I11); the single thing it hands back is row 11's de-tagged 120-character head of the
/// <b>server's</b> page — never the user's text — and E1.S5 is still the only story that writes
/// anything to the log.
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

    /// <summary>§4.3 step 2 — how much of the body the '&lt;' sniff may look at.</summary>
    private const int SniffPrefixChars = 200;

    /// <summary>§4.3 step 3 — how much DE-TAGGED text the markers are matched against.</summary>
    private const int MarkerHeadChars = 400;

    /// <summary>§10.1 — how much of that head may travel to the log line (I11).</summary>
    private const int LogHeadChars = 120;

    /// <summary>The de-tagger's input bound. It is NOT 400: the measured §3.1 page spends its first
    /// ~700 characters on a &lt;style&gt; block and a six-tag logo, so a 400-character *input* scan
    /// would stop before "automated queries" and classify the one body this story exists for as an
    /// unrecognised page. What is bounded is both ends — at most this many characters read, at most
    /// <see cref="MarkerHeadChars"/> produced — which is what "no catastrophic backtracking" asks
    /// for; a full abuse page is ~1.1 KB, so this is ~8× the real shape.</summary>
    private const int MaxScanChars = 8192;

    // Returns Cancelled ONLY when ct.IsCancellationRequested. The caller's contract is:
    //   if (kind == Cancelled) throw;   // rethrow the original OCE, never wrap it
    internal static TranslationErrorKind Classify(HttpResponseMessage? resp, string? bodyHead,
        Exception? transport, bool keyWasSent, CancellationToken ct)
        => Classify(resp, bodyHead, transport, keyWasSent, ct, out _);

    /// <summary>The same rules, plus row 11's by-product: when the body took the §4.3 HTML path,
    /// <paramref name="htmlHead"/> is its de-tagged, whitespace-collapsed first
    /// <see cref="LogHeadChars"/> characters — the only thing from a body that is allowed to travel
    /// anywhere (I11), and the field E1.S5 writes as <c>body=</c>. <c>null</c> whenever the HTML
    /// path was not taken, so a caller cannot log a head for a body that was never sniffed. The
    /// five-argument overload above keeps every existing call site compiling unchanged.</summary>
    internal static TranslationErrorKind Classify(HttpResponseMessage? resp, string? bodyHead,
        Exception? transport, bool keyWasSent, CancellationToken ct, out string? htmlHead)
    {
        htmlHead = null;

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

            // 6, 7, 8 — the split this story exists for, written as three separate rules in the
            // table's own order so the file diffs line by line against §4.2. (A single nested
            // conditional says the same thing, but it has to test row 8 first to stay readable,
            // and the one instruction that exists purely for reviewability is the order.) With a
            // key, a 403 is the key's problem — quota if the envelope says so, otherwise a
            // rejection; with no key it is the endpoint refusing this network, which is a block
            // and not a bug.
            if (code == 403 && keyWasSent && NamesAQuota(bodyHead))
                return TranslationErrorKind.QuotaExhausted;                // 6
            if (code == 403 && keyWasSent) return TranslationErrorKind.AuthFailed;   // 7
            if (code == 403) return TranslationErrorKind.Blocked;                    // 8

            if (code == 456) return TranslationErrorKind.QuotaExhausted;   // 9 — DeepL's own code
            if (code >= 500) return TranslationErrorKind.Unavailable;      // 10

            // 11 — the HTML abuse page (§4.3), BEFORE the success path. It applies to ANY status,
            // 2xx included, which is why its place in the order is this one and not inside the
            // branch below. Do not move the success path above it.
            //
            // The rule is an ORDERING, not a heuristic: the body is classified before it is parsed,
            // never after. benchmark-fournisseurs.md §11.4 item 1 names "parse-then-guess" as the
            // single most common bug across every project surveyed, and this app has it today — a
            // block page served with a 200 reaches JsonDocument.Parse and comes back as "an
            // unexpected response" by accident. The caller's part of the rule is §4.3 step 4: it
            // asks LooksLikeHtml FIRST and hands the body to its parser only if the answer is no.
            if (LooksLikeHtml(resp, bodyHead))
            {
                var head = DeTaggedHead(bodyHead);
                htmlHead = head.Length <= LogHeadChars ? head : head[..LogHeadChars];
                var lower = head.ToLowerInvariant();   // the markers are lower-case by contract

                // RateLimitMarkers FIRST: the measured page says "we're sorry" as well as
                // "automated queries", and it is a throttle, not a permanent refusal.
                if (Matches(lower, TranslationPolicy.RateLimitMarkers))
                    return TranslationErrorKind.RateLimited;

                // The two remaining §4.3 outcomes share a Kind but not a meaning, so they are
                // written as the table writes them (rows 6/7/8 above set the same precedent): a
                // captcha or interstitial we recognise…
                if (Matches(lower, TranslationPolicy.BlockMarkers))
                    return TranslationErrorKind.Blocked;

                // …and a page nobody has a phrase for yet. Still Blocked — HTML where a provider's
                // JSON belongs is a refusal — and htmlHead above is what makes the next phrasing a
                // one-line edit in TranslationPolicy instead of a guess.
                return TranslationErrorKind.Blocked;
            }

            // 12 — a success whose body does not parse into the provider's shape. The provider's
            // parser is what DETECTS that; the mapper only names it — which is also the row's
            // caller contract: Classify is asked what went wrong, so a caller must not hand it a
            // healthy 200. There is no signal here that could tell the two apart, and answering
            // "it parsed fine" is not this function's job. (E2.S5's HttpProviderCore is the next
            // caller: classify the failure, never the success.)
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

    /// <summary>
    /// §4.3 steps 1–2 — is this body an HTML page rather than the provider's JSON? Two signals, in
    /// the document's order.
    /// <para><b>Step 1, the content-type.</b> A DECLARED media type that is not a JSON one takes
    /// the HTML path, even when the body would have parsed: "never parse-then-guess" means the
    /// body's parseability is not allowed to be the tie-breaker. An <b>absent</b> content-type is
    /// the one case §4.3 does not name, and it is read the other way — nothing was declared, so
    /// step 2 answers alone. Reading "no JSON media type" as "including none at all" would turn an
    /// unlabelled healthy 200 into a Blocked with an empty log head, which is exactly the wrong
    /// guess row 13 exists to avoid, and it would cost nothing in return: an HTML page with no
    /// content-type still starts with '&lt;' and step 2 still catches it.</para>
    /// <para><b>Step 2, the body.</b> The first non-whitespace character of the first
    /// <see cref="SniffPrefixChars"/> characters being '&lt;'. This wins over a content-type that
    /// claims JSON (TP-MAP-14) — a mislabelled block page is the shape the rule is for.</para>
    /// </summary>
    internal static bool LooksLikeHtml(HttpResponseMessage? resp, string? body)
    {
        var media = resp?.Content?.Headers?.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(media) && !IsJsonMedia(media!)) return true;

        if (body == null) return false;
        int limit = Math.Min(body.Length, SniffPrefixChars);
        for (int i = 0; i < limit; i++)
        {
            if (char.IsWhiteSpace(body[i])) continue;
            return body[i] == '<';
        }
        return false;   // nothing but whitespace where a body should be: not an HTML page
    }

    /// <summary>The JSON family, including the <c>+json</c> structured suffix (RFC 6839) so a
    /// provider answering <c>application/problem+json</c> stays on the parser's road.</summary>
    private static bool IsJsonMedia(string media) =>
        media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
        media.Equals("text/json", StringComparison.OrdinalIgnoreCase) ||
        media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// §4.3 step 3's de-tagger: the page's visible text, whitespace-collapsed, at most
    /// <paramref name="maxChars"/> characters, in the page's own casing (§10.1's log line shows the
    /// server's wording as the server wrote it; only the marker MATCHING is lower-cased).
    /// <para>A hand-written single pass, not a regex — there is nothing here for a backtracking
    /// engine to blow up on, and both ends are bounded (<see cref="MaxScanChars"/> read,
    /// <paramref name="maxChars"/> produced). Tag boundaries become one space, so
    /// <c>&lt;div&gt;We're&lt;/div&gt;&lt;div&gt;sorry&lt;/div&gt;</c> still reads as two words.
    /// The CONTENT of &lt;script&gt; and &lt;style&gt; is dropped: it sits between tags rather than
    /// inside one, so a naive de-tagger keeps it, and the measured §3.1 page opens with a CSS block
    /// that would otherwise fill the head before a single marker appeared.</para>
    /// </summary>
    internal static string DeTaggedHead(string? body, int maxChars = MarkerHeadChars)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;

        var text = new StringBuilder(Math.Min(maxChars, 512));
        int limit = Math.Min(body!.Length, MaxScanChars);
        bool pendingSpace = false;
        int i = 0;

        while (i < limit && text.Length < maxChars)
        {
            char c = body[i];

            if (c == '<')
            {
                bool closing = i + 1 < limit && body[i + 1] == '/';
                string name = TagName(body, closing ? i + 2 : i + 1, limit);
                i = SkipToTagEnd(body, i, limit);
                if (!closing && (name == "script" || name == "style"))
                    i = SkipElementContent(body, i, limit, name);
                pendingSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c)) { pendingSpace = true; i++; continue; }

            if (pendingSpace && text.Length > 0) text.Append(' ');
            pendingSpace = false;
            text.Append(c);
            i++;
        }

        return text.ToString();
    }

    /// <summary>The lower-cased element name starting at <paramref name="from"/>, at most eight
    /// characters — long enough for "script" and "noscript", short enough to stay a fixed cost.</summary>
    private static string TagName(string body, int from, int limit)
    {
        var name = new StringBuilder(8);
        for (int i = from; i < limit && name.Length < 8; i++)
        {
            char c = body[i];
            if (!char.IsLetter(c)) break;
            name.Append(char.ToLowerInvariant(c));
        }
        return name.ToString();
    }

    /// <summary>Past the next '&gt;'. An unterminated tag runs to the scan limit, which ends the
    /// de-tagging — a truncated body is not a reason to look further than the bound.</summary>
    private static int SkipToTagEnd(string body, int from, int limit)
    {
        for (int i = from; i < limit; i++)
            if (body[i] == '>') return i + 1;
        return limit;
    }

    /// <summary>Past the matching <c>&lt;/name</c>, or to the scan limit if it never comes.</summary>
    private static int SkipElementContent(string body, int from, int limit, string name)
    {
        for (int i = from; i + 1 < limit; i++)
        {
            if (body[i] != '<' || body[i + 1] != '/') continue;
            if (TagName(body, i + 2, limit) != name) continue;
            return SkipToTagEnd(body, i, limit);
        }
        return limit;
    }

    /// <summary>Substring test over already-lower-cased text; the marker lists are lower-case by
    /// their own contract (<see cref="TranslationPolicy"/>), which the policy tests enforce. The
    /// arrays are read, never sorted or rewritten in place — they are shared, mutable state.</summary>
    private static bool Matches(string lowerText, string[] markers)
    {
        foreach (var m in markers)
            if (lowerText.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

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

using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace PWRUHelper.Services;

// TranslationException moved to Services/TranslationErrors.cs, where it carries a Kind.

/// <summary>Anything that can translate text. Kept as an interface so the app depends on
/// the capability, not on Google specifically — a different backend (or a test double) can
/// be dropped in without touching the UI.</summary>
public interface ITranslator
{
    Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default);
    Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source, string target,
        CancellationToken ct = default);
}

/// <summary>
/// Translates text using Google's free (unofficial) translate endpoint — the same
/// one translate.google.com uses. No API key, no cost. All work happens on Google's
/// servers, so this uses no local CPU/GPU.
/// </summary>
public class TranslationService : ITranslator
{
    /// <summary>How this endpoint names itself in the diagnostic log. Deliberately `google-gtx` and
    /// not `google`: it is the id this provider KEEPS once E2.S2 introduces `ProviderIds` and E3
    /// adds the other Google endpoints, so a field report from today still reads correctly after
    /// the rename. That registry is where this constant moves.</summary>
    private const string ProviderId = "google-gtx";

    private static readonly HttpClient Http = CreateClient();

    private readonly HttpClient _http;

    public TranslationService() : this(null) { }

    /// <summary>Test seam: a handler builds a private client — configured exactly like the shared
    /// one, so a test sees the same timeout and the same User-Agent — and the retry policy and the
    /// response parsing become reachable offline; the app passes nothing and keeps the shared
    /// static client. Nothing disposes the private client: production never takes this path, and a
    /// test handler owns no sockets.</summary>
    internal TranslationService(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? Http : CreateClient(handler);
    }

    /// <summary>The production handler: the client built on it lives for the whole process, and
    /// without a pooled-connection lifetime it can sit on a connection (or a DNS answer) that has
    /// gone stale and never replace it. `internal` so the lifetime can be pinned by a test without
    /// reflecting into HttpClient's private fields.</summary>
    internal static SocketsHttpHandler CreatePooledHandler() =>
        new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    // One factory for both paths, so a test client differs from the production one by its handler
    // and nothing else.
    private static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var c = new HttpClient(handler ?? CreatePooledHandler())
        {
            Timeout = TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds),
        };
        // A browser-like UA avoids the endpoint occasionally rejecting the request.
        c.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        return c;
    }

    /// <summary>
    /// Translate a single piece of text. Language codes are ISO ("en", "ru").
    /// Use "auto" for source to auto-detect. Long text is split into chunks so it
    /// never overflows the GET query.
    /// </summary>
    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";

        if (Encoding.UTF8.GetByteCount(text) <= TranslationPolicy.MaxQueryBytes)
            return await RequestAsync(text, source, target, ct);

        // Too long for one request: translate sentence-sized chunks and stitch back.
        var sb = new StringBuilder();
        foreach (var chunk in ChunkText(text, TranslationPolicy.MaxQueryBytes))
            sb.Append(await RequestAsync(chunk, source, target, ct));
        return sb.ToString();
    }

    /// <summary>
    /// Translate several lines. Tries a single batched request (lines joined by newlines)
    /// and falls back to one request per line if the batch fails or the line count doesn't
    /// line up. Returns a list the same length as <paramref name="lines"/>; a line that
    /// can't be translated comes back as "(translation failed: …)".
    /// </summary>
    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        if (lines.Count == 0) return new List<string>();
        if (lines.Count == 1)
            return new List<string> { await SafeOne(lines[0]) };

        var joined = string.Join("\n", lines);
        if (Encoding.UTF8.GetByteCount(joined) <= TranslationPolicy.MaxQueryBytes)
        {
            try
            {
                var full = await RequestAsync(joined, source, target, ct);
                var parts = full.Split('\n');
                if (parts.Length == lines.Count)
                    return parts.Select(p => p.Trim()).ToList();
                // else: segmentation didn't line up — fall through to per-line.
            }
            catch (TranslationException) { throw; }  // rate-limit etc. — let the caller show it
            // A genuine Stop must not be spent on a per-line retry of a batch the user abandoned.
            // The bare catch below is an OCE catch too, and I3 asks every one of them to say so.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* fall through to per-line */ }
        }

        // Per-line fallback. Translate as many as possible and KEEP the successes even if a
        // later line gets rate-limited — otherwise translating line 30 of 40 and hitting a
        // 429 would throw away the 29 good translations we already had.
        var result = new List<string>(lines.Count);
        bool rateLimited = false;
        foreach (var l in lines)
        {
            if (rateLimited) { result.Add("(skipped — rate-limited, try again shortly)"); continue; }
            try { result.Add(await TranslateAsync(l, source, target, ct)); }
            // A real cancel must propagate — and ONLY a real one. Unfiltered, this catch rethrew an
            // HttpClient timeout (an OCE whose token is NOT cancelled) as if the user had pressed
            // Stop, which threw away every line already translated above it: exactly what the
            // comment on this loop exists to prevent. RequestAsync now hands timeouts over as a
            // Timeout-kind TranslationException, so they latch below like any other failure (I3).
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TranslationException) { rateLimited = true; result.Add("(rate-limited — try again shortly)"); }
            catch (Exception ex) { result.Add($"(translation failed: {ex.Message})"); }
        }
        return result;

        async Task<string> SafeOne(string line)
        {
            try { return await TranslateAsync(line, source, target, ct); }
            catch (TranslationException) { throw; }
            // The one-line path is a third OCE catch, and I3 asks every one of them to say so: the
            // generic catch below is what a genuine Stop lands in, and it turned the cancel into a
            // translation-failed line instead of propagating. A timeout cannot reach here any more
            // — RequestAsync hands those over as a Timeout-kind TranslationException.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return $"(translation failed: {ex.Message})"; }
        }
    }

    /// <summary>One HTTP call with a couple of retries on transient throttling/errors.</summary>
    private async Task<string> RequestAsync(string text, string source, string target,
        CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx" +
                  $"&sl={source}&tl={target}&dt=t&q={HttpUtility.UrlEncode(text)}";

        // E1.S5 — the identity every §10.1 line of this logical call shares, built once. The address
        // travels as a Uri because RequestLog renders host+path and cannot render a query, and the
        // text is handed over only to be MEASURED: `Call` keeps its byte and line counts and not the
        // text itself, so the sentence a player typed has no route into a report they paste to
        // Discord (I11). `cid` is shared by all three attempts, so one call reads as one event.
        var endpoint = new Uri(url);
        var call = RequestLog.ForRequest(ProviderId, endpoint, source, target, text,
            TranslationPolicy.MaxAttemptsToday);

        string? json = null;
        // What the parse failure BELOW the loop needs about the attempt that produced `json`: by
        // then the loop's locals are gone, and re-deriving them would be a second source of truth.
        int okStatus = 0, okAttempt = 0, okBurst = 0;
        long okStarted = 0;

        for (int attempt = 0; attempt < TranslationPolicy.MaxAttemptsToday; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Every attempt is a request ISSUED, and burst60 counts them all — a failure log that
            // only counted failures would describe the incident and not the behaviour that caused
            // it (analyse… §2.3). This increment is the whole price of a successful request: no
            // header set, no body read, no line (§10.1, and the 1 MB cap at Logging.cs:69,95-105).
            int burst = RequestLog.Burst.Note(DateTimeOffset.UtcNow);
            long started = Stopwatch.GetTimestamp();
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    json = await resp.Content.ReadAsStringAsync(ct);
                    (okStatus, okAttempt, okBurst, okStarted) = ((int)resp.StatusCode, attempt, burst, started);

                    // §4.3 step 4 — the body is classified BEFORE it is parsed, never after. This
                    // is the story: Google answers a throttled network with an HTML "Sorry..." page
                    // (benchmark-fournisseurs.md §3.1), and served with a 200 that page used to
                    // sail into JsonDocument.Parse below and come back as "an unexpected response"
                    // — the right sentence for the wrong reason, and a Kind no gate could act on.
                    // The body is the one already read on this line (the stream is consumed once).
                    // Same contract as the status branch below: the token can be cancelled between
                    // the response arriving and this line, and row 1 would then answer with the
                    // cancel Kind — the one value a TranslationException may never carry.
                    ct.ThrowIfCancellationRequested();
                    if (ProviderErrorMapper.LooksLikeHtml(resp, json))
                    {
                        // E1.S5 — an abuse page served with a 200 is the measured P2 shape
                        // (benchmark… §3.1), so this attempt "ended non-success or exceptional" and
                        // gets its line. It is the one case where a body reaches the log, and only
                        // its de-tagged first 120 characters do; RequestLog decides that, not here.
                        RequestLog.Emit(call, attempt + 1, Stopwatch.GetElapsedTime(started), burst,
                            resp, json);

                        // Not retried, exactly as an unparseable 200 was not retried before: the
                        // retry DECISION is unchanged (E2.S5 owns it). The sentence is the one the
                        // parser's catch carries, and since E1.S6 it is the log's account only:
                        // what the player reads is the Kind's sentence from UserMessages.
                        throw new TranslationException(
                            ProviderErrorMapper.Classify(resp, json, transport: null,
                                keyWasSent: false, ct),
                            "The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.",
                            ProviderErrorMapper.RetryAfter(resp, DateTimeOffset.UtcNow));
                    }
                    break;
                }

                int code = (int)resp.StatusCode;
                // The single classification point (§4.2). keyWasSent is FALSE here — this is the
                // keyless Google endpoint — which is precisely why its 403 is a Blocked (the
                // endpoint refusing this network) and not a rejected key. No body is handed to the
                // CLASSIFIER on this branch, and E1.S4 deliberately left it that way: the sniff it
                // added sits on the success path, where the body is already in hand. Row 11
                // therefore sees only this response's Content-Type here — an error status that
                // declares text/html reads as Blocked with an empty head, and the measured 429
                // keeps its RateLimited from row 4 by the shorter road. E1.S5 does now READ the
                // body a few lines below, for the log's `body=` field and for nothing else; it is
                // deliberately not fed back into `kind`, so this classification is byte-for-byte
                // what E1.S4 shipped. E2.S5 owns making the two one read.
                // The mapper's caller contract, honoured rather than only quoted: row 1 returns the
                // cancel Kind whenever the token is cancelled, and every line below hands `kind`
                // straight to a TranslationException — the one construction TranslationErrors.cs
                // forbids outright. Checking here (the token can be cancelled between the response
                // arriving and this line) means row 1 cannot fire, so the only way a cancel leaves
                // this method is as an OperationCanceledException. The source scan cannot see a
                // phantom cancel; this can.
                ct.ThrowIfCancellationRequested();

                var kind = ProviderErrorMapper.Classify(resp, bodyHead: null, transport: null,
                    keyWasSent: false, ct);
                var retryAt = ProviderErrorMapper.RetryAfter(resp, DateTimeOffset.UtcNow);

                // E1.S5 — one line per non-success attempt, emitted BEFORE the throw decisions
                // below (§5.2), so a 429 retried three times leaves three lines under one cid with
                // their real spacing. The body is read HERE and nowhere else: E1.S4 deliberately
                // left this branch body-less, and §10.1's body= is the field that tells a real 429
                // apart from a captcha page. It is NOT handed to Classify — the Kind above stays
                // exactly what E1.S4 shipped, and E2.S5's HttpProviderCore is where the two become
                // one read. Reading cannot fail the request: SafeBodyAsync swallows its own errors.
                RequestLog.Emit(call, attempt + 1, Stopwatch.GetElapsedTime(started), burst,
                    resp, await RequestLog.SafeBodyAsync(resp, ct));

                // The retry DECISION is unchanged and stays here: 429 and 5xx are retried, nothing
                // else. Which Kinds are worth retrying is E2.S5's question, not this story's.
                bool transient = code == 429 || code >= 500;
                if (!transient)
                    // A real, non-retryable error — report it as-is instead of retrying and then
                    // mislabeling it as "no Internet". The HTTP code stays in the message because
                    // the message is now the log's, not the player's — for MOST of the statuses
                    // that reach here. Since E1.S6 a 403 renders as Blocked's sentence, a 401 as
                    // AuthFailed's and a 456 as QuotaExhausted's; every OTHER non-transient status
                    // (400 and 404 in practice, but the branch is only guarded by
                    // `!(429 || >= 500)`, so 402/405/409/410/422/451 too) classifies Unknown and
                    // still shows this text — HTTP code included — to the player. That is what
                    // §4.4 wants Unknown to do: it is the one Kind nobody has a rule for yet, and
                    // hiding what the provider said is what would make it unreportable.
                    throw new TranslationException(kind,
                        $"Translation service error (HTTP {code}). Please try again later.", retryAt);
                if (code == 429 && attempt == 2)
                    throw new TranslationException(kind,
                        "Google is limiting translations right now — wait a minute and try again.", retryAt);
                if (attempt == 2)
                    throw new TranslationException(kind,
                        $"Translation service is unavailable (HTTP {code}). Try again shortly.", retryAt);
                // transient and attempts left → fall through to the delay + retry below.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // E1.S5 — an attempt that ended exceptionally, so it gets its line, and it gets it
                // FIRST: a transport failure that really happened is evidence whatever the caller
                // does next. The status field is the exception TYPE, which is what discriminates
                // "no Internet" from "the request timed out" without asking the user (§5.2); the
                // exception's MESSAGE is never logged, because on this path it can quote the URL.
                RequestLog.Emit(call, attempt + 1, Stopwatch.GetElapsedTime(started), burst, ex);

                // A network blip keeps its two retries (unchanged). What is new is the exit: on the
                // last attempt the HttpRequestException used to escape RequestAsync raw — past the
                // TranslationException contract the caller is written against, and leaving the
                // could-not-reach sentence below the loop unreachable. A timeout is not retried
                // today either, and it now leaves as a Timeout instead of a bare OCE.
                // Same contract as the status branch: the filter above ran one statement ago, and
                // a token cancelled since then would make Classify answer with the cancel Kind.
                ct.ThrowIfCancellationRequested();
                if (ex is not HttpRequestException || attempt == 2)
                    throw new TranslationException(
                        ProviderErrorMapper.Classify(resp: null, bodyHead: null, transport: ex,
                            keyWasSent: false, ct),
                        ex is HttpRequestException
                            ? "Couldn't reach the translation service. Check your Internet connection."
                            // The log's account of a timeout. It used to be the sentence the user
                            // read, word for word; since E1.S6 the Timeout Kind carries its own
                            // copy-deck sentence and this literal never reaches a surface.
                            : "the request timed out");
            }

            await Task.Delay(300 * (attempt + 1), ct);
        }

        // Unreachable by construction: the third attempt always sets json or throws — every status
        // branch and the transport catch above end in a throw once `attempt == 2`. The `Network`
        // throw that used to stand here was dead code for the same reason (recorded by E1.S2's
        // review); its sentence now lives on the last-attempt transport failure, where it is
        // actually reached. The compiler cannot follow that, hence the single `!` below.

        // Response shape: [[["translated","original",...], ...], ...]
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var sb = new StringBuilder();
            var segments = doc.RootElement[0];
            foreach (var seg in segments.EnumerateArray())
            {
                var piece = seg[0].GetString();
                if (piece != null) sb.Append(piece);
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            // BadResponse is what reaching the parser means (§4.2 row 12). It is no longer how a
            // block page is discovered: the §4.3 sniff above classifies an HTML body before it can
            // get here, so what lands in this catch is a body that claimed to be JSON, did not
            // start with '<', and still is not the provider's shape — a genuinely unreadable
            // answer, which is exactly what this Kind and this sentence are for.

            // E1.S5 — the last of §5.2's insertion points: an attempt whose body only became a
            // failure after the response object was gone. It therefore carries the status that was
            // kept and the payload size, and `-` for everything a header would have said — §10.1
            // forbids paying for the header set on the success path, which is what this was. The
            // body is handed over and REFUSED by construction: it passed the §4.3 sniff, so it is
            // the provider's own JSON, and the provider's JSON is where the user's text lives.
            RequestLog.EmitStatus(call, okAttempt + 1, Stopwatch.GetElapsedTime(okStarted), okBurst,
                okStatus, json);

            throw new TranslationException(TranslationErrorKind.BadResponse,
                "The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.");
        }
    }

    /// <summary>Split long text into &lt;= maxBytes chunks on sentence boundaries.</summary>
    internal static IEnumerable<string> ChunkText(string text, int maxBytes)
    {
        var pieces = Regex.Split(text, @"(?<=[\.\!\?…\n])");
        var current = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (current.Length > 0 &&
                Encoding.UTF8.GetByteCount(current.ToString() + piece) > maxBytes)
            {
                yield return current.ToString();
                current.Clear();
            }
            // A single piece longer than the limit: hard-split it.
            if (Encoding.UTF8.GetByteCount(piece) > maxBytes)
            {
                foreach (var hard in HardSplit(piece, maxBytes)) yield return hard;
                continue;
            }
            current.Append(piece);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static IEnumerable<string> HardSplit(string s, int maxBytes)
    {
        // Split by characters so each chunk stays under the byte limit.
        var current = new StringBuilder();
        foreach (var ch in s)
        {
            if (Encoding.UTF8.GetByteCount(current.ToString() + ch) > maxBytes && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

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

        string? json = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    json = await resp.Content.ReadAsStringAsync(ct);

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
                        // Not retried, exactly as an unparseable 200 was not retried before: the
                        // retry DECISION is unchanged (E2.S5 owns it). The sentence is the one the
                        // parser's catch renders today, so nothing the user reads changes here —
                        // only the Kind, which is what E1.S6 will reword against.
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
                // endpoint refusing this network) and not a rejected key. STILL no body is read on
                // this branch, and E1.S4 deliberately left it that way: the sniff it added sits on
                // the success path, where the body is already in hand. Row 11 therefore sees only
                // this response's Content-Type here — an error status that declares text/html reads
                // as Blocked with an empty head, and the measured 429 keeps its RateLimited from
                // row 4 by the shorter road. Reading an error body belongs to the story that needs
                // it: E1.S5 wants it for the log's `body=` field, E2.S5 owns the shared shape.
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

                // The retry DECISION is unchanged and stays here: 429 and 5xx are retried, nothing
                // else. Which Kinds are worth retrying is E2.S5's question, not this story's.
                bool transient = code == 429 || code >= 500;
                if (!transient)
                    // A real, non-retryable error — report it as-is instead of retrying and then
                    // mislabeling it as "no Internet". The sentence is unchanged; what changed is
                    // that a 403 no longer arrives with the same Kind as a 400 (E1.S6 rewords it).
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
                            // The exact sentence Friendly() renders today for a raw
                            // TaskCanceledException (MainWindow.xaml.cs), so nothing the user reads
                            // changes here. E1.S6 owns the wording.
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

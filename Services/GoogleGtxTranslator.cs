using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;

namespace PWRUHelper.Services;

// TranslationException moved to Services/TranslationErrors.cs, where it carries a Kind.

/// <summary>Anything that can translate text. Kept as an interface so the app depends on
/// the capability, not on Google specifically — a different backend (or a test double) can
/// be dropped in without touching the UI.
///
/// <para><b>It stays in this file on purpose (E3.S6, invariant I1).</b> E3.S6 renamed the file
/// and the provider class around it, and the obvious tidy-up — "give the interface its own file"
/// — is exactly what must not happen: I1 says the interface is untouched, E1.S1's identity check
/// is on this declaration, and moving it would turn a rename nobody has to read into a diff
/// everybody does. Move it only in a story that says so.</para></summary>
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
public class GoogleGtxTranslator : ITranslator
{
    /// <summary>How this endpoint names itself in the diagnostic log — and, from E2.S5, which gate
    /// it consults. Deliberately `google-gtx` and not `google`: it is the id this provider KEEPS
    /// once E3 adds the other Google endpoints, so a field report from today still reads correctly
    /// after the rename. It is <b>not spelled here</b>: E2.S2 moved the spelling to
    /// <see cref="ProviderIds"/>, because a second spelling of an id is not a typo that fails
    /// loudly — it is a silently duplicated gate.</summary>
    private const string ProviderId = ProviderIds.GoogleGtx;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The browser-like User-Agent this endpoint has always sent: it avoids the endpoint
    /// occasionally rejecting the request. Frozen and NOT rotated (§7.0), and provider-specific —
    /// <c>DeepLTranslator</c> sends none, and the shared client factory must not give it one.</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private readonly HttpClient _http;

    /// <summary>§7.0's shared pipeline: gate admission, the ≤ 2 attempts with full jitter, one body
    /// read, classification, <c>Retry-After</c>, the §10.1 line and the outcome report. What is left
    /// in this file is what is Google's: the URL, the batch join/split and the parser — the chunker
    /// left with E3.S6, to <see cref="TextChunker"/>, because a second query-string provider needs
    /// it too.</summary>
    private readonly HttpProviderCore _core;

    public GoogleGtxTranslator() : this((HttpMessageHandler?)null) { }

    /// <summary>Test seam: a handler builds a private client — configured exactly like the shared
    /// one, so a test sees the same timeout and the same User-Agent — and the retry policy and the
    /// response parsing become reachable offline; the app passes nothing and keeps the shared
    /// static client. Nothing disposes the private client: production never takes this path, and a
    /// test handler owns no sockets. <paramref name="gate"/> is the same idea for E2's registry: a
    /// case that wants to watch the admission hands in its own gate instead of the shared one.</summary>
    internal GoogleGtxTranslator(HttpMessageHandler? handler = null, ProviderGate? gate = null)
    {
        _http = handler == null ? Http : CreateClient(handler);
        _core = new HttpProviderCore(Options, _http, gate);
    }

    /// <summary>What this provider tells the core about itself (§7.0). The sentences are the LOG's
    /// account of what the endpoint said — what the player reads is the Kind's sentence from
    /// <see cref="UserMessages"/> (E1.S6) — and folding them into that table is E7.S1's.</summary>
    private static readonly ProviderOptions Options = new(
        ProviderId,
        KeyWasSent: false,          // the keyless endpoint: its 403 is a block, never a rejected key
        StatusMessage: code => code switch
        {
            // A 2xx that reached here is the §4.3 abuse page served with a success status.
            >= 200 and < 300 => "The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.",
            429 => "Google is limiting translations right now — wait a minute and try again.",
            >= 500 => $"Translation service is unavailable (HTTP {code}). Try again shortly.",
            // Every other non-transient status, HTTP code included: §4.4 wants Unknown to say what
            // the provider said, because hiding it is what would make it unreportable.
            _ => $"Translation service error (HTTP {code}). Please try again later.",
        },
        TransportMessage: kind => kind == TranslationErrorKind.Timeout
            ? "the request timed out"
            : "Couldn't reach the translation service. Check your Internet connection.",
        PausedMessage: "The translation service is paused after a recent refusal.",
        UserAgent: UserAgent);

    // One factory for both paths, so a test client differs from the production one by its handler
    // and nothing else. The factory itself is the core's since E2.S5: it was byte-identical here
    // and in DeepLTranslator apart from this provider's User-Agent.
    private static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        HttpProviderCore.CreateClient(handler, UserAgent);

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
            return await RequestAsync(text, source, target, ct).ConfigureAwait(false);

        // Too long for one request: translate sentence-sized chunks and stitch back.
        var sb = new StringBuilder();
        foreach (var chunk in TextChunker.ChunkText(text, TranslationPolicy.MaxQueryBytes))
            sb.Append(await RequestAsync(chunk, source, target, ct).ConfigureAwait(false));
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
            return new List<string> { await SafeOne(lines[0]).ConfigureAwait(false) };

        var joined = string.Join("\n", lines);
        if (Encoding.UTF8.GetByteCount(joined) <= TranslationPolicy.MaxQueryBytes)
        {
            try
            {
                var full = await RequestAsync(joined, source, target, ct).ConfigureAwait(false);
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
            try { result.Add(await TranslateAsync(l, source, target, ct).ConfigureAwait(false)); }
            // A real cancel must propagate — and ONLY a real one. Unfiltered, this catch rethrew an
            // HttpClient timeout (an OCE whose token is NOT cancelled) as if the user had pressed
            // Stop, which threw away every line already translated above it: exactly what the
            // comment on this loop exists to prevent. RequestAsync now hands timeouts over as a
            // Timeout-kind TranslationException, so they are caught below as the failure of the one
            // line they happened on — since E3.S6 without latching the rest of the loop (I3).
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // AC 4 (E3.S6): ONLY a refusal latches. The latch exists because asking a provider that
            // just said "stop" for thirteen more lines is how a soft block becomes a hard one — a
            // reason that holds for RateLimited and Blocked and for nothing else. Unfiltered, it
            // also fired on a BadResponse or a Timeout on line 3 of 14 and turned lines 4-14 into
            // "(skipped — rate-limited…)", a sentence that was simply false: nobody was rate-
            // limiting anything, and eleven translatable lines were thrown away to say so. The
            // latch itself is kept (I16), and a latched line still reads exactly as it did.
            catch (TranslationException tex) when (tex.Kind is TranslationErrorKind.RateLimited
                                                            or TranslationErrorKind.Blocked)
            { rateLimited = true; result.Add("(rate-limited — try again shortly)"); }
            // Every other typed failure is this line's problem and no other line's. Written out
            // rather than left to fall into the generic catch below — which would render it
            // identically today — so that the intent survives an edit to that catch: this arm
            // exists to NOT latch, and the string it borrows is E7.S1's to reword (ruling E2-d).
            catch (TranslationException tex) { result.Add($"(translation failed: {tex.Message})"); }
            catch (Exception ex) { result.Add($"(translation failed: {ex.Message})"); }
        }
        return result;

        async Task<string> SafeOne(string line)
        {
            try { return await TranslateAsync(line, source, target, ct).ConfigureAwait(false); }
            catch (TranslationException) { throw; }
            // The one-line path is a third OCE catch, and I3 asks every one of them to say so: the
            // generic catch below is what a genuine Stop lands in, and it turned the cancel into a
            // translation-failed line instead of propagating. A timeout cannot reach here any more
            // — RequestAsync hands those over as a Timeout-kind TranslationException.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return $"(translation failed: {ex.Message})"; }
        }
    }

    /// <summary>One logical call: this method owns the URL and the parser, and hands everything
    /// else to <see cref="HttpProviderCore"/> (§7.0). <c>Interactive</c> is passed for now by
    /// ruling — it is never worse than today's behaviour, and E3.S7 / E5.S4 are where the LIVE
    /// loop starts saying <c>Background</c> and E2.S3's reserve becomes effective.</summary>
    private Task<string> RequestAsync(string text, string source, string target, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx" +
                  $"&sl={source}&tl={target}&dt=t&q={HttpUtility.UrlEncode(text)}";

        // The address travels as a Uri because RequestLog renders host + path and cannot render a
        // query, and the text is handed over only to be MEASURED (I11).
        return _core.SendAsync(new Uri(url), () => new HttpRequestMessage(HttpMethod.Get, url),
            Parse, source, target, text, RequestPriority.Interactive, ct);
    }

    /// <summary>Pull the translated segments out of a gtx response:
    /// <c>[[["translated","original",…], …], …]</c>. Called by the core INSIDE the admission, so a
    /// body that is not this shape reaches the gate as the <c>BadResponse</c> it is (§5.3).</summary>
    internal static string Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            var segments = doc.RootElement[0];
            foreach (var seg in segments.EnumerateArray())
            {
                var piece = seg[0].GetString();
                if (piece != null) sb.Append(piece);
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException
                                         or InvalidOperationException)
        {
            // §4.2 row 12: a success whose body is not the provider's shape. It is no longer how a
            // block page is discovered — the core's §4.3 sniff classifies an HTML body before this
            // method can see it — so what lands here is a body that claimed to be JSON, did not
            // start with '<', and still is not gtx's shape.
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.");
        }
    }
}

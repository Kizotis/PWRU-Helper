using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>
/// Optional Azure AI Translator backend (§7.5), used only when the user pastes an Azure key and its
/// region in Settings. It is the second keyed slot beside <see cref="DeepLTranslator"/> and the
/// fastest tier measured from the owner's network (93 ms RTT, <c>benchmark…</c> §3.3) — but the
/// reason it is worth having is the batch contract, not the latency: <b>Azure answers one element
/// per input, in order</b>, so this provider has none of the <c>\n</c>-join fragility of §7.1 and
/// none of the per-line fallback that goes with it. A count that does not match is therefore not a
/// shape to recover from, it is a <see cref="TranslationErrorKind.BadResponse"/> (I5).
///
/// <para><b>Raw <see cref="HttpClient"/>, not <c>Azure.AI.Translation.Text</c></b> (NFR6 / A5): the
/// SDK brings its own timeouts and retries, which would fight <see cref="HttpProviderCore"/>, plus
/// ~3.0–3.2 MB of assemblies of which ~1.3 MB is an MSAL stack this app never calls — for an
/// endpoint whose entire contract is "POST a JSON array with two headers". The publish is
/// single-file and uncompressed on purpose, so an added assembly lands in the exe roughly 1:1.</para>
///
/// <para>Everything that is not the URL, the two headers, the body and the parser belongs to
/// <see cref="HttpProviderCore"/> (§7.0): the gate, the ≤ 2 attempts with full jitter, the
/// <c>Retry-After</c> floor, the §10.1 log line, the key scrub and the one filtered
/// <c>OperationCanceledException</c> catch on the request path (I3 — there is not one in this
/// file, and there must never be).</para>
/// </summary>
public class AzureTranslator : ITranslator
{
    /// <summary>How this provider names itself in the diagnostic log and which gate it consults.
    /// Spelled once, in <see cref="ProviderIds"/>: a second spelling is a silently duplicated
    /// gate.</summary>
    private const string ProviderId = ProviderIds.Azure;

    /// <summary>The global endpoint. The region travels in a HEADER, never in the host, so a
    /// regional resource needs no second URL here (§7.5).</summary>
    private const string Endpoint = "https://api.cognitive.microsofttranslator.com/translate";

    private static readonly HttpClient Http = CreateClient();

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _region;
    private readonly HttpProviderCore _core;

    /// <summary>§5.4's reserve, per INSTANCE — <c>ITranslator</c> has no channel for a priority (I1)
    /// and E6.S4 needs the LIVE read chain to say <c>Background</c> while the Translator tab says
    /// <c>Interactive</c>. Same shape as <see cref="GoogleDictTranslator"/>'s.</summary>
    private readonly RequestPriority _priority;

    public AzureTranslator(string apiKey, string region) : this(apiKey, region, null) { }

    /// <summary>Test seam (IS-8/IS-10): a handler builds a private client — same factory, so the
    /// timeout and the (absent) User-Agent are the production ones — and the parser, the batch
    /// split and the status mapping become reachable offline. The app passes nothing and keeps the
    /// shared static client. <paramref name="gate"/> is the same idea for E2's registry.</summary>
    internal AzureTranslator(string apiKey, string region, HttpMessageHandler? handler = null,
        ProviderGate? gate = null, RequestPriority priority = RequestPriority.Interactive)
    {
        _key = (apiKey ?? "").Trim();
        _region = (region ?? "").Trim();
        _http = handler == null ? Http : CreateClient(handler);
        _core = new HttpProviderCore(Options(_key), _http, gate);
        _priority = priority;
    }

    /// <summary>
    /// What this provider tells the core about itself (§7.0). Two things here are load-bearing:
    /// <c>KeyWasSent: true</c>, which is what makes a 401 and a 403 the KEY's problem rather than a
    /// blocked connection (§4.2 rows 5–8, ruling E2-g) — a rejected Azure key that read as "your
    /// connection is blocked" would send the user to their router; and <c>Secret</c>, the key
    /// itself, which the core scrubs out of any body head before the §10.1 line is built.
    /// <see cref="RequestLog"/> cannot do that: it is UI-free and does not know a key's value (I11).
    /// <para><b>No User-Agent</b>: a vendor path that has never sent one must not start (see
    /// <see cref="HttpProviderCore.CreateClient"/>); the Chrome string is Google's.</para>
    /// <para>The sentences are the LOG's account of what the endpoint said. What the player reads is
    /// the Kind's sentence from <see cref="UserMessages"/> (E1.S6), and the vendor's own
    /// <c>message</c> is never one of them — §7.5: the status is logged, the message is never shown
    /// raw.</para>
    /// </summary>
    private static ProviderOptions Options(string key) => new(
        ProviderId,
        KeyWasSent: true,
        StatusMessage: code => code switch
        {
            // "About", not "Settings": this app has never had a Settings tab (see DeepL's note).
            401 => "Azure rejected the API key — check it in About.",
            // 403 is two rows of §4.2 at once: with a quota envelope the Kind is QuotaExhausted,
            // without one it is AuthFailed. The sentence has to serve both, because StatusMessage
            // is handed the status and not the Kind — and the region is named because a key whose
            // region is wrong is a 403 the user cannot otherwise explain.
            403 => "Azure refused the key — check the key and its region in About, or the free quota may be spent.",
            429 => "Azure is rate-limiting right now — try again shortly.",
            // A 2xx that reached here is an HTML page served with a success status (§4.3).
            >= 200 and < 300 => "Azure returned an unexpected response.",
            _ => $"Azure service error (HTTP {code}).",
        },
        TransportMessage: kind => kind == TranslationErrorKind.Timeout
            ? "Azure timed out — check your connection or try again."
            : "Couldn't reach Azure. Check your Internet connection.",
        PausedMessage: "Azure is paused after a recent refusal.",
        UserAgent: null,
        Secret: key);

    // One factory for both paths, so a test client differs from the production one by its handler
    // and nothing else (IS-8).
    private static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        HttpProviderCore.CreateClient(handler, userAgent: null);

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        var outp = await RequestAsync(new[] { text }, source, target, ct).ConfigureAwait(false);

        // One input, one translation — the same 1:1 rule TranslateLinesAsync throws on, applied to
        // the batch of one. Answering "" for a missing element would be padding with a blank (I5),
        // and a blank that reached the caller as a success would be cached as one.
        if (outp.Count != 1)
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "Azure returned an unexpected response.");
        return outp[0];
    }

    /// <summary>
    /// The native batch: one POST per <see cref="Batches"/> group, answers concatenated in input
    /// order. There is no <c>TextChunker</c> and no per-line fallback here — both exist for the
    /// Google endpoints' URL budget and their <c>\n</c>-join, neither of which this contract has.
    /// </summary>
    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        if (lines.Count == 0) return new List<string>();

        var outp = new List<string>(lines.Count);
        foreach (var batch in Batches(lines))
        {
            var part = await RequestAsync(batch, source, target, ct).ConfigureAwait(false);

            // Azure returns exactly one translation per input, in order. A count mismatch means the
            // response is malformed — throw so the next tier of the chain takes over. (Padding the
            // missing slots with the untranslated source lines is what DeepL used to do: it
            // bypassed the fallback AND cached raw Russian source as if it were a translation.)
            if (part.Count != batch.Count)
                throw new TranslationException(TranslationErrorKind.BadResponse,
                    "Azure returned an unexpected response.");

            outp.AddRange(part);
        }
        return outp;
    }

    /// <summary>
    /// The documented request limits of §7.5, applied: at most
    /// <see cref="TranslationPolicy.AzureMaxTextsPerRequest"/> elements and
    /// <see cref="TranslationPolicy.AzureMaxCharsPerRequest"/> characters per POST, split in input
    /// order so concatenating the answers rebuilds the caller's list. A single text longer than the
    /// character cap travels alone rather than being cut: this app never sends one (a chat line is
    /// three orders of magnitude short of it), and silently cutting a line is the failure mode I5
    /// is about, seen from the other side.
    /// </summary>
    internal static List<List<string>> Batches(IReadOnlyList<string> lines)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        int chars = 0;

        foreach (var line in lines)
        {
            int len = (line ?? "").Length;
            if (current.Count > 0 &&
                (current.Count >= TranslationPolicy.AzureMaxTextsPerRequest ||
                 chars + len > TranslationPolicy.AzureMaxCharsPerRequest))
            {
                batches.Add(current);
                current = new List<string>();
                chars = 0;
            }
            current.Add(line ?? "");
            chars += len;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>One logical call: this method owns the URL, the two headers, the body and the
    /// parser, and hands everything else to <see cref="HttpProviderCore"/> (§7.0).</summary>
    private Task<List<string>> RequestAsync(IReadOnlyList<string> texts, string source,
        string target, CancellationToken ct)
    {
        // Both halves of the credential, checked before anything is sent. A key without a region is
        // a guaranteed 401 (§12), and a real 401 costs a gate strike and blocks the provider for a
        // mistake the UI can prevent (E6.S3 prevents it at the Save button) — so it is refused here,
        // where it spends nothing.
        if (string.IsNullOrEmpty(_key))
            throw new TranslationException(TranslationErrorKind.AuthFailed, "No Azure API key set.");
        if (string.IsNullOrEmpty(_region))
            throw new TranslationException(TranslationErrorKind.AuthFailed, "No Azure region set.");

        var uri = BuildUri(source, target);
        var body = BuildBody(texts);

        // A fresh message per attempt: an HttpRequestMessage may not be sent twice, and this
        // provider has a second attempt to make.
        HttpRequestMessage Build()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                // StringContent with an explicit encoding renders exactly the header §7.5 asks
                // for — `application/json; charset=utf-8` — so nothing hand-adds a Content-Type.
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // The key travels in a HEADER and never in the query (I11: §10.1 renders host + path,
            // but a key in a URL also reaches proxies, referrers and the vendor's own access logs).
            // TryAddWithoutValidation for the same reason DeepL uses it: these are opaque values and
            // the framework's parser has no business rejecting one.
            req.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _key);
            req.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", _region);
            return req;
        }

        // The text is handed over only to be MEASURED (I11): the joined lines are what the §10.1
        // line's `bytes=` and `lines=` are counted from, and nothing keeps the text itself.
        return _core.SendAsync(uri, Build, Parse, source, target,
            string.Join("\n", texts), _priority, ct);
    }

    /// <summary>`?api-version=3.0&amp;from={src}&amp;to={tgt}`, with <c>from</c> omitted for
    /// auto-detect (§7.5). No key, ever.</summary>
    internal static Uri BuildUri(string source, string target)
    {
        var sb = new StringBuilder(Endpoint).Append("?api-version=3.0");
        var src = ToAzureSource(source);
        if (src != null) sb.Append("&from=").Append(Uri.EscapeDataString(src));
        sb.Append("&to=").Append(Uri.EscapeDataString(ToAzureTarget(target)));
        return new Uri(sb.ToString());
    }

    /// <summary>The serializer's default encoder escapes every non-ASCII character, which would
    /// send a Russian chat line as <c>пр…</c> — the same text at three times the bytes,
    /// on a path whose whole point is to be cheap. The relaxed encoder writes it as UTF-8, which is
    /// what the <c>charset=utf-8</c> content type promises. "Unsafe" names the HTML-escaping it
    /// drops (<c>&lt;</c>, <c>&amp;</c>, <c>'</c>): this body is JSON to an API and is never
    /// rendered as markup anywhere, so there is no injection surface to protect.</summary>
    private static readonly JsonSerializerOptions BodyOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>`[{"Text":"line 1"},{"Text":"line 2"}]`. <b>`Text` with a capital T</b>: Azure's
    /// request schema is case-sensitive on the property name.</summary>
    internal static string BuildBody(IReadOnlyList<string> texts)
    {
        var payload = new List<Dictionary<string, string>>(texts.Count);
        foreach (var t in texts) payload.Add(new Dictionary<string, string> { ["Text"] = t ?? "" });
        return JsonSerializer.Serialize(payload, BodyOptions);
    }

    /// <summary>
    /// Pull the ordered translations out of an Azure response: `[{"translations":[{"text":…}]}, …]`,
    /// strictly — <c>[i].translations[0].text</c> and nothing inferred. <c>detectedLanguage</c> is
    /// read PAST and not read (I7): which source a message was sent with is the caller's decision,
    /// per message, and a provider that reported back a different one would be answering a question
    /// nobody asked.
    /// </summary>
    internal static List<string> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                throw new JsonException("the root of an Azure response is an array");

            var list = new List<string>(root.GetArrayLength());
            foreach (var el in root.EnumerateArray())
            {
                var translations = el.GetProperty("translations");
                if (translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0)
                    throw new JsonException("an element carries no translations");

                var text = translations[0].GetProperty("text");
                if (text.ValueKind != JsonValueKind.String)
                    throw new JsonException("a translation carries no text");

                list.Add(text.GetString() ?? "");
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // §4.2 row 12: a success whose body is not the provider's shape. The parser is what
            // DETECTS that — the mapper only names it — and this method has no HttpResponseMessage
            // to hand it, so the Kind is stated here (a 200 with an unparseable body ⇒ BadResponse).
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "Azure returned an unexpected response.");
        }
    }

    // Azure takes plain ISO codes: `to=en`, never DeepL's `EN-US`. A target is always concrete
    // (never "auto"), so an unknown/empty target defaults to English.
    internal static string ToAzureTarget(string target) => (target ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "auto" => "en",
        var t => t,
    };

    // Source may be "auto" → omit `from` and let Azure detect. The detection that comes back with
    // it is discarded (I7).
    internal static string? ToAzureSource(string source)
    {
        var s = (source ?? "").Trim().ToLowerInvariant();
        return s is "" or "auto" ? null : s;
    }
}

using System.Net.Http;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>
/// Optional DeepL backend, used only when the user pastes a DeepL API key in Settings — the app
/// otherwise stays on the free Google endpoint. Free keys (ending ":fx") hit api-free.deepl.com,
/// paid keys hit api.deepl.com. DeepL translates several <c>text</c> params in ONE request and
/// returns them in order, so the batch path has none of the join/split fragility of the Google
/// one. Problems surface as <see cref="TranslationException"/> so the Google-fallback wrapper
/// (see <see cref="ChainTranslator"/>) can take over.
/// </summary>
public class DeepLTranslator : ITranslator
{
    /// <summary>How this provider names itself in the diagnostic log and which gate it consults.
    /// Spelled once, in <see cref="ProviderIds"/>: a second spelling is a silently duplicated
    /// gate.</summary>
    private const string ProviderId = ProviderIds.DeepL;

    private static readonly HttpClient Http = CreateClient();

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _endpoint;

    /// <summary>The key check of E6.S5, on the same host the translation would use. See
    /// <see cref="TestKeyAsync"/> — it is authenticated, it is free, and it is the reason DeepL's
    /// button can honestly say "Test key" while Azure's cannot.</summary>
    private readonly string _usageEndpoint;

    /// <summary>§7.0's shared pipeline. DeepL had none of it: one send, no retry, no log line and
    /// no gate. What stays in this file is DeepL's: the host choice, the form, the auth header, the
    /// parser and the count-mismatch throw.</summary>
    private readonly HttpProviderCore _core;

    public DeepLTranslator(string apiKey) : this(apiKey, null) { }

    /// <summary>Test seam: a handler builds a private client — same timeout as the shared one — so
    /// the status mapping and the parser can be exercised offline; the app passes nothing and keeps
    /// the shared static client. Nothing disposes the private client: production never takes this
    /// path, and a test handler owns no sockets. <paramref name="gate"/> is the same idea for E2's
    /// registry: a case that wants to watch the admission hands in its own gate.</summary>
    internal DeepLTranslator(string apiKey, HttpMessageHandler? handler = null, ProviderGate? gate = null)
    {
        _key = (apiKey ?? "").Trim();
        _endpoint = FreeKey(_key)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        // Same host selection, same rule: a ":fx" key on api.deepl.com is a 403 whichever path
        // asks. One expression would be tidier and would put the host choice in two places the
        // day one of them grows a condition, so both read FreeKey and neither derives the other.
        _usageEndpoint = FreeKey(_key)
            ? "https://api-free.deepl.com/v2/usage"
            : "https://api.deepl.com/v2/usage";
        _http = handler == null ? Http : CreateClient(handler);
        _core = new HttpProviderCore(Options(_key), _http, gate);
    }

    /// <summary>
    /// What this provider tells the core about itself (§7.0). Two things here are load-bearing:
    /// <c>KeyWasSent: true</c>, which is what makes a 403 a rejected key rather than a bot block
    /// (§4.2 rows 5-8); and <c>Secret</c>, the key itself — the core scrubs it out of any body head
    /// before the §10.1 line is built, which <see cref="RequestLog"/> cannot do because it is
    /// UI-free and does not know a key's value (I11).
    /// <para><b>No User-Agent</b>: this path has never sent one, and a shared factory that added
    /// Google's Chrome string here would be a behaviour change on a paid vendor path.</para>
    /// </summary>
    private static ProviderOptions Options(string key) => new(
        ProviderId,
        KeyWasSent: true,
        StatusMessage: code => code switch
        {
            // "About", not "Settings": this app has never had a Settings tab. The literal is the
            // LOG's account rather than the player's since E1.S6, but the log is pasted to Discord
            // by design (I11's premise) — a human still reads it and still cannot find the tab.
            401 or 403 => "DeepL rejected the API key — check it in About.",
            456 => "DeepL free quota is used up for this month.",
            429 => "DeepL is rate-limiting right now — try again shortly.",
            // A 2xx that reached here is an HTML page served with a success status (§4.3).
            >= 200 and < 300 => "DeepL returned an unexpected response.",
            _ => $"DeepL service error (HTTP {code}).",
        },
        TransportMessage: kind => kind == TranslationErrorKind.Timeout
            ? "DeepL timed out — check your connection or try again."
            : "Couldn't reach DeepL. Check your Internet connection.",
        PausedMessage: "DeepL is paused after a recent refusal.",
        UserAgent: null,
        Secret: key);

    // One factory for both paths, so a test client differs from the production one by its handler
    // and nothing else. The factory itself is the core's since E2.S5: it was byte-identical here
    // and in GoogleGtxTranslator apart from that provider's User-Agent.
    private static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        HttpProviderCore.CreateClient(handler, userAgent: null);

    // Free-tier keys carry a ":fx" suffix and must use the free host.
    internal static bool FreeKey(string key) => key.TrimEnd().EndsWith(":fx", StringComparison.Ordinal);

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        var outp = await RequestAsync(new[] { text }, source, target, ct).ConfigureAwait(false);

        // One input, one translation — the same 1:1 contract the batch path below enforces, and
        // aligned with it by ruling E6-d. `outp.Count > 0 ? outp[0] : ""` padded with a BLANK,
        // which is worse than padding with the source: an empty string is not a failure placeholder,
        // so it does not start with "(", so the chain counts this tier as a success and no fallback
        // runs, and CachingTranslator stores "" as that line's translation for the session (I4's
        // rule cannot see a blank). A count that is not exactly one is a BadResponse.
        if (outp.Count != 1)
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "DeepL returned an unexpected response.");
        return outp[0];
    }

    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        if (lines.Count == 0) return new List<string>();

        var outp = await RequestAsync(lines, source, target, ct).ConfigureAwait(false);
        if (outp.Count == lines.Count) return outp;

        // DeepL returns exactly one translation per input, in order. A count mismatch means the
        // response is malformed — throw so the next tier of the chain (Google) takes over.
        // (Previously we padded the missing slots with the untranslated source lines, but that
        // bypassed the fallback AND cached raw Russian source as if it were a translation.)
        throw new TranslationException(TranslationErrorKind.BadResponse,
            "DeepL returned an unexpected response.");
    }

    // =============================================================================================
    //  E6.S5 — "Test key", the half that is genuinely free
    // =============================================================================================

    /// <summary>
    /// Validate the key without translating a character. <c>GET {host}/v2/usage</c> takes the same
    /// <c>Authorization</c> header as a translation, spends nothing, and answers
    /// <c>{"character_count":…,"character_limit":…}</c> — so it settles BOTH §3.7 rows this button
    /// has to decide: whether the key is accepted, and whether its quota is spent. That is ruling
    /// <b>E6-b</b>, and it is why DeepL's button can honestly read "Test key" while Azure's cannot.
    ///
    /// <para><b>Through <see cref="HttpProviderCore"/>, not around it</b> — the answer to the
    /// story's OQ-f, recorded here beside the send because the next reader will ask. The core is
    /// what redacts the key out of any body head before the §10.1 line (I11): a probe sent on the
    /// bare client would have no scrubber, and E1.S5's review found a credential leak in exactly
    /// this class. It also means the gate is consulted, so a paused DeepL answers "paused" instead
    /// of a sentence about a key nobody asked about — E6-b's third clause. The cost is real and
    /// accepted: this probe cannot diagnose a provider while its own window is open. Nothing is
    /// reported to the gate that a translation would not have reported.</para>
    ///
    /// <para>A genuine cancel travels out untouched (I3): the only catches here are typed, and the
    /// core's own filtered one is what tells a 12 s HttpClient timeout from a user's cancel.</para>
    /// </summary>
    internal async Task<KeyTestResult> TestKeyAsync(CancellationToken ct = default)
    {
        // Nothing to test, and nothing is sent: an empty key would earn a real 401 and — through
        // the core — a real AuthFailed gate strike, for a mistake no request can fix. Same guard as
        // RequestAsync's, in the same place in the method, for the same reason.
        if (string.IsNullOrEmpty(_key)) return KeyTestResult.Failed(TranslationErrorKind.AuthFailed);

        var uri = new Uri(_usageEndpoint);

        // A fresh message per attempt (the core may make two), and the key in a HEADER only — never
        // in the query, which §10.1 renders and proxies keep (I11).
        HttpRequestMessage Build()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + _key);
            return req;
        }

        try
        {
            // No text and no direction: this is not a translation, so the §10.1 line's bytes/lines
            // are zero and `dir=` renders "-" rather than claiming a language pair.
            return await _core.SendAsync(uri, Build, ParseUsage, "", "", null,
                RequestPriority.Interactive, ct).ConfigureAwait(false);
        }
        catch (TranslationException ex) when (ex.NotSent)
        {
            // The gate refused it before anything left the machine (ruling E3-b). The seconds are
            // counted here and formatted by the UI (I2).
            return KeyTestResult.PausedFor(ex.Kind, LiveTickPolicy.CountdownSeconds(ex.RetryAt, _core.Now));
        }
        catch (TranslationException ex)
        {
            return KeyTestResult.Failed(ex.Kind);
        }
    }

    /// <summary>
    /// The <c>/v2/usage</c> body. <c>character_limit</c> is what turns a 200 into the quota row
    /// without a translation ever being sent — DeepL answers 456 only on the translate path, so a
    /// key whose allowance is spent would otherwise test as perfectly healthy and then fail in a
    /// raid, which is the exact thing this button exists to prevent.
    /// </summary>
    internal static KeyTestResult ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var used = doc.RootElement.GetProperty("character_count").GetInt64();
            long? limit = doc.RootElement.TryGetProperty("character_limit", out var el)
                          && el.TryGetInt64(out var value) ? value : null;

            var usage = UserMessages.DeepLUsage(used, limit);
            return limit is { } cap && cap > 0 && used >= cap
                ? KeyTestResult.Failed(TranslationErrorKind.QuotaExhausted, usage)
                : KeyTestResult.Works(usage);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Same rule as Parse above: a success whose body is not the provider's shape is a
            // BadResponse, stated here because this method has no response to hand the mapper.
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "DeepL returned an unexpected response.");
        }
    }

    /// <summary>One logical call: this method owns the form, the auth header and the parser, and
    /// hands everything else to <see cref="HttpProviderCore"/> (§7.0) — which is where DeepL gains
    /// the retry, the gate and the §10.1 log line it has never had. <c>Interactive</c> is passed
    /// for now by ruling; E3.S7 / E5.S4 are where the real priorities arrive.</summary>
    private Task<List<string>> RequestAsync(IReadOnlyList<string> texts, string source,
        string target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_key))
            throw new TranslationException(TranslationErrorKind.AuthFailed, "No DeepL API key set.");

        var form = new List<KeyValuePair<string, string>>();
        foreach (var t in texts) form.Add(new KeyValuePair<string, string>("text", t));
        form.Add(new KeyValuePair<string, string>("target_lang", ToDeepLTarget(target)));
        var src = ToDeepLSource(source);
        if (src != null) form.Add(new KeyValuePair<string, string>("source_lang", src));

        // A fresh message per attempt: an HttpRequestMessage may not be sent twice, and this
        // provider now has a second attempt to make.
        HttpRequestMessage Build()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            // Header auth is DeepL's recommended scheme (keeps the key out of the body/logs).
            req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + _key);
            return req;
        }

        // The text is handed over only to be MEASURED (I11): the joined form is what the §10.1
        // line's `bytes=` and `lines=` are counted from, and nothing keeps the text itself.
        return _core.SendAsync(new Uri(_endpoint), Build, Parse, source, target,
            string.Join("\n", texts), RequestPriority.Interactive, ct);
    }

    /// <summary>Pull the ordered translations out of a DeepL JSON response.</summary>
    internal static List<string> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.GetProperty("translations");
            var list = new List<string>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
                list.Add(el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // §4.2 row 12: a success whose body is not the provider's shape. The parser is what
            // DETECTS that — the mapper only names it — and this method has no HttpResponseMessage
            // to hand it, so the Kind is stated here and pinned against Classify by
            // ProviderErrorMapperTests (a 200 with an unparseable body ⇒ BadResponse).
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "DeepL returned an unexpected response.");
        }
    }

    // DeepL target codes want a regional variant for English; a target is always concrete
    // (never "auto"), so an unknown/empty target defaults to English.
    internal static string ToDeepLTarget(string target) => (target ?? "").Trim().ToLowerInvariant() switch
    {
        "en" or "" or "auto" => "EN-US",
        var t => t.ToUpperInvariant(),
    };

    // Source may be "auto" → omit source_lang and let DeepL detect. Regional variants aren't
    // used for the source language.
    internal static string? ToDeepLSource(string source)
    {
        var s = (source ?? "").Trim().ToLowerInvariant();
        return s is "" or "auto" ? null : s.ToUpperInvariant();
    }
}

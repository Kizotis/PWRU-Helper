using System.Net.Http;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>
/// Optional DeepL backend, used only when the user pastes a DeepL API key in Settings — the app
/// otherwise stays on the free Google endpoint. Free keys (ending ":fx") hit api-free.deepl.com,
/// paid keys hit api.deepl.com. DeepL translates several <c>text</c> params in ONE request and
/// returns them in order, so the batch path has none of the join/split fragility of the Google
/// one. Problems surface as <see cref="TranslationException"/> so the Google-fallback wrapper
/// (see <see cref="FallbackTranslator"/>) can take over.
/// </summary>
public class DeepLTranslator : ITranslator
{
    private static readonly HttpClient Http = CreateClient();

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _endpoint;

    public DeepLTranslator(string apiKey) : this(apiKey, null) { }

    /// <summary>Test seam: a handler builds a private client — same timeout as the shared one — so
    /// the status mapping and the parser can be exercised offline; the app passes nothing and keeps
    /// the shared static client. Nothing disposes the private client: production never takes this
    /// path, and a test handler owns no sockets.</summary>
    internal DeepLTranslator(string apiKey, HttpMessageHandler? handler = null)
    {
        _key = (apiKey ?? "").Trim();
        _endpoint = FreeKey(_key)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        _http = handler == null ? Http : CreateClient(handler);
    }

    /// <summary>Same reasoning as TranslationService: a process-lifetime client needs its pooled
    /// connections recycled, or a stale one is never replaced. `internal` so the lifetime can be
    /// pinned by a test without reflecting into HttpClient's private fields.</summary>
    internal static SocketsHttpHandler CreatePooledHandler() =>
        new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    // One factory for both paths, so a test client differs from the production one by its handler
    // and nothing else.
    private static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        new(handler ?? CreatePooledHandler())
        {
            Timeout = TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds),
        };

    // Free-tier keys carry a ":fx" suffix and must use the free host.
    internal static bool FreeKey(string key) => key.TrimEnd().EndsWith(":fx", StringComparison.Ordinal);

    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        var outp = await RequestAsync(new[] { text }, source, target, ct);
        return outp.Count > 0 ? outp[0] : "";
    }

    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        if (lines.Count == 0) return new List<string>();

        var outp = await RequestAsync(lines, source, target, ct);
        if (outp.Count == lines.Count) return outp;

        // DeepL returns exactly one translation per input, in order. A count mismatch means the
        // response is malformed — throw so the Google fallback (see FallbackTranslator) takes over.
        // (Previously we padded the missing slots with the untranslated source lines, but that
        // bypassed the fallback AND cached raw Russian source as if it were a translation.)
        throw new TranslationException(TranslationErrorKind.BadResponse,
            "DeepL returned an unexpected response.");
    }

    private async Task<List<string>> RequestAsync(IReadOnlyList<string> texts, string source,
        string target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_key))
            throw new TranslationException(TranslationErrorKind.AuthFailed, "No DeepL API key set.");

        var form = new List<KeyValuePair<string, string>>();
        foreach (var t in texts) form.Add(new KeyValuePair<string, string>("text", t));
        form.Add(new KeyValuePair<string, string>("target_lang", ToDeepLTarget(target)));
        var src = ToDeepLSource(source);
        if (src != null) form.Add(new KeyValuePair<string, string>("source_lang", src));

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        // Header auth is DeepL's recommended scheme (keeps the key out of the body/logs).
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + _key);

        string json;
        // The send AND the body read sit in the same try: a failure while reading the response used
        // to escape raw, past the TranslationException contract the FallbackTranslator and the LIVE
        // loop are written against. (With HttpClient's default ResponseContentRead the body is
        // already buffered by SendAsync, so that escape is defensive today — but the contract is
        // the point, not the odds.)
        try
        {
            using var resp = await _http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                // The single classification point (§4.2). A key is always sent on this path (the
                // empty-key case threw above), so the mapper reads 403 as a rejected key and not
                // as a bot block. No bodyHead: DeepL signals an exhausted allowance with its own
                // 456, so row 6's envelope test has nothing to read here — Azure (E6) is the
                // provider that will pass one. Every sentence below is unchanged.
                // The mapper's caller contract, honoured rather than only quoted: row 1 answers
                // with the cancel Kind whenever the token is cancelled, and the throw below would
                // hand it to a TranslationException — the one construction TranslationErrors.cs
                // forbids. Checking here (the token can be cancelled between the response arriving
                // and this line) means row 1 cannot fire and a cancel can only leave as an OCE.
                ct.ThrowIfCancellationRequested();

                var kind = ProviderErrorMapper.Classify(resp, bodyHead: null, transport: null,
                    keyWasSent: true, ct);
                // The sentence below is no longer what the player reads: E1.S6 made Friendly()
                // render one sentence per Kind from Services/UserMessages.cs, and this message is
                // now the LOG's account of what DeepL said. That defuses the two-switch trap this
                // warning was about — a bodyHead-driven QuotaExhausted can no longer arrive on
                // screen as "check the API key". It can still make the log disagree with the Kind
                // beside it, so whoever starts passing a bodyHead here still owns keeping the two
                // switches in step (E6).
                var message = code switch
                {
                    // "About", not "Settings": this app has never had a Settings tab, and although
                    // this literal is now the log's account rather than the player's, the log is
                    // pasted to Discord by design (I11's premise) — a human still reads it and
                    // still cannot find the tab. E1.S6's review: the last of the fifteen.
                    401 or 403 => "DeepL rejected the API key — check it in About.",
                    456 => "DeepL free quota is used up for this month.",
                    429 => "DeepL is rate-limiting right now — try again shortly.",
                    _ => $"DeepL service error (HTTP {code}).",
                };
                throw new TranslationException(kind, message,
                    ProviderErrorMapper.RetryAfter(resp, DateTimeOffset.UtcNow));
            }

            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // The OCE trap: on .NET 8 an HttpClient timeout is a TaskCanceledException with the
            // caller's ct NOT cancelled. The filter above takes every real cancellation, so the
            // mapper sees only the timeout — and says so, which is what makes the Google fallback
            // kick in instead of a raw OCE bubbling up past the FallbackTranslator.
            // Same contract as the status branch: the filter above ran one statement ago, and a
            // token cancelled since then would make Classify answer with the cancel Kind.
            ct.ThrowIfCancellationRequested();
            throw new TranslationException(
                ProviderErrorMapper.Classify(resp: null, bodyHead: null, transport: ex,
                    keyWasSent: true, ct),
                ex is HttpRequestException
                    ? "Couldn't reach DeepL. Check your Internet connection."
                    : "DeepL timed out — check your connection or try again.");
        }

        return Parse(json);
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

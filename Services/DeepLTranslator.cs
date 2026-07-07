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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly string _key;
    private readonly string _endpoint;

    public DeepLTranslator(string apiKey)
    {
        _key = (apiKey ?? "").Trim();
        _endpoint = FreeKey(_key)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
    }

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
        throw new TranslationException("DeepL returned an unexpected response.");
    }

    private async Task<List<string>> RequestAsync(IReadOnlyList<string> texts, string source,
        string target, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_key))
            throw new TranslationException("No DeepL API key set.");

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

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // On .NET 8 an HttpClient timeout surfaces as a TaskCanceledException (a subclass of
            // OperationCanceledException) with the caller's ct NOT cancelled. Turn it into a
            // TranslationException so the Google fallback kicks in instead of the raw OCE bubbling
            // up past the FallbackTranslator (which correctly refuses to swallow real cancellations).
            throw new TranslationException("DeepL timed out — check your connection or try again.");
        }
        catch (HttpRequestException)
        {
            throw new TranslationException("Couldn't reach DeepL. Check your Internet connection.");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                throw new TranslationException(code switch
                {
                    401 or 403 => "DeepL rejected the API key — check it in Settings.",
                    456 => "DeepL free quota is used up for this month.",
                    429 => "DeepL is rate-limiting right now — try again shortly.",
                    _ => $"DeepL service error (HTTP {code}).",
                });
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return Parse(json);
        }
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
            throw new TranslationException("DeepL returned an unexpected response.");
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

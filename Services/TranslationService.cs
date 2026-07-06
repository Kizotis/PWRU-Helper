using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace PWRUHelper.Services;

/// <summary>A translation problem worth showing to the user in plain language.</summary>
public class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}

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

    // The text travels in the GET query string; keep well under typical URL limits.
    private const int MaxQueryBytes = 1500;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
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

        if (Encoding.UTF8.GetByteCount(text) <= MaxQueryBytes)
            return await RequestAsync(text, source, target, ct);

        // Too long for one request: translate sentence-sized chunks and stitch back.
        var sb = new StringBuilder();
        foreach (var chunk in ChunkText(text, MaxQueryBytes))
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
        if (Encoding.UTF8.GetByteCount(joined) <= MaxQueryBytes)
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
            catch (OperationCanceledException) { throw; }   // a real cancel must propagate
            catch (TranslationException) { rateLimited = true; result.Add("(rate-limited — try again shortly)"); }
            catch (Exception ex) { result.Add($"(translation failed: {ex.Message})"); }
        }
        return result;

        async Task<string> SafeOne(string line)
        {
            try { return await TranslateAsync(line, source, target, ct); }
            catch (TranslationException) { throw; }
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
                using var resp = await Http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    json = await resp.Content.ReadAsStringAsync(ct);
                    break;
                }

                int code = (int)resp.StatusCode;
                bool transient = code == 429 || code >= 500;
                if (!transient)
                    // A real, non-retryable error (e.g. 400/403) — report it as-is instead of
                    // retrying and then mislabeling it as "no Internet".
                    throw new TranslationException($"Translation service error (HTTP {code}). Please try again later.");
                if (code == 429 && attempt == 2)
                    throw new TranslationException(
                        "Google is limiting translations right now — wait a minute and try again.");
                if (attempt == 2)
                    throw new TranslationException($"Translation service is unavailable (HTTP {code}). Try again shortly.");
                // transient and attempts left → fall through to the delay + retry below.
            }
            catch (HttpRequestException) when (attempt < 2) { /* network blip — retry */ }

            await Task.Delay(300 * (attempt + 1), ct);
        }

        if (json == null)
            throw new TranslationException("Couldn't reach the translation service. Check your Internet connection.");

        // Response shape: [[["translated","original",...], ...], ...]
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
        catch (JsonException)
        {
            // Usually an HTML captcha / throttle page instead of JSON.
            throw new TranslationException(
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

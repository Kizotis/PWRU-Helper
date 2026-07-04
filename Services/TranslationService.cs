using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;

namespace PWRUHelper.Services;

/// <summary>
/// Translates text using Google's free (unofficial) translate endpoint — the same
/// one translate.google.com uses. No API key, no cost. All work happens on Google's
/// servers, so this uses no local CPU/GPU.
/// </summary>
public class TranslationService
{
    private static readonly HttpClient Http = CreateClient();

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
    /// Use "auto" for source to auto-detect.
    /// </summary>
    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";

        var url = "https://translate.googleapis.com/translate_a/single?client=gtx" +
                  $"&sl={source}&tl={target}&dt=t&q={HttpUtility.UrlEncode(text)}";

        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        // Response shape: [[["translated","original",...], ...], ...]
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
}

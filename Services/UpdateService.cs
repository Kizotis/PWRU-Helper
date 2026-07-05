using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>Details of a newer release found on GitHub.</summary>
public record UpdateInfo(Version LatestVersion, string TagName, string ReleaseUrl);

/// <summary>
/// Checks GitHub's Releases for a newer version of PWRU Helper. Fully best-effort:
/// any problem (offline, rate-limited, no releases yet, odd tag) just returns null,
/// it never throws and never blocks the app.
/// </summary>
public class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Kizotis/PWRU-Helper/releases/latest";
    // Fallback link if the API response has no html_url.
    public const string ReleasesPage =
        "https://github.com/Kizotis/PWRU-Helper/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub's API rejects requests without a User-Agent.
        c.DefaultRequestHeaders.Add("User-Agent", "PWRUHelper-UpdateCheck");
        c.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return c;
    }

    /// <summary>The version this build reports (from the csproj &lt;Version&gt;).</summary>
    public static Version CurrentVersion =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>
    /// Returns info about the latest GitHub release when it's newer than this build,
    /// otherwise null (up to date, or the check couldn't complete).
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseApi, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !TryParseVersion(tag, out var latest))
                return null;

            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) url = ReleasesPage;

            return latest > CurrentVersion ? new UpdateInfo(latest, tag!, url!) : null;
        }
        catch
        {
            // Offline, timeout, rate-limited, no releases yet… — silently skip.
            return null;
        }
    }

    // Compare on major.minor.build only, treating unset parts as 0, so tags like
    // "0.3" and assembly versions like "0.3.0.0" line up instead of mismatching.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    private static bool TryParseVersion(string tag, out Version version)
    {
        // Tags look like "v0.3.0", "0.3", "v1.2.3-beta" — keep the leading number.
        var cleaned = tag.TrimStart('v', 'V').Trim();
        int cut = cleaned.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) cleaned = cleaned[..cut];
        if (!cleaned.Contains('.')) cleaned += ".0"; // Version needs at least major.minor

        if (Version.TryParse(cleaned, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0);
        return false;
    }
}

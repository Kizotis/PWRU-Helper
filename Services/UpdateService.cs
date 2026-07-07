using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>Details of a newer release found on GitHub, including the downloadable installer
/// assets (either may be null if that asset isn't attached to the release).</summary>
public record UpdateInfo(Version LatestVersion, string TagName, string ReleaseUrl,
    string? MsiUrl = null, string? ExeUrl = null);

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

            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
                || u.Scheme != Uri.UriSchemeHttps
                || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                url = ReleasesPage;

            var (msi, exe) = ReadAssetUrls(root);
            return latest > CurrentVersion ? new UpdateInfo(latest, tag!, url!, msi, exe) : null;
        }
        catch
        {
            // Offline, timeout, rate-limited, no releases yet… — silently skip.
            return null;
        }
    }

    // Pull the .msi and .exe download URLs out of the release's assets[] (only https GitHub
    // download links are trusted). Either can be null if not attached.
    internal static (string? Msi, string? Exe) ReadAssetUrls(JsonElement root)
    {
        string? msi = null, exe = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(name) || !IsTrustedDownload(dl)) continue;
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) msi ??= dl;
                else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe ??= dl;
            }
        }
        return (msi, exe);
    }

    private static bool IsTrustedDownload(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Download a release asset to <paramref name="destPath"/>, reporting 0–100% progress.
    /// Throws on any HTTP/IO error.</summary>
    public async Task DownloadAsync(string url, string destPath, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsTrustedDownload(url))
            throw new InvalidOperationException("Refusing to download from an untrusted URL.");

        using var resp = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report(Math.Min(100.0, read * 100.0 / total));
        }
    }

    // A separate client for the (large) asset download: no short timeout — the whole transfer is
    // bounded by the CancellationToken instead of the 8s update-check timeout.
    private static readonly HttpClient Downloads = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.Add("User-Agent", "PWRUHelper-Update");
        return c;
    }

    // Compare on major.minor.build only, treating unset parts as 0, so tags like
    // "0.3" and assembly versions like "0.3.0.0" line up instead of mismatching.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    internal static bool TryParseVersion(string tag, out Version version)
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

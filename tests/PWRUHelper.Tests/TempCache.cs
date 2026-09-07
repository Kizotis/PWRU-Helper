using System.IO;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-3, and the fourth instance of a pattern this repo has three times paid for: points
/// <see cref="TranslationCacheStore"/> at a throwaway <c>translation-cache.json</c> for the duration
/// of a test. Mirrors <c>TempGateState.cs:15-36</c> deliberately, down to the swallowed delete — the
/// suite already rewrote a developer's own <c>settings.json</c> once
/// (<c>SettingsService.PathOverride</c>), seeded a real error report with fixture failures once
/// (<c>Logging.DirectoryOverride</c>), and re-pointed <c>provider-state.json</c> at the developer's
/// own file once (E2.S2's review).
/// </summary>
internal sealed class TempCache : IDisposable
{
    private readonly DirectoryInfo _dir;

    /// <summary>The throwaway file <see cref="TranslationCacheStore.CachePath"/> resolves to while
    /// this object lives.</summary>
    public string Path { get; }

    public TempCache()
    {
        _dir = Directory.CreateTempSubdirectory("pwru-cache-");
        Path = System.IO.Path.Combine(_dir.FullName, "translation-cache.json");
        TranslationCacheStore.PathOverride = Path;
    }

    /// <summary>Write a file by hand at <see cref="Path"/> — corrupt, future-versioned, or carrying
    /// a row no code in this build can produce. Fixtures live in the test source and not under
    /// <c>Fixtures/</c> (IS-9): a corrupt-file case wants its corruption visible where it is
    /// asserted.</summary>
    public void Write(string json) => File.WriteAllText(Path, json);

    public string Read() => File.ReadAllText(Path);

    public void Dispose()
    {
        // Back to the run-wide redirect, never to null: null is the developer's real %AppData%, and
        // the point of this pair is that no instant of a test run resolves there.
        TranslationCacheStore.PathOverride = TestCacheRedirect.Path;
        // The debounce seam is process-wide state too (IS-4): a case that stretched the window to a
        // minute must not leave it stretched for the next one.
        TranslationCacheStore.SaveDebounceMs = TranslationPolicy.CacheSaveDebounceMs;
        try { _dir.Delete(recursive: true); } catch { /* the test already made its point */ }
    }
}

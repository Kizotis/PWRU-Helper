using System.Runtime.CompilerServices;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// The assembly-wide half of IS-2/IS-3, in the shape this repo already uses for the log
/// (<c>TestLogRedirect.cs:24-26</c>) and for the gate state (<c>TestGateStateRedirect.cs:23-33</c>):
/// a <c>[ModuleInitializer]</c> that points <see cref="TranslationCacheStore.PathOverride"/> at a
/// throwaway directory <b>before any test runs</b>, so a case that forgets <see cref="TempCache"/>
/// still cannot reach the developer's own <c>%AppData%\PWRUHelper\translation-cache.json</c>.
///
/// <para>Three files this suite has already poisoned — <c>settings.json</c>, the error-report
/// directory, <c>provider-state.json</c> — is why this pair landed before a line of E4.S2's feature
/// code. This is the fourth file, and it is the first one that would carry the user's own chat text
/// (I11).</para>
///
/// <para>Why both this and <c>TempCache</c>: the disposable gives one case its own file to
/// round-trip, this gives every other case a safe default. Between them there is no ordering in
/// which the real path is live — <see cref="Path"/> is what <c>TempCache.Dispose</c> restores, never
/// <c>null</c>.</para>
/// </summary>
internal static class TestCacheRedirect
{
    /// <summary>The whole test run's default cache file. A fixed temp path rather than a fresh
    /// subdirectory, exactly like <c>TestLogRedirect</c> and <c>TestGateStateRedirect</c>: a per-run
    /// directory would accumulate one empty folder per <c>dotnet test</c> for ever.</summary>
    internal static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pwru-test-cache", "translation-cache.json");

    [ModuleInitializer]
    internal static void Redirect() => TranslationCacheStore.PathOverride = Path;
}

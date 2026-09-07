using System.Runtime.CompilerServices;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// The assembly-wide half of IS-2/IS-3 for the fifth path this suite could poison, in the shape the
/// other four already use (<c>TestLogRedirect</c>, <c>TestGateStateRedirect</c>,
/// <c>TestCacheRedirect</c>): a <c>[ModuleInitializer]</c> that points
/// <see cref="OfflineModelStore.PathOverride"/> at a throwaway directory <b>before any test runs</b>,
/// so a case that forgets <see cref="TempModels"/> still cannot reach the developer's own
/// <c>%LocalAppData%\PWRUHelper\models</c>.
///
/// <para>It is the first of the five that would be measured in <b>tens of megabytes</b> rather than
/// kilobytes, and the first whose directory an app on this machine may legitimately have filled — so
/// the guard case asserts the real directory is UNTOUCHED rather than absent.</para>
/// </summary>
internal static class TestModelsRedirect
{
    /// <summary>The whole test run's default model root. A fixed temp path rather than a fresh
    /// subdirectory, exactly like the other three: a per-run directory would accumulate one empty
    /// folder per <c>dotnet test</c> for ever.</summary>
    internal static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pwru-test-models");

    [ModuleInitializer]
    internal static void Redirect() => OfflineModelStore.PathOverride = Path;
}

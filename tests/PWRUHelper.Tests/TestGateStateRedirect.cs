using System.IO;
using System.Runtime.CompilerServices;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// The assembly-wide half of IS-1/IS-3, in the shape this repo already uses for the log
/// (<c>TestLogRedirect.cs:24-26</c>): a <c>[ModuleInitializer]</c> that points
/// <see cref="ProviderGates.PathOverride"/> at a throwaway directory <b>before any test runs</b>,
/// so a case that forgets <see cref="TempGateState"/> still cannot reach the developer's own
/// <c>%AppData%\PWRUHelper\provider-state.json</c>.
///
/// <para>Why both this and <c>TempGateState</c>: the disposable gives one case its own file to
/// round-trip (E2.S4), this gives every other case a safe default. Between them there is no
/// ordering in which the real path is live — which the explicit guard would not have caught,
/// because <c>ProviderGates.ResetForTests()</c> deliberately nulls the override (it puts the
/// process back where it started), and <c>GatesTestBase</c> runs it before and after every case.
/// <see cref="Path"/> is therefore re-applied there, and the guard case asserts the override is
/// non-null <i>outside</i> any <c>using</c> block — the same assertion
/// <c>LoggingTests.The_test_run_never_writes_to_the_real_AppData_log</c> makes.</para>
/// </summary>
internal static class TestGateStateRedirect
{
    /// <summary>The whole test run's default gate-state file. Never written in this story — E2.S4's
    /// store is what will write it — and never the real one. A fixed temp path rather than a fresh
    /// subdirectory, exactly like <c>TestLogRedirect</c>: a per-run directory would accumulate one
    /// empty folder per <c>dotnet test</c> forever.</summary>
    internal static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pwru-test-gatestate", "provider-state.json");

    [ModuleInitializer]
    internal static void Redirect() => ProviderGates.PathOverride = Path;
}

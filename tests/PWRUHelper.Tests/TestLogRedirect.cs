using System.IO;
using System.Runtime.CompilerServices;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// Keeps the test run out of the developer's real application log.
///
/// The suite exercises the failure paths on purpose — a DeepL that throws "deepl down", a stale
/// squad.json being refreshed, a translator falling back. Every one of those logs a WARN, and
/// <see cref="Logging"/> writes to %AppData%\PWRUHelper\logs\log.txt: the same file the About
/// tab's "Copy error report" button reads back and puts on the clipboard for the user to paste
/// to someone on Discord. So a single `dotnet test` seeded the maintainer's own error report with
/// failures that never happened (this was found by reading a real log and spotting the fixture
/// string "deepl down" in it).
///
/// A module initialiser runs once, when the test assembly is loaded, before any test — which is
/// the only hook that catches the logging done by every test including the ones that never
/// mention <see cref="Logging"/>. A per-test IDisposable would leak whatever ran outside it.
/// </summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void RedirectLogsToTemp()
        => Logging.DirectoryOverride = Path.Combine(Path.GetTempPath(), "pwru-test-logs");
}

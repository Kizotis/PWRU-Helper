using System.IO;
using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// IS-3, and the third instance of a pattern this repo has twice paid for: points
/// <see cref="ProviderGates"/> at a throwaway <c>provider-state.json</c> for the duration of a test.
/// Mirrors <c>TempSettings</c> (<c>StaTestHost.cs:82-101</c>) deliberately, down to the swallowed
/// delete — the suite already rewrote a developer's own <c>settings.json</c> once
/// (<c>SettingsService.PathOverride</c>) and seeded a real error report with fixture failures once
/// (<c>Logging.DirectoryOverride</c>). The store that reads this path is <b>E2.S4</b>; the
/// disposable exists first so no gate test can ever be written without it.
/// </summary>
internal sealed class TempGateState : IDisposable
{
    private readonly DirectoryInfo _dir;

    /// <summary>The throwaway file <see cref="ProviderGates.StatePath"/> resolves to while this
    /// object lives. Nothing writes it in this story; E2.S4's round-trip cases will.</summary>
    public string Path { get; }

    public TempGateState()
    {
        _dir = Directory.CreateTempSubdirectory("pwru-gatestate-");
        Path = System.IO.Path.Combine(_dir.FullName, "provider-state.json");
        ProviderGates.PathOverride = Path;
    }

    public void Dispose()
    {
        // Back to the run-wide redirect, never to null: null is the developer's real %AppData%, and
        // the point of this pair is that no instant of a test run resolves there.
        ProviderGates.PathOverride = TestGateStateRedirect.Path;
        try { _dir.Delete(recursive: true); } catch { /* the test already made its point */ }
    }
}

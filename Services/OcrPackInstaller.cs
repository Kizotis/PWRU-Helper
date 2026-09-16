using System.Text;

namespace PWRUHelper.Services;

/// <summary>
/// Installs the Windows "Russian OCR" capability, and says in plain words why it failed.
///
/// Add-WindowsCapability does not carry the pack: it DOWNLOADS it through Windows Update. Many
/// players turn Windows Update off (debloat scripts, O&amp;O ShutUp10, Windows Update Blocker), and
/// the install then fails with 0x80070422 whether or not PowerShell is elevated — the old
/// "code 1, make sure you're online" sent them the wrong way. So the install script turns the
/// service on for the install only and puts it back to Disabled afterwards: the player switched it
/// off on purpose, and we respect that.
///
/// UI-free on purpose (the script text and the error table are unit-tested); the elevated
/// process launch lives in <c>MainWindow.Ocr.cs</c>.
/// </summary>
internal static class OcrPackInstaller
{
    public const string Capability = "Language.OCR~~~ru-RU~0.0.1.0";

    /// <summary>Exit code of <see cref="InstallScript"/> when Windows Update could not be turned
    /// on at all — typically a blocker tool has locked the service, or a "lite" Windows build removed
    /// it. Small and positive, so it can never collide with a Windows HRESULT (negative as an Int32).</summary>
    public const int CouldNotEnableWindowsUpdate = 10;

    // HRESULTs as the Int32 a process exit code carries them.
    public const int ServiceDisabled = unchecked((int)0x80070422);
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int BlockedByPolicy = unchecked((int)0x800F0954);
    public const int SourceNotFound = unchecked((int)0x800F081F);
    public const int SourceNotFoundAlt = unchecked((int)0x800F0950);

    private static readonly int[] NetworkErrors =
    {
        unchecked((int)0x80072EE7), // host name not resolved
        unchecked((int)0x80072EFD), // cannot connect
        unchecked((int)0x80072EE2), // timed out
        unchecked((int)0x80072F8F), // clock / TLS
        unchecked((int)0x8024402C), // WU: proxy / name resolution
        unchecked((int)0x80240438), // WU: no route to the service
    };

    /// <summary>The script the Install button runs elevated (hidden window). It exits with 0, with
    /// <see cref="CouldNotEnableWindowsUpdate"/>, or with the failing HRESULT.</summary>
    public static string InstallScript() => $$"""
        $ErrorActionPreference = 'Stop'
        try { $wasDisabled = (Get-Service -Name wuauserv).StartType -eq 'Disabled' }
        catch { exit {{CouldNotEnableWindowsUpdate}} }
        $code = 0
        try {
            try {
                if ($wasDisabled) { Set-Service -Name wuauserv -StartupType Manual }
                Start-Service -Name wuauserv
            } catch { exit {{CouldNotEnableWindowsUpdate}} }
            try { Add-WindowsCapability -Online -Name '{{Capability}}' | Out-Null }
            catch { $code = $_.Exception.HResult }
        } finally {
            if ($wasDisabled) {
                Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
                Set-Service -Name wuauserv -StartupType Disabled -ErrorAction SilentlyContinue
            }
        }
        exit $code
        """;

    /// <summary><see cref="InstallScript"/> as <c>powershell.exe -EncodedCommand</c> wants it
    /// (Base64 of UTF-16LE) — no quoting to get wrong on a multi-line script.</summary>
    public static string EncodedInstallScript()
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(InstallScript()));

    /// <summary>The one line a player pastes into an admin PowerShell. Same steps as the button,
    /// but no <c>exit</c> (it would close their window) and errors stay visible.</summary>
    public static string ManualCommand() =>
        "$off=(Get-Service wuauserv).StartType -eq 'Disabled'; " +
        "if($off){Set-Service wuauserv -StartupType Manual}; Start-Service wuauserv; " +
        $"Add-WindowsCapability -Online -Name '{Capability}'; " +
        "if($off){Stop-Service wuauserv -Force; Set-Service wuauserv -StartupType Disabled}";

    /// <summary>True when the registry "Start" value of the wuauserv service means Disabled (4).
    /// Readable without admin, so the tab can warn before the player clicks.</summary>
    public static bool IsDisabledStartValue(object? start) => start is int i && i == 4;

    public static bool IsWindowsUpdateDisabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\wuauserv");
            return IsDisabledStartValue(key?.GetValue("Start"));
        }
        catch { return false; }  // unreadable = unknown; the install reports the real cause anyway
    }

    /// <summary>What to tell the player after a failed install (exit code != 0).</summary>
    public static string DescribeFailure(int exitCode)
    {
        if (exitCode == CouldNotEnableWindowsUpdate || exitCode == ServiceDisabled || exitCode == AccessDenied)
            return "Windows Update is off or locked on this PC, and the pack downloads through it. " +
                   "If you use a tool like Windows Update Blocker, turn updates on there, install, then turn them off again.";
        if (exitCode == BlockedByPolicy || exitCode == SourceNotFound || exitCode == SourceNotFoundAlt)
            return "Windows is set to not download optional features (group policy or company PC). " +
                   "Ask whoever manages this PC to allow it.";
        if (Array.IndexOf(NetworkErrors, exitCode) >= 0)
            return "Windows couldn't reach its download servers. Check your internet, then try again.";
        return exitCode < 0
            ? $"Install failed (Windows error 0x{exitCode:X8}). Try the command below in an admin PowerShell."
            : "Install failed. Try the command below in an admin PowerShell.";
    }
}

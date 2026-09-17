using System.Text;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>The Russian OCR install: a player with Windows Update switched off got
/// "The service cannot be started" (0x80070422) from both the button and the pasted command.</summary>
public class OcrPackInstallerTests
{
    [Fact]
    public void InstallScript_TurnsWindowsUpdateOn_AndRestoresDisabled()
    {
        var s = OcrPackInstaller.InstallScript();
        Assert.Contains("Set-Service -Name wuauserv -StartupType Manual", s);
        Assert.Contains("Start-Service -Name wuauserv", s);
        Assert.Contains($"Add-WindowsCapability -Online -Name '{OcrPackInstaller.Capability}'", s);
        // The restore sits in a finally: a failed install must not leave Windows Update on.
        int fin = s.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(fin > 0);
        Assert.Contains("-StartupType Disabled", s[fin..]);
    }

    [Fact]
    public void EncodedInstallScript_IsUtf16Base64OfTheScript()
        => Assert.Equal(OcrPackInstaller.InstallScript(),
            Encoding.Unicode.GetString(Convert.FromBase64String(OcrPackInstaller.EncodedInstallScript())));

    [Fact]
    public void ManualCommand_IsOneLine_NoExit_AndRestores()
    {
        var c = OcrPackInstaller.ManualCommand();
        Assert.DoesNotContain('\n', c);
        Assert.DoesNotContain("exit", c);   // would close the player's PowerShell window
        Assert.Contains(OcrPackInstaller.Capability, c);
        Assert.EndsWith("Set-Service wuauserv -StartupType Disabled}", c);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    [InlineData(2, false)]
    [InlineData(null, false)]
    [InlineData("4", false)]
    public void IsDisabledStartValue(object? value, bool expected)
        => Assert.Equal(expected, OcrPackInstaller.IsDisabledStartValue(value));

    [Theory]
    [InlineData(OcrPackInstaller.CouldNotEnableWindowsUpdate, "Windows Update")]
    [InlineData(OcrPackInstaller.ServiceDisabled, "Windows Update")]
    [InlineData(OcrPackInstaller.AccessDenied, "Windows Update")]
    [InlineData(OcrPackInstaller.BlockedByPolicy, "group policy")]
    [InlineData(OcrPackInstaller.SourceNotFound, "group policy")]
    [InlineData(unchecked((int)0x80072EE7), "internet")]
    [InlineData(unchecked((int)0x80004005), "0x80004005")]
    [InlineData(1, "command below")]
    public void DescribeFailure_NamesTheRealCause(int code, string expected)
        => Assert.Contains(expected, OcrPackInstaller.DescribeFailure(code));

    [Fact]
    public void DescribeFailure_NeverBlamesTheNetwork_ForADisabledService()
        => Assert.DoesNotContain("internet", OcrPackInstaller.DescribeFailure(OcrPackInstaller.ServiceDisabled));
}

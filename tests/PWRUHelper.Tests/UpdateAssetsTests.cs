using System.Text.Json;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class UpdateAssetsTests
{
    private static (string? Msi, string? Exe) Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UpdateService.ReadAssetUrls(doc.RootElement);
    }

    [Fact]
    public void Extracts_msi_and_exe_download_urls()
    {
        var (msi, exe) = Read("""
        { "assets": [
          { "name": "PWRUHelper-0.12.0-setup.msi",
            "browser_download_url": "https://github.com/Kizotis/PWRU-Helper/releases/download/v0.12.0/PWRUHelper-0.12.0-setup.msi" },
          { "name": "PWRUHelper.exe",
            "browser_download_url": "https://github.com/Kizotis/PWRU-Helper/releases/download/v0.12.0/PWRUHelper.exe" }
        ]}
        """);

        Assert.EndsWith(".msi", msi);
        Assert.EndsWith(".exe", exe);
    }

    [Fact]
    public void Ignores_assets_hosted_off_github()
    {
        var (msi, exe) = Read("""
        { "assets": [ { "name": "evil.msi", "browser_download_url": "https://evil.example.com/evil.msi" } ] }
        """);

        Assert.Null(msi);
        Assert.Null(exe);
    }

    [Fact]
    public void Accepts_githubusercontent_download_host()
    {
        var (_, exe) = Read("""
        { "assets": [ { "name": "PWRUHelper.exe",
          "browser_download_url": "https://objects.githubusercontent.com/github-production-release-asset/PWRUHelper.exe" } ] }
        """);

        Assert.NotNull(exe);
    }

    [Fact]
    public void Returns_nulls_when_there_are_no_assets()
    {
        var (msi, exe) = Read("{}");
        Assert.Null(msi);
        Assert.Null(exe);
    }
}

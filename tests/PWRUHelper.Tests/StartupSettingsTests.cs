using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Guards the class of bug where STARTING the app silently rewrites the user's own settings.
///
/// The real one (v0.13.0): every launch persisted OcrFilterMode = "off", so a chosen "Boost
/// contrast" was gone by the next run. Nothing in the OCR code was wrong — XAML *loading* raised
/// `ValueChanged` on `<Slider Value="70" …/>` during InitializeComponent(), long before
/// ApplySettings(); the handler read the not-yet-restored filter combo (no selection → "off")
/// and saved that. The guard flag only covered the restore phase, not the build phase.
///
/// So: construct the real window against a saved file and assert the file is unchanged.
/// </summary>
[Collection("WPF")]
public class StartupSettingsTests
{
    // A saved file from a user who deliberately picked "Boost contrast" (and is on the current
    // schema, so no migration is in play — this is purely about startup not clobbering it).
    private const string ContrastSettings = """
    {
      "OcrFilterMode": "contrast",
      "OcrKeepColorHex": "#FFFFFF",
      "OcrColorTolerance": 70,
      "CaptureBackend": "gdi",
      "SquadUppercase": true,
      "SettingsVersion": 3
    }
    """;

    [Fact]
    public void Starting_the_app_does_not_overwrite_the_saved_background_filter()
    {
        using var settings = new TempSettings(ContrastSettings);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();   // XAML load + ApplySettings, exactly as at launch

            // The restored UI must show what was saved…
            Assert.Equal("contrast", (window.OcrFilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());
            Assert.True(window.SquadUppercaseCheck.IsChecked);
        });

        // …and, the part that actually broke, the file on disk must still say so.
        using var saved = JsonDocument.Parse(File.ReadAllText(settings.Path));
        Assert.Equal("contrast", saved.RootElement.GetProperty("OcrFilterMode").GetString());
    }

    // The same file, for the controls E6.S3 adds. The region is deliberately NOT one of the nine
    // seeded ones: `SelectTag` silently does nothing when no item matches, so a free-text region is
    // the case a seeded-list-only test would miss — and the one that would come back blank while
    // the file still held it.
    private const string AzureSettings = """
    {
      "AzureApiKey": "0123456789abcdef0123456789abcdef",
      "AzureRegion": "norwayeast",
      "UseKeyForReading": true,
      "OcrFilterMode": "contrast",
      "SettingsVersion": 3
    }
    """;

    /// <summary>
    /// TP-SET-05 — the clobber test for E6.S3's controls, and the DoD of that story. Same shape as
    /// the case above because it is the same bug: a change handler firing during
    /// <c>InitializeComponent()</c> (the region combo's <c>SelectionChanged</c>) writing the
    /// not-yet-restored UI back to disk.
    ///
    /// <para>Asserting the restored control is the half that already passed while the bug shipped —
    /// so the file on disk is re-read afterwards and must be untouched, key, region and opt-in
    /// alike.</para>
    /// </summary>
    [Fact]
    public void Starting_the_app_does_not_overwrite_the_saved_azure_key_and_region()
    {
        using var settings = new TempSettings(AzureSettings);
        var beforeStartup = settings.Read();

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            Assert.Equal("0123456789abcdef0123456789abcdef", window.AzureKeyBox.Password);
            Assert.Null(window.AzureRegionCombo.SelectedItem);           // free text: no item matches
            Assert.Equal("norwayeast", window.AzureRegionCombo.Text);
            // E6.S4's control, and it is the one with money behind it: a saved opt-in that came
            // back unticked would silently move the LIVE loop off the user's key, and a handler
            // firing during InitializeComponent() would write that false back over the file.
            Assert.True(window.AzureForReadingCheck.IsChecked);
            Assert.True(window.AzureForReadingCheck.IsEnabled);          // there IS a key to opt into
        });

        // TP-SET-05's own wording is "the file on disk is byte-identical", and the stronger assert
        // is worth the strictness: a clobbering handler that happened to write the SAME values back
        // would satisfy every value assert below while proving the guard did not hold. Starting the
        // app must not write settings.json at all — this file is already at the current version, so
        // no migration is owed either (AC 2).
        Assert.Equal(beforeStartup, settings.Read());

        using var saved = JsonDocument.Parse(File.ReadAllText(settings.Path));
        Assert.Equal("0123456789abcdef0123456789abcdef",
                     saved.RootElement.GetProperty("AzureApiKey").GetString());
        Assert.Equal("norwayeast", saved.RootElement.GetProperty("AzureRegion").GetString());
        Assert.True(saved.RootElement.GetProperty("UseKeyForReading").GetBoolean());
        Assert.Equal(3, saved.RootElement.GetProperty("SettingsVersion").GetInt32());
    }

    [Fact]
    public void A_user_still_stuck_on_off_is_moved_to_boost_contrast_once()
    {
        // v1 already tried this and lost the race with the startup clobber above, so every existing
        // user is on "off" whatever they chose. v2 re-applies the intended default — once.
        using var _ = new TempSettings("""{ "OcrFilterMode": "off", "SettingsVersion": 1 }""");

        var migrated = SettingsService.Load();
        Assert.Equal("contrast", migrated.OcrFilterMode);
        Assert.Equal(3, migrated.SettingsVersion);   // stamped with the current schema

        // Deliberately going back to Off now sticks: the migration is stamped and never re-runs.
        migrated.OcrFilterMode = "off";
        SettingsService.Save(migrated);
        Assert.Equal("off", SettingsService.Load().OcrFilterMode);
    }
}

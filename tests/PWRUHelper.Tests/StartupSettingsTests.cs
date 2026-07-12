using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Guards the class of bug where STARTING the app silently rewrites the user's own settings.
///
/// The real one (v0.12.3): every launch persisted OcrFilterMode = "off", so a chosen "Boost
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

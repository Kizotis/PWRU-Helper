using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// A settings file written by an older version keeps its values forever, so a default tuned later
/// only ever reaches NEW installs — everyone else sits on numbers nobody chose. The reset button is
/// the way back, and this drives the real window to prove it works end to end.
/// </summary>
[Collection("WPF")]
public class ResetTuningTests
{
    // Verbatim from the developer's own settings.json: values frozen since before v0.12.1, which
    // no migration can safely touch (they are indistinguishable from a deliberate choice).
    private const string StaleSettings = """
    {
      "SensitivityPercent": 60,
      "LiveSpeedPercent": 55,
      "MinFragmentLetters": 2,
      "StabilityPercent": 49,
      "OcrFilterMode": "off",
      "CaptureBackend": "wgc",
      "SettingsVersion": 3
    }
    """;

    [Fact]
    public void The_reset_button_puts_the_reading_settings_back_on_screen_and_on_disk()
    {
        using var settings = new TempSettings(StaleSettings);
        var expected = new AppSettings();

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            // The stale values are what the window came up with…
            Assert.Equal(60, (int)window.SensitivitySlider.Value);
            Assert.Equal(55, (int)window.LiveSpeedSlider.Value);

            window.ApplyRecommendedTuning();

            // …and the recommended ones are what it shows now.
            Assert.Equal(expected.SensitivityPercent, (int)window.SensitivitySlider.Value);
            Assert.Equal(expected.LiveSpeedPercent, (int)window.LiveSpeedSlider.Value);
            Assert.Equal(expected.MinFragmentLetters, (int)window.MinFragmentSlider.Value);
            Assert.Equal(expected.StabilityPercent, (int)window.StabilitySlider.Value);
            Assert.Equal("contrast", (window.OcrFilterCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        });

        // The point of the button: it must SURVIVE the restart. (And writing the sliders back into
        // the controls fires their change handlers — the guard has to hold, or this file would come
        // back half-applied.)
        using var saved = JsonDocument.Parse(File.ReadAllText(settings.Path));
        var root = saved.RootElement;
        Assert.Equal(expected.SensitivityPercent, root.GetProperty("SensitivityPercent").GetInt32());
        Assert.Equal(expected.LiveSpeedPercent, root.GetProperty("LiveSpeedPercent").GetInt32());
        Assert.Equal(expected.StabilityPercent, root.GetProperty("StabilityPercent").GetInt32());
        Assert.Equal("contrast", root.GetProperty("OcrFilterMode").GetString());

        // The capture method is a compatibility choice, not a preference: someone on "Windows
        // Graphics" picked it because GDI gave them a black screen. Resetting it would break them.
        Assert.Equal("wgc", root.GetProperty("CaptureBackend").GetString());
    }
}

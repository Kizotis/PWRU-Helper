using System.IO;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>User preferences that survive between runs. Plain data, JSON-serialised.</summary>
public class AppSettings
{
    // Translator + OCR choices
    public int SensitivityPercent { get; set; } = 60;
    public int LiveSpeedPercent { get; set; } = 55;
    public string OcrTargetLang { get; set; } = "en";
    public string TranslatorFrom { get; set; } = "en";
    public string TranslatorTo { get; set; } = "ru";

    // Window / behaviour
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoCopyTranslation { get; set; } = true;
    public int LastTab { get; set; }

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    // Compact overlay placement
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double? OverlayWidth { get; set; }
    public double? OverlayHeight { get; set; }

    // Last live-translation area (physical px): [x, y, w, h], or null if never used.
    public int[]? LastLiveRegion { get; set; }

    public bool FirstRunDone { get; set; }
}

/// <summary>Loads/saves <see cref="AppSettings"/> to %AppData%\PWRUHelper\settings.json.
/// Fully best-effort: any failure just yields defaults / is ignored, never throws.</summary>
public static class SettingsService
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PWRUHelper", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch { /* corrupt/unreadable — fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(settings, Options));
        }
        catch { /* not writable — preferences just won't persist this time */ }
    }
}

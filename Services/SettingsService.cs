using System.IO;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>User preferences that survive between runs. Plain data, JSON-serialised.</summary>
public class AppSettings
{
    // Translator + OCR choices.
    // Defaults are tuned for chat OCR out of the box: a very low sensitivity (5%) ignores
    // camera/background movement behind the chat, and ~1.0s between reads (LiveSpeed 80%)
    // keeps the feed responsive. These only apply on first launch — a saved settings.json
    // keeps whatever the user picked.
    public int SensitivityPercent { get; set; } = 5;
    public int LiveSpeedPercent { get; set; } = 80;   // → CurrentLiveIntervalMs() ≈ 1.0s

    // Fine OCR tuning (live mode). MinFragmentLetters = the smallest text fragment (in
    // letters) worth translating; StabilityPercent = how strictly a newly-appeared line
    // must persist across a frame before it's treated as a real message (→ ~0.72 by default).
    public int MinFragmentLetters { get; set; } = 2;
    public int StabilityPercent { get; set; } = 49;   // → StabilityThreshold() ≈ 0.72
    public string OcrTargetLang { get; set; } = "en";
    public string TranslatorFrom { get; set; } = "en";
    public string TranslatorTo { get; set; } = "ru";

    // The user's own language, used when replying (quick reply always targets Russian).
    // Tracked separately so the translator's auto-flip-to-Russian can't corrupt it.
    public string MyLanguage { get; set; } = "en";

    // Optional DeepL API key. Empty = use the free Google endpoint (the default). When set, the
    // app translates via DeepL and falls back to Google if DeepL fails. Stored in the local
    // settings file like every other preference.
    public string DeepLApiKey { get; set; } = "";

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

    // Phrasebook: pinned favourites and recently-copied phrases (by Russian text).
    public List<string> Favourites { get; set; } = new();
    public List<string> Recents { get; set; } = new();

    public double FontScale { get; set; } = 1.0;
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
                if (s != null) return Sanitize(s);
            }
        }
        catch { /* corrupt/unreadable — fall back to defaults */ }
        return new AppSettings();
    }

    // A hand-edited or partly-written file can contain nulls in place of the default
    // collections/strings (nullable ref types are compile-time only). Replace them so
    // the rest of the app can assume they're never null and never crashes at startup.
    private static AppSettings Sanitize(AppSettings s)
    {
        s.Favourites ??= new();
        s.Recents ??= new();
        s.Favourites.RemoveAll(string.IsNullOrEmpty);
        s.Recents.RemoveAll(string.IsNullOrEmpty);
        s.OcrTargetLang ??= "en";
        s.TranslatorFrom ??= "en";
        s.TranslatorTo ??= "ru";
        s.MyLanguage ??= "en";
        s.DeepLApiKey ??= "";
        if (s.LastLiveRegion is { Length: not 4 }) s.LastLiveRegion = null;
        return s;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            // Write to a temp file first, then swap it in, so a crash mid-write can never
            // leave a truncated settings.json (which would wipe favourites/last area).
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            if (File.Exists(Path_)) File.Replace(tmp, Path_, null);
            else File.Move(tmp, Path_);
        }
        catch { /* not writable — preferences just won't persist this time */ }
    }
}

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
    // must persist across a frame before it's treated as a real message (→ ~0.77 by default).
    public int MinFragmentLetters { get; set; } = 2;
    public int StabilityPercent { get; set; } = 60;   // → StabilityThreshold() ≈ 0.77
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

    // Optional pre-OCR background filter (helps read chat over a busy 3D scene).
    // "contrast" (default — brightness boost, any colour) · "off" · "color" (keep one chat colour).
    public string OcrFilterMode { get; set; } = "contrast";
    public string OcrKeepColorHex { get; set; } = "#FFFFFF";   // target colour for "color" mode
    public int OcrColorTolerance { get; set; } = 70;           // RGB distance kept around the target

    // Screen-capture backend: "gdi" (default) or "wgc" (experimental Windows.Graphics.Capture,
    // for full-screen games where GDI returns black). WGC falls back to GDI on any failure.
    public string CaptureBackend { get; set; } = "gdi";

    // Squad builder: write the assembled LFM message in capital letters (off by default).
    public bool SquadUppercase { get; set; }

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

    // Bumped when we want a one-time upgrade of an already-saved settings file (e.g. change a
    // default for existing users). Defaults to 0 so a file written before this field existed
    // — which has no such key — deserialises as 0 and gets migrated. A fresh install is stamped
    // with the current version in Load(), so migrations never touch it. See SettingsService.Migrate.
    public int SettingsVersion { get; set; }
}

/// <summary>Loads/saves <see cref="AppSettings"/> to %AppData%\PWRUHelper\settings.json.
/// Fully best-effort: any failure just yields defaults / is ignored, never throws.</summary>
public static class SettingsService
{
    private static readonly string DefaultPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PWRUHelper", "settings.json");

    /// <summary>Where settings are read from / written to. Tests point this at a temp file: they
    /// construct a real MainWindow, which loads AND saves settings, and must never touch (or
    /// corrupt) the developer's own %AppData% file — which is exactly how the "filter resets to
    /// Off" bug was reproduced.</summary>
    internal static string? PathOverride;

    private static string Path_ => PathOverride ?? DefaultPath;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Latest settings-schema version. Bump when adding a <see cref="Migrate"/> step.</summary>
    private const int CurrentSettingsVersion = 2;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null)
                {
                    s = Sanitize(s);
                    // One-time upgrades for an existing file. Persist right away so they never
                    // re-run (and so a later deliberate change by the user isn't reverted).
                    if (Migrate(s)) Save(s);
                    return s;
                }
            }
        }
        catch { /* corrupt/unreadable — fall back to defaults */ }
        // Fresh install: already has the current defaults; stamp it so migrations skip it.
        return new AppSettings { SettingsVersion = CurrentSettingsVersion };
    }

    /// <summary>Apply one-time upgrades to an already-saved settings file. Returns true if it
    /// changed anything (so the caller persists it). Each step is guarded by the stored version.</summary>
    internal static bool Migrate(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentSettingsVersion) return false;

        // v1 (v0.12.2): the background filter default became "boost contrast". Bring existing
        // users who were still on the old "off" default onto it too; leave a deliberate "color"
        // (or an already-chosen "contrast") alone.
        if (s.SettingsVersion < 1 && s.OcrFilterMode == "off")
            s.OcrFilterMode = "contrast";

        // v2 (v0.12.3): the v1 migration above never actually stuck — starting the app wrote
        // "off" straight back over it (a XAML-load ValueChanged persisted the not-yet-restored
        // filter combo; see MainWindow._restoringSettings). So EVERY user is sitting on "off",
        // whether they chose it or not. Now that the clobber is fixed, apply the intended
        // default once more; from here on, a deliberate "off" survives a restart.
        if (s.SettingsVersion < 2 && s.OcrFilterMode == "off")
            s.OcrFilterMode = "contrast";

        s.SettingsVersion = CurrentSettingsVersion;
        return true;
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
        s.OcrFilterMode = s.OcrFilterMode is "contrast" or "color" ? s.OcrFilterMode : "off";
        s.OcrKeepColorHex ??= "#FFFFFF";
        s.OcrColorTolerance = Math.Clamp(s.OcrColorTolerance, 0, 441);
        s.CaptureBackend = s.CaptureBackend == "wgc" ? "wgc" : "gdi";
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

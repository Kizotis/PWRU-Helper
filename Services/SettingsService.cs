using System.IO;
using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>User preferences that survive between runs. Plain data, JSON-serialised.</summary>
public class AppSettings
{
    // Translator + OCR choices.
    // Defaults are tuned for chat OCR out of the box: a very low sensitivity (5%) ignores
    // camera/background movement behind the chat, and ~0.7s between reads (LiveSpeed 92%)
    // keeps up with a message that scrolls past quickly. These only apply on first launch — a
    // saved settings.json keeps whatever the user picked (the ↺ button on the Screen OCR tab
    // brings these values back).
    public int SensitivityPercent { get; set; } = 5;
    public int LiveSpeedPercent { get; set; } = 92;   // → MainWindow.LiveIntervalMs() = 0.7s

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

    // Optional Azure Translator credentials (architecture §12, ruling R-7 owns both names). They are
    // a PAIR: the region travels in a header of every request, so a key without one is a guaranteed
    // 401 — TranslationChains.BuildWrite adds the Azure tier only when neither is empty, and the
    // About tab refuses the half-entered pair at the Save button. Empty = no Azure, which is the
    // default and the only state a fresh install can be in.
    public string AzureApiKey { get; set; } = "";
    public string AzureRegion { get; set; } = "";

    // Opt-in: use the key for READING the screen too (the LIVE loop and read-once), not just for
    // what the user writes. FALSE by default and it must stay false (R9 / ruling R-15) — a metered
    // key behind an unmetered loop is the money consequence I8 keeps DeepL away from, and Azure is
    // allowed there only because the user asked for it. E6.S4 owns the check box and the read tier.
    // Written as a bare bool like SquadUppercase above: `= false` is the same value, spelled twice.
    public bool UseKeyForReading { get; set; }

    // Whether the offline engine may answer when everything else is paused. FALSE by default, and
    // there is deliberately NO check box: ruling R-4 makes the About tab's Download / Remove actions
    // (E8.S3) its only writers. It lands here so the chain builders have something to read before
    // E8 exists, and so E8 does not have to touch this type.
    public bool OfflineFallbackEnabled { get; set; }

    // Diagnostic hatch for the six provider-gate numbers (E2.S7): a JSON object of GatePolicy
    // fields. null — the default — means "nothing said", i.e. the graded table. No UI: whatever
    // reaches it was typed by hand, which is why Sanitize deliberately leaves it alone and
    // GatePolicy.Parse owns the whole rule ("unparseable ⇒ defaults, silently, never a throw").
    public string? ProviderGateOverrides { get; set; }

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
    private const int CurrentSettingsVersion = 3;

    /// <summary>The live-speed default before v0.13.0 (≈1.0s between reads). A saved file still
    /// holding exactly this was never touched by its owner, so the new default may replace it.</summary>
    private const int PreviousLiveSpeedDefault = 80;

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

        // v2 (v0.13.0): the v1 migration above never actually stuck — starting the app wrote
        // "off" straight back over it (a XAML-load ValueChanged persisted the not-yet-restored
        // filter combo; see MainWindow._restoringSettings). So EVERY user is sitting on "off",
        // whether they chose it or not. Now that the clobber is fixed, apply the intended
        // default once more; from here on, a deliberate "off" survives a restart.
        if (s.SettingsVersion < 2 && s.OcrFilterMode == "off")
            s.OcrFilterMode = "contrast";

        // v3 (v0.13.0): live reads go from ~1.0s to ~0.7s, so a message that scrolls past quickly
        // still gets the two frames it needs to be confirmed. Only for someone who never moved the
        // slider — a file holding EXACTLY the old default is one nobody chose. A deliberate speed
        // (anything else, including a slower one) is left alone.
        if (s.SettingsVersion < 3 && s.LiveSpeedPercent == PreviousLiveSpeedDefault)
            s.LiveSpeedPercent = new AppSettings().LiveSpeedPercent;

        // No v4, and that is a decision rather than an omission — the one place a future reader
        // would otherwise assume a mistake. E6 added five settings (AzureApiKey, AzureRegion,
        // UseKeyForReading, OfflineFallbackEnabled, ProviderGateOverrides) and NONE of them needs a
        // step: every one is NEW, so an old file that has no such key deserialises it to its
        // initializer — which is exactly the value a migration would have written. A step is owed
        // only when a DEFAULT CHANGES for somebody who already has a file (the three above), and a
        // version bump for new fields would run a no-op against every user's settings once, for
        // nothing. (architecture §12, ruling OQ-e; asserted by AzureSettingsTests' TP-SET-02.)
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
        // The Azure pair. The key is opaque, so it is only null-guarded and trimmed; the region is
        // a lower-case vendor slug ("westeurope"), so it is lower-cased here — ToLowerInvariant and
        // not ToLower, like the line below it, because a Turkish locale turns "I" into "ı" and a
        // region is not the user's language. A value carrying a CONTROL character is emptied rather
        // than kept: both travel in an HTTP header, which may not carry one, so a hand-edited file
        // holding one describes a credential that can only ever throw. The Save button refuses the
        // same value (E6.S2's review), and an empty field is the state the UI can explain.
        s.AzureApiKey = SanitizeCredential(s.AzureApiKey);
        s.AzureRegion = SanitizeCredential(s.AzureRegion).ToLowerInvariant();
        // ProviderGateOverrides is deliberately NOT sanitised: null is its valid default, and
        // GatePolicy.Parse owns "unparseable ⇒ defaults" for every other value.
        s.OcrFilterMode = s.OcrFilterMode is "contrast" or "color" ? s.OcrFilterMode : "off";
        s.OcrKeepColorHex ??= "#FFFFFF";
        s.OcrColorTolerance = Math.Clamp(s.OcrColorTolerance, 0, 441);
        s.CaptureBackend = s.CaptureBackend == "wgc" ? "wgc" : "gdi";
        if (s.LastLiveRegion is { Length: not 4 }) s.LastLiveRegion = null;
        return s;
    }

    /// <summary>A credential as the app is willing to hold it: never null, trimmed, and empty if it
    /// carries anything an HTTP header may not (<see cref="AzureTranslator.HasControlChar"/> — the
    /// one spelling of that rule, next to the provider that would have had to send it).</summary>
    private static string SanitizeCredential(string? value)
    {
        var v = (value ?? "").Trim();
        return AzureTranslator.HasControlChar(v) ? "" : v;
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

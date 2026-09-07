using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E6.S3 — the settings surface, which <c>plan-migration.md</c> increment 5 calls "the code path
/// with the most expensive prior bugs in this repo (two migrations exist solely to undo one)".
///
/// <para>Three things are asserted here and each of them is a shipped bug or one line away from
/// one: the five new fields survive a round trip and reach an OLD file at their defaults with
/// <b>no</b> migration (I13); the About tab refuses a half-entered credential <i>before</i> a
/// request is spent earning an invisible <c>AuthFailed</c> (AC 5, E6.S2's review); and every new
/// change handler bails on <c>_restoringSettings</c> (I12 — the v0.12.3 clobber, whose end-to-end
/// case is <c>StartupSettingsTests</c>' and is the one that matters).</para>
///
/// <para><c>[Collection("WPF")]</c>: half the cases construct a real <see cref="MainWindow"/>, and
/// all of them point <see cref="SettingsService"/> at a temp file — one STA thread, one static
/// path override. Nothing here names <c>ProviderGates</c>: the key save reaches the registry
/// through <c>TranslationChains.OnKeySaved</c>, and the gate half of AC 6 is asserted where that
/// facade lives, in <c>ChainCompositionTests</c> (the "Gates" collection).</para>
/// </summary>
[Collection("WPF")]
public class AzureSettingsTests
{
    private const string RealLookingKey = "0123456789abcdef0123456789abcdef";

    // =============================================================================================
    //  TP-SET-01..04, 11 — the fields themselves
    // =============================================================================================

    /// <summary>TP-SET-01. All five survive a save and a load through the real serialiser.</summary>
    [Fact]
    public void TP_SET_01_The_five_new_fields_survive_a_save_and_a_load()
    {
        using var temp = new TempSettings("{}");

        SettingsService.Save(new AppSettings
        {
            AzureApiKey = RealLookingKey,
            AzureRegion = "francecentral",
            UseKeyForReading = true,
            OfflineFallbackEnabled = true,
            ProviderGateOverrides = """{"OpenBaseSeconds":2}""",
        });

        var back = SettingsService.Load();
        Assert.Equal(RealLookingKey, back.AzureApiKey);
        Assert.Equal("francecentral", back.AzureRegion);
        Assert.True(back.UseKeyForReading);
        Assert.True(back.OfflineFallbackEnabled);
        Assert.Equal("""{"OpenBaseSeconds":2}""", back.ProviderGateOverrides);
    }

    /// <summary>
    /// TP-SET-02 / AC 2 — <b>the counter-intuitive one.</b> Five NEW fields need no
    /// <c>Migrate</c> step and no <c>SettingsVersion</c> bump: an old file that has no such key
    /// deserialises each of them to its initializer, which is exactly the value a migration would
    /// have written. I13 owes a step when a DEFAULT CHANGES for someone who already has a file —
    /// and none does here. So the stored version must come back UNCHANGED.
    /// </summary>
    [Fact]
    public void TP_SET_02_An_old_file_gains_the_new_fields_at_their_defaults_and_keeps_its_version()
    {
        using var temp = new TempSettings("""
        {
          "OcrFilterMode": "contrast",
          "DeepLApiKey": "abc-123:fx",
          "SettingsVersion": 3
        }
        """);

        var loaded = SettingsService.Load();

        Assert.Equal("", loaded.AzureApiKey);
        Assert.Equal("", loaded.AzureRegion);
        Assert.False(loaded.UseKeyForReading);
        Assert.False(loaded.OfflineFallbackEnabled);
        Assert.Null(loaded.ProviderGateOverrides);

        // …and nothing ran against the file: same version, and the user's own values intact.
        Assert.Equal(3, loaded.SettingsVersion);
        Assert.Equal("abc-123:fx", loaded.DeepLApiKey);
        Assert.Equal("contrast", loaded.OcrFilterMode);
    }

    /// <summary>TP-SET-03. A hand-edited or partly-written file can hold nulls where a string is
    /// declared (nullable reference types are compile-time only), and a region pasted from a portal
    /// arrives with spaces and capitals.</summary>
    [Fact]
    public void TP_SET_03_Sanitize_null_guards_the_credentials_and_trims_and_lower_cases_the_region()
    {
        using var temp = new TempSettings("""
        { "AzureApiKey": null, "AzureRegion": null, "SettingsVersion": 3 }
        """);
        var nulls = SettingsService.Load();
        Assert.Equal("", nulls.AzureApiKey);
        Assert.Equal("", nulls.AzureRegion);

        File.WriteAllText(temp.Path, """
        { "AzureApiKey": "  KEY  ", "AzureRegion": "  WestEurope  ", "SettingsVersion": 3 }
        """);
        var trimmed = SettingsService.Load();
        Assert.Equal("KEY", trimmed.AzureApiKey);           // the key is opaque: trimmed, not cased
        Assert.Equal("westeurope", trimmed.AzureRegion);    // the region is a lower-case vendor slug
    }

    /// <summary>
    /// TP-SET-03, the half <c>Trim</c> cannot reach. Both values travel in an HTTP header, which may
    /// not carry a control character, so a hand-edited file holding one is emptied rather than kept:
    /// the About tab refuses the same value at Save (AC 5), and an empty field is the state the UI
    /// can explain. Keeping it would put a tier in the write chain that can only ever throw.
    /// </summary>
    [Fact]
    public void TP_SET_03_A_credential_carrying_a_control_character_is_emptied_on_load()
    {
        using var temp = new TempSettings("""
        { "AzureApiKey": "abc\u0007def", "AzureRegion": "west\neurope", "SettingsVersion": 3 }
        """);

        File.WriteAllText(temp.Path, File.ReadAllText(temp.Path)
            .Replace("\"SettingsVersion\": 3", "\"DeepLApiKey\": \"abc-123:fx\", \"SettingsVersion\": 3"));

        var loaded = SettingsService.Load();
        Assert.Equal("", loaded.AzureApiKey);
        Assert.Equal("", loaded.AzureRegion);

        // Non-vacuity: the file really WAS read. A malformed one falls back to defaults, which
        // carry two empty credentials AND a version 3 — so without this line the case would pass
        // just as happily over a file Sanitize never saw.
        Assert.Equal("abc-123:fx", loaded.DeepLApiKey);
    }

    /// <summary>TP-SET-04 / R9 / ruling R-15 — a fresh install has no key, and above all no opt-in:
    /// a metered key behind the unmetered LIVE loop has to be asked for.</summary>
    [Fact]
    public void TP_SET_04_A_fresh_install_has_no_key_no_region_and_no_opt_in()
    {
        var fresh = new AppSettings();
        Assert.Equal("", fresh.AzureApiKey);
        Assert.Equal("", fresh.AzureRegion);
        Assert.False(fresh.UseKeyForReading);
        Assert.False(fresh.OfflineFallbackEnabled);
        Assert.Null(fresh.ProviderGateOverrides);
    }

    /// <summary>TP-SET-11 / IS-12. <c>ProviderGateOverrides</c> is a field hatch with no UI, so
    /// whatever reaches it was typed by hand. Unparseable ⇒ the graded defaults, silently, and
    /// never a throw — a diagnostic hatch that crashes the app on a typo is worse than no hatch.
    /// It is also the one new field <c>Sanitize</c> deliberately leaves alone: null is its valid
    /// value and the parser owns the rest.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"OpenBaseSeconds\":")]
    [InlineData("{\"OpenBaseSeconds\":\"soon\"}")]
    public void TP_SET_11_An_unparseable_gate_override_is_ignored_and_never_throws(string? json)
    {
        var s = new AppSettings { ProviderGateOverrides = json };
        Assert.Equal(GatePolicy.Default, GatePolicy.Parse(s.ProviderGateOverrides));
    }

    // =============================================================================================
    //  AC 5 — the pair validation, at the seam
    // =============================================================================================

    /// <summary>
    /// AC 5. A key without a region is a guaranteed 401, and E6.S2's guard throws
    /// <c>AuthFailed</c> <b>without</b> <c>NotSent</c> — so a half-entered credential would be
    /// counted by the chain as a tier that tried and failed, and its sentence outranks every
    /// skipped tier. The pair is therefore refused before anything is built or sent. Both empty is
    /// not an error: it is clearing the key, and it must stay allowed.
    /// </summary>
    [Theory]
    [InlineData("", "", false)]                              // clearing — allowed
    [InlineData(RealLookingKey, "westeurope", false)]        // complete — allowed
    [InlineData(RealLookingKey, "", true)]                   // key, no region
    [InlineData("", "westeurope", true)]                     // region, no key
    [InlineData("abc\u0007def", "westeurope", true)]         // unsendable key
    [InlineData(RealLookingKey, "west\u0001europe", true)]   // unsendable region
    public void AC5_Only_a_complete_or_an_empty_pair_is_accepted(string key, string region, bool refused)
    {
        var problem = TranslationChains.AzureCredentialProblem(key, region);

        Assert.Equal(refused, problem != null);
        if (refused)
        {
            Assert.False(string.IsNullOrWhiteSpace(problem));
            // I11: a refusal names the field, never the value.
            if (key.Length > 0) Assert.DoesNotContain(key, problem!, StringComparison.Ordinal);
        }
    }

    // =============================================================================================
    //  AC 3 / I12 — the guard, and the explicit side effect
    // =============================================================================================

    /// <summary>
    /// TP-SET-06. XAML <i>loading</i> raises <c>SelectionChanged</c> during
    /// <c>InitializeComponent()</c>, long before <c>ApplySettings</c> runs, so a handler that
    /// writes settings there persists an unrestored control. That is the v0.12.3 bug, and
    /// <c>SettingsService.Migrate</c>'s v2 step exists solely because v1 lost that race. The guard
    /// is asserted as the handler's FIRST statement, because a guard three lines down has already
    /// read the control.
    /// </summary>
    [Fact]
    public void TP_SET_06_Every_new_change_handler_bails_while_settings_are_being_restored()
    {
        var body = Body(Code(File.ReadAllText(RepoFile("MainWindow.Translate.cs"))),
                        "private void AzureRegionCombo_Changed(");

        var first = body.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && l != "{");
        Assert.Equal("if (_restoringSettings) return;", first);
    }

    /// <summary>
    /// TP-SET-07 / AC 3. <c>ApplySettings</c> suppresses every change handler for the whole
    /// restore, so the UI side effects those handlers would have produced have to be applied
    /// EXPLICITLY — which is what <c>UpdateOcrFilterUi</c> is doing there today and what
    /// <c>UpdateEngineStatusUi</c> now does beside it.
    /// </summary>
    [Fact]
    public void TP_SET_07_ApplySettings_applies_the_engine_status_side_effect_explicitly()
    {
        var body = Code(Body(File.ReadAllText(RepoFile("MainWindow.xaml.cs")),
                             "private void ApplySettings()"));

        Assert.Contains("UpdateEngineStatusUi();", body, StringComparison.Ordinal);
        Assert.Contains("UpdateOcrFilterUi(", body, StringComparison.Ordinal);

        // …and inside the try whose finally clears the flag, not after it.
        Assert.True(body.IndexOf("UpdateEngineStatusUi();", StringComparison.Ordinal)
                  < body.IndexOf("finally", StringComparison.Ordinal),
                  "the explicit side effect must run while the restore guard is still up");
    }

    /// <summary>
    /// AC 3's restore half, at the control. The region combo is <c>IsEditable="True"</c>, so a
    /// region outside the nine seeded ones matches no <c>ComboBoxItem</c> — and <c>SelectTag</c>
    /// silently does nothing when no item matches. Without the <c>.Text</c> fallback the box comes
    /// back blank on every launch while <c>settings.json</c> still holds the region.
    /// </summary>
    [Fact]
    public void A_free_text_region_is_restored_into_the_editable_combo()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "norwayeast", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            Assert.Equal(RealLookingKey, window.AzureKeyBox.Password);
            Assert.Null(window.AzureRegionCombo.SelectedItem);          // not one of the nine
            Assert.Equal("norwayeast", window.AzureRegionCombo.Text);   // …and still shown

            // …and the box can actually be typed into. A ComboBox looks PART_EditableTextBox up BY
            // NAME and, not finding one, renders a control that silently refuses the keyboard —
            // which is what the app's dark ComboBox template did until E6.S3 added the part.
            window.AzureRegionCombo.ApplyTemplate();
            Assert.NotNull(window.AzureRegionCombo.Template.FindName(
                "PART_EditableTextBox", window.AzureRegionCombo));
        });
    }

    /// <summary>One of the nine takes the seeded item, which is what <c>SelectTag</c> is for.</summary>
    [Fact]
    public void A_seeded_region_is_restored_as_a_selected_item()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "francecentral", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.Equal("francecentral",
                (window.AzureRegionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        });
    }

    // =============================================================================================
    //  AC 5 / AC 6 at the button
    // =============================================================================================

    /// <summary>
    /// AC 5 end to end: Save with a key and no region writes the sentence to the status line,
    /// persists NOTHING and rebuilds NOTHING — no request can be spent earning an
    /// <c>AuthFailed</c> whose cause the player cannot see.
    /// </summary>
    [Fact]
    public void AC5_Saving_a_key_without_a_region_persists_nothing_and_rebuilds_nothing()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var before = WriteChainOf(window);

            window.AzureKeyBox.Password = RealLookingKey;
            Save(window);

            Assert.Equal(UserMessages.AzureNeedsARegion(), window.AzureStatus.Text);
            Assert.Same(before, WriteChainOf(window));
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.False(saved.RootElement.TryGetProperty("AzureApiKey", out var stored)
                     && stored.GetString()!.Length > 0);
    }

    /// <summary>AC 5's other pole: a complete pair is saved, lower-cased, and the chain is rebuilt
    /// so the key takes effect on the very next translation.</summary>
    [Fact]
    public void AC5_A_complete_pair_is_saved_lower_cased_and_takes_effect_at_once()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var before = WriteChainOf(window);

            window.AzureKeyBox.Password = "  " + RealLookingKey + "  ";
            window.AzureRegionCombo.Text = "  FranceCentral  ";
            Save(window);

            Assert.NotSame(before, WriteChainOf(window));
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal(RealLookingKey, saved.RootElement.GetProperty("AzureApiKey").GetString());
        Assert.Equal("francecentral", saved.RootElement.GetProperty("AzureRegion").GetString());
    }

    /// <summary>Both empty is not an error — it is clearing the key, and <c>ux</c> §3.7 gives it a
    /// row of its own. It must persist the empties and rebuild, or the cleared key keeps
    /// translating until the next restart.</summary>
    [Fact]
    public void Clearing_both_halves_is_allowed_and_is_not_an_error()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westeurope", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            window.AzureKeyBox.Password = "";
            window.AzureRegionCombo.SelectedItem = null;
            window.AzureRegionCombo.Text = "";
            Save(window);

            Assert.Equal(UserMessages.AzureNoKeyStatus(), window.AzureStatus.Text);
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal("", saved.RootElement.GetProperty("AzureApiKey").GetString());
        Assert.Equal("", saved.RootElement.GetProperty("AzureRegion").GetString());
    }

    // =============================================================================================
    //  AC 7 — I11
    // =============================================================================================

    /// <summary>
    /// AC 7 / I11. The error report is the recent LOG and nothing else — it may never be composed
    /// from <c>_settings</c> or read a key box. The other half of I11 is already pinned where the
    /// key could actually leak: <c>RequestLogTests</c> asserts no key reaches a log line, so a
    /// report built only from the log carries none either.
    /// </summary>
    [Fact]
    public void AC7_The_error_report_is_the_log_and_never_a_key()
    {
        var body = Code(Body(File.ReadAllText(RepoFile("MainWindow.xaml.cs")),
                             "private async void CopyErrorReport_Click("));

        Assert.Contains("Logging.ReadRecent()", body, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "_settings", "ApiKey", "KeyBox", "Password", "AzureRegion" })
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static void Save(MainWindow window) =>
        typeof(MainWindow)
            .GetMethod("AzureSaveKey_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { window, null });

    private static object WriteChainOf(MainWindow window) =>
        typeof(MainWindow)
            .GetField("_writeTranslator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved — fix the scan, not the guard");

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"'{signature}' has an unbalanced body");
        return "";
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root");
        var path = Path.Combine(dir!.FullName, relative);
        Assert.True(File.Exists(path), $"expected {relative} at the repo root");
        return path;
    }
}

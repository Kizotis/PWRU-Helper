using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// <para><c>[Collection("Gates")]</c>: half the cases construct a real <see cref="MainWindow"/>,
/// and all of them point <see cref="SettingsService"/> at a temp file — one STA thread, one static
/// path override. It is the same collection the registry cases use, and deliberately so
/// (E7.S8): a real <c>MainWindow</c> reaches <c>ProviderGates</c> through
/// <c>TranslationChains</c> whether or not this file names it, so the STA classes and the gate
/// classes have to be serialised against each other — see <c>GatesCollection</c>.</para>
/// </summary>
[Collection("Gates")]
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
    /// skipped tier. That pair is therefore refused before anything is built or sent.
    ///
    /// <para><b>An empty KEY is the other direction and is not an error at all</b> since ruling
    /// <b>E6-e</b>: it is the gesture that removes Azure, and it clears the region with it. E6.S3
    /// refused an empty key over a leftover region and told the user to empty the region box too;
    /// its own review recorded that as a dead end and referred the AC change upward. One box
    /// cleared, one engine gone.</para>
    /// </summary>
    [Theory]
    [InlineData("", "", false)]                              // clearing — allowed
    [InlineData(RealLookingKey, "westeurope", false)]        // complete — allowed
    [InlineData(RealLookingKey, "", true)]                   // key, no region
    [InlineData("", "westeurope", false)]                    // E6-e: clearing, region and all
    [InlineData("abc\u0007def", "westeurope", true)]         // unsendable key
    [InlineData(RealLookingKey, "west\u0001europe", true)]   // unsendable region
    // …and the ORDER of the two rules, added at review of E6.S4. An empty key over a region the
    // user pasted a line break into is still the clearing gesture: refusing it would leave them
    // unable to REMOVE Azure until they had tidied a field that is about to be blanked anyway — a
    // refusal with no exit (R-01 in miniature). AzureSaveKey_Click clears the region with the key,
    // so nothing unsendable is persisted by letting this through.
    [InlineData("", "westeurope", false)]              // clearing wins over "unsendable"
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
        var source = Code(File.ReadAllText(RepoFile("Views/MainWindow.Translate.cs")));

        // Both of E6's persisted controls, and E6.S4's is the one the rule was written for: a
        // CheckBox raises Checked during InitializeComponent() exactly as the combo raises
        // SelectionChanged, and `IsChecked="False"` in the XAML would be a false the handler
        // persisted over a saved true.
        foreach (var handler in new[] { "AzureRegionCombo_Changed", "AzureForReading_Changed" })
        {
            var body = Body(source, $"private void {handler}(");
            var first = body.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && l != "{");
            Assert.Equal("if (_restoringSettings) return;", first);
        }
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
        var body = Code(Body(File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")),
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

    /// <summary>
    /// The <c>Theme.xaml</c> deviation, at BOTH poles — the condition on which it was accepted.
    /// The dark <c>ComboBox</c> template gained a <c>PART_EditableTextBox</c> because WPF looks
    /// that name up and, not finding it, renders an editable combo that silently refuses the
    /// keyboard. It is <c>Collapsed</c> by default and swapped with the selection presenter by an
    /// <c>IsEditable</c> trigger, so <b>every existing combo must render exactly as it did</b>:
    /// part collapsed, presenter visible. The editable one is the mirror image, and its text box
    /// has to be reachable by the keyboard and writable or the free-text region is dead on arrival
    /// while every assert on <c>SelectedItem</c> still passes.
    /// </summary>
    [Fact]
    public void The_editable_text_part_is_visible_on_the_editable_combo_and_collapsed_on_every_other()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            foreach (var plain in new[] { window.FromCombo, window.ToCombo, window.OcrTargetCombo,
                                          window.OcrFilterCombo, window.CaptureBackendCombo })
            {
                Assert.False(plain.IsEditable);
                Assert.Equal(Visibility.Collapsed, Part<TextBox>(plain, "PART_EditableTextBox").Visibility);
                Assert.Equal(Visibility.Visible, Part<ContentPresenter>(plain, "selection").Visibility);
            }

            var combo = window.AzureRegionCombo;
            Assert.True(combo.IsEditable);
            var box = Part<TextBox>(combo, "PART_EditableTextBox");
            Assert.Equal(Visibility.Visible, box.Visibility);
            Assert.Equal(Visibility.Collapsed, Part<ContentPresenter>(combo, "selection").Visibility);
            Assert.True(box.Focusable);       // the caret can get there…
            Assert.False(box.IsReadOnly);     // …and what is typed stays
        });
    }

    /// <summary>
    /// The nastiest shape of the free-text case, and the reason the restore is asserted twice: a
    /// stored region that is a strict PREFIX of a seeded one. <c>westus</c> is a real Azure region
    /// and <c>westus2</c> is one of the nine, so a restore that let the combo's text search finish
    /// the word would hand the next Save a region the user never stored — silently repointing a
    /// working key at the wrong endpoint, with the About tab showing the substitution as if it
    /// were the saved value.
    /// </summary>
    [Fact]
    public void A_region_that_prefixes_a_seeded_one_is_restored_as_typed_not_completed()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westus", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            Assert.Null(window.AzureRegionCombo.SelectedItem);      // NOT westus2
            Assert.Equal("westus", window.AzureRegionCombo.Text);
            Assert.Equal(UserMessages.AzureKeySetStatus("westus"), window.AzureStatus.Text);
        });

        // …and nothing wrote the completed value back over the stored one.
        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal("westus", saved.RootElement.GetProperty("AzureRegion").GetString());
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
            var before = Chains(window);

            window.AzureKeyBox.Password = RealLookingKey;
            Save(window);

            // E7.S7 split the two: the refusal is transient (the feedback line) and the standing
            // state — what the app will do with the key it has — keeps the status line above it.
            Assert.Equal(UserMessages.AzureNeedsARegion(), window.AzureFeedback.Text);
            Assert.Equal(UserMessages.AzureNoKeyStatus(), window.AzureStatus.Text);
            // NOTHING was rebuilt — all four references are the ones the constructor made.
            var after = Chains(window);
            for (var i = 0; i < before.Length; i++) Assert.Same(before[i], after[i]);
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.False(saved.RootElement.TryGetProperty("AzureApiKey", out var stored)
                     && stored.GetString()!.Length > 0);
    }

    /// <summary>
    /// AC 5's other pole, and AC 6's "rebuilds <b>both</b> chains" where it is actually owed — at
    /// the handler. A complete pair is saved lower-cased, and all four references are new: the
    /// write chain (where an Azure key is used today), the two read translators and
    /// <c>_readChain</c> (because <c>UseKeyForReading</c> may already be on from a previous
    /// session, and because a rebuild that forgot <c>_readChain</c> would leave the LIVE pause
    /// check answering for a chain nothing translates through — I9 makes that invisible to every
    /// behaviour test, so it is asserted by identity here).
    /// </summary>
    [Fact]
    public void AC5_A_complete_pair_is_saved_lower_cased_and_takes_effect_at_once()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var before = Chains(window);

            window.AzureKeyBox.Password = "  " + RealLookingKey + "  ";
            window.AzureRegionCombo.Text = "  FranceCentral  ";
            Save(window);

            var after = Chains(window);
            Assert.Equal(before.Length, after.Length);
            for (var i = 0; i < before.Length; i++)
                Assert.NotSame(before[i], after[i]);
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal(RealLookingKey, saved.RootElement.GetProperty("AzureApiKey").GetString());
        Assert.Equal("francecentral", saved.RootElement.GetProperty("AzureRegion").GetString());
    }

    /// <summary>
    /// The review finding at the OTHER writer of the same field. <c>AzureRegionCombo_Changed</c>
    /// persists a picked region on the spot (T6), and until the review it stopped there — so a
    /// player with a saved key who corrected their region (<c>ux</c> §5 flow (c).4 calls a wrong
    /// region "the likely mistake") got <c>settings.json</c> holding the new one, a write chain
    /// still calling the old one (the region is baked into <c>AzureTranslator</c> when the tier is
    /// built) and a status line naming the old one, until they restarted. The file, the chain and
    /// the line that reports them must agree.
    /// </summary>
    [Fact]
    public void Picking_a_region_rebuilds_the_chains_and_the_status_line_it_just_changed()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westeurope", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.Equal(UserMessages.AzureKeySetStatus("westeurope"), window.AzureStatus.Text);
            var before = Chains(window);

            // As the user does it: pick one of the nine, which raises SelectionChanged for real
            // rather than through a reflected handler call.
            window.AzureRegionCombo.SelectedItem = window.AzureRegionCombo.Items
                .OfType<ComboBoxItem>().First(i => (string)i.Tag == "francecentral");

            Assert.Equal(UserMessages.AzureKeySetStatus("francecentral"), window.AzureStatus.Text);
            var after = Chains(window);
            for (var i = 0; i < before.Length; i++) Assert.NotSame(before[i], after[i]);
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
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

    /// <summary>
    /// AC 6's facade, pinned at <b>every</b> key-save handler — the review finding this story's own
    /// work turned into a one-line fix. An <c>AuthFailed</c> block is <c>MaxValue</c> and
    /// <c>ClearAuthBlock</c> is its only exit (TP-GATE-09), and it is not persisted (E2-a), so a
    /// handler that saves a key without routing through <see cref="TranslationChains.OnKeySaved"/>
    /// leaves the user's corrected key doing nothing until they restart the app. DeepL's Save had
    /// exactly that shape until the Azure one landed the facade beside it.
    ///
    /// <para>Asserted in source rather than at the gate, deliberately: this file is the WPF
    /// collection and never names <c>ProviderGates</c> (the behaviour is
    /// <c>ChainCompositionTests</c>' TP-SET-09, in the Gates collection).</para>
    /// </summary>
    [Fact]
    public void Every_key_save_handler_lifts_that_provider_account_scoped_block()
    {
        var translate = Code(File.ReadAllText(RepoFile("Views/MainWindow.Translate.cs")));

        foreach (var (handler, providerId) in new[]
                 {
                     ("DeepLSaveKey_Click", "ProviderIds.DeepL"),
                     ("AzureSaveKey_Click", "ProviderIds.Azure"),
                 })
            Assert.Contains($"TranslationChains.OnKeySaved({providerId});",
                            Body(translate, $"private void {handler}("), StringComparison.Ordinal);
    }

    // =============================================================================================
    //  E6.S4 — the "use my key for screen reading" opt-in
    // =============================================================================================

    /// <summary>
    /// <b>AC 4, and the whole of it is what does NOT happen.</b> An existing user upgrades with an
    /// Azure key already saved: their <c>settings.json</c> has no <c>UseKeyForReading</c> member at
    /// all, it deserialises to the property's default <c>false</c>, and the read chain is built
    /// without an Azure tier. No <c>Migrate</c> step, no <c>SettingsVersion</c> bump (I13) — the
    /// invariant delivers the AC by inaction. The way to break it is to seed the box from "a key
    /// exists"; ruling OQ-12 is the one line to remember, and it is that an opt-in which arrives
    /// pre-ticked is not an opt-in.
    /// </summary>
    [Fact]
    public void AC4_An_old_file_with_a_key_and_no_opt_in_loads_off_and_reads_on_the_free_engines()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westeurope", "SettingsVersion": 3 }
        """);

        var loaded = SettingsService.Load();
        Assert.Equal(RealLookingKey, loaded.AzureApiKey);
        Assert.False(loaded.UseKeyForReading);
        Assert.Equal(3, loaded.SettingsVersion);          // nothing migrated, nothing stamped

        Assert.False(TranslationChains.AzureReadsTheScreen(loaded));

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.False(window.AzureForReadingCheck.IsChecked);
            Assert.True(window.AzureForReadingCheck.IsEnabled);        // there IS a key to opt into
            Assert.Equal(UserMessages.AzureKeySetStatus("westeurope"), window.AzureStatus.Text);
        });

        // …and the load did not write anything back, so the next launch says the same thing.
        Assert.Equal(3, SettingsService.Load().SettingsVersion);
    }

    /// <summary>
    /// <b>AC 2.</b> Ticking the box persists, rebuilds the READ chains and refreshes the line that
    /// reports them — exactly as the key Save rebuilds the write chain. The three read references
    /// are compared by identity because that is the only thing a behaviour test could not see (all
    /// chains resolve the same process-global gates, I9), and <c>_writeTranslator</c> is asserted
    /// <b>unchanged</b>: this setting does not touch what the user writes, and a handler that
    /// rebuilt everything would be hiding that it does not know which chain it is for.
    ///
    /// <para>The rebuild takes effect on the next LIVE tick — both read fields are read per tick
    /// and never captured at <c>StartLive</c> — which is the semantics E6.S3 recorded and this
    /// handler inherits.</para>
    /// </summary>
    [Fact]
    public void AC2_Ticking_the_box_persists_rebuilds_the_read_chains_and_updates_the_status()
    {
        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westeurope", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var before = Chains(window);
            Assert.Equal(UserMessages.AzureKeySetStatus("westeurope"), window.AzureStatus.Text);

            // As the user does it — a real Checked event, not a reflected handler call.
            window.AzureForReadingCheck.IsChecked = true;

            var after = Chains(window);
            Assert.Same(before[0], after[0]);                    // _writeTranslator — untouched
            for (var i = 1; i < before.Length; i++)
                Assert.NotSame(before[i], after[i]);             // the three read references

            Assert.Equal(UserMessages.AzureKeySetForReadingStatus("westeurope"), window.AzureStatus.Text);

            // …and back off again: an untick must persist too (Checked and Unchecked are two
            // events, and wiring only one is how a tick sticks and an untick does not).
            window.AzureForReadingCheck.IsChecked = false;
            Assert.Equal(UserMessages.AzureKeySetStatus("westeurope"), window.AzureStatus.Text);
            for (var i = 1; i < before.Length; i++)
                Assert.NotSame(after[i], Chains(window)[i]);
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.False(saved.RootElement.GetProperty("UseKeyForReading").GetBoolean());
        Assert.Equal(3, saved.RootElement.GetProperty("SettingsVersion").GetInt32());
    }

    /// <summary>The box is dead while there is nothing to opt into — a tickable control over an
    /// empty key box promises a choice the builder would ignore, and AC 1 means it really would.
    /// </summary>
    [Fact]
    public void The_opt_in_is_disabled_until_a_usable_credential_exists()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.False(window.AzureForReadingCheck.IsEnabled);
            Assert.False(window.AzureForReadingCheck.IsChecked);
            Assert.Equal(UserMessages.AzureNoKeyStatus(), window.AzureStatus.Text);

            // A complete pair saved from the About tab enables it on the spot — no restart.
            window.AzureKeyBox.Password = RealLookingKey;
            window.AzureRegionCombo.Text = "westeurope";
            Save(window);

            Assert.True(window.AzureForReadingCheck.IsEnabled);
            Assert.False(window.AzureForReadingCheck.IsChecked);   // enabled is not ticked (OQ-12)
        });
    }

    /// <summary>The hint that carries the cost lives in <see cref="UserMessages"/> (ruling GAP-4)
    /// and is put on screen explicitly, like every other side effect a suppressed handler cannot
    /// produce (I12). Asserted through the window so a `TextBlock` that exists but is never filled
    /// fails here rather than in a screenshot.</summary>
    [Fact]
    public void AC3_The_quota_hint_is_rendered_from_the_one_place_it_is_written()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.Equal(UserMessages.AzureForReadingHint(), window.AzureForReadingHint.Text);
        });

        // The sentence itself, re-derived in E6.S4 (T6) and unchanged by it: 2 M chars/month over
        // ≈48 k per hour of busy chat is ≈41 h at ×1.0, and the billing multiplier is ≈×1.2–2.0
        // now that E4.S4 has left one shared cache ⇒ ≈21–35 h, inside the range promised here.
        var hint = UserMessages.AzureForReadingHint();
        Assert.Contains("2 million characters a month", hint, StringComparison.Ordinal);
        Assert.Contains("20 to 40 hours", hint, StringComparison.Ordinal);
        Assert.Contains("off by default", hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Ruling E6-e.</b> Emptying the key box and pressing Save removes Azure whole: the key and
    /// the region are both cleared on disk, the region box is emptied on screen, the status line
    /// says so, and the chains no longer carry the tier. Before this ruling the same gesture was
    /// REFUSED — "Azure also needs your key" over a leftover region — which asked the user to
    /// answer a question they had not asked.
    /// </summary>
    [Fact]
    public void E6e_Saving_an_empty_key_clears_the_region_too_and_removes_the_engine()
    {
        using var temp = new TempSettings($$"""
        {
          "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westeurope",
          "UseKeyForReading": true, "SettingsVersion": 3
        }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.Equal(UserMessages.AzureKeySetForReadingStatus("westeurope"), window.AzureStatus.Text);

            // Only the key box is emptied. The region is left exactly as it was.
            window.AzureKeyBox.Password = "";
            Save(window);

            Assert.Equal(UserMessages.AzureNoKeyStatus(), window.AzureStatus.Text);
            Assert.Equal("", window.AzureRegionCombo.Text);
            Assert.Null(window.AzureRegionCombo.SelectedItem);
            Assert.False(window.AzureForReadingCheck.IsEnabled);
        });

        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal("", saved.RootElement.GetProperty("AzureApiKey").GetString());
        Assert.Equal("", saved.RootElement.GetProperty("AzureRegion").GetString());

        // The opt-in itself is NOT rewritten — clearing a key is not un-choosing what to do with
        // the next one — and with no credential the builder ignores it anyway. That is the tier
        // being gone for the reason AC 1 gives, and not for a second one bolted on here.
        Assert.True(saved.RootElement.GetProperty("UseKeyForReading").GetBoolean());
        Assert.False(TranslationChains.AzureReadsTheScreen(SettingsService.Load()));
    }

    /// <summary>
    /// <b>AC 5 / R9, end to end on the shape the read chain has when the opt-in is on.</b> Azure
    /// answers its 403 out-of-quota envelope; the call is served by the next tier with no error
    /// text anywhere (UX hint 5 — a successful fallback is silent), the gate is blocked for the
    /// hour <c>TranslationPolicy.QuotaOpenMinutes</c> states (rulings E2-f / E2-h: a 30-day
    /// <c>Retry-After</c> is deliberately discarded in favour of 60-minute windows with probes),
    /// and the SECOND call sends <b>zero</b> Azure requests — ruling E3-a, the chain skips a tier
    /// while <c>BlockedUntil &gt; Now()</c>.
    ///
    /// <para>No production code was written for this AC. Local <see cref="ProviderGate"/>s with an
    /// injected clock (IS-6) rather than the registry, so this file needs no <c>Gates</c>
    /// collection and nothing sleeps (CI-3) — <c>ReadOnceStatusTests</c> is the precedent.</para>
    /// </summary>
    [Fact]
    public async Task AC5_A_quota_exhausted_azure_falls_through_for_an_hour_and_is_not_retried()
    {
        var clock = new FakeClock();
        var azureGate = new ProviderGate(clock.Read);
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.Forbidden, Fixture("azure-error-quota.json"));

        var azure = new AzureTranslator(RealLookingKey, "westeurope", handler, azureGate,
                                        RequestPriority.Background);
        var free = new CountingTranslator();
        var chain = new ChainTranslator(new[]
        {
            new ChainTier(ProviderIds.Azure, azureGate, azure),
            new ChainTier(ProviderIds.GoogleDict, new ProviderGate(clock.Read), free),
        });

        Assert.Equal(new[] { "T:привет" }, await chain.TranslateLinesAsync(new[] { "привет" }, "ru", "en"));
        Assert.Equal(1, handler.Requests);          // one request, and it is not retried (§5.6)
        Assert.Equal(1, free.Calls);

        var blocked = azureGate.Snapshot().BlockedUntil;
        Assert.NotNull(blocked);
        Assert.Equal(clock.Now.AddMinutes(TranslationPolicy.QuotaOpenMinutes), blocked!.Value);

        // Half an hour later the LIVE loop ticks again: Azure is skipped without a request.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(new[] { "T:пока" }, await chain.TranslateLinesAsync(new[] { "пока" }, "ru", "en"));
        Assert.Equal(1, handler.Requests);          // ZERO new Azure requests — the AC's whole point
        Assert.Equal(2, free.Calls);
    }

    /// <summary>
    /// <b>Review of E6.S4, Winston's first focus: one rule, two readers — and they may never
    /// disagree.</b> <c>TranslationChains.AzureReadsTheScreen</c> is what <c>BuildRead</c> branches
    /// on AND what the About tab's status line asks, which is only a guarantee if something checks
    /// that the two answers move together. This sweeps <b>every settings shape</b>
    /// <c>ChainCompositionTests.EveryPermutation()</c> knows — the same list TP-CHN-14 uses, so E8's
    /// next setting is covered here the day it is declared — and asserts the equivalence itself:
    /// the line says "AND for screen reading" <i>if and only if</i> the built chain's FIRST tier is
    /// Azure.
    ///
    /// <para>The failure it prevents is §1's fourth principle in one sentence: a status line that
    /// claims the user's metered key is reading the screen while the builder never constructed the
    /// tier (or, worse, the silent direction — the chain spending the key while the line still
    /// promises the free engines will do the reading). Both priorities, because the read chain is
    /// built twice.</para>
    /// </summary>
    [Fact]
    public void The_status_line_and_the_read_chain_can_never_disagree()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");

        var permutations = ChainCompositionTests.EveryPermutation();
        Assert.True(permutations.Count > 8, "the permutation sweep found no settings to vary");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var settingsField = typeof(MainWindow)
                .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var refresh = typeof(MainWindow)
                .GetMethod("UpdateEngineStatusUi", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = settingsField.GetValue(window);

            var bothSidesSeen = new HashSet<bool>();
            var bothWriteSidesSeen = new HashSet<bool>();
            try
            {
                foreach (var settings in permutations)
                {
                    settingsField.SetValue(window, settings);
                    refresh.Invoke(window, null);

                    var region = (settings.AzureRegion ?? "").Trim();
                    var lineSaysAzureReads = window.AzureStatus.Text ==
                        UserMessages.AzureKeySetForReadingStatus(region);
                    bothSidesSeen.Add(lineSaysAzureReads);

                    foreach (var priority in new[] { RequestPriority.Background, RequestPriority.Interactive })
                        Assert.Equal(lineSaysAzureReads,
                            ChainCompositionTests.IdsOf(TranslationChains.BuildRead(settings, priority))[0]
                                == ProviderIds.Azure);

                    // E7.S7 — the same equivalence for the WRITE half, which E6.S4's review recorded
                    // as missing: the line used `hasKey && region.Length > 0` and not the
                    // sendability predicate, so a region carrying a control character made it claim
                    // an engine BuildWrite adds no tier for. Either configured line means "Azure is
                    // on the write chain"; the no-key line means it is not.
                    var lineSaysAzureWrites = window.AzureStatus.Text == UserMessages.AzureKeySetStatus(region)
                                           || lineSaysAzureReads;
                    bothWriteSidesSeen.Add(lineSaysAzureWrites);
                    Assert.Equal(lineSaysAzureWrites,
                        ChainCompositionTests.IdsOf(TranslationChains.BuildWrite(settings))
                            .Contains(ProviderIds.Azure));

                    // …and the opt-in may only be tickable when there is something to opt into —
                    // the same predicate again, at the control.
                    Assert.Equal(lineSaysAzureWrites, window.AzureForReadingCheck.IsEnabled);
                }
            }
            finally { settingsField.SetValue(window, original); }

            // Non-vacuity: an equivalence both of whose sides were always false would pass over a
            // predicate that answered "no" to everything.
            Assert.Equal(2, bothSidesSeen.Count);
            Assert.Equal(2, bothWriteSidesSeen.Count);
        });
    }

    /// <summary>
    /// <b>Review of E6.S4, Winston's second focus: the clearing is silenced by its OWN flag.</b>
    /// E6-e's gesture empties the region combo in code, which raises <c>SelectionChanged</c>;
    /// <c>AzureRegionCombo_Changed</c> answering it would persist the OLD key with no region and
    /// rebuild both chains, half a gesture before the save handler does it properly. The
    /// implementation first borrowed <c>_restoringSettings</c> for that, and it may not: that flag
    /// means "a RESTORE is in progress", it is true for the whole of startup, and one flag answering
    /// two questions is how a handler that <i>should</i> have run stops running — the very bug class
    /// it guards.
    ///
    /// <para>So: the save handler never touches <c>_restoringSettings</c>, and the narrow flag is
    /// what the region handler bails on — silent while it is up, and persisting normally the moment
    /// it comes back down (which a <c>finally</c> guarantees even if the clearing throws).</para>
    /// </summary>
    [Fact]
    public void E6e_The_clearing_silences_the_region_handler_with_its_own_flag_and_only_for_itself()
    {
        // The structural half: whatever silences the combo inside the save handler, it is not the
        // restore guard. A scan, because "did not raise a flag" is not observable at runtime.
        var saveBody = Body(Code(File.ReadAllText(RepoFile("Views/MainWindow.Translate.cs"))),
                            "private void AzureSaveKey_Click(");
        Assert.DoesNotContain("_restoringSettings", saveBody, StringComparison.Ordinal);
        Assert.Contains("finally { _suppressAzureRegionHandler = false; }", saveBody, StringComparison.Ordinal);

        using var temp = new TempSettings($$"""
        { "AzureApiKey": "{{RealLookingKey}}", "AzureRegion": "westeurope", "SettingsVersion": 3 }
        """);

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var flag = typeof(MainWindow)
                .GetField("_suppressAzureRegionHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var regionChanged = typeof(MainWindow)
                .GetMethod("AzureRegionCombo_Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void RaiseRegionChanged() => regionChanged.Invoke(window, new object?[]
            {
                window.AzureRegionCombo,
                new SelectionChangedEventArgs(Selector.SelectionChangedEvent,
                                              Array.Empty<object>(), Array.Empty<object>()),
            });

            // (a) DURING the clearing — the flag is up, so the handler writes nothing, whatever the
            //     combo now reads.
            Assert.False((bool)flag.GetValue(window)!, "the flag is only ever up inside the gesture");
            flag.SetValue(window, true);
            window.AzureRegionCombo.Text = "francecentral";   // raises the real event too
            RaiseRegionChanged();
            Assert.Equal("westeurope", SettingsService.Load().AzureRegion);

            // (b) AFTERWARDS — the same handler, the same control, persists again. A suppression
            //     that outlived its gesture would leave the region box dead for the session.
            flag.SetValue(window, false);
            RaiseRegionChanged();
            Assert.Equal("francecentral", SettingsService.Load().AzureRegion);

            // …and the real gesture end to end: the flag is down again on the way out, so the box
            // the user types in next is live.
            window.AzureKeyBox.Password = "";
            Save(window);
            Assert.False((bool)flag.GetValue(window)!);
            Assert.Equal("", SettingsService.Load().AzureRegion);

            window.AzureRegionCombo.Text = "norwayeast";
            RaiseRegionChanged();
            Assert.Equal("norwayeast", SettingsService.Load().AzureRegion);
        });
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
        var body = Code(Body(File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")),
                             "private async void CopyErrorReport_Click("));

        Assert.Contains("Logging.ReadRecent()", body, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "_settings", "ApiKey", "KeyBox", "Password", "AzureRegion" })
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>IS-6. A clock the case moves by hand, so a 60-minute quota window is asserted
    /// without anything sleeping (CI-3).</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(TimeSpan d) => Now += d;
    }

    /// <summary>The tier that answers when Azure is skipped, and counts how often it was asked.
    /// It marks its output so a fall-through cannot be mistaken for a cached or echoed line.</summary>
    private sealed class CountingTranslator : ITranslator
    {
        public int Calls;

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
        { Calls++; return Task.FromResult("T:" + text); }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t,
                                                      CancellationToken ct = default)
        { Calls++; return Task.FromResult(lines.Select(l => "T:" + l).ToList()); }
    }

    private static string Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        Assert.True(File.Exists(path), $"fixture {name} not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>A named piece of a control's applied template — the template has to be applied
    /// first, because nothing here is ever shown on screen.</summary>
    private static T Part<T>(Control control, string name) where T : class
    {
        control.ApplyTemplate();
        var part = control.Template.FindName(name, control) as T;
        Assert.True(part != null, $"'{name}' is missing from {control.GetType().Name}'s template");
        return part!;
    }

    private static void Save(MainWindow window) =>
        typeof(MainWindow)
            .GetMethod("AzureSaveKey_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { window, null });

    /// <summary>The four chain references a key save has to replace — write, LIVE read, read-once,
    /// and the <c>ChainTranslator</c> the LIVE pause check asks. Read by identity, because "both
    /// chains were rebuilt" is a statement about objects and nothing about behaviour can see it
    /// (every rebuild resolves the same process-global gates — I9).</summary>
    private static object[] Chains(MainWindow window) =>
        new[] { "_writeTranslator", "_readTranslator", "_readOnceTranslator", "_readChain" }
            .Select(f => typeof(MainWindow)
                .GetField(f, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!)
            .ToArray();

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

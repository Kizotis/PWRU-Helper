using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PWRUHelper;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <b>E8.S3 — the About block, the setting and the two rules that make the whole story worth
/// having</b>: ruling R-4 (Download and Remove are the only writers of
/// <c>OfflineFallbackEnabled</c>) and AC 2 / NFR12 (no background path can open a dialog). Cases 1,
/// 2 and 8 of the story's list.
///
/// <para>STA, so it joins the non-parallel <c>Gates</c> collection: a real <c>MainWindow</c> reaches
/// <c>ProviderGates</c> through <c>TranslationChains</c> whether or not this file names the
/// registry (E7.S8's finding, and the scan in <c>ProviderGatesTests</c> enforces it).</para>
/// </summary>
[Collection("Gates")]
public class OfflineInstallUiTests : GatesTestBase
{
    private const string NoSettings = """{ "SettingsVersion": 3 }""";
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] FreeChain = { ProviderIds.GoogleDict, ProviderIds.GoogleGtx };

    // =============================================================================================
    //  Case 1 — OfflineFallbackEnabled round-trips, and only two gestures write it (ruling R-4)
    // =============================================================================================

    [Fact]
    public void The_setting_round_trips_through_settings_json()
    {
        using var temp = new TempSettings(NoSettings);

        // A settings file that predates the field deserialises it to its default. No Migrate step
        // and no SettingsVersion bump: I13 governs CHANGED defaults, and there are none here (§12).
        Assert.False(SettingsService.Load().OfflineFallbackEnabled);

        var settings = SettingsService.Load();
        settings.OfflineFallbackEnabled = true;
        SettingsService.Save(settings);

        Assert.True(SettingsService.Load().OfflineFallbackEnabled);
        Assert.True(JsonDocument.Parse(temp.Read()).RootElement
                        .GetProperty("OfflineFallbackEnabled").GetBoolean());

        // …and the version really was left alone. A bump here would re-run every migration step on
        // every existing user's file for a field that needs none.
        Assert.Equal(3, JsonDocument.Parse(temp.Read()).RootElement
                            .GetProperty("SettingsVersion").GetInt32());
    }

    /// <summary>
    /// <b>Ruling R-4 as a scan</b>, in the shape <c>ChainTranslatorTests</c> uses for <c>NotSent</c>:
    /// the field has exactly two writers in the whole app, and they are the Download success path
    /// and <c>Remove</c>. A third — a check box, a "helpful" seed from "is the engine on disk?", a
    /// migration step — is the failure this ruling exists to prevent, and it would be invisible in
    /// behaviour until a user found the app had turned something on for them.
    /// </summary>
    [Fact]
    public void OfflineFallbackEnabled_is_written_in_exactly_two_places_and_both_are_buttons()
    {
        var writers = new List<string>();

        foreach (var file in ProductionSources())
            foreach (var line in Code(File.ReadAllText(file)).Split('\n'))
                if (Regex.IsMatch(line, @"OfflineFallbackEnabled\s*=[^=]"))
                    writers.Add(Path.GetFileName(file) + ": " + line.Trim());

        Assert.Equal(new[]
        {
            "MainWindow.Offline.cs: _settings.OfflineFallbackEnabled = true;",
            "MainWindow.Offline.cs: _settings.OfflineFallbackEnabled = false;",
        }, writers);

        // …and the two really are the two gestures, not two lines of one of them.
        var offline = Code(File.ReadAllText(RepoFile("MainWindow.Offline.cs")));
        Assert.Contains("OfflineFallbackEnabled = true;",
            Body(offline, "private async void OfflineEngine_Click("), StringComparison.Ordinal);
        Assert.Contains("OfflineFallbackEnabled = false;",
            Body(offline, "private void RemoveOfflineEngine("), StringComparison.Ordinal);

        // There is no check box (§4.3, R-4): a second control would be a second way to express one
        // decision, and one of these two actions costs 50 MB of somebody's connection. Comments
        // stripped, because the block's own comment explains the ruling by name — which is the
        // documentation doing its job, not a control.
        var xaml = XamlCode(File.ReadAllText(RepoFile("MainWindow.xaml")));
        Assert.DoesNotContain("OfflineFallback", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OfflineEngineCheck", xaml, StringComparison.Ordinal);
        foreach (Match m in Regex.Matches(xaml, @"<CheckBox[^>]*x:Name=""(\w+)"""))
            Assert.DoesNotContain("Offline", m.Groups[1].Value, StringComparison.Ordinal);

        // No SettingsVersion step was added for it (I13, §12).
        var settingsSource = Code(File.ReadAllText(RepoFile(Path.Combine("Services", "SettingsService.cs"))));
        Assert.DoesNotContain("OfflineFallbackEnabled", settingsSource[settingsSource
            .IndexOf("Migrate(", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    // =============================================================================================
    //  Case 2 — the nudge, and no dialog from any background path (AC 2, NFR12)
    // =============================================================================================

    [Fact]
    public void The_all_paused_state_nudges_once_and_only_while_the_engine_is_missing()
    {
        var allPaused = AllPaused();
        var chip = MainWindow.ChipFor(allPaused);

        Assert.True(allPaused.AllReadTiersPaused);
        Assert.False(chip.IsHealthy);

        Assert.Equal("All engines are paused — you can add an offline engine in About.",
                     MainWindow.StateNotice(allPaused, chip, wasDegraded: false, offlineInstalled: false));

        // Installed: pointing at About to add something that is already there is worse than saying
        // nothing at all (§1 principle 4 — honest status).
        Assert.Null(MainWindow.StateNotice(allPaused, chip, wasDegraded: false, offlineInstalled: true));

        // …and it does not displace the sentence a partly-degraded state already owes: §3.5's
        // fallback notice names what really happened, and the nudge is what the app says when there
        // is nothing left to explain.
        var fellBack = EngineStatus.Of(FreeChain,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = new(GateState.Open, Now + TimeSpan.FromSeconds(58), 1,
                                               TranslationErrorKind.RateLimited),
            },
            FreeChain,
            new ChainTranslator.Outcome(ProviderIds.GoogleGtx,
                new[] { (ProviderId: ProviderIds.GoogleDict, Reason: "Paused") },
                Now + TimeSpan.FromSeconds(58), null),
            stateKnown: true, Now);

        Assert.Equal("Translated by Google (backup) — Google is paused.",
                     MainWindow.StateNotice(fellBack, MainWindow.ChipFor(fellBack),
                                            wasDegraded: false, offlineInstalled: false));

        // A healthy chip says nothing, engine or no engine — a nudge on a working app is noise.
        var healthy = EngineStatus.Of(FreeChain,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal), FreeChain,
            new ChainTranslator.Outcome(ProviderIds.GoogleDict, Array.Empty<(string, string)>(), null, null),
            stateKnown: true, Now);
        Assert.Null(MainWindow.StateNotice(healthy, MainWindow.ChipFor(healthy),
                                           wasDegraded: false, offlineInstalled: false));
    }

    /// <summary>
    /// <b>The whole reason §3.6 diverges from the brief's flow (d)</b>, as a scan: a
    /// <c>MessageBox</c> over a fullscreen game, opened by a LIVE loop the player forgot was
    /// running, is the single worst thing this app could do (principle 3, NFR12). So the LIVE and
    /// OCR paths may not reach a dialog API at all, and the consent dialog is reachable from exactly
    /// one <c>Click</c> handler.
    /// </summary>
    [Fact]
    public void No_dialog_api_is_reachable_from_the_live_or_ocr_path()
    {
        // A modal is allowed where the USER asked for it and nowhere else, so the carve-outs are
        // NAMED members rather than pattern-matched — adding one has to be a decision, which is the
        // same shape UserMessagesTests' status-line scan and TP-START-02 both use.
        //
        //  · MainWindow.Live.cs — none at all. The tick loop cannot reach a dialog API.
        //  · ResetTuning_Click — the Screen OCR tab's "put the reading settings back" confirmation:
        //    a button the player pressed a moment earlier.
        //  · SelectRegionAsync — the drag-a-rectangle overlay, which IS the gesture rather than an
        //    announcement about one. It is reached only from Select-area / read-once, never from a
        //    failure path, and a background caller would have nothing to say through it.
        var allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["MainWindow.Live.cs"] = Array.Empty<string>(),
            ["MainWindow.Ocr.cs"] = new[]
            {
                "private void ResetTuning_Click(",
                "private async Task<System.Drawing.Rectangle?> SelectRegionAsync(",
            },
        };

        var dialogApis = new[] { "MessageBox.", "ShowDialog(", "new Window(" };

        foreach (var (file, gestures) in allowed)
        {
            var code = Code(File.ReadAllText(RepoFile(file)));
            // Everything the player did NOT press: the tick loop, the capture path, the translation
            // continuations, the retry queue — all of it, with the user's own gestures cut out.
            var background = gestures.Aggregate(code, (rest, gesture) => rest.Replace(Body(rest, gesture), ""));

            foreach (var dialog in dialogApis)
                Assert.False(background.Contains(dialog, StringComparison.Ordinal),
                    $"{file} can open a modal (\"{dialog}\") outside the gestures this scan names — "
                    + "a dialog raised by a background loop over a fullscreen game is what AC 2 / "
                    + "NFR12 forbid outright");

            // …and no carve-out is vacuous: each named member really does open something, so a
            // rename cannot quietly turn this scan into a list of nothing.
            foreach (var gesture in gestures)
                Assert.Contains(dialogApis,
                    d => Body(code, gesture).Contains(d, StringComparison.Ordinal));
        }

        // …and the transitive half (review): AC 2 is about a PATH, and both of the files above call
        // out of themselves on every tick — into the translation chain, the gates, the store, the
        // dedup. Every one of those callees is under Services/, which is I2's own boundary, so the
        // sweep is the whole directory with no carve-out at all: a dialog API anywhere in it would
        // be reachable from the LIVE loop by construction.
        foreach (var service in ProductionSources()
                     .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Services{Path.DirectorySeparatorChar}",
                                            StringComparison.Ordinal)))
        {
            var code = Code(File.ReadAllText(service));
            foreach (var dialog in dialogApis)
                Assert.False(code.Contains(dialog, StringComparison.Ordinal),
                    $"{Path.GetFileName(service)} can open a modal (\"{dialog}\") — Services/ is what "
                    + "the LIVE and OCR loops call, so a dialog there is a dialog on a background "
                    + "path (AC 2 / NFR12, and I2)");
        }

        // …and the two dialogs this story adds are reachable from the About tab's own gestures and
        // nowhere else: one Click handler for consent, one private method for Remove that only that
        // handler calls.
        var offline = Code(File.ReadAllText(RepoFile("MainWindow.Offline.cs")));
        Assert.Equal(1, Occurrences(Body(offline, "private async void OfflineEngine_Click("),
                                    "MessageBox.Show(this,"));
        Assert.Equal(1, Occurrences(Body(offline, "private void RemoveOfflineEngine("),
                                    "MessageBox.Show(this,"));
        // With an owner, both times (project-context.md, and §3.6 says it twice): a parentless
        // dialog behind an always-on-top window over a fullscreen game is unfindable.
        Assert.Equal(Occurrences(offline, "MessageBox.Show(this,"),
                     Occurrences(offline, "MessageBox.Show("));

        // The nudge is a status-line sentence chosen by a pure function, which is what makes it
        // structurally incapable of becoming a dialog or a toast.
        var main = Code(File.ReadAllText(RepoFile("MainWindow.xaml.cs")));
        Assert.Contains("UserMessages.AllPausedOfflineNudge()",
                        Body(main, "internal static string? StateNotice("), StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(main, "UserMessages.AllPausedOfflineNudge()"));
    }

    // =============================================================================================
    //  Case 8 — ApplySettings restores the row, and no handler writes settings back (I12, AC 9)
    // =============================================================================================

    [Theory]
    [InlineData(false, "○ Not installed — about 50 MB to download, works with no internet at all. "
                     + "Used only when every online engine is unavailable.", "Download the offline engine")]
    [InlineData(true, "● Offline engine ready — used only when everything else is unavailable.", "Remove")]
    public void ApplySettings_restores_the_row_without_a_handler_writing_settings_back(
        bool enabled, string expectedRow, string expectedLabel)
    {
        var json = $$"""{ "SettingsVersion": 3, "OfflineFallbackEnabled": {{(enabled ? "true" : "false")}} }""";
        using var settings = new TempSettings(json);
        using var models = new TempModels();

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            // The whole of AC 9: the restore applied the button's side effects EXPLICITLY, because
            // ApplySettings suppresses change handlers for its whole run and this control has none
            // to suppress. _restoringSettings is deliberately not borrowed (I12) — E6.S3's review
            // found a real bug doing that.
            Assert.Equal(expectedRow, window.OfflineEngineText.Text);
            Assert.Equal(expectedLabel, (string)window.OfflineEngineButton.Content);
            Assert.Equal(Visibility.Collapsed, window.OfflineCancelButton.Visibility);
        });

        // …and nothing wrote the setting back on the way through. This is the v0.12.3 bug class:
        // XAML loading raises Checked/SelectionChanged during InitializeComponent(), long before
        // ApplySettings runs, and a handler that persists there clobbers what was saved.
        Assert.Equal(enabled, JsonDocument.Parse(settings.Read()).RootElement
                                  .GetProperty("OfflineFallbackEnabled").GetBoolean());
    }

    [Fact]
    public void The_offline_block_is_one_region_with_the_wrapping_and_the_style_the_tab_already_uses()
    {
        using var settings = new TempSettings(NoSettings);
        using var models = new TempModels();

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            // E7.S7 left the heading and the row; this story filled that block in rather than adding
            // a second one. One heading, one row TextBlock, both named.
            var xaml = File.ReadAllText(RepoFile("MainWindow.xaml"));
            Assert.Equal(1, Occurrences(xaml, "Text=\"Offline engine (optional)\""));
            Assert.Equal(1, Occurrences(xaml, "x:Name=\"OfflineEngineText\""));

            // §4.3's control: a Button whose content flips, never a check box — "the two actions
            // have very different weight".
            Assert.IsType<Button>(window.OfflineEngineButton);
            Assert.IsType<Button>(window.OfflineCancelButton);

            // E6.S5's idiom, and E7.S7's review found it the hard way: this tab scrolls vertically
            // only, so a horizontal StackPanel wider than the viewport is CLIPPED and unreachable at
            // any window size. TP-RENDER-09 measures the consequence at both shipped sizes; this
            // asserts the cause.
            Assert.IsType<WrapPanel>(window.OfflineEngineButton.Parent);
            Assert.Same(window.OfflineEngineButton.Parent, window.OfflineCancelButton.Parent);
            Assert.True(window.OfflineEngineText.TextWrapping == TextWrapping.Wrap,
                "AC 3's failure sentence is the longest string on this tab and its actionable half "
                + "is at the end — an unwrapped line clips exactly that");

            // No new brush, font or colour (UX-DR18): the same GhostButton every other button on
            // this tab uses.
            Assert.Same(window.FindResource("GhostButton"), window.OfflineEngineButton.Style);
            Assert.Same(window.FindResource("GhostButton"), window.OfflineCancelButton.Style);

            // Every label comes from the deck (GAP-4 / UX-DR19), and not from a Content attribute:
            // this button's label CHANGES, so a literal in the XAML would be a second spelling of it.
            Assert.DoesNotContain("Download the offline engine", xaml, StringComparison.Ordinal);
            Assert.Equal(UserMessages.OfflineDownloadLabel(), (string)window.OfflineEngineButton.Content);
            Assert.Equal(UserMessages.OfflineCancelLabel(), (string)window.OfflineCancelButton.Content);
        });
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    /// <summary>§2.1's S5: every rung of the read chain inside a window, with nothing having
    /// answered.</summary>
    private static EngineStatus AllPaused()
        => EngineStatus.Of(FreeChain,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = new(GateState.Open, Now + TimeSpan.FromMinutes(4), 1,
                                               TranslationErrorKind.RateLimited),
                [ProviderIds.GoogleGtx] = new(GateState.Open, Now + TimeSpan.FromMinutes(4), 1,
                                              TranslationErrorKind.RateLimited),
            },
            FreeChain, outcome: null, stateKnown: true, Now);

    private static int Occurrences(string text, string fragment)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0) { count++; at += fragment.Length; }
        return count;
    }

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    /// <summary>XAML with its <c>&lt;!-- --&gt;</c> comments removed — the same idea as
    /// <see cref="Code"/>, for the file where the block's own comment explains ruling R-4 by
    /// name.</summary>
    private static string XamlCode(string text) =>
        Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);

    // =============================================================================================
    //  E8.S5 — ruling E8-b's residual case: a Remove whose delete fails
    // =============================================================================================

    /// <summary>
    /// <b>The sentence E8-b has owed since E8.S3, landed with its deck line in one commit</b> — the
    /// rule that ruling made for the directory, applied to the copy it left behind. A <c>Remove</c>
    /// whose delete fails used to report "Offline engine removed — 0 MB freed from your disk" over
    /// 50 MB that had not moved, which is the one thing principle 1 forbids outright.
    ///
    /// <para>Both causes the app can remove are removed first (the engine is freed <i>and</i> closed
    /// before the delete), so what is left is a handle this process does not hold. The sentence says
    /// what happened, how much is left, and the one thing that ends it — principle 2's "never a
    /// statement with no exit".</para>
    /// </summary>
    [Fact]
    public void E8_b_A_remove_that_could_not_finish_says_so_in_the_code_and_in_the_deck()
    {
        var sentence = UserMessages.OfflineRemoveIncomplete(52_428_800);

        Assert.Equal("Could not remove every file — 50 MB left; close the app and try again.", sentence);
        Assert.True(sentence.Length <= 120, $"the row is one status line, not two ({sentence.Length})");
        Assert.DoesNotContain("{", sentence, StringComparison.Ordinal);

        // Never "0 MB left": a leftover smaller than a megabyte is still a leftover, and a sentence
        // that says nothing is left while something is is the bug this replaces.
        Assert.Equal("Could not remove every file — 1 MB left; close the app and try again.",
                     UserMessages.OfflineRemoveIncomplete(1));

        // …and it is the row's real chooser: what is still on disk decides, not what the delete
        // claimed to free.
        var handler = Body(Code(File.ReadAllText(RepoFile("MainWindow.Offline.cs"))),
                           "private void RemoveOfflineEngine()");
        Assert.Contains("_offlineStore.BytesOnDisk", handler, StringComparison.Ordinal);
        Assert.Contains("UserMessages.OfflineRemoveIncomplete(left)", handler, StringComparison.Ordinal);

        // The deck carries the same sentence, in the same commit as the code — which is the whole of
        // ruling E8-b's process half.
        var deck = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "investigations",
                                                 "02-traduction", "ux-mode-degrade.md"));
        Assert.Contains("Could not remove every file — {n} MB left; close the app and try again.",
                        deck, StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");
        var open = source.IndexOf('{', at);
        Assert.True(open >= 0, $"could not find the body of {signature}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced braces after {signature}");
        return "";
    }

    private static IEnumerable<string> ProductionSources()
    {
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = sep + Path.GetRelativePath(root, file);
            if (relative.Contains($"{sep}tests{sep}") || relative.Contains($"{sep}bin{sep}")
                || relative.Contains($"{sep}obj{sep}")) continue;
            yield return file;
        }
    }

    private static string RepoFile(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), $"expected {relative} at the repo root");
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}

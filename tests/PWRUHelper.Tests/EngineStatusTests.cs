using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using PWRUHelper;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <b>E7.S7 — the About tab's "Translation engines" block.</b> Three claims are asserted here and
/// each of them is a way the tab could lie:
///
/// <list type="number">
/// <item>the <b>Chain</b> lines are the chain the app really builds — over every settings shape
///       <c>ChainCompositionTests.EveryPermutation()</c> knows, and at both read priorities — so a
///       tier that is not shipped (Edge, ruling E3-d; the offline engine, E8) can never be printed
///       and DeepL can never appear on the read line (I8);</item>
/// <item>the <b>In use now</b> line is the chip's own words, from the same pure composer, so the
///       tab and the chip cannot come to describe one state two ways (UX-DR19 across surfaces);</item>
/// <item><b>Clear cache</b> empties the store in memory <i>and</i> on disk, a debounced save queued
///       before the click cannot resurrect the file, and the next translation re-populates it.</item>
/// </list>
///
/// <para>No collection attribute for the pure half; the cases that build a <see cref="MainWindow"/>
/// live in <c>AzureSettingsTests</c> / <c>TemplateRenderTests</c> (the <c>WPF</c> collection). The
/// cache cases run inside a <see cref="TempCache"/> (IS-3) and reset the process-wide store after
/// themselves, exactly as <c>TranslationCachePersistenceTests</c> does.</para>
/// </summary>
public class EngineStatusTests
{
    private const string AzureKey = "0123456789abcdef0123456789abcdef";

    // =============================================================================================
    //  T3 — the Chain lines are the chain, and they cannot drift from the builders
    // =============================================================================================

    /// <summary>
    /// The whole guarantee of T3 in one assertion: the ids the About tab renders are exactly the
    /// ids the builders construct, for <b>every</b> settings shape the sweep knows and at both read
    /// priorities. Without it the tab would hold a second expression of §8.1's composition, and the
    /// two would drift the first time a tier's condition changed.
    /// </summary>
    [Fact]
    public void The_chain_lines_list_exactly_what_the_builders_construct()
    {
        var permutations = ChainCompositionTests.EveryPermutation();
        Assert.True(permutations.Count > 8, "the permutation sweep found no settings to vary");

        foreach (var settings in permutations)
        {
            Assert.Equal(ChainCompositionTests.IdsOf(TranslationChains.BuildWrite(settings)),
                         TranslationChains.WriteTierIds(settings));

            foreach (var priority in new[] { RequestPriority.Background, RequestPriority.Interactive })
                Assert.Equal(ChainCompositionTests.IdsOf(TranslationChains.BuildRead(settings, priority)),
                             TranslationChains.ReadTierIds(settings));
        }
    }

    /// <summary>
    /// <b>I8 as a second, cheap guard</b> (the first is <c>TP_CHN_14</c>, on the built chain): the
    /// read line may never NAME DeepL either. A status surface that claimed the screen reader was
    /// spending a DeepL key would be the same lie whether or not the tier existed.
    /// </summary>
    [Fact]
    public void The_read_line_never_names_DeepL_and_the_write_line_does_when_a_key_is_set()
    {
        foreach (var settings in ChainCompositionTests.EveryPermutation())
        {
            Assert.DoesNotContain(ProviderIds.DeepL, TranslationChains.ReadTierIds(settings));
            Assert.DoesNotContain("DeepL",
                UserMessages.EngineChainLine(TranslationChains.ReadTierIds(settings)));
        }

        // Non-vacuity: the write line really does lead with DeepL when there is a key.
        Assert.StartsWith("DeepL", UserMessages.EngineChainLine(
            TranslationChains.WriteTierIds(new AppSettings { DeepLApiKey = "abc-123:fx" })));
    }

    /// <summary>
    /// <b>The line must not print a chain the app does not have.</b> §4.2's mockup reads
    /// <c>Google → Edge → Google (backup) → Offline engine (not installed)</c>; <b>Edge is not
    /// shipped</b> (ruling E3-d, U2 owner-blocked) and <b>Bergamot is not shipped</b> (E8), so
    /// neither name may appear in either line, under any settings.
    ///
    /// <para>The absent tiers are stated where a reader can act on them instead: the offline engine
    /// has its own block on this tab, and the chip's tooltip lists every id in
    /// <see cref="ProviderIds.All"/> with <c>— not available</c> / <c>— not installed</c>. Omission
    /// is the option the story allows and the only one that keeps an arrow honest.</para>
    /// </summary>
    [Fact]
    public void No_chain_line_ever_claims_a_tier_the_app_does_not_ship()
    {
        foreach (var settings in ChainCompositionTests.EveryPermutation())
            foreach (var line in new[]
                     {
                         UserMessages.EngineChainLine(TranslationChains.WriteTierIds(settings)),
                         UserMessages.EngineChainLine(TranslationChains.ReadTierIds(settings)),
                     })
            {
                Assert.DoesNotContain("Edge", line);
                Assert.DoesNotContain("Offline", line);
                // …and it is never empty: every settings shape still has the two free tiers.
                Assert.Contains("Google", line);
            }
    }

    /// <summary>The free chain, spelled out — the line a user with no key reads, in §3.0's names
    /// (A2: <c>google-gtx</c> is "Google (backup)", never "Google (old)").</summary>
    [Fact]
    public void The_free_chain_reads_as_the_two_google_tiers_in_order()
    {
        var free = new AppSettings();
        Assert.Equal("Google → Google (backup)",
                     UserMessages.EngineChainLine(TranslationChains.ReadTierIds(free)));
        Assert.Equal("Google → Google (backup)",
                     UserMessages.EngineChainLine(TranslationChains.WriteTierIds(free)));

        // Both keys, and the opt-in: DeepL then Azure on the write path (§8.1), Azure first on the
        // read path — and the two lines really do differ, which is the whole reason there are two.
        var keyed = new AppSettings
        {
            DeepLApiKey = "abc-123:fx",
            AzureApiKey = AzureKey,
            AzureRegion = "westeurope",
            UseKeyForReading = true,
        };
        Assert.Equal("DeepL → Azure → Google → Google (backup)",
                     UserMessages.EngineChainLine(TranslationChains.WriteTierIds(keyed)));
        Assert.Equal("Azure → Google → Google (backup)",
                     UserMessages.EngineChainLine(TranslationChains.ReadTierIds(keyed)));
    }

    // =============================================================================================
    //  T2 — the "In use now" line is the chip, and the reason clause is evidence-backed
    // =============================================================================================

    /// <summary>
    /// The fourth placement of E7.S3's chip, and the assertion that makes it a placement rather than
    /// a re-derivation: for every one of §2.1's states the About tab renders <b>the same string</b>
    /// the chip does. The tab and the chip are drawn from one pure function, so they cannot disagree
    /// (UX-DR19 applied across surfaces).
    /// </summary>
    [Fact]
    public void The_in_use_line_says_exactly_what_the_chip_says()
    {
        foreach (var status in EveryStatus())
        {
            var chip = MainWindow.ChipFor(status);
            Assert.Equal(chip.Label, MainWindow.AboutChipFor(status, showTheClock: true).Label);
        }
    }

    /// <summary>
    /// The clock rule, written as behaviour: the About line carries a countdown only when it is
    /// allowed to. Every countdown surface on the main window lives inside a tab and tabs are
    /// mutually exclusive, so "only while the About tab is selected" is what keeps §2.4's one clock
    /// per window true — and it also makes the 1 Hz repaint free while the user is elsewhere.
    /// </summary>
    [Fact]
    public void The_in_use_line_drops_its_clock_when_the_tab_does_not_own_it()
    {
        var paused = PausedStatus(58);

        Assert.Contains("0:58", MainWindow.AboutChipFor(paused, showTheClock: true).Label);
        Assert.DoesNotContain("0:58", MainWindow.AboutChipFor(paused, showTheClock: false).Label);
        // …and the state is still named without it: NFR11 — the glyph and the word carry it.
        Assert.Contains("paused", MainWindow.AboutChipFor(paused, showTheClock: false).Label);
    }

    /// <summary>
    /// §4.2's reason column — <c>Google is paused, retries in 0:58</c> — and the two rules that
    /// keep it from being noise: it renders only when the chip is NOT itself about the pause (i.e.
    /// a lower tier answered while a higher one was skipped, ruling E3-b's evidence), and it never
    /// names the engine that is serving.
    /// </summary>
    [Fact]
    public void The_reason_clause_names_the_paused_engine_only_when_a_backup_is_answering()
    {
        // S1 — healthy: nothing to explain.
        Assert.Null(MainWindow.EngineReasonLine(Healthy(), showTheClock: true));

        // S2/S3 — the backup answered because Google was skipped, and Google is inside a window.
        var fellBack = PausedStatus(58);
        var reason = MainWindow.EngineReasonLine(fellBack, showTheClock: true);
        Assert.Equal("Google is paused, retries in 0:58", reason);
        Assert.Equal("Google is paused", MainWindow.EngineReasonLine(fellBack, showTheClock: false));

        // …and it is the same walk the §3.5 notice makes, so the two can never name different
        // engines on one window.
        Assert.Contains("Google is paused", MainWindow.FallbackNotice(fellBack)!);
    }

    // =============================================================================================
    //  A10 — "Clear cache"
    // =============================================================================================

    // The two cases that touch the FILE live in `TranslationCachePersistenceTests`, and that is not
    // tidiness: that class is the only one in the suite that writes
    // `TranslationCacheStore.PathOverride` / `SaveDebounceMs`, it says so in its own header, and it
    // runs in parallel with this one. A `TempCache` opened here would move the path under a case
    // running there — the exact hazard IS-3 exists to prevent.

    /// <summary>The feedback sentence, from the deck, with the count the store reported.</summary>
    [Fact]
    public void The_cleared_sentence_carries_the_count()
    {
        Assert.Equal("Cache cleared — 0 saved translation(s) removed.", UserMessages.CacheClearedStatus(0));
        Assert.Equal("Cache cleared — 12 saved translation(s) removed.", UserMessages.CacheClearedStatus(12));
    }

    /// <summary>
    /// I10 / TP-START-02's shape, applied to the button: the code-behind names the FACADE and never
    /// the store. <c>TranslationCachePersistenceTests</c> asserts the same rule over every source
    /// outside <c>Services/</c>; this is the one that says which call replaces it, so a future
    /// "just call Clear() directly" is a decision rather than a convenience.
    /// </summary>
    [Fact]
    public void The_clear_button_routes_through_the_facade_and_never_names_the_store()
    {
        var code = File.ReadAllText(RepoFile("MainWindow.Translate.cs"));

        Assert.Contains("TranslationChains.ClearCache()", code, System.StringComparison.Ordinal);
        Assert.DoesNotContain("TranslationCacheStore", code, System.StringComparison.Ordinal);
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Free = { ProviderIds.GoogleDict, ProviderIds.GoogleGtx };

    private static EngineStatus Healthy()
        => EngineStatus.Of(Free, new Dictionary<string, GateSnapshot>(StringComparer.Ordinal), Free,
                           new ChainTranslator.Outcome(ProviderIds.GoogleDict,
                               new List<(string ProviderId, string Reason)>(), null, null),
                           stateKnown: true, Now);

    /// <summary>§2.1's <b>S3</b>: Google is inside a window and the backup answered, which is the
    /// one state whose reason clause has something to say.</summary>
    private static EngineStatus PausedStatus(int seconds)
        => EngineStatus.Of(Free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.GoogleDict] = new(GateState.Open, Now.AddSeconds(seconds), 1,
                                               TranslationErrorKind.RateLimited),
            },
            Free,
            new ChainTranslator.Outcome(ProviderIds.GoogleGtx,
                new List<(string ProviderId, string Reason)> { (ProviderIds.GoogleDict, "Paused") },
                null, null),
            stateKnown: true, Now);

    private static IEnumerable<EngineStatus> EveryStatus()
    {
        yield return Healthy();
        yield return PausedStatus(58);
        yield return PausedStatus(3600);
        // Not warmed up yet (ruling E6-a) — the chip says "checking…" and so must the tab.
        yield return EngineStatus.Of(Free, new Dictionary<string, GateSnapshot>(StringComparer.Ordinal),
                                     Free, null, stateKnown: false, Now);
        // S5 / S6 — every read tier inside a window, and the same with a network cause.
        foreach (var kind in new[] { TranslationErrorKind.RateLimited, TranslationErrorKind.Network })
            yield return EngineStatus.Of(Free,
                new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
                {
                    [ProviderIds.GoogleDict] = new(GateState.Open, Now.AddSeconds(200), 1, kind),
                    [ProviderIds.GoogleGtx] = new(GateState.Open, Now.AddSeconds(260), 1, kind),
                },
                Free, null, stateKnown: true, Now);
        // S7 / S8 — the two a key can be in.
        var keys = new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx, ProviderIds.DeepL, ProviderIds.Azure };
        yield return EngineStatus.Of(Free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.DeepL] = new(GateState.Open, DateTimeOffset.MaxValue, 1,
                                          TranslationErrorKind.AuthFailed),
            },
            keys,
            new ChainTranslator.Outcome(ProviderIds.GoogleDict,
                new List<(string ProviderId, string Reason)>(), null, null),
            stateKnown: true, Now);
        yield return EngineStatus.Of(Free,
            new Dictionary<string, GateSnapshot>(StringComparer.Ordinal)
            {
                [ProviderIds.Azure] = new(GateState.Open, Now.AddMinutes(60), 1,
                                          TranslationErrorKind.QuotaExhausted),
            },
            keys,
            new ChainTranslator.Outcome(ProviderIds.GoogleDict,
                new List<(string ProviderId, string Reason)> { (ProviderIds.Azure, "Paused") },
                null, null),
            stateKnown: true, Now);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}

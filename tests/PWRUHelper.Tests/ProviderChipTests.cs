using System.Collections.Generic;
using System.Linq;
using PWRUHelper;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E7.S3 — <b>the provider chip, as arithmetic.</b> Eight states, eight strings, one tooltip, and
/// not a window in sight: the whole of §2.1's table is a pure function over
/// <see cref="EngineStatus"/>, which is itself a pure function over gate snapshots and one
/// <c>ChainTranslator.Outcome</c>. That is what the epic asks for — "a unit test that each of the
/// eight states produces a distinct glyph+word pair" — and it is L1 only because neither half
/// touches a control, a gate or a clock.
///
/// <para><b>NFR11 is the assertion style, not a separate case.</b> Every expectation below is on
/// the TEXT. The brush key is checked once per state, in its own list, and no assertion anywhere
/// needs it to tell two states apart — which is exactly the test a colour-blind player applies:
/// <c>○ Google paused 0:58</c> says everything with the palette stripped out.</para>
///
/// <para><b>No registry, no collection.</b> The instants are this file's own (IS-6), the snapshots
/// are literals, and nothing here names <c>ProviderGates</c> — so this class needs neither the
/// <c>Gates</c> collection nor the STA host, and nothing sleeps (CI-3).</para>
/// </summary>
public class ProviderChipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset In(int seconds) => Now + TimeSpan.FromSeconds(seconds);

    private static GateSnapshot Paused(int seconds, TranslationErrorKind kind)
        => new(GateState.Open, In(seconds), 1, kind);

    /// <summary>The <c>AuthFailed</c> sentinel: a block with no honest countdown, whose exit is
    /// re-saving the key and not a second passing.</summary>
    private static GateSnapshot Refused(TranslationErrorKind kind)
        => new(GateState.Open, DateTimeOffset.MaxValue, 1, kind);

    private static ChainTranslator.Outcome Answered(string providerId, params string[] skipped)
        => new(providerId,
               skipped.Select(s => (ProviderId: s, Reason: "Paused")).ToList(),
               skipped.Length == 0 ? null : In(58),
               null);

    private static readonly string[] FreeChain = { ProviderIds.GoogleDict, ProviderIds.GoogleGtx };

    private static EngineStatus Status(
        IReadOnlyList<string>? readTiers = null,
        Dictionary<string, GateSnapshot>? gates = null,
        IReadOnlyCollection<string>? configured = null,
        ChainTranslator.Outcome? outcome = null,
        bool stateKnown = true)
    {
        readTiers ??= FreeChain;
        return EngineStatus.Of(readTiers,
            gates ?? new Dictionary<string, GateSnapshot>(StringComparer.Ordinal),
            configured ?? readTiers, outcome, stateKnown, Now);
    }

    // =============================================================================================
    //  §2.1 — the eight states
    // =============================================================================================

    private static (string Text, string Brush) Chip(EngineStatus status,
        bool statusLineOwnsTheClock = false)
    {
        var chip = MainWindow.ChipFor(status, statusLineOwnsTheClock);
        return (chip.Label, chip.BrushKey);
    }

    /// <summary>S1 — healthy. The first tier answered, so the chip is a name and nothing else.</summary>
    private static EngineStatus S1() => Status(outcome: Answered(ProviderIds.GoogleDict));

    /// <summary>S2 — a lower tier answered because a higher one was SKIPPED (ruling E3-b), and the
    /// higher one's window has since elapsed, so this is a fallback rather than a live pause.</summary>
    private static EngineStatus S2()
        => Status(outcome: Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict));

    /// <summary>S3 — the preferred engine is inside a window right now and something below it still
    /// serves. <c>BlockedUntil &gt; now</c> (ruling E3-a), never <c>State == Open</c>.</summary>
    private static EngineStatus S3() => Status(
        gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.GoogleDict] = Paused(58, TranslationErrorKind.RateLimited),
        },
        outcome: Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict));

    /// <summary>S4 — the offline engine answered. Unreachable until E8 ships the tier; the MAPPING
    /// is total over <see cref="ProviderIds.All"/> today, and this is what proves it.</summary>
    private static EngineStatus S4()
    {
        var chain = new[] { ProviderIds.GoogleDict, ProviderIds.Bergamot };
        return Status(readTiers: chain, configured: chain,
            outcome: Answered(ProviderIds.Bergamot, ProviderIds.GoogleDict));
    }

    /// <summary>S5 — every rung of the read chain is inside a window.</summary>
    private static EngineStatus S5() => Status(
        gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.GoogleDict] = Paused(200, TranslationErrorKind.RateLimited),
            [ProviderIds.GoogleGtx] = Paused(260, TranslationErrorKind.Blocked),
        });

    /// <summary>S6 — nothing resolves. Ruling GAP-3: the full pause is universal, so it behaves
    /// like S5 and only the cause differs.</summary>
    private static EngineStatus S6() => Status(
        gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.GoogleDict] = Paused(30, TranslationErrorKind.Network),
            [ProviderIds.GoogleGtx] = Paused(30, TranslationErrorKind.Network),
        });

    /// <summary>S7 — the user's DeepL key was refused. No countdown, because the sentinel behind it
    /// is <see cref="DateTimeOffset.MaxValue"/> and the exit is a key save.</summary>
    private static EngineStatus S7() => Status(
        gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.DeepL] = Refused(TranslationErrorKind.AuthFailed),
        },
        configured: new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx, ProviderIds.DeepL },
        outcome: Answered(ProviderIds.GoogleDict));

    /// <summary>S8 — an Azure quota ran out; the free engines carry on, and the chip says both.</summary>
    private static EngineStatus S8() => Status(
        gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.Azure] = Paused(3600, TranslationErrorKind.QuotaExhausted),
        },
        configured: new[] { ProviderIds.GoogleDict, ProviderIds.GoogleGtx, ProviderIds.Azure },
        outcome: Answered(ProviderIds.GoogleDict, ProviderIds.Azure));

    /// <summary>
    /// <b>The eight chips of §2.1, verbatim.</b> A row that changes here changes what a player reads
    /// in the corner of the app they never open a tab for, so it is written as literals rather than
    /// as a re-derivation of the code under test.
    /// </summary>
    [Fact]
    public void The_eight_states_render_the_decks_eight_chips()
    {
        Assert.Equal("● Google", Chip(S1()).Text);
        Assert.Equal("● Google (backup)", Chip(S2()).Text);
        Assert.Equal("○ Google paused 0:58", Chip(S3()).Text);
        Assert.Equal("● Offline", Chip(S4()).Text);
        Assert.Equal("○ All paused about 4 min", Chip(S5()).Text);
        Assert.Equal("⚠ No internet", Chip(S6()).Text);
        Assert.Equal("⚠ DeepL key refused", Chip(S7()).Text);
        Assert.Equal("● Google · Azure quota out", Chip(S8()).Text);
    }

    /// <summary>
    /// <b>NFR11 / AC 3, as the epic words it</b> — "each of the eight states produces a distinct
    /// glyph+word pair". Distinct <i>as text</i>: the assertion touches no brush at all, so a build
    /// that rendered S2 and S8 in the same gold still passes, and a build that rendered them with
    /// the same WORDS does not.
    /// </summary>
    [Fact]
    public void The_eight_states_are_distinguishable_with_the_palette_stripped_out()
    {
        var chips = new[] { S1(), S2(), S3(), S4(), S5(), S6(), S7(), S8() }
            .Select(s => Chip(s).Text).ToList();

        Assert.Equal(8, chips.Distinct(StringComparer.Ordinal).Count());

        // …and none of them is carried by its glyph alone either: strip the glyph and the eight
        // words are still eight.
        Assert.Equal(8, chips.Select(c => c[2..]).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// §2.3's vocabulary, and the reason it is worth a case: "no new fonts, images or colours" (§6)
    /// is an invariant a single pasted character breaks silently. Three glyphs, three brush keys
    /// that all exist in <c>Theme.xaml</c>, and nothing else.
    /// </summary>
    [Fact]
    public void Every_chip_uses_one_of_the_three_shipped_glyphs_and_a_Theme_brush()
    {
        foreach (var status in new[] { S1(), S2(), S3(), S4(), S5(), S6(), S7(), S8(),
                                       Status(stateKnown: false) })
        {
            var chip = MainWindow.ChipFor(status);
            Assert.Contains(chip.Glyph, new[] { "●", "○", "⚠" });
            Assert.Contains(chip.BrushKey,
                new[] { "TealBrush", "GoldBrush", "AccentBrush", "TextMutedBrush" });
            Assert.StartsWith(chip.Glyph + " ", chip.Label, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The colours §2.3's table actually assigns. Checked in ONE place, so every other case in this
    /// file can be about the words — which is the whole of NFR11's discipline.
    /// </summary>
    [Fact]
    public void The_brushes_are_the_decks_brushes()
    {
        Assert.Equal("TealBrush", Chip(S1()).Brush);          // serving you
        Assert.Equal("GoldBrush", Chip(S2()).Brush);          // serving you, but it is the backup
        Assert.Equal("TextMutedBrush", Chip(S3()).Brush);     // paused / not sending
        Assert.Equal("TealBrush", Chip(S4()).Brush);
        Assert.Equal("GoldBrush", Chip(S5()).Brush);
        Assert.Equal("AccentBrush", Chip(S6()).Brush);        // you may need to act
        Assert.Equal("AccentBrush", Chip(S7()).Brush);
        Assert.Equal("GoldBrush", Chip(S8()).Brush);
    }

    /// <summary>
    /// <b>Ruling E6-a's first second.</b> Until the warm-up has read <c>provider-state.json</c> the
    /// chip claims no health at all — it says so. It is not an engine state and carries no name.
    /// </summary>
    [Fact]
    public void Before_the_gate_state_is_read_the_chip_says_it_is_checking()
    {
        var chip = MainWindow.ChipFor(Status(stateKnown: false));
        Assert.Equal("○ checking…", chip.Label);
        Assert.Equal("TextMutedBrush", chip.BrushKey);

        // …and it does not ask for the 1 Hz tick: "checking" ends when the warm-up finishes, which
        // is a completed task and not a second passing (NFR7 — the timer never runs idle).
        Assert.False(Status(stateKnown: false).NeedsTick);
    }

    /// <summary>
    /// §3.0 rule 2: the <c>· backup</c> suffix is dropped when the name already carries it. A chip
    /// reading <c>Google (backup) · backup</c> is the failure this rule was written for.
    /// </summary>
    [Fact]
    public void A_name_that_already_says_backup_does_not_say_it_twice()
    {
        Assert.DoesNotContain("· backup", Chip(S2()).Text, StringComparison.Ordinal);
        Assert.Equal("Edge · backup", UserMessages.EngineChipBackup(ProviderIds.Edge));
    }

    /// <summary>
    /// <b>AC 4 of E7.S2, obeyed rather than re-negotiated</b>: at most ONE countdown per window. The
    /// LIVE status line steps its own clock while the whole chain is paused, and the chip beside it
    /// on the same window therefore drops its number — keeping the glyph and the word, which is
    /// where NFR11 says the information lives anyway.
    /// </summary>
    [Fact]
    public void The_chip_drops_its_clock_when_the_status_line_is_already_stepping_one()
    {
        Assert.Equal("○ All paused about 4 min", Chip(S5()).Text);
        Assert.Equal("○ All paused", Chip(S5(), statusLineOwnsTheClock: true).Text);

        // The glyph and the brush do NOT change with it: the state is the same state.
        Assert.Equal(Chip(S5()).Brush, Chip(S5(), statusLineOwnsTheClock: true).Brush);
    }

    // =============================================================================================
    //  §2.3 — the tooltip
    // =============================================================================================

    /// <summary>
    /// One line per <see cref="ProviderIds.All"/> member, in chain order, aligned — and every one of
    /// them rendered honestly, including the two tiers that do not exist: Edge is not shipped
    /// (ruling E3-d) and the offline engine has not landed (E8). §2.3 lists both, and a tooltip that
    /// silently dropped a row the About tab shows would be worse than one that explains it.
    /// </summary>
    [Fact]
    public void The_tooltip_lists_the_whole_chain_in_order_one_line_per_provider()
    {
        var lines = MainWindow.EngineTooltip(S3()).Split('\n');

        Assert.Equal(ProviderIds.All.Count, lines.Length);
        Assert.Equal("Google", lines[0].Split("  ")[0]);
        Assert.StartsWith("Edge", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Google (backup)", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("DeepL", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("Azure", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("Offline engine", lines[5], StringComparison.Ordinal);

        // Aligned: every state column starts in the same place, which is what "aligned" means when
        // the padding is spaces in the composer rather than a Grid (§2.3 / T4).
        var columns = lines.Select(l => l.IndexOf('●') is var d && d >= 0
                                        ? d : l.IndexOfAny(new[] { '○', '—' })).ToList();
        Assert.Single(columns.Distinct());
    }

    /// <summary>§2.3's rows, verbatim, for the state each provider is actually in.</summary>
    [Fact]
    public void The_tooltip_rows_are_the_decks_rows()
    {
        var lines = MainWindow.EngineTooltip(S3()).Split('\n');

        Assert.Contains("○ paused — retries in 0:58", lines[0]);
        Assert.Contains("(asked us to slow down)", lines[0]);
        Assert.Contains("— not available", lines[1]);      // Edge, ruling E3-d
        Assert.Contains("● in use", lines[2]);             // google-gtx answered
        Assert.Contains("— not set", lines[3]);            // no DeepL key
        Assert.Contains("— not set", lines[4]);            // no Azure key
        Assert.Contains("— not installed", lines[5]);      // E8 has not shipped

        // A ready tier that has not answered anything reads "ready", not "in use".
        Assert.Contains("● ready", MainWindow.EngineTooltip(S1()).Split('\n')[2]);
    }

    /// <summary>
    /// The house rules §2.3 states and the epic repeats: <b>no jargon, no HTTP codes</b>, and no
    /// provider internal (I11). The same scan E7.S1 writes for the deck, pointed at the tooltip.
    /// </summary>
    [Fact]
    public void The_tooltip_carries_no_status_code_and_no_provider_id()
    {
        foreach (var status in new[] { S1(), S2(), S3(), S4(), S5(), S6(), S7(), S8() })
        {
            var text = MainWindow.EngineTooltip(status);

            // The INTERNAL spellings only: "edge", "deepl" and "azure" happen to be their own
            // user-facing names modulo case (§3.0's table), and forbidding those would forbid the
            // tooltip from naming three of its six providers.
            foreach (var id in ProviderIds.All.Where(
                         i => !string.Equals(i, ProviderNames.Display(i), StringComparison.OrdinalIgnoreCase)))
                Assert.DoesNotContain(id, text, StringComparison.OrdinalIgnoreCase);

            // No 4xx/5xx anywhere. A countdown is "0:58" / "about 4 min", so a bare digit triplet
            // can only have come from a status code or from an invented number.
            foreach (var word in text.Split(' ', '\n', '(', ')'))
                Assert.False(word.Length == 3 && word.All(char.IsDigit),
                             $"the tooltip must carry no status code — {word} in: {text}");
        }
    }

    /// <summary>
    /// AC 5 / §6: the tooltip is a <b>string</b>, so the dark <c>ToolTip</c> style in
    /// <c>Theme.xaml</c> — which <c>project-context.md</c> calls load-bearing — applies untouched. A
    /// <c>StackPanel</c> or a <c>ContentTemplate</c> here is what re-styles it by accident.
    /// </summary>
    [Fact]
    public void The_tooltip_is_plain_text()
    {
        object tooltip = MainWindow.EngineTooltip(S5());
        Assert.IsType<string>(tooltip);
        Assert.DoesNotContain('<', (string)tooltip);
    }

    // =============================================================================================
    //  EngineStatus itself — the pure half, over snapshots and a fake clock
    // =============================================================================================

    /// <summary>
    /// Ruling <b>E3-a</b>, the one this whole record could get wrong in a way nobody would notice:
    /// paused is <c>BlockedUntil &gt; now</c> and never <c>State == Open</c>, because that state
    /// deliberately outlives its window (R-01 — an app correctly paused for ever).
    /// </summary>
    [Fact]
    public void A_gate_whose_window_has_elapsed_is_ready_however_open_its_state_says_it_is()
    {
        var elapsed = new GateSnapshot(GateState.Open, Now - TimeSpan.FromSeconds(1), 3,
                                       TranslationErrorKind.RateLimited);
        var status = Status(gates: new(StringComparer.Ordinal) { [ProviderIds.GoogleDict] = elapsed });

        Assert.Equal(EngineState.Ready, status.For(ProviderIds.GoogleDict)!.State);
        Assert.False(status.AllReadTiersPaused);
        Assert.False(status.NeedsTick);
    }

    /// <summary>
    /// The tick's own question (NFR7). A window with an end to it needs the 1 Hz repaint; the
    /// <c>AuthFailed</c> sentinel does not — it counts down to nothing, for ever, and its way out is
    /// a key save. This is what E7.S2's "paint-free 1 Hz tick on an AuthFailed gate" costs once the
    /// chip is the thing asking.
    /// </summary>
    [Fact]
    public void Only_a_pause_with_an_end_to_it_asks_for_the_tick()
    {
        Assert.True(S3().NeedsTick);
        Assert.True(S5().NeedsTick);
        Assert.False(S1().NeedsTick);

        var sentinelOnly = Status(gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.GoogleDict] = Refused(TranslationErrorKind.AuthFailed),
            [ProviderIds.GoogleGtx] = Refused(TranslationErrorKind.AuthFailed),
        });
        Assert.True(sentinelOnly.AllReadTiersPaused);
        Assert.False(sentinelOnly.NeedsTick);
    }

    /// <summary>
    /// A tier the user does not have is <b>Off with a reason</b>, and it is that even when a gate
    /// from earlier in the session still carries a window for it — an Azure key cleared mid-session
    /// leaves its gate behind, and reporting that stale window as a pause would tell a player to
    /// wait for an engine that is in no chain at all.
    /// </summary>
    [Fact]
    public void An_unconfigured_provider_is_off_with_a_reason_even_over_a_stale_gate()
    {
        var status = Status(gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.Azure] = Paused(600, TranslationErrorKind.QuotaExhausted),
        });

        var azure = status.For(ProviderIds.Azure)!;
        Assert.Equal(EngineState.Off, azure.State);
        Assert.Equal(EngineStatus.NotSet, azure.OffReason);
        Assert.Null(azure.PausedUntil);

        // …and the chip is not S8: there is no key to have a quota.
        Assert.Equal("● Google", Chip(status with { LastAnswered = ProviderIds.GoogleDict }).Text);
    }

    /// <summary>
    /// <b>S2 is two conditions, not one</b> (ruling E3-b). A lower tier answered AND something above
    /// it was skipped: a chain whose first tier answered skips nothing, and "not the first tier" on
    /// its own is meaningless for a chain with one tier.
    /// </summary>
    [Fact]
    public void A_fallback_needs_both_a_lower_answer_and_a_skipped_tier()
    {
        Assert.False(Status(outcome: Answered(ProviderIds.GoogleDict)).FellBack);
        Assert.False(Status(outcome: new ChainTranslator.Outcome(
            ProviderIds.GoogleGtx, Array.Empty<(string, string)>(), null, null)).FellBack);
        Assert.True(Status(outcome: Answered(ProviderIds.GoogleGtx, ProviderIds.GoogleDict)).FellBack);

        // Nothing has been translated yet: no name is invented (§3.0 rule 1).
        Assert.Null(Status().LastAnswered);
        Assert.False(Status().FellBack);
    }

    /// <summary>
    /// The earliest instant a paused read tier comes back — the number S5's chip counts down. The
    /// sentinel is not a candidate, so a chain holding one real window and one refused key still
    /// counts down to the real one.
    /// </summary>
    [Fact]
    public void The_soonest_retry_is_the_earliest_honest_window_of_the_read_chain()
    {
        Assert.Equal(In(200), S5().SoonestReadRetry);
        Assert.Null(S1().SoonestReadRetry);

        var mixed = Status(gates: new(StringComparer.Ordinal)
        {
            [ProviderIds.GoogleDict] = Refused(TranslationErrorKind.AuthFailed),
            [ProviderIds.GoogleGtx] = Paused(45, TranslationErrorKind.RateLimited),
        });
        Assert.Equal(In(45), mixed.SoonestReadRetry);
    }

    /// <summary>
    /// §3.5's recovery line, the one E7.S3 owns: the player is told the chip changed for a reason.
    /// Null with no name to say, and the caller then writes nothing — "Back on the translation
    /// service." is a sentence with the information taken out of it.
    /// </summary>
    [Fact]
    public void The_recovery_notice_names_the_engine_or_says_nothing()
    {
        Assert.Equal("Back on Google.", UserMessages.BackOn(ProviderIds.GoogleDict));
        Assert.Equal("Back on Google (backup).", UserMessages.BackOn(ProviderIds.GoogleGtx));
        Assert.Null(UserMessages.BackOn(null));
        Assert.Null(UserMessages.BackOn("something-new"));
    }
}

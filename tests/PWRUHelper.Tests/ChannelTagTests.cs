using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The game draws a coloured channel badge («Мир», «Клан», «Сист.») in front of every chat line.
/// It must never reach the translator, and never be re-drawn in the feed.
///
/// Every string below is REAL output of the Windows OCR engine on a genuine screenshot of the game
/// chat (2026-07-12) — including its mistakes. The OCR reads the badge in two very different ways:
///   • background filter OFF → inline and lower-cased: "мир Hokasse: ОР вар прист +3"
///   • background filter ON  → the chip's own text becomes legible and lands on a LINE OF ITS OWN,
///     with all the badges grouped ahead of the messages. Those lines used to be glued together
///     into a fake message ("Мир Мир Клан Клан") and sent off to be translated.
/// </summary>
public class ChannelTagTests
{
    // ---- badge-only lines (what the "Boost contrast" filter produces — the default) ----

    [Fact]
    public void The_badges_the_filter_reads_as_their_own_lines_never_become_a_message()
    {
        // Verbatim OCR of the screenshot with Boost contrast on: ten badges, then the messages.
        var lines = new[]
        {
            "Мир", "Мир", "Клан", "Клан", "Клан", "Клан", "Мир", "Клан", "Мир", "Мир",
            "Hokasse: ОР вар прист +3",
            "kizotis: привет",
        };

        var messages = TextMatching.SplitChatMessages(lines);

        Assert.Equal(new[] { "Hokasse: ОР вар прист +3", "kizotis: привет" }, messages);
    }

    [Fact]
    public void A_badge_line_does_not_glue_itself_onto_the_message_before_it()
    {
        var messages = TextMatching.SplitChatMessages(new[] { "kizotis: привет", "Клан", "Атарион: доброе утро" });

        Assert.Equal(new[] { "kizotis: привет", "Атарион: доброе утро" }, messages);
    }

    // ---- inline badges (filter off) ----

    [Theory]
    [InlineData("мир Hokasse: ОР вар прист +3", "Hokasse", "ОР вар прист +3")]
    [InlineData("Клан Kizotis: привет", "Kizotis", "привет")]
    // A speck of the chip's border, read as a letter, sitting in front of the tag.
    [InlineData("т мир Hokasse: ОР вар прист +1", "Hokasse", "ОР вар прист +1")]
    // The OCR's rendering of the badge border, which has no letters and isn't part of the nick.
    [InlineData("[32) Атарион: доброе утро", "Атарион", "доброе утро")]
    public void An_inline_badge_is_stripped_and_the_nickname_survives(string ocrLine, string nick, string body)
    {
        var message = Assert.Single(TextMatching.SplitChatMessages(new[] { ocrLine }));

        Assert.Equal($"{nick}: {body}", message);
        Assert.Equal((nick, body), TextMatching.SplitSpeakerStrict(message));
    }

    [Fact]
    public void A_badged_line_starts_a_new_message_even_when_the_nickname_is_lost()
    {
        // «Сист.» — with the dot the OCR keeps. Before the fix this line was not recognised as a
        // header (its colon sits far too deep) and was silently glued onto the PREVIOUS player's
        // message, mangling both.
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "мир Лунаед: ГИ 01ympus приглашает игроков 100+ 2 рб КХ",
            "ЗУ ДЖ общение ТС",                                            // genuine wrap — must stay glued
            "сист. Head of the Secret ViIlage announces Ioudly: JezibeI",   // new message, badge dropped
        });

        // Two messages: the player's (with its wrapped tail glued back on) and the system one.
        Assert.Equal(2, messages.Count);
        Assert.Equal("Лунаед: ГИ 01ympus приглашает игроков 100+ 2 рб КХ ЗУ ДЖ общение ТС", messages[0]);
        Assert.StartsWith("Head of the Secret", messages[1]);
        Assert.DoesNotContain("сист", messages[1], StringComparison.OrdinalIgnoreCase);
    }

    // ---- the tag vocabulary ----

    [Theory]
    [InlineData("Мир")]        // world (yellow)
    [InlineData("Клан")]       // faction (blue)
    [InlineData("Сист.")]      // system (red) — the trailing dot is part of what the OCR reads
    [InlineData("Отряд")]      // squad (green)
    [InlineData("Обыч.")]      // normal / local (white)
    [InlineData("Обычный")]
    [InlineData("[Торговля]")] // the bracketed form
    [InlineData("Mиp")]        // Latin look-alikes the OCR mixes in — folded to Cyrillic
    public void Every_channel_the_game_shows_is_recognised(string badge)
    {
        Assert.True(TextMatching.TryPeelChannelTag(badge, out var rest), $"'{badge}' should read as a channel badge.");
        Assert.Equal("", rest);
    }

    [Theory]
    [InlineData("kizotis: привет")]                 // a plain nickname is not a badge
    [InlineData("доброе утро")]                     // nor is ordinary chat
    [InlineData("ЗУ ДЖ общение ТС")]                // "общение" must not match the "обыч" family
    public void Ordinary_text_is_never_mistaken_for_a_badge(string line)
    {
        Assert.False(TextMatching.TryPeelChannelTag(line, out var rest));
        Assert.Equal(line, rest);
    }
}

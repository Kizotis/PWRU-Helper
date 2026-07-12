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
    [InlineData("Лично")]      // private / whisper
    [InlineData("Оснв.")]      // main
    [InlineData("Групп.")]     // group
    [InlineData("грул.")]      // …which the OCR reliably reads like this. Ground truth beats theory.
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

    // ---- a channel WORD inside a sentence is not a badge ----

    [Fact]
    public void A_wrapped_line_that_merely_begins_with_a_channel_word_keeps_every_word()
    {
        // Real message: a player listing the chat channels. Its wrapped tail starts with "фракция,"
        // — a channel name. Treating that as a badge peeled the word off and split the message in
        // two: the player's own words were silently deleted. A badge never carries a comma, and a
        // real chat line always has a colon; both are required now.
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "Оснв.",                                                     // the badge, on its own line
            "kizotis: мир, некоторые отряды, частные, сообщения,",
            "фракция, гильдия, объявление, админис",                     // wrapped tail — must survive whole
        });

        var message = Assert.Single(messages);
        Assert.Equal("kizotis: мир, некоторые отряды, частные, сообщения, фракция, гильдия, объявление, админис",
                     message);
    }

    // ---- the whole of a real screen ----

    [Fact]
    public void A_real_screenful_of_chat_comes_out_as_clean_messages()
    {
        // Verbatim OCR (Boost contrast) of a screenshot showing one message per channel.
        var lines = new[]
        {
            "Мир", "Лично", "Мир", "Оснв.",
            "винчик: в ТС СЛОЖКУ ПРИСТ.",
            "kizotis: мир, некоторые отряды, частные, сообщения,",
            "фракция, гильдия, объявление, админис",
            "грул. kizotis: Всем привет",
            "клан kizotis: Ьоор",
            "мир Ianhua: Договор смены класса продаёт кто?",
            "грул. м@лыШ: привет)",
            "грул. ИНЕЙ: приветы",
            "мир —V00D00-: шоп продает всегда",
        };

        var messages = TextMatching.SplitChatMessages(lines);

        // Not one of the four badges became a message of its own…
        Assert.Equal(8, messages.Count);
        Assert.DoesNotContain(messages, m => m is "Мир" or "Лично" or "Оснв." or "грул." or "клан");
        // …and no badge is left clinging to the front of a message.
        Assert.All(messages, m => Assert.False(TextMatching.TryPeelChannelTag(m, out _),
                                               $"a badge survived in: {m}"));

        var speakers = messages.Select(m => TextMatching.SplitSpeakerStrict(m).Speaker).ToList();
        Assert.Equal(
            // "винчик" is empty on purpose — see the known limitation below.
            new[] { "", "kizotis", "kizotis", "kizotis", "Ianhua", "м@лыШ", "ИНЕЙ", "V00D00" },
            speakers);
    }

    [Fact]
    public void KNOWN_LIMITATION_an_all_lowercase_cyrillic_nickname_is_still_translated_with_the_message()
    {
        // "винчик: в ТС СЛОЖКУ ПРИСТ." — a real player. SplitSpeakerStrict only keeps a nickname when
        // it carries a capital, a Latin letter or a digit, because a chat BODY can itself start
        // "тс: сбор у входа" and the slang "тс" would otherwise be stolen as a fake speaker (that bug
        // shipped once, in v0.11.1). A lowercase Cyrillic nickname is indistinguishable from that by
        // shape alone — and the badge, which would prove it IS a header, is read on a separate line
        // by the OCR, so it can't be tied back to this one.
        //
        // Cost: the nickname is sent to the translator with the message instead of being kept grey.
        // Telling the two apart needs the slang glossary (a body starting with a KNOWN slang word is
        // not a speaker), which TextMatching deliberately doesn't depend on today.
        //
        // This test asserts what the app really does. Change it when the behaviour is fixed.
        var (speaker, body) = TextMatching.SplitSpeakerStrict("винчик: в ТС СЛОЖКУ ПРИСТ.");

        Assert.Equal("", speaker);
        Assert.Equal("винчик: в ТС СЛОЖКУ ПРИСТ.", body);
    }

    [Fact]
    public void A_badge_the_OCR_prints_after_a_message_does_not_end_up_inside_it()
    {
        // The badges do not always come out grouped ahead of the messages — here one lands right
        // after the whisper it belongs to, and used to be glued onto its text ("фул Лично").
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "Антарес whispers: фул",
            "Лично",
            "Wei Xiaobao: 0kika becomes the owner of а геа1 rarity.",
        });

        Assert.Equal(2, messages.Count);
        Assert.Equal("Антарес whispers: фул", messages[0]);
        Assert.StartsWith("Wei Xiaobao:", messages[1]);
    }
}

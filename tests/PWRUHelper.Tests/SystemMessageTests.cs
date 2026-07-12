using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The game prints its own announcements into the chat — a rarity drop, a squad join, the header
/// above a whisper. They carry no "Nick:" header, and their «Сист.» badge is red on a dark
/// background, so the contrast filter erases it and the OCR never reads it. The result: such a line
/// looked exactly like the WRAPPED TAIL of the player message above it, and was glued onto it —
/// corrupting the player's text and the translation of it.
///
/// Every string here is real Windows-OCR output from a screenshot of the game chat, garbling and
/// all: the engine reads these English announcements through a Russian model, so "You are" comes
/// back as "Уои аге" and "loudly" as "Ioudly".
/// </summary>
public class SystemMessageTests
{
    [Fact]
    public void An_announcement_does_not_glue_onto_the_player_message_above_it()
    {
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "мир с й к э: только Прист в Тс сложку",                        // a player
            "Elder of the City of Swords announces Ioudly: GARIk",          // the game
            "upgrades [±Great Cbck] to +9",                                 // …wrapping over two lines
        });

        Assert.Equal(2, messages.Count);
        Assert.Equal("с й к э: только Прист в Тс сложку", messages[0]);   // intact, and badge-free
        // The announcement's OWN tail still glues back onto it — it carries no marker, so it can't
        // be mistaken for the start of anything.
        Assert.Equal("Elder of the City of Swords announces Ioudly: GARIk upgrades [±Great Cbck] to +9",
                     messages[1]);
    }

    [Fact]
    public void Two_announcements_in_a_row_stay_two_messages()
    {
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "Elder of the City of Swords announces Ioudly: GARIk",
            "upgrades [±Great Cbck] to +9",
            "Elder of the City of Swords announces Ioudly: GARIk",
            "upgrades [±NeckIace of В!ие Mist] to +9",
        });

        Assert.Equal(2, messages.Count);
        Assert.EndsWith("[±Great Cbck] to +9", messages[0]);
        Assert.EndsWith("[±NeckIace of В!ие Mist] to +9", messages[1]);
    }

    [Fact]
    public void A_marker_from_the_TAIL_of_an_announcement_must_not_cut_it_in_half()
    {
        // "…has become the supreme deity!" ENDS an announcement. If phrases like that counted as a
        // system line, this one announcement would arrive as two half-sentences — which is exactly
        // what happened before only OPENING phrases were kept as markers.
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "The heavens gateway have opened! Мася has",
            "Ьесоте the supreme deity: [Whewl!",
        });

        var message = Assert.Single(messages);
        Assert.Equal("The heavens gateway have opened! Мася has Ьесоте the supreme deity: [Whewl!", message);
    }

    [Fact]
    public void The_whisper_header_the_game_prints_is_its_own_message()
    {
        // "You are speaking to <player>:" — read by a Russian OCR model as "Уои аге speaking to".
        // Its colon sits far too deep to pass as a "Nick:" header, so it used to glue onto the line
        // above it.
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "винчик: в ТС СЛОЖКУ ПРИСТ.",
            "Уои аге speaking to Rampage: +сик",
        });

        Assert.Equal(2, messages.Count);
        Assert.Equal("винчик: в ТС СЛОЖКУ ПРИСТ.", messages[0]);
        Assert.Equal("Уои аге speaking to Rampage: +сик", messages[1]);
    }

    [Fact]
    public void A_squad_join_is_its_own_message()
    {
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "грул. ИНЕЙ: приветы",
            "B!oodCat joined the squad",
        });

        Assert.Equal(new[] { "ИНЕЙ: приветы", "B!oodCat joined the squad" }, messages);
    }

    // ---- what counts as a system line ----

    [Theory]
    [InlineData("Elder of the City of Swords announces Ioudly: GARIk")]   // capital I for l
    [InlineData("Elder of the Ctty of Swords аппои")]                     // Cyrillic "аппои" = "annou"
    [InlineData("Уои аге speaking to Св к э: +siker")]                    // Cyrillic "Уои аге" = "You are"
    [InlineData("ИНЕЙ joined the squad")]
    [InlineData("The heavens gateway have opened! Мася has")]
    public void The_game_s_own_announcements_are_recognised_through_the_OCR_s_garbling(string line)
        => Assert.True(TextMatching.IsSystemLine(line), $"'{line}' should read as a system line.");

    [Theory]
    [InlineData("kizotis: привет")]
    [InlineData("мир Ianhua: Договор смены класса продаёт кто?")]
    [InlineData("Всем привет")]
    [InlineData("мир, некоторые отряды, частные, сообщения,")]
    [InlineData("фракция, гильдия, объявление, админис")]
    public void A_players_message_is_never_taken_for_a_system_line(string line)
        => Assert.False(TextMatching.IsSystemLine(line), $"'{line}' is a player's message.");
}

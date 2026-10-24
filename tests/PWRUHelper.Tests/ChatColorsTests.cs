using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The feed repeats the game's chat colours (the owner, 2026-10-24): a word's colour is voted for
/// by the pixels under it, a message finds its own words again among the frame's, and the
/// translation gets its body's colour plus every coloured piece that survived translation.
///
/// The palette values are the measured peaks of the owner's seven chat screenshots; the end-to-end
/// check of this pipeline on those screenshots (real OCR, every channel recognised) was run by hand
/// and is not part of the suite — the screenshots carry other players' names and stay off the
/// public repo, and CI has no Russian OCR pack.
/// </summary>
public class ChatColorsTests
{
    private const uint Group = 0x00FF00, World = 0xFFE400, Faction = 0x00FFFF, Item = 0xFF6600,
                       Epic = 0xAC34FF, System = 0xFF3300, Whisper = 0x3D8BFF;

    // ---- the vote ----

    /// <summary>A 20×10 BGRA image of <paramref name="bg"/> with <paramref name="textPixels"/>
    /// pixels of <paramref name="text"/> drawn in its top rows.</summary>
    private static byte[] Image(uint bg, uint text, int textPixels, out int stride)
    {
        const int w = 20, h = 10;
        stride = w * 4;
        var buf = new byte[stride * h];
        for (int i = 0; i < w * h; i++)
        {
            uint c = i < textPixels ? text : bg;
            buf[i * 4] = (byte)c; buf[i * 4 + 1] = (byte)(c >> 8); buf[i * 4 + 2] = (byte)(c >> 16); buf[i * 4 + 3] = 255;
        }
        return buf;
    }

    [Fact]
    public void A_word_drawn_in_the_group_green_reads_as_the_group_colour()
    {
        // Slightly off the palette, as an anti-aliased glyph core is, on the dark outline.
        var img = Image(bg: 0x101418, text: 0x08F010, textPixels: 40, out int stride);

        Assert.Equal(Group, ChatPalette.SampleBox(img, 20, 10, stride, 0, 0, 20, 10));
    }

    [Fact]
    public void The_whisper_blue_is_read_from_the_game_colour_and_painted_lighter()
    {
        var img = Image(bg: 0x000000, text: 0x0060F8, textPixels: 40, out int stride);

        Assert.Equal(Whisper, ChatPalette.SampleBox(img, 20, 10, stride, 0, 0, 20, 10));
    }

    [Theory]
    [InlineData(0xC8B070u)]   // the beige wooden deck behind the chat on the owner's screenshots
    [InlineData(0x3060A8u)]   // the blue sea
    public void Game_scenery_votes_for_no_colour(uint scenery)
    {
        var img = Image(bg: scenery, text: scenery, textPixels: 0, out int stride);

        Assert.Null(ChatPalette.SampleBox(img, 20, 10, stride, 0, 0, 20, 10));
    }

    [Fact]
    public void A_few_stray_pixels_do_not_paint_a_word()
    {
        var img = Image(bg: 0x101418, text: 0x00F8F8, textPixels: 2, out int stride);

        Assert.Null(ChatPalette.SampleBox(img, 20, 10, stride, 0, 0, 20, 10));
    }

    [Fact]
    public void A_box_outside_the_image_reads_nothing_rather_than_throwing()
    {
        var img = Image(bg: 0x101418, text: 0x00F8F8, textPixels: 40, out int stride);

        Assert.Null(ChatPalette.SampleBox(img, 20, 10, stride, 30, 30, 60, 60));
    }

    [Fact]
    public void The_two_yellows_split_their_votes_by_the_nearest()
    {
        Assert.Equal("world", ChatPalette.Entries[ChatPalette.Nearest(0xF8, 0xE0, 0x00)].Name);
        Assert.Equal("announcer", ChatPalette.Entries[ChatPalette.Nearest(0xF8, 0xF8, 0x10)].Name);
        Assert.Equal("nick", ChatPalette.Entries[ChatPalette.Nearest(0xF8, 0xD8, 0x88)].Name);
    }

    // ---- a message finds its words ----

    [Fact]
    public void A_message_takes_its_colours_from_its_own_words_skipping_the_badge()
    {
        var words = new[]
        {
            new OcrWord("Мир", null), new OcrWord("Igrushka:", ChatPalette.Nick),
            new OcrWord("продам", World), new OcrWord("слезу", World),
            new OcrWord("Клан", null), new OcrWord("Anmut:", ChatPalette.Nick),
            new OcrWord("нет", Faction), new OcrWord("уже", Faction),
        };

        var runs = ChatColors.ColorBody("Anmut: нет уже", "нет уже", words);

        Assert.Equal(new[] { new ChatRun("нет уже", Faction) }, runs);
    }

    [Fact]
    public void Two_identical_short_bodies_are_told_apart_by_the_nickname_in_front()
    {
        var words = new[]
        {
            new OcrWord("Igrushka:", ChatPalette.Nick), new OcrWord("да", World),
            new OcrWord("Anmut:", ChatPalette.Nick), new OcrWord("да", Faction),
        };

        Assert.Equal(Faction, Assert.Single(ChatColors.ColorBody("Anmut: да", "да", words)!).Rgb);
        Assert.Equal(World, Assert.Single(ChatColors.ColorBody("Igrushka: да", "да", words)!).Rgb);
    }

    [Fact]
    public void The_runs_of_an_announcement_put_its_body_back_together_exactly()
    {
        var words = new[]
        {
            new OcrWord("Got", Item), new OcrWord("10", Item),
            new OcrWord("[Cloud", Epic), new OcrWord("dust]", Epic),
        };
        const string body = "Got 10 [Cloud dust]";

        var runs = ChatColors.ColorBody(body, body, words)!;

        Assert.Equal(body, string.Concat(runs.Select(r => r.Text)));
        Assert.Equal(new[] { new ChatRun("Got 10 ", Item), new ChatRun("[Cloud dust]", Epic) }, runs);
    }

    [Fact]
    public void A_word_whose_colour_was_not_read_takes_its_neighbours()
    {
        var words = new[] { new OcrWord("на", Group), new OcrWord("госта", null), new OcrWord("все", Group) };

        Assert.Equal(new[] { new ChatRun("на госта все", Group) }, ChatColors.ColorBody("на госта все", "на госта все", words));
    }

    [Theory]
    [InlineData(false)]   // no words at all: the filter's frame, or a test
    [InlineData(true)]    // words, but not this message's
    public void A_message_that_cannot_be_placed_stays_uncoloured(bool someWords)
    {
        var words = someWords ? new[] { new OcrWord("совсем", World), new OcrWord("другое", World) } : Array.Empty<OcrWord>();

        Assert.Null(ChatColors.ColorBody("Anmut: нет уже", "нет уже", words));
    }

    // ---- the translation (A + B) ----

    [Fact]
    public void A_players_message_is_translated_in_its_channel_colour()
    {
        var body = new[] { new ChatRun("продам слезу, цены в пм", World) };

        Assert.Equal(new[] { new ChatRun("selling a tear, prices in PM", World) },
                     ChatColors.ColorTranslation(body, "selling a tear, prices in PM"));
    }

    [Fact]
    public void An_item_that_survives_translation_keeps_its_own_colour()
    {
        var body = new[]
        {
            new ChatRun("Карачун ", ChatPalette.Nick),
            new ChatRun("becomes the owner of a real rarity. It's ", System),
            new ChatRun("[Emerald Rune Lv.10]!", Item),
        };

        var runs = ChatColors.ColorTranslation(body, "Karachun becomes the owner of a real rarity. It's [Emerald Rune Lv.10]!");

        Assert.Equal(new[]
        {
            new ChatRun("Karachun becomes the owner of a real rarity. It's ", System),
            new ChatRun("[Emerald Rune Lv.10]", Item),
            new ChatRun("!", System),
        }, runs);
    }

    [Fact]
    public void A_lone_digit_does_not_paint_every_digit_of_the_translation()
    {
        var body = new[] { new ChatRun("1 ", Item), new ChatRun("исчез из сумки 1 раз", System) };

        var runs = ChatColors.ColorTranslation(body, "disappeared from the bag 1 time");

        Assert.Equal(new[] { new ChatRun("disappeared from the bag 1 time", System) }, runs);
    }

    [Theory]
    [InlineData("…")]
    [InlineData("(could not translate)")]
    public void A_placeholder_row_keeps_the_apps_own_colour(string placeholder)
    {
        var body = new[] { new ChatRun("привет", Group) };

        Assert.Equal(new[] { new ChatRun(placeholder, null) }, ChatColors.ColorTranslation(body, placeholder));
    }

    [Fact]
    public void A_row_with_no_known_colours_is_one_uncoloured_run()
        => Assert.Equal(new[] { new ChatRun("hello", null) }, ChatColors.ColorTranslation(null, "hello"));

    // ---- the colour proves a nickname ----

    [Fact]
    public void A_pale_yellow_lowercase_nickname_is_kept_out_of_the_translation()
    {
        // Real: «заткнись: че где красивая пушка» on the owner's screenshot. «заткнись» is a player,
        // and the translator turned them into "shut up" because the text alone couldn't tell.
        const string message = "заткнись: че где красивая пушка";
        var words = new[]
        {
            new OcrWord("заткнись:", ChatPalette.Nick), new OcrWord("че", Group), new OcrWord("где", Group),
            new OcrWord("красивая", Group), new OcrWord("пушка", Group),
        };

        var (speaker, body, colors) = ChatColors.Colorize(message, TextMatching.SplitSpeakerStrict(message), words);

        Assert.Equal("заткнись", speaker);
        Assert.Equal("че где красивая пушка", body);
        Assert.Equal(new[] { new ChatRun("че где красивая пушка", Group) }, colors);
    }

    [Fact]
    public void Slang_in_the_body_colour_is_still_not_mistaken_for_a_nickname()
    {
        // The reason SplitSpeakerStrict is strict: "тс:" is slang, and here it is drawn in the body's colour.
        const string message = "тс: сбор у входа";
        var words = new[] { new OcrWord("тс:", World), new OcrWord("сбор", World), new OcrWord("у", World), new OcrWord("входа", World) };

        var (speaker, body, _) = ChatColors.Colorize(message, TextMatching.SplitSpeakerStrict(message), words);

        Assert.Equal("", speaker);
        Assert.Equal(message, body);
    }
}

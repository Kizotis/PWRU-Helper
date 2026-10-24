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

    // ---- message boundaries from colours (the owner's three leftovers, 2026-10-24) ----
    // Every line below is the real OCR of the owner's screenshots, with the colour each word voted for.

    private const uint Nick = ChatPalette.Nick;

    private static IReadOnlyList<OcrWord> W(params (string Text, uint? Rgb)[] words)
        => words.Select(w => new OcrWord(w.Text, w.Rgb)).ToList();

    [Fact]
    public void The_chips_debris_in_front_of_a_nickname_is_dropped()
    {
        Assert.Equal("Онегатива: ток в реально жизни", ChatColors.RepairHeader("[З— Онегатива: ток в реально жизни",
            W(("[З—", null), ("Онегатива:", Nick), ("ток", Faction), ("в", Faction), ("реально", Faction), ("жизни", Faction))));
        Assert.Equal("6iX9ine: Значит", ChatColors.RepairHeader("•[ЗђЗ*2 6iX9ine: Значит",
            W(("•[ЗђЗ*2", null), ("6iX9ine:", Nick), ("Значит", Faction))));
    }

    [Theory]
    [InlineData("[Зђ?) Frmr. шо", "Frmr: шо")]
    [InlineData("мир kibtaeer. ищу", "kibtaeer: ищу")]
    [InlineData("Frmr.", "Frmr:")]                         // a message that was only a smiley
    public void A_colon_the_OCR_read_as_a_dot_is_put_back(string line, string repaired)
    {
        var tokens = line.Split(' ');
        var words = tokens.Select((t, i) => new OcrWord(t,
            i == tokens.Length - 1 && tokens.Length > 1 && !t.EndsWith('.') ? Faction
            : t.EndsWith('.') ? Nick
            : t == "мир" ? World : null)).ToList();

        Assert.Equal(repaired, ChatColors.RepairHeader(line, words));
    }

    [Theory]
    // A name inside an announcement: pale yellow, but no punctuation — the line is left alone.
    [InlineData("MicoIash upgrades", 0xFFDC8Cu, 0xFF3300u)]
    // The whisper header: the name is the 5th word, not a header.
    [InlineData("You are speaking to увущька. hi", 0x3D8BFFu, 0x3D8BFFu)]
    public void A_name_that_is_not_a_header_is_left_alone(string line, uint first, uint rest)
    {
        var tokens = line.Split(' ');
        var words = tokens.Select((t, i) => new OcrWord(t, t.StartsWith("увущ") ? Nick : i == 0 ? first : rest)).ToList();

        Assert.Equal(line, ChatColors.RepairHeader(line, words));
    }

    [Fact]
    public void A_group_header_the_OCR_garbled_on_one_LIVE_frame_is_still_a_header()
    {
        // The owner's LIVE feed, 2026-10-24: «Груп. Kizotis: well» read once as "Гоъ•л. ki70tis•, well",
        // which came out of the translator as a second row "Go•l. ki70tis•,well".
        Assert.Equal("ki70tis: well", ChatColors.RepairHeader("Гоъ•л. ki70tis•, well",
            W(("Гоъ•л.", null), ("ki70tis•,", Nick), ("well", Group))));
        Assert.Equal("VBPIOCb: Куплю", ChatColors.RepairHeader("мир “VBPIOCb•. Куплю",
            W(("мир", World), ("“VBPIOCb•.", Nick), ("Куплю", World))));
    }

    [Fact]
    public void A_colon_inside_an_items_brackets_is_not_a_nickname_separator()
    {
        // The owner's screen: "It's [Courage Stone]!" read as "l€'$ [Cour_age:Stone]!" became a row of
        // its own, «1t's [Соигаде Stonel!», with "l€'$ [Cour_age" taken for a player.
        var lines = new[] { "Wei Xiaobao: mideo becomes the owner of a real rarity,", "l€'$ [Cour_age:Stone]!" };

        Assert.Single(TextMatching.SplitChatMessages(lines));
        Assert.False(TextMatching.TryParseHeader("l€'$ [Cour_age:Stone]!", out _, out _));
        // …while the badge's border, which opens a bracket it never closes, still leaves the header alone.
        Assert.True(TextMatching.TryParseHeader("[32) Атарион: доброе утро", out var nick, out _));
        Assert.Equal("Атарион", nick);
    }

    [Fact]
    public void A_line_whose_words_do_not_line_up_is_left_alone()
        => Assert.Equal("[З— Anmut: да", ChatColors.RepairHeader("[З— Anmut: да", W(("Anmut:", Nick), ("да", Faction))));

    [Fact]
    public void A_line_in_another_colour_family_is_not_glued_onto_the_message_above()
    {
        var lines = new[] { "6iX9ine: Жанна", "Got 10 [Cloud dust]", "Anmut: ну это от региона", "В ГШ ВАР МИСТДД В ПМ" };
        var words = new[]
        {
            W(("6iX9ine:", Nick), ("Жанна", Faction)),
            W(("Got", Item), ("10", Item), ("[Cloud", Epic), ("dust]", Epic)),
            W(("Anmut:", Nick), ("ну", Faction), ("это", Faction), ("от", Faction), ("региона", Faction)),
            W(("В", World), ("ГШ", World), ("ВАР", World), ("МИСТДД", World), ("В", World), ("ПМ", World)),
        };

        Assert.Equal(lines, TextMatching.SplitChatMessages(lines, words));
    }

    [Fact]
    public void A_wrapped_line_in_its_messages_colour_still_glues_back_on()
    {
        var lines = new[] { "kibtaeer: ищу парня для поиграть,", "погулять" };
        var words = new[]
        {
            W(("kibtaeer:", Nick), ("ищу", World), ("парня", World), ("для", World), ("поиграть,", World)),
            W(("погулять", World)),
        };

        Assert.Equal(new[] { "kibtaeer: ищу парня для поиграть, погулять" }, TextMatching.SplitChatMessages(lines, words));
    }

    [Fact]
    public void An_announcement_that_switches_colours_on_its_wrapped_line_stays_whole()
    {
        // "Name upgrades" then "[★★Starcatcher Shogri] to +9": red, then the item's orange — one message.
        var lines = new[] { "Сист. Elder of Dream Port announces loudly: Графиня upgrades", "[ * *Starcatcher Shogrl to +9" };
        var words = new[]
        {
            W(("Сист.", Nick), ("Elder", 0xFFFF00), ("of", 0xFFFF00), ("Dream", 0xFFFF00), ("Port", 0xFFFF00),
              ("announces", System), ("loudly:", System), ("Графиня", Nick), ("upgrades", System)),
            W(("[", Item), ("*", Item), ("*Starcatcher", Item), ("Shogrl", Item), ("to", System), ("+9", Group)),
        };

        var message = Assert.Single(TextMatching.SplitChatMessages(lines, words));
        Assert.EndsWith("upgrades [ * *Starcatcher Shogrl to +9", message);
    }

    [Fact]
    public void The_tab_bar_under_the_chat_is_never_a_message()
    {
        var lines = new[] { "Anmut: да это ужас(", "Сот. Squad GuiId РМ Trade Room Wodd Нот Cross-serv. Awards" };

        Assert.Equal(new[] { "Anmut: да это ужас(" }, TextMatching.SplitChatMessages(lines));
        Assert.True(TextMatching.IsChatTabBar("Сот. Squad GuiId РМ Trade Room Wodd Нот Cross-seN. Awards"));
        Assert.False(TextMatching.IsChatTabBar("продам в пм, trade room squad"));   // a player can say three of them
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

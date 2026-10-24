using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// «Груп.» came out of the translator as "grul" (the owner, 2026-10-24). The cause is in the
/// owner's own translation cache, which keeps the exact text each request carried: the badge was
/// read fine — as "грул." — but the OCR had lost the nickname's colon behind it, so the line had
/// no header, was taken for the wrapped tail of the message above, and went to the translator
/// badge and all. Every input below is one of those cache keys, taken apart into the lines it
/// was glued from.
///
/// "грул.", "груп.", "сист.", "оснв." and "м/серв" are not Russian words: a line can never start
/// with one unless it is a badge, so they open a message with or without a colon — and so does
/// one the OCR has read onto the END of the row above.
/// </summary>
public class StrongBadgeTests
{
    [Fact]
    public void A_group_badge_whose_nickname_was_lost_starts_its_own_message()
    {
        // Cache key: "спасибо грул. спасибо" → "thanks grul. Thank you"
        var messages = TextMatching.SplitChatMessages(new[] { "Frmr: спасибо", "грул. спасибо" });

        Assert.Equal(new[] { "Frmr: спасибо", "спасибо" }, messages);
    }

    [Fact]
    public void A_group_badge_read_onto_the_end_of_a_row_is_cut_off_with_its_message()
    {
        var messages = TextMatching.SplitChatMessages(new[] { "Frmr: спасибо грул. спасибо" });

        Assert.Equal(new[] { "Frmr: спасибо", "спасибо" }, messages);
    }

    [Fact]
    public void A_long_message_does_not_swallow_the_next_one_behind_a_group_badge()
    {
        // Cache key: "НЯШКИ 300+ТС,КХ,20Д. грул. если я где то на боссе умру не ждите добивайте, я уже был сегодня"
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "Igrushka: НЯШКИ 300+ТС,КХ,20Д.",
            "грул. если я где то на боссе умру не ждите добивайте, я уже был сегодня",
        });

        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, m => m.Contains("грул", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_colon_misread_as_a_semicolon_no_longer_sends_the_badge_to_the_translator()
    {
        // Cache key: "Lordpf DragonsV Грул. ГолубьАзу;аче когда Территориальные войны будут живые? посмотреть хоть"
        var messages = TextMatching.SplitChatMessages(new[]
        {
            "Lordpf DragonsV Грул. ГолубьАзу;аче когда Территориальные войны будут живые? посмотреть хоть",
        });

        Assert.DoesNotContain(messages, m => m.Contains("Грул", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, m => m.StartsWith("ГолубьАзу", StringComparison.Ordinal));
    }

    [Fact]
    public void Leaving_the_safe_zone_is_an_announcement_and_not_the_tail_of_a_message()
    {
        // Cache key: "Грул. грац Leaving Safe Zone" → "Grul. Graz Leaving Safe Zone"
        var messages = TextMatching.SplitChatMessages(new[] { "Грул. грац", "Leaving Safe Zone" });

        Assert.Equal(new[] { "грац", "Leaving Safe Zone" }, messages);
    }

    [Fact]
    public void The_group_badge_read_as_fut_is_peeled_off_the_nickname()
    {
        // Real OCR of the owner's 2026-10-24 screenshot: the green chip over a light background.
        // (The lowercase nickname is then proved by its colour — see ChatColorsTests.)
        Assert.Equal(new[] { "увущька: да" }, TextMatching.SplitChatMessages(new[] { "фут. увущька: да" }));
    }

    [Theory]
    // "груп" without its dot is a player's clipped "group" — it stays in the sentence.
    [InlineData("Anmut: ищу", "груп на мист")]
    // "клан" IS a word: a wrapped line that starts with it still needs a colon to be a new message.
    [InlineData("Anmut: вступай в наш", "клан пока места есть")]
    public void A_badge_that_is_also_a_word_still_needs_a_colon(string first, string wrapped)
    {
        var message = Assert.Single(TextMatching.SplitChatMessages(new[] { first, wrapped }));

        Assert.Equal($"{first} {wrapped}", message);
    }
}

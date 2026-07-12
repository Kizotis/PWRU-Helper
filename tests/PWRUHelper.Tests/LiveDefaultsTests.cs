using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// What the shipped live-mode defaults actually do, replayed over a real chat (messages straight
/// off the user's screenshots) arriving one by one as they would scroll past the reader.
///
/// The sliders map to: sensitivity 0–100% → 0.80…0.95 "same message" similarity;
/// stability 0–100% → 0.50…0.95 frame-confirmation. Defaults: 5% and 60%.
/// </summary>
public class LiveDefaultsTests
{
    // The defaults, computed exactly as MainWindow does.
    private const double DefaultSensitivity = 0.80 + 5 / 100.0 * 0.15;    // ≈ 0.808
    private const double DefaultStability = 0.50 + 60 / 100.0 * 0.45;     // ≈ 0.77
    private const int VisibleLines = 6;

    /// <summary>Replay a chat: each message appears, and the loop reads the box twice (1 read/second,
    /// messages land slower than that) before the next one arrives. Returns what reached the user.</summary>
    private static List<string> Replay(IEnumerable<string> chat, double sensitivity, double stability)
    {
        var lines = chat.ToList();
        var dedup = new LiveDedup();
        var emitted = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            var visible = lines.Take(i + 1).TakeLast(VisibleLines).ToList();
            for (int frame = 0; frame < 2; frame++)
                emitted.AddRange(dedup.Next(visible, sensitivity, stability));
        }
        return emitted;
    }

    [Fact]
    public void The_defaults_show_every_distinct_message_once_and_swallow_an_exact_re_send()
    {
        var emitted = Replay(new[]
        {
            "джероми: ОР прист танк мист +5дд шифт",
            "JEKA: В ХХ 4-1 ВАР ТАНК ДД ЕЖА СТУК",
            "Jennifer: +2 на ИБ с ТС",
            "джероми: ОР прист танк мист +5дд шифт",   // the same LFM re-posted — spam, drop it
            "JEKA: В ХХ 4-1 ВАР ЕЖА СТУК",             // a genuinely different LFM — keep it
        }, DefaultSensitivity, DefaultStability);

        Assert.Equal(new[]
        {
            "джероми: ОР прист танк мист +5дд шифт",
            "JEKA: В ХХ 4-1 ВАР ТАНК ДД ЕЖА СТУК",
            "Jennifer: +2 на ИБ с ТС",
            "JEKA: В ХХ 4-1 ВАР ЕЖА СТУК",
        }, emitted);
    }

    [Theory]
    // At every slider setting, from one end to the other: the digit rule does not depend on it.
    [InlineData(0.80)]   // slider at 0%
    [InlineData(0.808)]  // the shipped default, 5%
    [InlineData(0.95)]   // slider at 100%
    public void A_repost_with_a_changed_number_reaches_the_user(double sensitivity)
    {
        // In an LFM chat the NUMBER is the whole message: "+5дд" becoming "+2дд" means three slots
        // just filled. One digit in a 28-character line is a 0.96 similarity — above even the top of
        // the slider's band (0.95) — so the fuzzy test alone called this "the same message" and the
        // user never saw it, wherever they put the slider. The digits are now compared separately.
        var emitted = Replay(new[]
        {
            "джероми: ОР прист танк мист +5дд шифт",
            "джероми: ОР прист танк мист +2дд шифт",   // three slots filled — must not be swallowed
        }, sensitivity, DefaultStability);

        Assert.Equal(2, emitted.Count);
        Assert.Contains("+2дд", emitted[1]);
    }

    [Fact]
    public void The_same_message_re_read_with_the_OCR_s_invented_digits_is_still_the_same_message()
    {
        // The trap the digit rule could have walked into. The OCR invents digits INSIDE words:
        // "Olympus" is read "01ympus", "f1oomy" as "f100my", "real" as "геа1" — all real readings.
        // Those digits flicker with the background and mean nothing. If they counted, the same
        // message would look new on the next frame and be translated over and over.
        var emitted = Replay(new[]
        {
            "Лунаед: ГИ Olympus приглашает игроков",
            "Лунаед: ГИ 01ympus приглашает игроков",   // same message, the OCR just wobbled
        }, DefaultSensitivity, DefaultStability);

        Assert.Single(emitted);
    }

    [Theory]
    [InlineData("джероми: ОР прист танк мист +5дд шифт", "5")]
    [InlineData("JEKA: В ХХ 4-1 ВАР ЕЖА СТУК", "41")]
    [InlineData("Лунаед: ГИ 01ympus приглашает игроков 100+ 2 рб КХ", "1002")]  // "01ympus" ignored
    [InlineData("f100my whispers: Нашел?", "")]                                 // a nickname, not a count
    [InlineData("Wei Xiaobao: kiwi becomes the owner of а геа1 rarity", "")]    // "геа1" = "real"
    public void Only_the_digits_that_carry_meaning_are_counted(string line, string expected)
        => Assert.Equal(expected, TextMatching.MeaningfulDigits(line));

    [Fact]
    public void KNOWN_LIMITATION_a_shortened_repost_is_swallowed_by_the_containment_rule()
    {
        // LiveDedup drops a candidate whose signature is contained in one it already showed (≥10
        // chars) — that is how a wrapped tail is kept from being re-emitted on its own. A player
        // re-posting a shorter version of their LFM pays for it.
        var emitted = Replay(new[]
        {
            "джероми: ОР прист танк мист +5дд шифт",
            "джероми: ОР прист танк",
        }, DefaultSensitivity, DefaultStability);

        Assert.Single(emitted);
    }
}

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
    // The sensitivity slider, from one end to the other. It changes NOTHING here — which is the
    // point of the test below.
    [InlineData(0.80)]   // slider at 0%
    [InlineData(0.808)]  // the shipped default, 5%
    [InlineData(0.95)]   // slider at 100% — as strict as it can be
    public void KNOWN_LIMITATION_a_repost_with_a_changed_number_is_dropped_at_every_slider_setting(double sensitivity)
    {
        // In an LFM chat the NUMBER is the whole message: "+5дд" becoming "+2дд" means three slots
        // just filled. But one digit in a 28-character line is a 0.96 similarity — above even the
        // top of the slider's band (0.95) — so the second post counts as "the same message" and the
        // user never sees it. Turning sensitivity up does not help; nothing in the UI does.
        //
        // The band stops at 0.95 on purpose: the OCR itself flickers by a character or two between
        // frames ("прист" / "приег" / "приз-танк" are all real readings of the same word), and a
        // stricter threshold would re-translate the same message over and over. Letters flicker;
        // digits carry the meaning. Telling the two apart is the fix — not a slider.
        var emitted = Replay(new[]
        {
            "джероми: ОР прист танк мист +5дд шифт",
            "джероми: ОР прист танк мист +2дд шифт",   // three slots filled — the user must see this
        }, sensitivity, DefaultStability);

        Assert.Single(emitted);   // ← today. Assert two when the digit rule lands.
    }

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

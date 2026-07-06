using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Behaviour of the live de-dup filter that decides which chat lines are new enough to
/// translate. These reproduce the real problems from the game chat: animated emojis and
/// colour flicker re-reading the same message, and a message scrolling off then being sent
/// again. Match ≈ 0.85, confirm ≈ 0.70 mirror the mid-slider defaults.
/// </summary>
public class LiveDedupTests
{
    private const double Match = 0.85, Confirm = 0.70;

    private static List<string> Feed(LiveDedup d, params string[] lines)
        => d.Next(lines, Match, Confirm);

    [Fact]
    public void NewMessage_NeedsTwoFramesBeforeTranslating()
    {
        var d = new LiveDedup();
        Assert.Empty(Feed(d, "proBlemka: ТС ЛЕГА 2 ДД"));          // first sight → awaiting confirmation
        Assert.Single(Feed(d, "proBlemka: ТС ЛЕГА 2 ДД"));         // survived a frame → translate once
    }

    [Fact]
    public void StableMessage_IsTranslatedOnlyOnce()
    {
        var d = new LiveDedup();
        Feed(d, "Wups: В ТС легу 2ДД");
        Assert.Single(Feed(d, "Wups: В ТС легу 2ДД"));             // confirmed
        // It keeps sitting on screen for many frames — never re-translated.
        for (int i = 0; i < 10; i++)
            Assert.Empty(Feed(d, "Wups: В ТС легу 2ДД"));
    }

    [Fact]
    public void AnimatedEmojiFlicker_DoesNotRetranslate()
    {
        var d = new LiveDedup();
        Feed(d, "proBlemka: ТС ЛЕГА 2 ДД ❤❤");
        Assert.Single(Feed(d, "proBlemka: ТС ЛЕГА 2 ДД ❤❤"));      // confirmed once
        // The animated hearts make the OCR wobble frame to frame — must stay silent.
        Assert.Empty(Feed(d, "proBlemka: ТС ЛЕГА 2 ДД W"));       // heart read as a stray letter
        Assert.Empty(Feed(d, "proBlemka: ТС ЛЕГА 2 ДД"));         // heart dropped entirely
        Assert.Empty(Feed(d, "proBlemka: ТС ЛЕГА 2 ДД ❤"));
    }

    [Fact]
    public void BriefFlickerOffScreen_DoesNotRetranslate()
    {
        var d = new LiveDedup();
        Feed(d, "Reyna: В ХХ4-1 Ежа прист");
        Assert.Single(Feed(d, "Reyna: В ХХ4-1 Ежа прист"));       // confirmed
        Feed(d);                                                  // gone for a single frame (pan/anim)
        Assert.Empty(Feed(d, "Reyna: В ХХ4-1 Ежа прист"));        // same message back → not new
        Assert.Empty(Feed(d, "Reyna: В ХХ4-1 Ежа прист"));
    }

    [Fact]
    public void MessageResentAfterScrollingOff_IsTranslatedAgain()
    {
        var d = new LiveDedup();
        Feed(d, "Kizotis: Салют");
        Assert.Single(Feed(d, "Kizotis: Салют"));                 // confirmed
        // It scrolls off the top and is absent for a long stretch.
        for (int i = 0; i < 8; i++) Feed(d);
        // Now the same line is sent again → it counts as new and is translated once more.
        Assert.Empty(Feed(d, "Kizotis: Салют"));                  // first sight of the re-send
        Assert.Single(Feed(d, "Kizotis: Салют"));                 // confirmed re-send
    }

    [Fact]
    public void DistinctMessages_AreBothTranslated()
    {
        var d = new LiveDedup();
        Feed(d, "proBlemka: ТС ЛЕГА 2 ДД", "Wups: В ТС легу 2ДД");
        var second = Feed(d, "proBlemka: ТС ЛЕГА 2 ДД", "Wups: В ТС легу 2ДД");
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void OrphanedWrappedFragment_IsNotReEmitted()
    {
        var d = new LiveDedup();
        var full = "Reyna: В ХХ4-1 Ежа прист мист вар син сик дру 3дд";
        Feed(d, full);
        Assert.Single(Feed(d, full));                             // confirmed full message
        // The "Nick:" line scrolls off the top; only the wrapped tail remains as a standalone read.
        // Its short signature can't fuzzy-match the full one, but the full one CONTAINS it, so it
        // must be recognised as the same message still on screen — never a partial duplicate.
        var tail = "мист вар син сик дру 3дд";
        Assert.Empty(Feed(d, tail));
        Assert.Empty(Feed(d, tail));
    }

    [Fact]
    public void PureNoiseLines_AreNeverEmitted()
    {
        var d = new LiveDedup();
        Feed(d, "❤❤", "…", "   ");
        Assert.Empty(Feed(d, "❤❤", "…", "   "));
    }
}

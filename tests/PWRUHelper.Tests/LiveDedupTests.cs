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
    // ---- the resume path: lines a read-once put in the feed ------------------------------------

    /// <summary>
    /// The flow the owner reported, end to end: LIVE is running, he presses Read once (which stops
    /// the loop and appends what it read to the SAME feed), then resumes LIVE. The feed survives the
    /// resume now, so a line the read-once appended must not be translated and appended a second
    /// time — and the loop only knows that if it was told.
    /// </summary>
    [Fact]
    public void ALineAReadOncePutInTheFeed_IsNotTranslatedAgainWhenLiveResumes()
    {
        var d = new LiveDedup();

        // The read-once read it while the loop was stopped — it never came through Next.
        d.RememberAlreadyShown(new[] { "Reyna: В ХХ4-1 Ежа прист" }, Match);

        // LIVE resumes and the message is still on screen, frame after frame.
        for (int i = 0; i < 4; i++)
            Assert.Empty(Feed(d, "Reyna: В ХХ4-1 Ежа прист"));

        // …and the OCR wobble that made the old filter re-emit is covered too, because a registered
        // line is remembered exactly the way an emitted one is (one Remember, both callers).
        Assert.Empty(Feed(d, "Reyna: В ХХ4-1 Ежа приег"));
    }

    /// <summary>Registering says nothing about the OTHER lines on screen: it invents no frame, so
    /// the two-frame confirmation still runs normally for a genuinely new message arriving after a
    /// read-once — the resume must stay silent about what it has seen, not deaf to everything.</summary>
    [Fact]
    public void RememberingAReadOnce_DoesNotSwallowTheNextRealMessage()
    {
        var d = new LiveDedup();
        d.RememberAlreadyShown(new[] { "Reyna: В ХХ4-1 Ежа прист" }, Match);

        Assert.Empty(Feed(d, "Reyna: В ХХ4-1 Ежа прист", "Wups: В ТС легу 2ДД"));   // frame 1
        var second = Feed(d, "Reyna: В ХХ4-1 Ежа прист", "Wups: В ТС легу 2ДД");    // frame 2
        Assert.Equal(new[] { "Wups: В ТС легу 2ДД" }, second);
    }

    /// <summary>A re-post with a different number is still a different message (the digits rule),
    /// even against a line a read-once registered — an LFM's digits ARE the message, and swallowing
    /// one of those is the invisible failure this whole registration has to avoid.</summary>
    [Fact]
    public void RememberedLines_StillObeyTheDigitsRule()
    {
        var d = new LiveDedup();
        d.RememberAlreadyShown(new[] { "proBlemka: ТС ЛЕГА +5ДД" }, Match);

        Feed(d, "proBlemka: ТС ЛЕГА +2ДД");
        Assert.Single(Feed(d, "proBlemka: ТС ЛЕГА +2ДД"));
    }

    /// <summary>Nothing to remember is not an error, and neither is a line that signatures to
    /// nothing (pure punctuation/emoji) — Next drops those too, and the two must agree.</summary>
    [Fact]
    public void RememberingNothing_IsHarmless()
    {
        var d = new LiveDedup();
        d.RememberAlreadyShown(Array.Empty<string>(), Match);
        d.RememberAlreadyShown(new[] { "!!! ❤❤❤" }, Match);

        Feed(d, "Wups: В ТС легу 2ДД");
        Assert.Single(Feed(d, "Wups: В ТС легу 2ДД"));
    }

}

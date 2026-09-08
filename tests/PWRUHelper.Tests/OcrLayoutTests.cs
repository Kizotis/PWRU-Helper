using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// Reading order out of OCR geometry.
///
/// The bug these exist for: Windows.Media.Ocr does not promise its <c>Lines</c> come out
/// top-to-bottom, and on text whose columns line up it really doesn't. Pointed at a Notepad
/// window holding a ten-line Russian dialogue where every speaker name is the same width
/// («Анна:» / «Иван:», so every message body starts at the same x), the engine reported TWENTY
/// lines: all ten nicknames first (x≈42, y 46→586), then all ten bodies (x≈196, y 46→586). It
/// had decided the text was two columns and read them column by column.
///
/// The numbers in <see cref="TheOwnersTwoColumnCase"/> are that dump, not invented ones: the
/// diagnostic rendered the owner's dialogue, ran the real ru engine over it and printed every
/// line's bounding box. Fed to <c>TextMatching.SplitChatMessages</c>, that order produced the
/// eight empty-bodied "Иван:" / "Анна:" cards and the three run-together bodies he reported —
/// with the grey ORIGINAL line already wrong, before translation was involved at all.
/// </summary>
public class OcrLayoutTests
{
    private static OcrTextBox Box(string text, double left, double top, double width, double height)
        => new(text, left, top, width, height);

    // ---- the reported bug ----

    [Fact]
    public void TheOwnersTwoColumnCase()
    {
        // Verbatim geometry from the engine dump: the nickname column, then the body column.
        // Both columns run y = 46, 106, 166 … so the engine's own order is column-major.
        var boxes = new List<OcrTextBox>
        {
            Box("Анна:", 42, 46, 119, 32),
            Box("Иван:", 43, 106, 118, 32),
            Box("Анна:", 42, 166, 119, 32),
            Box("Привет! Как тебя зовут?", 196, 46, 582, 41),
            Box("Привет! Меня зовут Иван. А тебя?", 196, 106, 811, 41),
            Box("Меня зовут Анна. Очень приятно.", 196, 166, 781, 41),
        };

        Assert.Equal(new[]
        {
            "Анна: Привет! Как тебя зовут?",
            "Иван: Привет! Меня зовут Иван. А тебя?",
            "Анна: Меня зовут Анна. Очень приятно.",
        }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void TheOwnersCase_SurvivesTheWholeChatSplit()
    {
        // The end the player actually sees: one card per exchange, speaker and body together.
        var boxes = new List<OcrTextBox>
        {
            Box("Анна:", 42, 46, 119, 32),
            Box("Иван:", 43, 106, 118, 32),
            Box("Привет! Как тебя зовут?", 196, 46, 582, 41),
            Box("Привет! Меня зовут Иван. А тебя?", 196, 106, 811, 41),
        };

        var messages = TextMatching.SplitChatMessages(OcrLayout.OrderIntoLines(boxes));

        Assert.Equal(new[]
        {
            "Анна: Привет! Как тебя зовут?",
            "Иван: Привет! Меня зовут Иван. А тебя?",
        }, messages);
    }

    // ---- the case that must NOT change: ordinary single-column game chat ----

    [Fact]
    public void SingleColumnChat_ComesBackExactlyAsItWent_In()
    {
        // One fragment per row: the output is the engine's own lines, and nothing is joined.
        var boxes = new List<OcrTextBox>
        {
            Box("proBlemka: ТС ЛЕГА 2 ДД", 20, 10, 400, 24),
            Box("Hokasse: ОР вар прист +3", 20, 40, 380, 24),
            Box("~V0oDo0~: кто на босса?", 20, 70, 350, 24),
        };

        Assert.Equal(new[]
        {
            "proBlemka: ТС ЛЕГА 2 ДД",
            "Hokasse: ОР вар прист +3",
            "~V0oDo0~: кто на босса?",
        }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void SingleColumnChat_IsSortedTopToBottom_EvenWhenTheEngineShufflesIt()
    {
        var boxes = new List<OcrTextBox>
        {
            Box("третья", 20, 70, 200, 24),
            Box("первая", 20, 10, 200, 24),
            Box("вторая", 20, 40, 200, 24),
        };

        Assert.Equal(new[] { "первая", "вторая", "третья" }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void ConsecutiveRowsAtATightPitchAreNeverGluedTogether()
    {
        // Boxes 24 px tall at a 26 px pitch — barely any air between them, and still two rows.
        var boxes = new List<OcrTextBox>
        {
            Box("первая строка", 20, 10, 200, 24),
            Box("вторая строка", 20, 36, 200, 24),
            Box("третья строка", 20, 62, 200, 24),
        };

        Assert.Equal(3, OcrLayout.OrderIntoLines(boxes).Count);
    }

    [Fact]
    public void AWrappedContinuationStaysItsOwnLine_AndKeepsItsPlace()
    {
        // The tail of a long message is a row of its own, indented or not; gluing it INTO the
        // header's row would defeat SplitChatMessages' wrapped-continuation rule.
        var boxes = new List<OcrTextBox>
        {
            Box("Мир", 10, 100, 40, 22),
            Box("proBlemka: собираем отряд на", 60, 100, 400, 24),
            Box("вечернюю осаду, пишите в лс", 60, 130, 380, 24),
        };

        var lines = OcrLayout.OrderIntoLines(boxes);

        Assert.Equal(new[]
        {
            "Мир proBlemka: собираем отряд на",
            "вечернюю осаду, пишите в лс",
        }, lines);

        // …and the badge that was merged back in is the INLINE form TryPeelChannelTag already
        // handles, so the whole message survives as one card with the chip dropped.
        Assert.Equal(new[] { "proBlemka: собираем отряд на вечернюю осаду, пишите в лс" },
                     TextMatching.SplitChatMessages(lines));
    }

    // ---- ragged rows: real text is not a grid ----

    [Fact]
    public void RaggedBoxesOnOneRowAreOneRow()
    {
        // Straight off the dump: an x-height-only fragment («из», y=295 h=23) sits inside a
        // neighbour that has both ascenders and descenders («Петербурга.», y=345 h=42).
        var boxes = new List<OcrTextBox>
        {
            Box("из", 249, 355, 46, 23),
            Box("Я", 197, 346, 21, 32),
            Box("Петербурга.", 324, 345, 271, 42),
            Box("Красивый", 631, 346, 199, 41),
        };

        Assert.Equal(new[] { "Я из Петербурга. Красивый" }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void AFragmentBetweenTwoRowsJoinsTheOneItOverlapsMost()
    {
        var boxes = new List<OcrTextBox>
        {
            Box("верх", 10, 100, 60, 30),      // 100–130
            Box("низ", 10, 160, 60, 30),       // 160–190
            Box("гость", 90, 148, 60, 30),     // 148–178: 0 with the first row, 18 with the second
        };

        Assert.Equal(new[] { "верх", "низ гость" }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void ARowIsOrderedLeftToRight_WhateverOrderItArrivesIn()
    {
        var boxes = new List<OcrTextBox>
        {
            Box("третий", 300, 50, 80, 24),
            Box("первый", 10, 50, 80, 24),
            Box("второй", 150, 50, 80, 24),
        };

        Assert.Equal(new[] { "первый второй третий" }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void ThreeColumnsBecomeRowsAndNotSixScatteredLines()
    {
        // A badge column, a nickname column and a body column — the shape a filter-on capture of
        // real game chat produces, and the shape the engine is most likely to read column-major.
        var boxes = new List<OcrTextBox>
        {
            Box("Мир", 10, 10, 40, 22),
            Box("Клан", 10, 40, 45, 22),
            Box("proBlemka:", 70, 10, 120, 24),
            Box("Hokasse:", 70, 40, 110, 24),
            Box("ТС ЛЕГА 2 ДД", 200, 10, 200, 24),
            Box("ОР вар прист +3", 200, 40, 220, 24),
        };

        Assert.Equal(new[]
        {
            "Мир proBlemka: ТС ЛЕГА 2 ДД",
            "Клан Hokasse: ОР вар прист +3",
        }, OcrLayout.OrderIntoLines(boxes));
    }

    // ---- degenerate input ----

    [Fact]
    public void NoBoxesIsNoLines()
    {
        Assert.Empty(OcrLayout.OrderIntoLines(Array.Empty<OcrTextBox>()));
        Assert.Empty(OcrLayout.OrderIntoLines(null!));
    }

    [Fact]
    public void OneBoxIsOneLine()
    {
        Assert.Equal(new[] { "привет" }, OcrLayout.OrderIntoLines(new[] { Box("привет", 5, 5, 60, 20) }));
    }

    [Fact]
    public void BlankFragmentsAreDroppedAndTheRestIsTrimmed()
    {
        var boxes = new List<OcrTextBox>
        {
            Box("   ", 10, 10, 30, 20),
            Box("  привет  ", 50, 10, 80, 20),
            Box("", 10, 40, 30, 20),
        };

        Assert.Equal(new[] { "привет" }, OcrLayout.OrderIntoLines(boxes));
    }

    [Fact]
    public void AZeroHeightBoxIsItsOwnRowRatherThanSwallowingOne()
    {
        // Degenerate geometry must not make the "half the shorter box overlaps" test trivially
        // true (half of zero is zero), which would let one bad box absorb everything it touches.
        var boxes = new List<OcrTextBox>
        {
            Box("призрак", 10, 20, 0, 0),
            Box("строка", 10, 10, 200, 30),
        };

        Assert.Equal(2, OcrLayout.OrderIntoLines(boxes).Count);
    }

    [Fact]
    public void IdenticalGeometryKeepsTheOrderTheEngineGaveUs()
    {
        var boxes = new List<OcrTextBox>
        {
            Box("раз", 10, 10, 50, 20),
            Box("два", 10, 10, 50, 20),
        };

        Assert.Equal(new[] { "раз два" }, OcrLayout.OrderIntoLines(boxes));
    }
}

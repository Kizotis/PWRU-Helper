using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class TextMatchingTests
{
    // ---- ToSentences: stitch wrapped lines, split multi-sentence blocks ----

    [Fact]
    public void ToSentences_StitchesWrappedLinesIntoOneSentence()
    {
        var lines = new[] { "Привет, как", "у тебя дела сегодня?" };
        var result = TextMatching.ToSentences(lines);
        Assert.Single(result);
        Assert.Equal("Привет, как у тебя дела сегодня?", result[0]);
    }

    [Fact]
    public void ToSentences_SplitsMultipleSentencesInOneLine()
    {
        var result = TextMatching.ToSentences(new[] { "Гоу! Я готов. Идём?" });
        Assert.Equal(new[] { "Гоу!", "Я готов.", "Идём?" }, result);
    }

    [Fact]
    public void ToSentences_BlankLineBreaksTheRun()
    {
        var result = TextMatching.ToSentences(new[] { "первое сообщение", "", "второе сообщение" });
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ToSentences_CapsStitchingAtThreeLines()
    {
        // Four wrapped lines with no sentence punctuation must not all glue into one blob.
        var result = TextMatching.ToSentences(new[] { "a", "b", "c", "d" });
        Assert.True(result.Count >= 2);
    }

    // ---- SplitForGameChat ----

    [Fact]
    public void SplitForGameChat_ShortTextIsOneChunk()
    {
        var chunks = TextMatching.SplitForGameChat("привет всем", 80);
        Assert.Equal(new[] { "привет всем" }, chunks);
    }

    [Fact]
    public void SplitForGameChat_BreaksOnWordBoundaries()
    {
        var chunks = TextMatching.SplitForGameChat("one two three four", 9);
        Assert.All(chunks, c => Assert.True(c.Length <= 9));
        Assert.DoesNotContain(chunks, c => c.EndsWith(" ") || c.StartsWith(" "));
        Assert.Equal("one two three four", string.Join(" ", chunks));
    }

    [Fact]
    public void SplitForGameChat_HardSplitsAnOverlongWord()
    {
        var chunks = TextMatching.SplitForGameChat("supercalifragilistic", 5);
        Assert.All(chunks, c => Assert.True(c.Length <= 5));
        Assert.Equal("supercalifragilistic", string.Concat(chunks));
    }

    // ---- Normalize ----

    [Fact]
    public void Normalize_LowersCollapsesWhitespaceAndTrimsEdgePunctuation()
    {
        Assert.Equal("привет мир", TextMatching.Normalize("  Привет   МИР!!  "));
    }

    // ---- SimilarEnough ----

    [Fact]
    public void SimilarEnough_IdenticalStringsMatch()
        => Assert.True(TextMatching.SimilarEnough("абвгд", "абвгд", 0.9));

    [Fact]
    public void SimilarEnough_VeryDifferentLengthsRejectedFast()
        => Assert.False(TextMatching.SimilarEnough("hi", "a much longer line entirely", 0.7));

    [Fact]
    public void SimilarEnough_SmallOcrNoiseStillMatches()
        => Assert.True(TextMatching.SimilarEnough("привет мир", "привег мир", 0.8));

    // ---- CyrillicShare (the auto-flip guard) ----

    [Fact]
    public void CyrillicShare_EmptyOrNoLetters_IsZero()
    {
        Assert.Equal(0, TextMatching.CyrillicShare(""));
        Assert.Equal(0, TextMatching.CyrillicShare("123 !!! @@@"));
    }

    [Fact]
    public void CyrillicShare_AllLatin_IsZero()
        => Assert.Equal(0, TextMatching.CyrillicShare("hello world"));

    [Fact]
    public void CyrillicShare_AllCyrillic_IsOne()
        => Assert.Equal(1.0, TextMatching.CyrillicShare("привет"), 3);

    [Fact]
    public void CyrillicShare_SingleStrayCyrillic_StaysBelowFlipThreshold()
    {
        // A French sentence with one Cyrillic character must NOT read as "Russian".
        double share = TextMatching.CyrillicShare("bonjour tout le monde я");
        Assert.True(share < 0.3, $"expected < 0.3 but got {share}");
    }

    [Fact]
    public void CyrillicShare_MostlyCyrillic_IsAboveFlipThreshold()
        => Assert.True(TextMatching.CyrillicShare("привет hi") >= 0.3);

    // ---- Signature (the de-dup core) ----

    [Fact]
    public void Signature_KeepsOnlyLettersAndDigitsLowercased()
        => Assert.Equal("problemkaтслега2дд", TextMatching.Signature("proBlemka: ТС ЛЕГА 2 ДД"));

    [Fact]
    public void Signature_IgnoresTrailingEmojiArtifacts()
    {
        // Same message, two frames: one read with an emoji artifact, one without → same signature.
        var a = TextMatching.Signature("proBlemka: ТС ЛЕГА 2 ДД ❤❤");
        var b = TextMatching.Signature("proBlemka: ТС ЛЕГА 2 ДД W");   // heart mis-read as a stray letter
        // The letters differ by the stray 'w', but the Cyrillic core is identical and dominates.
        Assert.StartsWith("problemkaтслега2дд", a);
        Assert.StartsWith("problemkaтслега2дд", b);
        Assert.True(TextMatching.SimilarEnough(a, b, 0.9));
    }

    [Fact]
    public void Signature_DistinctMessagesStayDistinct()
    {
        // proBlemka's line and Wups's line must NOT collapse together (sender name differentiates).
        var a = TextMatching.Signature("proBlemka: ТС ЛЕГА 2 ДД");
        var b = TextMatching.Signature("Wups: В ТС легу 2ДД");
        Assert.False(TextMatching.SimilarEnough(a, b, 0.80));
    }

    [Fact]
    public void Signature_EmptyForPureNoise()
        => Assert.Equal("", TextMatching.Signature("  ❤❤ !!! …  "));

    // ---- StripNoise ----

    [Fact]
    public void StripNoise_RemovesSymbolsKeepsWordsAndPunctuation()
        => Assert.Equal("proBlemka: ТС ЛЕГА 2 ДД", TextMatching.StripNoise("proBlemka: ТС ЛЕГА 2 ДД ❤❤"));

    [Fact]
    public void StripNoise_DropsEmojiSurrogatePairs()
        => Assert.Equal("Салют", TextMatching.StripNoise("Салют 🐷🦊"));

    [Fact]
    public void StripNoise_LeavesPlainTextUntouched()
        => Assert.Equal("привет мир", TextMatching.StripNoise("привет мир"));
}

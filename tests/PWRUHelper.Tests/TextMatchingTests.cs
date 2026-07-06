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

    // ---- SplitChatMessages: group by "[Tag] Nick:" instead of punctuation ----

    [Fact]
    public void SplitChatMessages_SplitsByNickColonWithoutAnyPunctuation()
    {
        // The whole point: no periods anywhere, yet the two messages are separated.
        var result = TextMatching.SplitChatMessages(new[]
        {
            "proBlemka: ТС ЛЕГА 2 ДД",
            "Wups: В ТС легу 2ДД",
        });
        Assert.Equal(new[] { "proBlemka: ТС ЛЕГА 2 ДД", "Wups: В ТС легу 2ДД" }, result);
    }

    [Fact]
    public void SplitChatMessages_DropsTheChannelTagKeepsTheNick()
    {
        Assert.Equal(new[] { "Kizotis: Салют" },
            TextMatching.SplitChatMessages(new[] { "Клан Kizotis: Салют" }));
        Assert.Equal(new[] { "proBlemka: привет" },
            TextMatching.SplitChatMessages(new[] { "[Мир] proBlemka: привет" }));
    }

    [Fact]
    public void SplitChatMessages_GluesWrappedContinuationOntoTheMessage()
    {
        var result = TextMatching.SplitChatMessages(new[]
        {
            "Reyna: В ХХ4-1 Ежа прист",
            "мист вар син сик дру 3дд",     // wrapped 2nd line, no "Nick:" → continuation
        });
        Assert.Equal(new[] { "Reyna: В ХХ4-1 Ежа прист мист вар син сик дру 3дд" }, result);
    }

    [Fact]
    public void SplitChatMessages_SameSpeakerTwice_StaysTwoMessages()
    {
        var result = TextMatching.SplitChatMessages(new[] { "Kizotis: Салют", "Kizotis: Салют" });
        Assert.Equal(2, result.Count);   // detecting "Nick:" beats detecting a nickname *change*
    }

    [Fact]
    public void SplitChatMessages_BlankLineBreaksTheRun()
    {
        var result = TextMatching.SplitChatMessages(new[] { "Nick: hello", "", "loose line" });
        Assert.Equal(new[] { "Nick: hello", "loose line" }, result);
    }

    [Fact]
    public void SplitChatMessages_CapsRunawayStitchWhenNoHeaderEverAppears()
    {
        var result = TextMatching.SplitChatMessages(new[] { "a", "b", "c", "d", "e", "f" });
        Assert.True(result.Count >= 2);   // never glue a whole screen into one blob
    }

    // ---- TryParseHeader / SplitSpeaker / WithSpeaker ----

    [Fact]
    public void TryParseHeader_ParsesTagNickAndBody()
    {
        Assert.True(TextMatching.TryParseHeader("Сист Wei Xiaobao: becomes owner", out var sp, out var body));
        Assert.Equal("Wei Xiaobao", sp);
        Assert.Equal("becomes owner", body);
    }

    [Fact]
    public void TryParseHeader_RejectsATimestampColon()
        => Assert.False(TextMatching.TryParseHeader("12:30 го", out _, out _));

    [Fact]
    public void TryParseHeader_RejectsAColonDeepInTheLine()
        => Assert.False(TextMatching.TryParseHeader("this is a long sentence with a colon: here", out _, out _));

    [Fact]
    public void TryParseHeader_TaggedLine_AcceptsALongNickWithColonPast28()
    {
        // "[Торговля] Продавец_Мечей_777:" — the tag pushes the colon to 29, past MaxHeaderChars,
        // yet the 18-char nick after the peeled tag is well within the tight nick limit.
        Assert.True(TextMatching.TryParseHeader("[Торговля] Продавец_Мечей_777: продам меч", out var sp, out var body));
        Assert.Equal("Продавец_Мечей_777", sp);
        Assert.Equal("продам меч", body);
    }

    [Fact]
    public void TryParseHeader_UntaggedLine_StillRejectsAColonPast28()
        // No recognised tag → the wider window doesn't apply; a colon at 30 is a body colon.
        => Assert.False(TextMatching.TryParseHeader("aaaaa bbbbb ccccc ddddd eeeeee: nope", out _, out _));

    // ---- SplitSpeakerStrict (live/read path: don't steal a body's leading "word:" as a nick) ----

    [Fact]
    public void SplitSpeakerStrict_DoesNotStealLowercaseCyrillicSlangAsNick()
    {
        // "тс" is chat slang for a dungeon, not a nickname — it must stay in the translated body.
        var (speaker, body) = TextMatching.SplitSpeakerStrict("тс: сбор у входа");
        Assert.Equal("", speaker);
        Assert.Equal("тс: сбор у входа", body);
    }

    [Fact]
    public void SplitSpeakerStrict_KeepsARealNickname()
    {
        var (speaker, body) = TextMatching.SplitSpeakerStrict("proBlemka: ТС ЛЕГА 2 ДД");
        Assert.Equal("proBlemka", speaker);
        Assert.Equal("ТС ЛЕГА 2 ДД", body);
    }

    [Fact]
    public void SplitSpeaker_SeparatesNickFromBody()
    {
        var (speaker, body) = TextMatching.SplitSpeaker("proBlemka: ТС ЛЕГА 2 ДД");
        Assert.Equal("proBlemka", speaker);
        Assert.Equal("ТС ЛЕГА 2 ДД", body);
    }

    [Fact]
    public void SplitSpeaker_NoHeader_ReturnsWholeLineAsBody()
    {
        var (speaker, body) = TextMatching.SplitSpeaker("прист мист вар");
        Assert.Equal("", speaker);
        Assert.Equal("прист мист вар", body);
    }

    [Fact]
    public void WithSpeaker_PrefixesNickOrLeavesBodyAlone()
    {
        Assert.Equal("proBlemka: LF Terrace", TextMatching.WithSpeaker("proBlemka", "LF Terrace"));
        Assert.Equal("LF Terrace", TextMatching.WithSpeaker("", "LF Terrace"));
    }

    // ---- IsProbablyRussian (pick "ru" vs "auto" source, so English isn't forced through ru) ----

    [Fact]
    public void IsProbablyRussian_TrueForCyrillicChat()
        => Assert.True(TextMatching.IsProbablyRussian("ТС ЛЕГА 2 ДД"));

    [Fact]
    public void IsProbablyRussian_FalseForPlainEnglish()
        => Assert.False(TextMatching.IsProbablyRussian("becomes the owner of a real rarity"));

    [Fact]
    public void IsProbablyRussian_TrueForNumbersOrSlangWithNoLetters()
        => Assert.True(TextMatching.IsProbablyRussian("2 3 999"));   // RU chat shorthand, not "English"

    [Fact]
    public void IsProbablyRussian_TrueForMostlyCyrillicWithAStrayLatinLetter()
        => Assert.True(TextMatching.IsProbablyRussian("привет всем gl"));

    [Fact]
    public void LatinNickname_SpeakingRussian_IsStillDetectedAsRussian()
    {
        // A player whose name is in English but who writes in Russian must translate FROM ru:
        // detection runs on the BODY (after the nick is split off), not the nickname.
        var (_, body) = TextMatching.SplitSpeaker("proBlemka: ТС ЛЕГА 2 ДД");
        Assert.True(TextMatching.IsProbablyRussian(body));
    }

    [Fact]
    public void RussianNickname_SpeakingEnglish_IsDetectedAsNonRussian()
    {
        // …and the reverse: a Cyrillic name writing English translates via auto-detect, not ru.
        var (_, body) = TextMatching.SplitSpeaker("Вася: hello guys where are you");
        Assert.False(TextMatching.IsProbablyRussian(body));
    }
}

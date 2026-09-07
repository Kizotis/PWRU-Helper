using System.Text;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class SlangGlossaryTests
{
    [Fact]
    public void FromJson_Null_IsEmpty()
        => Assert.True(SlangGlossary.FromJson(null).IsEmpty);

    [Fact]
    public void FromJson_Garbage_IsEmpty()
        => Assert.True(SlangGlossary.FromJson("{ not valid json ").IsEmpty);

    [Fact]
    public void Decode_KnownTerm_IsExpanded()
    {
        var g = SlangGlossary.FromJson(
            """{ "entries": [ { "keys": ["пп"], "meaning": "Full Moon Pavilion" } ] }""");
        var decoded = g.Decode("го в пп");
        Assert.Contains("Full Moon Pavilion", decoded);
    }

    [Fact]
    public void Decode_NoSlang_ReturnsEmpty()
    {
        var g = SlangGlossary.FromJson(
            """{ "entries": [ { "keys": ["пп"], "meaning": "Full Moon Pavilion" } ] }""");
        Assert.Equal("", g.Decode("просто обычное сообщение"));
    }

    [Fact]
    public void Decode_ContextOnlyTerm_NeedsAnAnchor()
    {
        var g = SlangGlossary.FromJson("""
            { "entries": [
                { "keys": ["в"], "meaning": "LFM", "context": true },
                { "keys": ["пп"], "meaning": "Full Moon Pavilion" }
            ] }
            """);

        // "в" alone is context-only → nothing decoded.
        Assert.Equal("", g.Decode("в"));

        // With a real anchor present, the context term is included.
        var decoded = g.Decode("в пп");
        Assert.Contains("LFM", decoded);
        Assert.Contains("Full Moon Pavilion", decoded);
    }
}

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v0.9.0", 0, 9, 0)]
    [InlineData("0.9", 0, 9, 0)]
    [InlineData("v1.2.3-beta", 1, 2, 3)]
    [InlineData("2.0.0+build7", 2, 0, 0)]
    public void TryParseVersion_ParsesCommonTagShapes(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("v")]
    public void TryParseVersion_RejectsGarbage(string tag)
        => Assert.False(UpdateService.TryParseVersion(tag, out _));
}

/// <summary>
/// TP-PRV-13. The two cases below are the ones that shipped with the gtx provider, re-pointed by
/// E3.S6 when <c>ChunkText</c>/<c>HardSplit</c> moved to <see cref="TextChunker"/> — <b>not
/// re-written</b>: they are the only proof the moved code is right, and a moved test that changed
/// its assertions proves nothing about the move. The cases after them are new, and they exist
/// because the splitter now has a second caller (<c>GoogleDictTranslator</c>, E3.S4) and its two
/// real hazards — the budget is in UTF-8 BYTES, and a single sentence can be longer than the whole
/// budget — were each covered by exactly one assertion inside one case.
/// </summary>
public class TextChunkerTests
{
    [Fact]
    public void ChunkText_ShortText_IsSingleChunk()
    {
        var chunks = TextChunker.ChunkText("Just a short sentence.", 1500).ToList();
        Assert.Single(chunks);
    }

    [Fact]
    public void ChunkText_LongText_StaysUnderByteLimit_AndPreservesContent()
    {
        var sentence = "Это довольно длинное предложение для проверки разбиения. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 80));   // well over 1500 bytes
        const int limit = 1500;

        var chunks = TextChunker.ChunkText(text, limit).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= limit));
        Assert.Equal(text, string.Concat(chunks));   // nothing lost or duplicated
    }

    /// <summary>Nothing in, nothing out — and, above all, no chunk. An empty chunk becomes an empty
    /// <c>q=</c> and a request that asks a provider to translate nothing.</summary>
    [Fact]
    public void ChunkText_EmptyInput_YieldsNoChunks()
        => Assert.Empty(TextChunker.ChunkText("", 1500));

    /// <summary>The boundary is counted in BYTES, so the interesting input is the one that is
    /// exactly at the budget: 750 Cyrillic characters are 1500 UTF-8 bytes and must still travel as
    /// one chunk. A character-counting splitter passes every other case in this class and fails
    /// this one — which is why it is written in Cyrillic and asserted at equality, not below it.</summary>
    [Fact]
    public void ChunkText_ExactlyAtTheByteLimit_IsStillOneChunk()
    {
        var text = new string('я', 750);            // 2 bytes each
        Assert.Equal(1500, Encoding.UTF8.GetByteCount(text));

        var chunks = TextChunker.ChunkText(text, 1500).ToList();

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    /// <summary>One byte over the same budget is two chunks, and the split lands on the BYTE count,
    /// not on the character count — the other half of the boundary above.</summary>
    [Fact]
    public void ChunkText_OneByteOverTheLimit_Splits()
    {
        var text = new string('я', 751);            // 1502 bytes, one sentence, no boundary to split on

        var chunks = TextChunker.ChunkText(text, 1500).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= 1500));
        Assert.Equal(text, string.Concat(chunks));
    }

    /// <summary>The hard split: a single 3000-byte "sentence" carries no boundary to break on, so
    /// the sentence-wise pass cannot help and the character-wise one has to. ASCII here on purpose
    /// — the Cyrillic cases above cover the multi-byte arithmetic, and this one is about the path
    /// being taken at all.</summary>
    [Fact]
    public void ChunkText_LineLongerThanTheWholeBudget_IsHardSplit()
    {
        var text = new string('a', 3000);           // 3000 bytes, no '.', '!', '?', '…' or newline
        const int limit = 1500;

        var chunks = TextChunker.ChunkText(text, limit).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= limit));
        Assert.All(chunks, c => Assert.NotEqual("", c));
        Assert.Equal(text, string.Concat(chunks));
    }

    /// <summary>
    /// The bug E3.S4 found by becoming the splitter's second caller: <c>HardSplit</c> walked UTF-16
    /// <b>code units</b>, so a budget that ran out between the two halves of a surrogate pair cut
    /// the pair in half — one chunk ending in a lone high surrogate, the next starting with the lone
    /// low one. Neither is valid UTF-8, so both encode to U+FFFD on their way into the query string
    /// and the user's emoji comes back as two replacement characters, silently, in the middle of a
    /// long message.
    ///
    /// <para>Asserted as a UTF-8 <b>round trip</b> rather than by looking for surrogates, because
    /// that is exactly the harm: the query string is UTF-8, and a chunk that does not survive the
    /// encode is a chunk whose text changed. The input is built to land the boundary inside the
    /// pair on purpose — 1497 ASCII bytes leave three of the emoji's four.</para>
    /// </summary>
    [Fact]
    public void ChunkText_NeverSplitsInsideASurrogatePair()
    {
        var text = new string('a', 1497) + "\U0001F600" + new string('a', 600);
        const int limit = 1500;

        var chunks = TextChunker.ChunkText(text, limit).ToList();

        Assert.Equal(text, string.Concat(chunks));                       // nothing lost or duplicated
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= limit));
        Assert.All(chunks, c => Assert.Equal(c, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(c))));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c[^1]), "a chunk ends mid-pair"));
        Assert.All(chunks, c => Assert.False(char.IsLowSurrogate(c[0]), "a chunk starts mid-pair"));
        Assert.Contains(chunks, c => c.Contains("\U0001F600", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same two properties, swept across <b>every</b> alignment of the budget against a 4-byte
    /// code point rather than the one offset the case above happens to use. This is the half of the
    /// fix that is easy to get wrong and impossible to see in a single example: when a pair is moved
    /// to the next chunk, the running byte count has to be reset <i>with</i> it, or the chunk it
    /// moved into starts life already three bytes into its budget and eventually goes over. Sweeping
    /// the offset puts the boundary before, inside (both halves) and after the pair.
    /// </summary>
    [Theory]
    [InlineData(1496)] [InlineData(1497)] [InlineData(1498)] [InlineData(1499)] [InlineData(1500)]
    public void ChunkText_KeepsBothPropertiesAtEveryBoundaryAlignment(int prefix)
    {
        const int limit = 1500;
        // Emoji all the way down after the prefix: every subsequent chunk boundary also has to
        // decide about a pair, so one correct decision at the first boundary cannot carry the test.
        var text = new string('a', prefix) + string.Concat(Enumerable.Repeat("\U0001F600", 400));

        var chunks = TextChunker.ChunkText(text, limit).ToList();

        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= limit,
            $"a chunk of {Encoding.UTF8.GetByteCount(c)} bytes went over the {limit}-byte budget"));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c[^1]), "a chunk ends mid-pair"));
        Assert.All(chunks, c => Assert.False(char.IsLowSurrogate(c[0]), "a chunk starts mid-pair"));

        // PROGRESS, not just safety — and it is the assertion that matters most here, because the
        // three above are all satisfied by a splitter that has stopped making progress. `bytes` is a
        // running counter that must be reset in lockstep with the buffer; drop the reset and every
        // chunk after the first is ONE code point, which still round-trips, still fits the budget
        // and still never cuts a pair. What it costs is one HTTP request per code point against the
        // rented endpoint this whole epic exists to avoid annoying. Bounded against the budget
        // rather than pinned to a number, so the case survives a change to `limit`.
        int minimum = (Encoding.UTF8.GetByteCount(text) + limit - 1) / limit;
        Assert.True(chunks.Count <= minimum + 1,
            $"{chunks.Count} chunks for {Encoding.UTF8.GetByteCount(text)} bytes at a {limit}-byte "
            + $"budget: the splitter stopped making progress (at most {minimum + 1} expected)");
    }

    /// <summary>Ill-formed input is carried through unchanged rather than repaired: the property the
    /// whole class rests on is that the chunks concatenate back to the input, and a splitter that
    /// substituted U+FFFD for a lone surrogate would be editing the user's text on its way to a
    /// translator. (This is why the fix walks chars in pairs instead of enumerating runes, which
    /// substitutes.)</summary>
    [Fact]
    public void ChunkText_LeavesAnUnpairedSurrogateExactlyAsItFoundIt()
    {
        var text = new string('a', 1400) + '\uD83D' + new string('a', 400);   // a high surrogate, alone

        var chunks = TextChunker.ChunkText(text, 1500).ToList();

        Assert.Equal(text, string.Concat(chunks));
    }

    /// <summary>Order is content: the chunks are stitched back together in the order they came out,
    /// so a splitter that returned them out of order would silently scramble a translation. Asserted
    /// on distinguishable sentences, which <c>string.Concat</c> equality alone cannot do when every
    /// sentence is the same one.</summary>
    [Fact]
    public void ChunkText_PreservesOrderAcrossChunks()
    {
        var text = string.Concat(Enumerable.Range(0, 60)
            .Select(i => $"Sentence number {i} padded out to make the chunker split somewhere. "));

        var chunks = TextChunker.ChunkText(text, 300).ToList();

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(chunks));
        Assert.StartsWith("Sentence number 0 ", chunks[0]);
        Assert.EndsWith("split somewhere. ", chunks[^1]);
    }
}

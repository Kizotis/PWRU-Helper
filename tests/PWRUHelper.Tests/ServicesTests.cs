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

public class TranslationServiceChunkingTests
{
    [Fact]
    public void ChunkText_ShortText_IsSingleChunk()
    {
        var chunks = TranslationService.ChunkText("Just a short sentence.", 1500).ToList();
        Assert.Single(chunks);
    }

    [Fact]
    public void ChunkText_LongText_StaysUnderByteLimit_AndPreservesContent()
    {
        var sentence = "Это довольно длинное предложение для проверки разбиения. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 80));   // well over 1500 bytes
        const int limit = 1500;

        var chunks = TranslationService.ChunkText(text, limit).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= limit));
        Assert.Equal(text, string.Concat(chunks));   // nothing lost or duplicated
    }
}

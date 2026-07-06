using System.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class DeepLTranslatorTests
{
    [Theory]
    [InlineData("en", "EN-US")]
    [InlineData("EN", "EN-US")]
    [InlineData("fr", "FR")]
    [InlineData("ru", "RU")]
    [InlineData("", "EN-US")]
    [InlineData("auto", "EN-US")]
    public void ToDeepLTarget_maps_codes(string input, string expected)
        => Assert.Equal(expected, DeepLTranslator.ToDeepLTarget(input));

    [Theory]
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData("ru", "RU")]
    [InlineData("en", "EN")]
    public void ToDeepLSource_maps_codes_and_omits_auto(string input, string? expected)
        => Assert.Equal(expected, DeepLTranslator.ToDeepLSource(input));

    [Theory]
    [InlineData("abcd-1234:fx", true)]
    [InlineData("abcd-1234", false)]
    public void FreeKey_detects_fx_suffix(string key, bool expected)
        => Assert.Equal(expected, DeepLTranslator.FreeKey(key));

    [Fact]
    public void Parse_returns_translations_in_order()
    {
        var json = """{"translations":[{"detected_source_language":"RU","text":"hello"},{"text":"world"}]}""";
        Assert.Equal(new[] { "hello", "world" }, DeepLTranslator.Parse(json));
    }

    [Fact]
    public void Parse_throws_TranslationException_on_non_json()
        => Assert.Throws<TranslationException>(() => DeepLTranslator.Parse("<html>blocked</html>"));

    [Fact]
    public void Parse_throws_TranslationException_when_shape_is_wrong()
        => Assert.Throws<TranslationException>(() => DeepLTranslator.Parse("""{"message":"quota exceeded"}"""));
}

public class FallbackTranslatorTests
{
    private sealed class Fake : ITranslator
    {
        private readonly Func<string> _f;
        public int Calls;
        public Fake(Func<string> f) { _f = f; }

        public Task<string> TranslateAsync(string text, string s, string t, CancellationToken ct = default)
        { Calls++; return Task.FromResult(_f()); }

        public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string s, string t, CancellationToken ct = default)
        { Calls++; return Task.FromResult(lines.Select(_ => _f()).ToList()); }
    }

    [Fact]
    public async Task Uses_primary_when_it_succeeds()
    {
        var fallback = new Fake(() => "G");
        var ft = new FallbackTranslator(new Fake(() => "P"), fallback);

        Assert.Equal("P", await ft.TranslateAsync("x", "ru", "en"));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Falls_back_when_primary_fails()
    {
        var fallback = new Fake(() => "G");
        var ft = new FallbackTranslator(new Fake(() => throw new TranslationException("deepl down")), fallback);

        Assert.Equal("G", await ft.TranslateAsync("x", "ru", "en"));
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_fallback()
    {
        var fallback = new Fake(() => "G");
        var ft = new FallbackTranslator(new Fake(() => throw new OperationCanceledException()), fallback);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ft.TranslateAsync("x", "ru", "en"));
        Assert.Equal(0, fallback.Calls);
    }
}

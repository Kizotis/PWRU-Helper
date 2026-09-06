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

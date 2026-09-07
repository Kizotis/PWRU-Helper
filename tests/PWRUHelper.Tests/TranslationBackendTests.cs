using System.Threading;
using System.Web;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <c>[Collection("Gates")]</c> since E3.S8: the count-mismatch case below drives the REAL provider
/// through <see cref="HttpProviderCore"/>, which consults the process-global registry (IS-5). The
/// pure mapping and parser cases above it touch nothing.
/// </summary>
[Collection("Gates")]
public class DeepLTranslatorTests : GatesTestBase
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

    /// <summary>
    /// <b>TP-CHN-09 / TP-PRV-08 — I5, the reason E3.S8 exists.</b> DeepL is a 1:1 provider: one
    /// translation per input, in order. Three answers for four inputs is a <c>BadResponse</c> and
    /// <b>never</b> a padded list — padding once bypassed the fallback (the chain read the padded
    /// list as a success and never tried Google) AND cached raw Russian source as if it were a
    /// translation. The provider has been correct since that bug; this case is the guard, so a
    /// future 1:1 provider (Azure, E6.S2) inherits a pin rather than a habit.
    ///
    /// <para>Asserted twice over: the exception, and that not one source line came back as its own
    /// translation — which is exactly the shape padding produces and the only assertion an
    /// implementation that "helpfully" filled the gap could not satisfy.</para>
    /// </summary>
    [Fact]
    public async Task TP_CHN_09_a_short_batch_is_a_BadResponse_and_the_list_is_never_padded()
    {
        var lines = new[] { "раз", "два", "три", "четыре" };
        var fake = new FakeHandler().RespondJson(
            """{"translations":[{"text":"one"},{"text":"two"},{"text":"three"}]}""");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", fake).TranslateLinesAsync(lines, "ru", "en"));

        Assert.Equal(TranslationErrorKind.BadResponse, ex.Kind);
        // Non-vacuity: the request really was made and really carried all four lines, so the
        // mismatch is the provider's answer and not a call that never happened.
        var sent = Assert.Single(fake.Calls).Body ?? "";
        Assert.All(lines, l => Assert.Contains(HttpUtility.UrlEncode(l), sent, StringComparison.OrdinalIgnoreCase));
        // And nothing came back at all: a padded implementation returns a list whose fourth element
        // is the untranslated source. There is no list to inspect, which is the point.
        Assert.DoesNotContain("четыре", ex.Message);
    }
}

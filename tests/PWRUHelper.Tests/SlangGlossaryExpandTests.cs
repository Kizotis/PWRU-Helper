using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class SlangGlossaryExpandTests
{
    private const string Json = """
    {
      "entries": [
        { "keys": ["хил"], "meaning": "heal", "full": "лекарь" },
        { "keys": ["лфг", "lfg"], "meaning": "LFG", "full": "ищу группу" },
        { "keys": ["пп"], "meaning": "Full Moon Pavilion" },
        { "keys": ["в"], "meaning": "LFM", "context": true, "full": "набор в" }
      ]
    }
    """;

    private static SlangGlossary G() => SlangGlossary.FromJson(Json);

    [Fact]
    public void Expands_a_term_that_has_a_full_form()
        => Assert.Equal("нужен лекарь", G().Expand("нужен хил"));

    [Fact]
    public void Leaves_terms_without_a_full_form_untouched()
        => Assert.Equal("в пп", G().Expand("в пп"));

    [Fact]
    public void Expands_several_terms_in_one_line()
        => Assert.Equal("ищу группу лекарь", G().Expand("лфг хил"));

    [Fact]
    public void Context_only_terms_are_never_expanded_even_with_a_full()
        => Assert.Equal("в лес", G().Expand("в лес"));   // "в" is context:true → stays the preposition

    [Fact]
    public void Latin_lookalike_and_case_still_expand()
        => Assert.Equal("ищу группу", G().Expand("LFG"));

    [Fact]
    public void Line_with_no_slang_is_unchanged()
        => Assert.Equal("привет всем", G().Expand("привет всем"));

    [Fact]
    public void Empty_glossary_returns_the_line_unchanged()
        => Assert.Equal("нужен хил", SlangGlossary.FromJson(null).Expand("нужен хил"));

    [Fact]
    public void Decode_still_shows_the_english_meaning_after_expansion_is_available()
    {
        // Expand and Decode are independent: Decode keeps showing the English reading.
        var g = G();
        Assert.Contains("хил = heal", g.Decode("нужен хил"));
    }
}

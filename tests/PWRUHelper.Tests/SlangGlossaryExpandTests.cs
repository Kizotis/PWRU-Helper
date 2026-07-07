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

    // --- BUG A: a party-size count glued to the term must survive the rewrite ---
    // "2хил" matches via TryLookup's digit-strip; the "2" was silently dropped before.
    [Fact]
    public void Glued_count_is_re_attached_before_the_full_form()
        => Assert.Equal("нужно 2 лекарь в др", G().Expand("нужно 2хил в др"));

    // --- BUG B: edge punctuation on the matched token must wrap the rewrite ---
    // NormalizeToken trims the comma for lookup; Expand must put it back.
    [Fact]
    public void Trailing_punctuation_is_preserved_around_the_full_form()
        => Assert.Equal("нужен лекарь, потом го", G().Expand("нужен хил, потом го"));

    // --- BUG C: separators between UNTOUCHED tokens must appear exactly as typed ---
    // The either/or slash and the space between 4-1 and 5-1 must not be rewritten.
    [Fact]
    public void Separators_between_untouched_tokens_are_kept_verbatim()
        => Assert.Equal("лекарь 4-1/5-1", G().Expand("хил 4-1/5-1"));

    // Slash directly between two known terms: only the one with a Full form changes,
    // and the slash itself is preserved (пп has no Full, so it stays put).
    [Fact]
    public void Slash_between_two_known_terms_only_rewrites_the_one_with_a_full()
        => Assert.Equal("лекарь/пп", G().Expand("хил/пп"));

    // A run of whitespace between replaced terms is copied through, not collapsed.
    [Fact]
    public void Double_space_between_replaced_terms_is_kept()
        => Assert.Equal("ищу группу  лекарь", G().Expand("лфг  хил"));

    // --- Decode regression guards: the shared matcher must not change Decode's output ---
    [Fact]
    public void Decode_renders_a_single_term()
        => Assert.Equal("🔑 хил = heal", G().Decode("нужен хил"));

    [Fact]
    public void Decode_ignores_a_context_only_term_with_no_anchor()
        => Assert.Equal("", G().Decode("в лес"));   // "в" alone → nothing lights up

    [Fact]
    public void Decode_shows_a_context_term_alongside_a_real_anchor()
        => Assert.Equal("🔑 в = LFM  ·  пп = Full Moon Pavilion", G().Decode("в пп"));
}

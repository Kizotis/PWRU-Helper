using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The Translator tab highlights where the game would cut a long translation (78 characters per
/// chat message) instead of splitting it. The spans it highlights are offsets into the ORIGINAL
/// string — never a rebuilt one — because the text the user selects and pastes into the game must
/// be character-for-character what was translated.
/// </summary>
public class GameChatBlockSpanTests
{
    [Fact]
    public void A_message_that_fits_has_nothing_to_highlight()
    {
        Assert.Empty(TextMatching.GameChatBlockSpans("привет всем", TextMatching.GameChatLimit));
        Assert.Empty(TextMatching.GameChatBlockSpans("", TextMatching.GameChatLimit));
    }

    [Fact]
    public void The_spans_cover_the_text_in_order_and_none_exceeds_the_limit()
    {
        var text = string.Join(" ", Enumerable.Repeat("слово", 40));   // 239 chars → several blocks

        var spans = TextMatching.GameChatBlockSpans(text, TextMatching.GameChatLimit);

        Assert.True(spans.Count > 1);
        int previousEnd = 0;
        foreach (var (start, length) in spans)
        {
            Assert.True(length <= TextMatching.GameChatLimit, "a block must fit in one chat message");
            Assert.True(start >= previousEnd, "blocks must not overlap or run backwards");
            previousEnd = start + length;
        }
        Assert.True(previousEnd <= text.Length);
    }

    [Fact]
    public void The_highlighted_blocks_are_exactly_the_blocks_the_compact_overlay_would_send()
    {
        var text = string.Join(" ", Enumerable.Repeat("длинное сообщение", 12));

        var spans = TextMatching.GameChatBlockSpans(text, TextMatching.GameChatLimit);
        var split = TextMatching.SplitForGameChat(text, TextMatching.GameChatLimit);

        Assert.Equal(split, spans.Select(s => text.Substring(s.Start, s.Length)).ToList());
    }

    [Fact]
    public void A_multi_line_translation_over_the_limit_is_still_split_and_still_highlighted()
    {
        // The Translator input is multi-line (Shift+Enter), so a translation can carry a newline. The
        // highlighter used to LOCATE its blocks by searching for them in the original text — and the
        // splitter rejoined words with single spaces, so any block spanning a newline was never found.
        // Result: spans empty, no highlight, and the status cheerfully reported a 100-character text
        // as fitting in one 78-character chat message. The compact overlay, splitting the same text,
        // disagreed. Both are built on one primitive now.
        var text = new string('а', 50) + "\n" + new string('б', 50);   // 101 chars, separated by a newline

        var spans = TextMatching.GameChatBlockSpans(text, TextMatching.GameChatLimit);

        Assert.Equal(2, spans.Count);
        Assert.Equal(TextMatching.SplitForGameChat(text, TextMatching.GameChatLimit),
                     spans.Select(s => text.Substring(s.Start, s.Length)).ToList());
    }

    [Fact]
    public void A_double_space_between_words_does_not_hide_the_cut_either()
    {
        var text = string.Join("  ", Enumerable.Repeat("слово", 20));   // double spaces throughout

        Assert.True(TextMatching.GameChatBlockSpans(text, TextMatching.GameChatLimit).Count > 1);
    }

    [Fact]
    public void A_block_never_exceeds_the_limit_even_counting_the_separators_it_spans()
    {
        // The span is what the user selects and the game counts — separators included. Measuring the
        // rejoined words instead would under-count a newline-separated block.
        var text = string.Join("\n", Enumerable.Repeat("длинное сообщение", 12));

        Assert.All(TextMatching.GameChatBlockSpans(text, TextMatching.GameChatLimit),
                   s => Assert.True(s.Length <= TextMatching.GameChatLimit));
    }

    [Fact]
    public void Rebuilding_from_the_spans_gives_back_the_original_even_across_a_hard_split_word()
    {
        // A word longer than the limit is cut mid-word, with NO space between the halves. Rejoining
        // the split pieces with a space (the obvious implementation) would invent a character that
        // was never translated — and the user would paste it into the game. Spans can't: they only
        // ever point INTO the original.
        var text = new string('ы', 100) + " хвост";

        var spans = TextMatching.GameChatBlockSpans(text, TextMatching.GameChatLimit);
        var rebuilt = "";
        int cursor = 0;
        foreach (var (start, length) in spans)
        {
            rebuilt += text[cursor..start];                 // whatever separated the blocks (or nothing)
            rebuilt += text.Substring(start, length);
            cursor = start + length;
        }
        rebuilt += text[cursor..];

        Assert.Equal(text, rebuilt);
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace PWRUHelper.Services;

/// <summary>
/// Splits text that is too long for one GET query into pieces that fit, on sentence boundaries
/// where it can and by characters where it cannot. The budget is counted in <b>UTF-8 bytes</b>,
/// never in characters: a Cyrillic line is two bytes per character, so a character count would
/// let a "safe" chunk be twice the size the URL can carry.
///
/// <para>Moved here verbatim from the gtx provider's own <c>ChunkText</c>/<c>HardSplit</c> (E3.S6):
/// <see cref="GoogleGtxTranslator"/> is no longer the only provider that sends its text in a
/// query string — <c>GoogleDictTranslator</c> (E3.S4) needs the same budget — and a second copy
/// of a splitter is a second place for a chunk boundary to be wrong. <b>Not rewritten</b>: the
/// two properties the tests assert (every chunk within the budget, and the concatenation is the
/// input again, nothing lost or duplicated) are the only proof this code is right, and they only
/// prove it about the code that was already shipping.</para>
///
/// <para>The limit itself does not live here: it is <c>TranslationPolicy.MaxQueryBytes</c>, passed
/// in as the <c>maxBytes</c> argument, so a provider with a different query budget can use the
/// splitter.</para>
/// </summary>
internal static class TextChunker
{
    /// <summary>Split long text into &lt;= maxBytes chunks on sentence boundaries.</summary>
    internal static IEnumerable<string> ChunkText(string text, int maxBytes)
    {
        var pieces = Regex.Split(text, @"(?<=[\.\!\?…\n])");
        var current = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (current.Length > 0 &&
                Encoding.UTF8.GetByteCount(current.ToString() + piece) > maxBytes)
            {
                yield return current.ToString();
                current.Clear();
            }
            // A single piece longer than the limit: hard-split it.
            if (Encoding.UTF8.GetByteCount(piece) > maxBytes)
            {
                foreach (var hard in HardSplit(piece, maxBytes)) yield return hard;
                continue;
            }
            current.Append(piece);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    // private, as it was before the move: the only entry point is ChunkText, and a caller that
    // reached HardSplit directly would be splitting mid-sentence for no reason. Nothing outside
    // this class ever called it (E3.S6 review).
    private static IEnumerable<string> HardSplit(string s, int maxBytes)
    {
        // Split by characters so each chunk stays under the byte limit.
        var current = new StringBuilder();
        foreach (var ch in s)
        {
            if (Encoding.UTF8.GetByteCount(current.ToString() + ch) > maxBytes && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

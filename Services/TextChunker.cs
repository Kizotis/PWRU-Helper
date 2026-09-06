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
/// <para><b>One change since, and it is a fix, not a rewrite (E3.S4):</b> <c>HardSplit</c> walked
/// UTF-16 code units and could put a chunk boundary inside a surrogate pair, so an emoji straddling
/// the budget reached the endpoint as two U+FFFD. It now advances a code point at a time. The
/// properties above are unchanged and still hold — including for ill-formed input, which is carried
/// through byte for byte rather than repaired.</para>
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
        // Split by CODE POINTS, not by chars, so each chunk stays under the byte limit AND no chunk
        // boundary ever lands inside a surrogate pair.
        //
        // The char-wise version this replaces (E3.S6 moved it here verbatim, bug included) walked
        // UTF-16 units: an emoji is two of them, and a budget that ran out between them yielded a
        // chunk ending in a lone high surrogate and started the next one with the lone low
        // surrogate. Both encode to U+FFFD on their way into the query string, so the user got two
        // replacement characters instead of their emoji — silently, in the middle of a long
        // message, on the path a whole chat feed travels. E3.S4 is the story that found it, because
        // it is the second caller of this splitter (its provider carries the LIVE loop's volume).
        //
        // Deliberately NOT Rune enumeration: `EnumerateRunes` substitutes U+FFFD for an unpaired
        // surrogate, which would make the chunks no longer concatenate back to the input — the one
        // property every test in TextChunkerTests rests on. Ill-formed text goes through unchanged,
        // exactly as it did before; it is only the boundary that stops cutting pairs in half.
        var current = new StringBuilder();
        int bytes = 0;
        for (int i = 0; i < s.Length;)
        {
            int units = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                ? 2 : 1;
            // Measured on this code point alone and accumulated, rather than re-encoding the whole
            // buffer once per character: a 1500-byte chunk re-encoded 1500 times is quadratic work
            // on the rare path a long OCR line takes. (A span would say this more directly, but a
            // ReadOnlySpan cannot live across the `yield return` below.)
            int size = units == 2 ? 4 : Utf8Bytes(s[i]);

            if (bytes + size > maxBytes && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
                bytes = 0;
            }

            current.Append(s, i, units);
            bytes += size;
            i += units;
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>UTF-8 length of one BMP code unit — and of an unpaired surrogate, which the encoder
    /// writes as U+FFFD's three bytes. A paired surrogate is never asked about: its pair is four
    /// bytes and its caller knows it.</summary>
    private static int Utf8Bytes(char c) => c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
}

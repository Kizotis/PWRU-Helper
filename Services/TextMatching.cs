using System.Text.RegularExpressions;

namespace PWRUHelper.Services;

/// <summary>
/// Pure text helpers for the live-translation diff: splitting OCR output into
/// sentences and deciding when two noisy OCR readings are "the same line".
/// No UI dependencies, so it's easy to reason about (and unit-test) on its own.
/// </summary>
public static class TextMatching
{
    /// <summary>How many wrapped OCR lines a single sentence may span before we flush it
    /// anyway. A long chat message usually wraps over 1–3 on-screen lines; capping at 3
    /// stops us from ever gluing a whole screen of separate messages into one blob.</summary>
    private const int MaxLinesPerSentence = 3;

    /// <summary>Rebuild OCR output into whole sentences instead of chopping it line-by-line.
    /// A message that wrapped over 2–3 screen lines is stitched back together (so it gets
    /// translated as one coherent sentence), and a line holding several sentences is split
    /// on . ! ? … Consecutive lines are joined until one ends on sentence punctuation, a
    /// blank line breaks the run, or <see cref="MaxLinesPerSentence"/> lines have piled up.</summary>
    public static List<string> ToSentences(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var buffer = new List<string>();

        void Flush()
        {
            if (buffer.Count == 0) return;
            var joined = string.Join(" ", buffer).Trim();
            // The stitched block may itself contain several complete sentences — split those.
            foreach (var part in Regex.Split(joined, @"(?<=[\.\!\?…])\s+"))
            {
                var t = part.Trim();
                if (t.Length > 0) result.Add(t);
            }
            buffer.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }   // blank line = hard break
            buffer.Add(line);
            if (EndsSentence(line) || buffer.Count >= MaxLinesPerSentence) Flush();
        }
        Flush();
        return result;
    }

    /// <summary>True if the line ends on sentence-final punctuation (. ! ? …).</summary>
    private static bool EndsSentence(string line)
    {
        var t = line.TrimEnd();
        return t.Length > 0 && (t[^1] is '.' or '!' or '?' or '…');
    }

    /// <summary>Split a message into chunks no longer than <paramref name="maxChars"/>,
    /// breaking only between words (never mid-word) so each chunk can be pasted into a
    /// game chat that caps a single message's length. A word longer than the limit on its
    /// own is hard-split as a last resort. Returns one chunk if it already fits.</summary>
    public static List<string> SplitForGameChat(string text, int maxChars)
    {
        var chunks = new List<string>();
        text = text?.Trim() ?? "";
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            if (text.Length > 0) chunks.Add(text);
            return chunks;
        }

        var current = "";
        foreach (var word in Regex.Split(text, @"\s+"))
        {
            if (word.Length == 0) continue;

            // A single over-long word: emit what we have, then hard-split the word itself.
            if (word.Length > maxChars)
            {
                if (current.Length > 0) { chunks.Add(current); current = ""; }
                var rest = word;
                while (rest.Length > maxChars)
                {
                    chunks.Add(rest[..maxChars]);
                    rest = rest[maxChars..];
                }
                current = rest;
                continue;
            }

            if (current.Length == 0) current = word;
            else if (current.Length + 1 + word.Length <= maxChars) current += " " + word;
            else { chunks.Add(current); current = word; }
        }
        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }

    /// <summary>True if a line is worth translating (has at least <paramref name="minLetters"/>
    /// letters), rather than background specks. The threshold is user-tunable in live mode.</summary>
    public static bool LooksLikeText(string s, int minLetters = 2)
        => s.Count(char.IsLetter) >= Math.Max(1, minLetters);

    /// <summary>Lower-case, collapse whitespace, drop edge punctuation — so trivial OCR
    /// variations of the same line compare equal.</summary>
    public static string Normalize(string s)
    {
        s = Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
        return s.Trim(' ', '.', ',', '!', '?', ':', ';', '"', '\'', '-', '…', '(', ')');
    }

    /// <summary>True if <paramref name="line"/> fuzzy-matches any entry in the set.</summary>
    public static bool ContainsSimilar(IEnumerable<string> set, string line, double threshold)
        => set.Any(s => SimilarEnough(s, line, threshold));

    /// <summary>0..1 similarity from Levenshtein edit distance (1 = identical).</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    /// <summary>Like <see cref="Similarity"/> ≥ threshold, but skips the full edit-distance
    /// computation when the length difference alone already rules a match out — most
    /// candidate pairs in a chat differ in length, so this avoids the O(n·m) matrix.</summary>
    public static bool SimilarEnough(string a, string b, double threshold)
    {
        if (a == b) return true;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return true;
        // |lenA − lenB| edits are unavoidable, so this bounds the best possible score.
        if (1.0 - (double)Math.Abs(a.Length - b.Length) / max < threshold) return false;
        return 1.0 - (double)Levenshtein(a, b) / max >= threshold;
    }

    // Two-row Levenshtein: O(min(n,m)) memory instead of the full n·m matrix.
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

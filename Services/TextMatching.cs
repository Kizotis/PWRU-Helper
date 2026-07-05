using System.Text.RegularExpressions;

namespace PWRUHelper.Services;

/// <summary>
/// Pure text helpers for the live-translation diff: splitting OCR output into
/// sentences and deciding when two noisy OCR readings are "the same line".
/// No UI dependencies, so it's easy to reason about (and unit-test) on its own.
/// </summary>
public static class TextMatching
{
    /// <summary>Break OCR lines into individual sentences (split on . ! ? …).</summary>
    public static List<string> ToSentences(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
            foreach (var part in Regex.Split(line, @"(?<=[\.\!\?…])\s+"))
            {
                var t = part.Trim();
                if (t.Length > 0) result.Add(t);
            }
        return result;
    }

    /// <summary>True if a line is worth translating (has ≥2 letters), not background specks.</summary>
    public static bool LooksLikeText(string s) => s.Count(char.IsLetter) >= 2;

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

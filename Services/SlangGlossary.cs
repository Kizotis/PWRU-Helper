using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PWRUHelper.Services;

/// <summary>One glossary line: the Russian (or look-alike) tokens that mean the same thing,
/// and the community reading to show. <see cref="Context"/> entries (e.g. the bare word "в")
/// are only decoded when another, non-context term is present on the same line — that keeps
/// very common words from lighting up on ordinary chat.</summary>
public class SlangEntry
{
    public List<string> Keys { get; set; } = new();
    public string Meaning { get; set; } = "";
    public bool Context { get; set; }
    /// <summary>Optional grouping for the squad builder: "class", "dungeon" or "role"
    /// (see <see cref="SquadCatalog"/>). Absent on plain decode-only entries.</summary>
    public string Category { get; set; } = "";
    /// <summary>Optional Russian long-form used to REWRITE this term before machine translation
    /// (e.g. "хил" → "лекарь"), so the translation reads properly instead of leaving the raw
    /// abbreviation. Empty = leave the term unchanged. Only <see cref="SlangGlossary.Expand"/>
    /// uses it; the 🔑 decode line still shows <see cref="Meaning"/>.</summary>
    public string Full { get; set; } = "";
}

/// <summary>
/// A small dictionary of Perfect World RU chat slang (instances, roles, classes, events).
/// Given a chat line it returns a compact decode like "В = LFM · ПП = Full Moon Pavilion",
/// shown under the machine translation so the shorthand is readable. Pure/no UI, and fully
/// best-effort: a missing or broken glossary just decodes nothing.
/// </summary>
public class SlangGlossary
{
    private readonly Dictionary<string, SlangEntry> _byKey = new();
    private readonly List<SlangEntry> _entries = new();
    private int _maxWords = 1;

    public bool IsEmpty => _byKey.Count == 0;

    /// <summary>All glossary entries in file order (each once), for consumers like the squad
    /// builder that need to group them — as opposed to <see cref="Decode"/>'s per-key lookup.</summary>
    public IReadOnlyList<SlangEntry> Entries => _entries;

    private class Root { public List<SlangEntry>? Entries { get; set; } }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SlangGlossary FromJson(string? json)
    {
        var g = new SlangGlossary();
        if (string.IsNullOrWhiteSpace(json)) return g;
        try
        {
            var doc = JsonSerializer.Deserialize<Root>(json, Opts);
            if (doc?.Entries != null)
                foreach (var e in doc.Entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Meaning) || e.Keys == null) continue;
                    g._entries.Add(e);
                    foreach (var k in e.Keys)
                    {
                        var nk = NormalizeKey(k);
                        if (nk.Length == 0) continue;
                        g._byKey[nk] = e;
                        int words = nk.Count(c => c == ' ') + 1;
                        if (words > g._maxWords) g._maxWords = words;
                    }
                }
        }
        catch { /* bad json → empty glossary */ }
        return g;
    }

    /// <summary>Decode any slang on a line as "token = meaning · …", or "" if none found.</summary>
    public string Decode(string line)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(line)) return "";

        var raw = SplitTokens(line);
        var norm = raw.Select(NormalizeToken).ToList();

        // The tokenize + longest-match scaffold lives in MatchSpans (shared with Expand);
        // Decode just renders each matched span as "raw-token = meaning".
        var hits = new List<(string Display, SlangEntry Entry)>();
        foreach (var (idx, width, entry, _) in MatchSpans(norm))
            hits.Add((string.Join(" ", raw.GetRange(idx, width)), entry));

        if (hits.Count == 0) return "";

        // Context-only terms (e.g. "в") count only when a real term is also present.
        bool hasAnchor = hits.Any(h => !h.Entry.Context);
        var kept = hits.Where(h => hasAnchor || !h.Entry.Context).ToList();
        if (kept.Count == 0) return "";

        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        foreach (var (display, entry) in kept)
        {
            var pair = $"{display} = {entry.Meaning}";
            if (!seen.Add(pair)) continue;   // drop exact repeats
            if (sb.Length > 0) sb.Append("  ·  ");
            sb.Append(pair);
        }
        return sb.Length == 0 ? "" : "🔑 " + sb;
    }

    /// <summary>
    /// Rewrite a line for the translation backend: replace each known slang term that has a
    /// Russian long-form (<see cref="SlangEntry.Full"/>) with that long-form, so the machine
    /// translation is meaningful (e.g. "нужен хил" → "нужен лекарь" → "need a healer"). Terms
    /// without a Full form — and context-only terms like "в" — are left untouched, and a line
    /// with no expandable term comes back unchanged. This affects ONLY the text sent to the
    /// translator; the displayed original and the 🔑 decode still use the raw line.
    /// </summary>
    public string Expand(string line)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(line)) return line;

        // Position-aware tokens: each knows its exact span in the ORIGINAL string, so the
        // untouched runs between replacements can be copied back verbatim (keeps separators,
        // slashes and whitespace runs intact — see BUG C).
        var toks = TokenizeWithPositions(line);
        var raw = toks.Select(t => t.Token).ToList();
        var norm = raw.Select(NormalizeToken).ToList();

        var sb = new StringBuilder(line.Length);
        bool changed = false;
        int cursor = 0;   // how far into the ORIGINAL line we've already emitted

        foreach (var (idx, width, entry, count) in MatchSpans(norm))
        {
            // Filter AFTER matching (mirrors Decode's hasAnchor rule): a matched span that
            // is context-only, or has no Russian long-form, is left exactly as written — it
            // is NOT retried at a smaller width. Skipping here leaves its raw text inside the
            // next verbatim copy, so it survives unchanged.
            if (entry.Context || string.IsNullOrWhiteSpace(entry.Full)) continue;

            int spanStart = toks[idx].Start;
            var last = toks[idx + width - 1];
            int spanEnd = last.Start + last.Length;

            // Copy the original text (separators + unmatched tokens) up to this span verbatim.
            sb.Append(line, cursor, spanStart - cursor);

            // Re-attach everything that was trimmed BEFORE the lookup, so nothing is silently
            // dropped: leading punctuation of the first token, then the glued party-size count,
            // then the rewrite, then the trailing punctuation of the last token.
            sb.Append(LeadingTrim(raw[idx]));                      // BUG B: leading punctuation
            if (count.Length > 0) sb.Append(count).Append(' ');   // BUG A: "2хил" → "2 лекарь"
            sb.Append(entry.Full.Trim());                         // the rewrite itself
            sb.Append(TrailingTrim(raw[idx + width - 1]));        // BUG B: trailing punctuation

            cursor = spanEnd;
            changed = true;
        }

        if (!changed) return line;

        sb.Append(line, cursor, line.Length - cursor);   // the tail, verbatim
        return sb.ToString();
    }

    /// <summary>The shared tokenize + longest-match scaffold used by both <see cref="Decode"/>
    /// and <see cref="Expand"/>. Yields non-overlapping matched spans left-to-right, longest
    /// first (so "арена героев" beats "арена"). A span is yielded iff <see cref="TryLookup"/>
    /// succeeds — NO Context/Full filtering here; each caller filters afterwards. <c>Count</c>
    /// is the leading party-size that TryLookup stripped ("" when the key matched directly),
    /// so Expand can re-attach it.</summary>
    private IEnumerable<(int Index, int Width, SlangEntry Entry, string Count)> MatchSpans(IReadOnlyList<string> norm)
    {
        int i = 0;
        while (i < norm.Count)
        {
            bool matched = false;
            int maxSpan = Math.Min(_maxWords, norm.Count - i);
            for (int w = maxSpan; w >= 1 && !matched; w--)
            {
                var key = string.Join(" ", Enumerable.Range(i, w).Select(k => norm[k])).Trim();
                if (key.Length == 0) continue;
                if (TryLookup(key, out var entry, out var count))
                {
                    yield return (i, w, entry, count);
                    i += w;
                    matched = true;
                }
            }
            if (!matched) i++;
        }
    }

    private bool TryLookup(string key, out SlangEntry entry, out string count)
    {
        count = "";
        if (_byKey.TryGetValue(key, out entry!)) return true;
        // "2дд" / "3танк": a leading count glued to the term — retry without the digits, and
        // remember them so Expand can re-attach the party size ("2хил" → "2 лекарь"). Only the
        // stripped path sets Count; a direct key match like "4-1" keeps Count empty.
        var m = Regex.Match(key, @"^\d+");
        if (m.Success)
        {
            var stripped = key.Substring(m.Length);
            if (stripped.Length > 0 && _byKey.TryGetValue(stripped, out entry!))
            {
                count = m.Value;
                return true;
            }
        }
        entry = null!;
        return false;
    }

    // Split on whitespace and slashes so "ДРУ/Жнец" and "seek/heal" become separate tokens.
    // Both this and TokenizeWithPositions treat [\s/] as the separator class, so the two agree
    // on token boundaries.
    private static List<string> SplitTokens(string line)
        => TokenizeWithPositions(line).Select(t => t.Token).ToList();

    // Tokens plus their exact span in the original line. The pattern is the complement of the
    // [\s/]+ separator class used by SplitTokens, so it yields the same tokens — with positions.
    private static List<(string Token, int Start, int Length)> TokenizeWithPositions(string line)
    {
        var list = new List<(string, int, int)>();
        foreach (Match m in Regex.Matches(line, @"[^\s/]+"))
            list.Add((m.Value, m.Index, m.Length));
        return list;
    }

    // Edge punctuation stripped from a token before lookup. Factored into one const so the trim
    // (NormalizeToken) and the re-attach (Expand's Leading/TrailingTrim) can never drift apart.
    private static readonly char[] EdgeTrim =
        { '+', '.', ',', '!', '?', ':', ';', '"', '\'', '(', ')', '…', '-', '*' };

    // Lower-case; drop leading "+" and edge punctuation, but keep inner digits/hyphens
    // so "4-1", "+ДД" and "999" still resolve.
    private static string NormalizeToken(string t)
    {
        t = Fold(t.Trim().ToLowerInvariant());
        return t.Trim(EdgeTrim);
    }

    // The leading / trailing run of edge punctuation on a raw token — exactly the characters
    // NormalizeToken's Trim(EdgeTrim) removes from each end. Expand wraps the rewrite with these
    // so "хил," → "лекарь," keeps its comma (BUG B). Folding only maps letters, so the punctuation
    // positions are identical in the raw and normalized forms.
    private static string LeadingTrim(string raw)
    {
        int i = 0;
        while (i < raw.Length && Array.IndexOf(EdgeTrim, raw[i]) >= 0) i++;
        return raw.Substring(0, i);
    }

    private static string TrailingTrim(string raw)
    {
        int i = raw.Length;
        while (i > 0 && Array.IndexOf(EdgeTrim, raw[i - 1]) >= 0) i--;
        return raw.Substring(i);
    }

    private static string NormalizeKey(string k)
        => Fold(Regex.Replace(k.Trim().ToLowerInvariant(), @"\s+", " "));

    // Players mix Latin look-alikes into Cyrillic slang ("ТC" with a Latin c, "APA", "MOPE"),
    // and OCR does the same. Fold the common visually-identical Latin letters onto their
    // Cyrillic twin so both the token and the key land on the same string before matching.
    // Applied to keys too, so a key written in Latin ("apa"/"cnh") folds the same way.
    private static readonly Dictionary<char, char> Homoglyphs = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к',
        ['m'] = 'м', ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у',
    };

    private static string Fold(string s)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Homoglyphs.TryGetValue(c, out var cyr) ? cyr : c);
        return sb.ToString();
    }
}

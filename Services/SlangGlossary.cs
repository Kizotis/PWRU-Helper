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

        var hits = new List<(string Display, SlangEntry Entry)>();
        int i = 0;
        while (i < raw.Count)
        {
            bool matched = false;
            // Longest match first (so "арена героев" beats "арена").
            int maxSpan = Math.Min(_maxWords, raw.Count - i);
            for (int w = maxSpan; w >= 1 && !matched; w--)
            {
                var key = string.Join(" ", norm.GetRange(i, w)).Trim();
                if (key.Length == 0) continue;
                if (TryLookup(key, out var entry))
                {
                    hits.Add((string.Join(" ", raw.GetRange(i, w)), entry));
                    i += w;
                    matched = true;
                }
            }
            if (!matched) i++;
        }

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

    private bool TryLookup(string key, out SlangEntry entry)
    {
        if (_byKey.TryGetValue(key, out entry!)) return true;
        // "2дд" / "3танк": a leading count glued to the term — retry without the digits.
        var stripped = Regex.Replace(key, @"^\d+", "");
        if (stripped.Length > 0 && stripped != key && _byKey.TryGetValue(stripped, out entry!))
            return true;
        entry = null!;
        return false;
    }

    // Split on whitespace and slashes so "ДРУ/Жнец" and "seek/heal" become separate tokens.
    private static List<string> SplitTokens(string line)
        => Regex.Split(line, @"[\s/]+").Where(t => t.Length > 0).ToList();

    // Lower-case; drop leading "+" and edge punctuation, but keep inner digits/hyphens
    // so "4-1", "+ДД" and "999" still resolve.
    private static string NormalizeToken(string t)
    {
        t = Fold(t.Trim().ToLowerInvariant());
        return t.Trim('+', '.', ',', '!', '?', ':', ';', '"', '\'', '(', ')', '…', '-', '*');
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

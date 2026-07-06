namespace PWRUHelper.Services;

/// <summary>One tickable option in the squad builder: the Russian token to drop into the
/// chat phrase, and the readable English label shown next to the checkbox.</summary>
public sealed class SquadOption
{
    public string Token { get; init; } = "";   // the RU token pasted into chat (e.g. "лега")
    public string Label { get; init; } = "";   // the English meaning shown to the user
}

/// <summary>
/// Turns the shared slang glossary (<see cref="SlangGlossary"/>) into the three tick-lists the
/// squad builder shows — dungeons, classes and roles — and assembles the LFM chat phrase from
/// what the user ticked. Pure and UI-free, so it's unit-tested directly.
///
/// Categories come from the glossary entry's <see cref="SlangEntry.Category"/> when present
/// (new/edited <c>slang.json</c>), and fall back to a built-in token map so an older editable
/// <c>slang.json</c> without categories still populates the tab. That keeps the JSON the source
/// of truth for new terms while never leaving an existing user with an empty tab.
/// </summary>
public static class SquadCatalog
{
    public const string Dungeon = "dungeon", Class = "class", Role = "role";

    // Built-in fallback: canonical token (first key, folded to Cyrillic look-alikes) → category.
    // Covers every class/dungeon/role currently in slang.json so the tab works even if the user's
    // editable copy predates the "category" field.
    private static readonly Dictionary<string, string> Fallback = new()
    {
        // dungeons / instances
        ["ара"] = Dungeon, ["пп"] = Dungeon, ["ми"] = Dungeon, ["гш"] = Dungeon, ["сц"] = Dungeon,
        ["4-1"] = Dungeon, ["4-2"] = Dungeon, ["тс"] = Dungeon, ["5-1"] = Dungeon, ["5-2"] = Dungeon,
        ["5-3"] = Dungeon, ["хс"] = Dungeon, ["ла"] = Dungeon, ["др"] = Dungeon, ["ор"] = Dungeon,
        // roles
        ["дд"] = Role, ["хил"] = Role, ["танк"] = Role,
        // classes
        ["прист"] = Class, ["мист"] = Class, ["сик"] = Class, ["вар"] = Class, ["дру"] = Class,
        ["син"] = Class, ["жнец"] = Class, ["лук"] = Class, ["шам"] = Class, ["пал"] = Class,
        ["гост"] = Class, ["макака"] = Class, ["ганер"] = Class, ["бард"] = Class, ["дк"] = Class,
    };

    /// <summary>Category of an entry: its explicit <c>category</c>, else the built-in fallback
    /// keyed on its first token, else "" (not shown in the squad builder).</summary>
    public static string CategoryOf(SlangEntry e)
    {
        var explicitCat = (e.Category ?? "").Trim().ToLowerInvariant();
        if (explicitCat is Dungeon or Class or Role) return explicitCat;
        var token = e.Keys != null && e.Keys.Count > 0 ? Fold(e.Keys[0]) : "";
        return Fallback.TryGetValue(token, out var cat) ? cat : "";
    }

    /// <summary>Split the glossary into the three ordered tick-lists for the tab. Order follows
    /// the glossary (i.e. slang.json) so the user controls it by editing the file.</summary>
    public static (List<SquadOption> Dungeons, List<SquadOption> Classes, List<SquadOption> Roles)
        Build(IEnumerable<SlangEntry> entries)
    {
        var dungeons = new List<SquadOption>();
        var classes = new List<SquadOption>();
        var roles = new List<SquadOption>();
        var seen = new HashSet<string>();

        foreach (var e in entries)
        {
            if (e.Keys == null || e.Keys.Count == 0) continue;
            var token = e.Keys[0].Trim();
            if (token.Length == 0 || !seen.Add(token.ToLowerInvariant())) continue;

            var opt = new SquadOption { Token = token, Label = e.Meaning };
            switch (CategoryOf(e))
            {
                case Dungeon: dungeons.Add(opt); break;
                case Class: classes.Add(opt); break;
                case Role: roles.Add(opt); break;
            }
        }
        return (dungeons, classes, roles);
    }

    /// <summary>Assemble the LFM chat phrase: <c>prefix dungeons classes roles</c>, each group in
    /// the order it was ticked. Empty groups and a blank prefix are simply skipped.</summary>
    public static string BuildPhrase(string prefix,
        IEnumerable<string> dungeons, IEnumerable<string> classes, IEnumerable<string> roles)
    {
        var body = dungeons.Concat(classes).Concat(roles)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (body.Count == 0) return "";   // nothing ticked → no phrase (never a lone "в")

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) parts.Add(prefix.Trim());
        parts.AddRange(body);
        return string.Join(" ", parts);
    }

    // Same Latin→Cyrillic homoglyph folding SlangGlossary uses, so a fallback key written in
    // Latin look-alikes (rare, but possible in a hand-edited file) still matches.
    private static readonly Dictionary<char, char> Homoglyphs = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к',
        ['m'] = 'м', ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у',
    };

    private static string Fold(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Homoglyphs.TryGetValue(c, out var cyr) ? cyr : c);
        return sb.ToString();
    }
}

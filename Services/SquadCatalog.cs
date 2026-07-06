using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>One tickable option in the squad builder.</summary>
public sealed class SquadOption
{
    public string Code { get; set; } = "";    // EN abbreviation shown first (EU, MWT, Sin…)
    public string Ru { get; set; } = "";       // the Russian form shown in quotes (APA, ПП, син…)
    public string Name { get; set; } = "";     // English name (Cave of Eternity, assassin…)
    public string Token { get; set; } = "";    // what actually gets pasted into chat (апа, пп, син…)
}

/// <summary>A titled column of options (a dungeon/class group like WEAPON or DD).</summary>
public sealed class SquadColumn
{
    public string Title { get; set; } = "";
    public List<SquadOption> Items { get; set; } = new();
}

/// <summary>
/// The squad builder's catalogue, loaded from <c>Data/squad.json</c>: dungeons and classes,
/// each as a list of titled columns of tick-boxes, plus the LFM phrase assembly. Pure and
/// UI-free so it's unit-tested directly; a missing/broken file just yields empty lists.
/// </summary>
public sealed class SquadCatalog
{
    public List<SquadColumn> Dungeons { get; private set; } = new();
    public List<SquadColumn> Classes { get; private set; } = new();

    public bool IsEmpty => Dungeons.Count == 0 && Classes.Count == 0;

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class Root
    {
        public List<SquadColumn>? Dungeons { get; set; }
        public List<SquadColumn>? Classes { get; set; }
    }

    public static SquadCatalog FromJson(string? json)
    {
        var cat = new SquadCatalog();
        if (string.IsNullOrWhiteSpace(json)) return cat;
        try
        {
            var doc = JsonSerializer.Deserialize<Root>(json, Opts);
            if (doc != null)
            {
                cat.Dungeons = Clean(doc.Dungeons);
                cat.Classes = Clean(doc.Classes);
            }
        }
        catch { /* bad json → empty catalogue (tab shows a hint) */ }
        return cat;
    }

    // Drop empty columns/items and any item without a paste token, so the UI never renders blanks.
    private static List<SquadColumn> Clean(List<SquadColumn>? columns)
    {
        var result = new List<SquadColumn>();
        if (columns == null) return result;
        foreach (var col in columns)
        {
            var items = (col.Items ?? new()).Where(i => !string.IsNullOrWhiteSpace(i.Token)).ToList();
            if (items.Count == 0) continue;
            result.Add(new SquadColumn { Title = col.Title ?? "", Items = items });
        }
        return result;
    }

    /// <summary>Assemble the LFM chat phrase: <c>prefix dungeons classes</c>, each group in the
    /// order it was ticked. Empty groups and a blank prefix are skipped; nothing ticked → "".</summary>
    public static string BuildPhrase(string prefix, IEnumerable<string> dungeons, IEnumerable<string> classes)
    {
        var body = dungeons.Concat(classes)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (body.Count == 0) return "";   // nothing ticked → no phrase (never a lone "в")

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) parts.Add(prefix.Trim());
        parts.AddRange(body);
        return string.Join(" ", parts);
    }
}

using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class SquadCatalogTests
{
    // ---- BuildPhrase ----

    [Fact]
    public void BuildPhrase_AssemblesPrefixThenDungeonsClassesRoles()
        => Assert.Equal("в лега прист вар дд",
            SquadCatalog.BuildPhrase("в", new[] { "лега" }, new[] { "прист", "вар" }, new[] { "дд" }));

    [Fact]
    public void BuildPhrase_SkipsEmptyGroupsAndBlankPrefix()
        => Assert.Equal("прист",
            SquadCatalog.BuildPhrase("", System.Array.Empty<string>(), new[] { "прист" }, System.Array.Empty<string>()));

    [Fact]
    public void BuildPhrase_NothingTicked_IsEmpty()
        => Assert.Equal("", SquadCatalog.BuildPhrase("в",
            System.Array.Empty<string>(), System.Array.Empty<string>(), System.Array.Empty<string>()));

    // ---- Build: explicit categories from slang.json ----

    [Fact]
    public void Build_GroupsByExplicitCategory()
    {
        var g = SlangGlossary.FromJson("""
        { "entries": [
            { "keys": ["тс"],   "meaning": "Terrace",  "category": "dungeon" },
            { "keys": ["прист"],"meaning": "cleric",   "category": "class" },
            { "keys": ["дд"],   "meaning": "DD",       "category": "role" },
            { "keys": ["гвг"],  "meaning": "GvG" }
        ] }
        """);
        var (dungeons, classes, roles) = SquadCatalog.Build(g.Entries);
        Assert.Equal(new[] { "тс" }, dungeons.ConvertAll(o => o.Token));
        Assert.Equal(new[] { "прист" }, classes.ConvertAll(o => o.Token));
        Assert.Equal(new[] { "дд" }, roles.ConvertAll(o => o.Token));
        Assert.Equal("cleric", classes[0].Label);   // label is the English meaning
    }

    [Fact]
    public void Build_EmitsFirstKeyAsTheToken()
    {
        var g = SlangGlossary.FromJson("""
        { "entries": [ { "keys": ["5-3", "лега"], "meaning": "Terrace legendary", "category": "dungeon" } ] }
        """);
        var (dungeons, _, _) = SquadCatalog.Build(g.Entries);
        Assert.Equal("5-3", dungeons[0].Token);      // the first key is what gets pasted
    }

    // ---- Build: fallback for an older slang.json without categories ----

    [Fact]
    public void Build_FallsBackToBuiltInWhenNoCategory()
    {
        // No "category" fields at all (an existing user's editable copy) — known tokens still group.
        var g = SlangGlossary.FromJson("""
        { "entries": [
            { "keys": ["тс"],    "meaning": "Terrace" },
            { "keys": ["вар"],   "meaning": "BM" },
            { "keys": ["танк"],  "meaning": "tank" },
            { "keys": ["привет"],"meaning": "hello" }
        ] }
        """);
        var (dungeons, classes, roles) = SquadCatalog.Build(g.Entries);
        Assert.Contains(dungeons, o => o.Token == "тс");
        Assert.Contains(classes, o => o.Token == "вар");
        Assert.Contains(roles, o => o.Token == "танк");
        Assert.DoesNotContain(dungeons, o => o.Token == "привет");   // unknown, uncategorised → excluded
    }

    [Fact]
    public void CategoryOf_ExplicitWinsOverFallback()
    {
        // "тс" would fall back to dungeon, but an explicit category must win.
        var e = new SlangEntry { Keys = { }, Meaning = "x", Category = "class" };
        e.Keys.Add("тс");
        Assert.Equal("class", SquadCatalog.CategoryOf(e));
    }
}

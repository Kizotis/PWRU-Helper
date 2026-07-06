using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

public class SquadCatalogTests
{
    private const string Sample = """
    {
      "dungeons": [
        { "title": "WEAPON", "items": [
          { "code": "EU",  "ru": "APA", "name": "Cave of Eternity (low)", "token": "апа" },
          { "code": "MWT", "ru": "ПП",  "name": "Full Moon Pavilion",     "token": "пп" }
        ]},
        { "title": "ARMOR", "items": [
          { "code": "5-3", "ru": "Лега", "name": "Terrace (legendary)", "token": "лега" }
        ]}
      ],
      "classes": [
        { "title": "DD", "items": [
          { "code": "Sin", "ru": "син", "name": "assassin", "token": "син" }
        ]}
      ]
    }
    """;

    // ---- FromJson ----

    [Fact]
    public void FromJson_ParsesColumnsAndItems()
    {
        var cat = SquadCatalog.FromJson(Sample);
        Assert.Equal(new[] { "WEAPON", "ARMOR" }, cat.Dungeons.ConvertAll(c => c.Title));
        Assert.Single(cat.Classes);
        Assert.Equal("Sin", cat.Classes[0].Items[0].Code);
        Assert.Equal("син", cat.Classes[0].Items[0].Token);
        Assert.Equal("APA", cat.Dungeons[0].Items[0].Ru);
    }

    [Fact]
    public void FromJson_BadOrEmptyJson_IsEmpty()
    {
        Assert.True(SquadCatalog.FromJson(null).IsEmpty);
        Assert.True(SquadCatalog.FromJson("{ not valid json").IsEmpty);
    }

    [Fact]
    public void FromJson_DropsItemsWithoutATokenAndEmptyColumns()
    {
        var cat = SquadCatalog.FromJson("""
        { "dungeons": [
            { "title": "A", "items": [ { "code": "X", "token": "" }, { "code": "Y", "token": "гш" } ] },
            { "title": "B", "items": [ { "code": "Z", "token": "" } ] }
        ] }
        """);
        Assert.Single(cat.Dungeons);                       // column B had only a tokenless item → dropped
        Assert.Single(cat.Dungeons[0].Items);              // the tokenless item in A is gone
        Assert.Equal("гш", cat.Dungeons[0].Items[0].Token);
    }

    // ---- BuildPhrase ----

    [Fact]
    public void BuildPhrase_AssemblesPrefixThenDungeonsThenClasses()
        => Assert.Equal("в лега прист вар",
            SquadCatalog.BuildPhrase("в", new[] { "лега" }, new[] { "прист", "вар" }));

    [Fact]
    public void BuildPhrase_MultiWordTokenIsKept()
        => Assert.Equal("в тс простой прист",
            SquadCatalog.BuildPhrase("в", new[] { "тс простой" }, new[] { "прист" }));

    [Fact]
    public void BuildPhrase_NothingTicked_IsEmpty()
        => Assert.Equal("", SquadCatalog.BuildPhrase("в", System.Array.Empty<string>(), System.Array.Empty<string>()));
}

using System.IO;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>The shipped squad.json (labels the user reads in game) and the two mechanisms that
/// make a change to it actually reach a player: the UPPERCASE tick, and the versioned refresh of
/// the editable copy.</summary>
public class SquadDataTests
{
    // ---- UPPERCASE tick ----

    [Fact]
    public void BuildPhrase_shouts_the_whole_line_when_asked()
    {
        var phrase = SquadCatalog.BuildPhrase("в", new[] { "лега" }, new[] { "хил", "дд" }, uppercase: true);
        Assert.Equal("В ЛЕГА ХИЛ ДД", phrase);
    }

    [Fact]
    public void BuildPhrase_leaves_the_line_alone_by_default()
    {
        Assert.Equal("в лега хил", SquadCatalog.BuildPhrase("в", new[] { "лега" }, new[] { "хил" }));
    }

    [Fact]
    public void BuildPhrase_uppercase_of_nothing_is_still_nothing()
    {
        // Never a lone shouted "В" when no box is ticked.
        Assert.Equal("", SquadCatalog.BuildPhrase("в", Array.Empty<string>(), Array.Empty<string>(), uppercase: true));
    }

    // ---- versioned refresh of the editable copy ----
    //
    // The editable copy is written once, on first run, and then belongs to the user — so every
    // later fix to the shipped file (these very labels) used to reach only NEW installs. The
    // shipped file now carries a "version"; a copy older than it is replaced, with a .bak kept.

    [Fact]
    public void DataVersionOf_reads_the_shipped_version_and_tolerates_anything_else()
    {
        Assert.Equal(2, MainWindow.DataVersionOf("""{ "version": 2, "classes": [] }"""));
        Assert.Equal(0, MainWindow.DataVersionOf("""{ "classes": [] }"""));   // unversioned → never refreshed
        Assert.Equal(0, MainWindow.DataVersionOf("[ { \"en\": \"hi\" } ]"));  // phrases.json is an array, not an object
        Assert.Equal(0, MainWindow.DataVersionOf("not json at all"));         // corrupt → left strictly alone
        Assert.Equal(0, MainWindow.DataVersionOf(null));
    }

    [Fact]
    public void An_older_editable_copy_is_replaced_by_the_shipped_one_and_backed_up()
    {
        var dir = Directory.CreateTempSubdirectory("pwru-squad-");
        try
        {
            var copy = Path.Combine(dir.FullName, "squad.json");
            File.WriteAllText(copy, """{ "version": 1, "classes": [] }""");     // what the user has
            const string shipped = """{ "version": 2, "classes": [] }""";       // what we now ship

            MainWindow.UpgradeEditableIfStale(copy, shipped);

            Assert.Equal(2, MainWindow.DataVersionOf(File.ReadAllText(copy)));                  // refreshed
            Assert.Equal(1, MainWindow.DataVersionOf(File.ReadAllText(copy + ".bak")));         // recoverable
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void An_up_to_date_or_newer_copy_is_never_touched()
    {
        var dir = Directory.CreateTempSubdirectory("pwru-squad-");
        try
        {
            var copy = Path.Combine(dir.FullName, "squad.json");
            // Same version, but the user has added their own item — their edits must survive.
            const string mine = """{ "version": 2, "classes": [ { "title": "Mine", "items": [] } ] }""";
            File.WriteAllText(copy, mine);

            MainWindow.UpgradeEditableIfStale(copy, """{ "version": 2, "classes": [] }""");

            Assert.Equal(mine, File.ReadAllText(copy));
            Assert.False(File.Exists(copy + ".bak"));   // nothing happened, so nothing to back up
        }
        finally { dir.Delete(recursive: true); }
    }

    // ---- the shipped file itself ----

    [Fact]
    public void The_shipped_squad_json_is_versioned_and_parses()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "squad.json"));
        Assert.True(MainWindow.DataVersionOf(json) >= 2,
            "Bump \"version\" in Data/squad.json whenever you edit it, or existing players keep their old copy.");

        var catalog = SquadCatalog.FromJson(json);
        var classes = catalog.Classes.SelectMany(c => c.Items).ToList();

        // Classes show the English name in gold and the Russian in quotes — and nothing in white
        // (the extra "name" column is for dungeons, whose code is a cryptic abbreviation).
        Assert.All(classes, c => Assert.Equal("", c.Name));
        Assert.All(classes, c => Assert.False(string.IsNullOrWhiteSpace(c.Ru)));
        Assert.Contains(classes, c => c.Code == "invite on me" && c.Token == "стук");
        Assert.Contains(classes, c => c.Code == "blademaster" && c.Token == "вар");
        Assert.Contains(classes, c => c.Code == "duskblade" && c.Token == "гост");

        // Dungeons keep their white English name — that's the only clue to what "ГШ" means.
        Assert.All(catalog.Dungeons.SelectMany(d => d.Items),
            d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
    }
}

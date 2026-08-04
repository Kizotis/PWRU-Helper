using System.IO;
using System.Linq;
using System.Text.Json;
using PWRUHelper.Models;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The shipped phrases.json, and the two things that decide whether an edit to it is worth making:
/// it has to LOAD (in both shapes that exist in the wild) and it has to REACH people who already
/// ran the app.
///
/// That second one was silently broken until now: phrases.json had its own create-only locator and
/// no "version", so an editable copy was written on first run and then frozen forever. Every
/// phrasebook change since the app shipped reached new installs only.
/// </summary>
public class PhrasebookDataTests
{
    private static string ShippedJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "phrases.json"));

    private static List<Phrase> Shipped() => MainWindow.ParsePhrases(ShippedJson())!;

    // ---- it reaches existing users ----

    [Fact]
    public void The_shipped_phrases_json_is_versioned()
    {
        Assert.True(MainWindow.DataVersionOf(ShippedJson()) > 0,
            "Bump \"version\" in Data/phrases.json whenever you edit it, or existing players keep their old copy forever.");
    }

    [Fact]
    public void A_bare_array_copy_still_loads_so_nobody_is_left_with_an_empty_phrasebook()
    {
        // The shape every editable copy had before versioning. It is refreshed on the next launch,
        // but it must still load on a run where the refresh can't happen (read-only folder).
        var items = MainWindow.ParsePhrases("""[ {"category":"Greetings","en":"Hi","ru":"Привет","translit":"Privet"} ]""");

        Assert.NotNull(items);
        Assert.Equal("Привет", Assert.Single(items!).Ru);
    }

    [Fact]
    public void Both_shapes_yield_the_same_list()
    {
        const string entry = """{"category":"Greetings","en":"Hi","ru":"Привет","translit":"Privet"}""";

        var fromArray = MainWindow.ParsePhrases($"[ {entry} ]")!;
        var fromObject = MainWindow.ParsePhrases($$"""{ "version": 1, "phrases": [ {{entry}} ] }""")!;

        Assert.Equal(fromArray.Single().Ru, fromObject.Single().Ru);
        Assert.Equal(fromArray.Single().Category, fromObject.Single().Category);
    }

    [Fact]
    public void An_object_without_a_phrases_array_is_no_list_rather_than_a_crash()
    {
        Assert.Null(MainWindow.ParsePhrases("""{ "version": 1 }"""));
        Assert.Null(MainWindow.ParsePhrases("""{ "version": 1, "phrases": "oops" }"""));
        Assert.Null(MainWindow.ParsePhrases(""));
        Assert.Null(MainWindow.ParsePhrases(null));
    }

    // ---- what the list contains ----

    [Fact]
    public void Every_squad_dungeon_role_and_class_is_in_the_phrasebook_under_its_exact_squad_name()
    {
        var squad = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "squad.json"))).RootElement;
        var phrases = Shipped();

        foreach (var section in new[] { "dungeons", "classes" })
            foreach (var column in squad.GetProperty(section).EnumerateArray())
                foreach (var item in column.GetProperty("items").EnumerateArray())
                {
                    var code = item.GetProperty("code").GetString()!;
                    var token = item.GetProperty("token").GetString()!;

                    // The paste token is what a phrase card copies, so it must match exactly —
                    // a card that pastes something Squad wouldn't is worse than no card.
                    Assert.True(phrases.Any(p => p.Ru == token && p.En.Contains(code, StringComparison.Ordinal)),
                        $"Squad has \"{code}\" (pastes \"{token}\") but the phrasebook has no entry for it.");
                }
    }

    [Fact]
    public void The_squad_entries_are_grouped_under_their_own_headings()
    {
        var byCategory = Shipped().GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(15, byCategory["Dungeons"]);
        Assert.Equal(3, byCategory["Squad roles"]);
        Assert.Equal(16, byCategory["Squad classes"]);
    }

    [Fact]
    public void Every_entry_has_something_to_copy_and_something_to_read()
    {
        foreach (var p in Shipped())
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Ru), $"\"{p.En}\" has nothing to copy.");
            Assert.False(string.IsNullOrWhiteSpace(p.En), $"\"{p.Ru}\" has no English meaning.");
            Assert.False(string.IsNullOrWhiteSpace(p.Category), $"\"{p.Ru}\" has no category.");
        }
    }

    [Fact]
    public void The_phrases_the_translator_covers_on_its_own_are_gone()
    {
        // Trimmed deliberately: anything a user would rather type into the Translator does not earn
        // a permanent card. Kept here as a list so re-adding one is a decision, not an accident.
        string[] removed =
        {
            "Здравствуйте", "Прив", "Добрый день", "Салют", "Пока-пока", "Плиз", "Извините",
            "Сори", "Хорошая игра", "Го го го", "Нужен хил", "Сколько стоит?", "Слишком дорого",
            "Обмен?", "Ок", "Окей", "Может быть",
        };
        var present = Shipped().Select(p => p.Ru).ToHashSet();

        foreach (var gone in removed) Assert.DoesNotContain(gone, present);
    }

    [Fact]
    public void Trimming_did_not_take_the_lookalikes_with_it()
    {
        // Each of these is one edit away from something on the removed list ("Прив" vs "Всем прив",
        // "Извините" vs "Извини", "Сори" vs "Сорри") — exactly what a sloppy delete would catch.
        var present = Shipped().Select(p => p.Ru).ToHashSet();

        foreach (var kept in new[] { "Привет", "Всем прив", "Всем привет", "Извини", "Сорри", "Пока", "Го" })
            Assert.Contains(kept, present);
    }

    [Fact]
    public void No_two_cards_are_the_same_card()
    {
        // Duplicates by (ru + en) only: "апа" legitimately appears twice — Cave of Eternity low and
        // high are different dungeons that players type the same way — but two identical cards are
        // just clutter, which is what merging the Squad classes in nearly reintroduced ("танк").
        var dupes = Shipped()
            .GroupBy(p => (p.Ru, p.En))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.En} / {g.Key.Ru}")
            .ToList();

        Assert.Empty(dupes);
    }
}

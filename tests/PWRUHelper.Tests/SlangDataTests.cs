using System.IO;
using System.Linq;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The shipped slang.json, and the mechanism that decides whether an edit to it ever reaches a
/// player. phrases.json and squad.json have carried a "version" since the editable-copy refresh was
/// added; slang.json was simply forgotten, so <see cref="MainWindow.DataVersionOf"/> read it as 0,
/// <see cref="MainWindow.UpgradeEditableIfStale"/> left every existing copy alone, and a new
/// glossary entry only ever reached brand-new installs.
///
/// That matters more since the offline engine shipped: a "full" form is what rewrites player slang
/// into ordinary Russian BEFORE translation, and the small offline model has no chance on raw
/// transliterated slang. Filling the glossary is worthless if the file cannot travel.
/// </summary>
public class SlangDataTests
{
    private static string ShippedJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "slang.json"));

    [Fact]
    public void The_shipped_slang_json_is_versioned_and_parses()
    {
        var json = ShippedJson();

        Assert.True(MainWindow.DataVersionOf(json) >= 1,
            "Bump \"version\" in Data/slang.json whenever you edit it, or existing players keep their old copy.");

        var glossary = SlangGlossary.FromJson(json);
        Assert.False(glossary.IsEmpty);
        Assert.All(glossary.Entries, e => Assert.NotEmpty(e.Keys));
        Assert.All(glossary.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Meaning)));
    }

    [Fact]
    public void Every_full_form_is_russian_and_can_actually_rewrite_something()
    {
        // A "full" earns its place only if some key it belongs to differs from it, and only if it
        // is Russian. Both halves cost a shipped bug otherwise: a Latin-script "full" is handed to
        // a ru->en engine as though it were Russian, and a full equal to EVERY one of its keys is a
        // lookup and a string replace that can never change anything.
        //
        // "every key" and NOT "any key" — the first version of this test got that wrong. An entry
        // may legitimately carry its own long form among its keys so the glossary decodes the long
        // form too: `ара` expands to `Пещеры Вечности`, and `Пещеры Вечности` is searchable as
        // well. Forbidding that rejected 685 sound entries when the full glossary arrived.
        foreach (var entry in SlangGlossary.FromJson(ShippedJson()).Entries.Where(e => e.Full.Length > 0))
        {
            Assert.False(entry.Keys.All(k => string.Equals(k, entry.Full, StringComparison.OrdinalIgnoreCase)),
                $"'{entry.Full}' equals every key it has — that rewrite can never fire; drop the full form.");

            Assert.True(entry.Full.Any(IsCyrillic),
                $"'{entry.Full}' has no Cyrillic — a full form is the RUSSIAN long form, not the English meaning.");
        }
    }

    [Fact]
    public void A_real_lfm_line_is_rewritten_into_ordinary_russian()
    {
        // End-to-end on the SHIPPED glossary, not on a fixture: this is the sentence the whole file
        // exists for, and it is what the translation engine actually receives.
        var g = SlangGlossary.FromJson(ShippedJson());

        var expanded = g.Expand("в апа 5-2 нужен хил и танк, стук");

        Assert.Contains("Пещеры Вечности", expanded);   // апа  -> the dungeon's real Russian name
        Assert.Contains("лекарь", expanded);            // хил  -> healer, the headline case
        Assert.Contains("напишите мне", expanded);      // стук -> "whisper me for an invite"
        Assert.DoesNotContain("апа", expanded);
    }

    [Fact]
    public void An_ordinary_russian_sentence_is_handed_to_the_engine_untouched()
    {
        // The counterpart, and the one that matters more: 1952 entries and 10k keys must not start
        // rewriting normal speech. `в` and `или` are real prepositions, which is why they are
        // context-only. A regression here corrupts every message the app translates.
        var g = SlangGlossary.FromJson(ShippedJson());

        const string ordinary = "он копал яму, погода огонь, я в ярости";
        Assert.Equal(ordinary, g.Expand(ordinary));
    }

    private static bool IsCyrillic(char c) => c >= 'Ѐ' && c <= 'ӿ';
}

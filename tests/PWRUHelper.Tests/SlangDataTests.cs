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
    public void Every_full_form_is_a_different_russian_phrase_not_the_term_or_its_english_meaning()
    {
        // A "full" is only worth having if it is a DIFFERENT, longer Russian phrase. Rewriting a
        // term to itself costs a lookup and buys nothing, and a Latin-script "full" would be handed
        // to a ru->en engine as though it were Russian — the exact mistake the glossary exists to
        // prevent. Cheap to check, and the check is what makes filling the file safe to do quickly.
        foreach (var entry in SlangGlossary.FromJson(ShippedJson()).Entries.Where(e => e.Full.Length > 0))
        {
            Assert.False(entry.Keys.Any(k => string.Equals(k, entry.Full, StringComparison.OrdinalIgnoreCase)),
                $"'{entry.Full}' is the term itself — a full form must be a different Russian expansion.");

            Assert.True(entry.Full.Any(IsCyrillic),
                $"'{entry.Full}' has no Cyrillic — a full form is the RUSSIAN long form, not the English meaning.");
        }
    }

    private static bool IsCyrillic(char c) => c >= 'Ѐ' && c <= 'ӿ';
}

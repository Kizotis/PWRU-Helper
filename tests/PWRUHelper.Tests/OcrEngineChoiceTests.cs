using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The single most damaging OCR bug there is: reading Cyrillic with a LATIN engine.
///
/// A Latin engine does not fail on Russian text — it confidently misreads it as look-alikes.
/// Measured on a real chat screenshot: the ru engine reads «нашел кто поможет?» perfectly, while
/// the fr-FR engine returns "Hawen KTO norsaoxer?". That gibberish then reaches the translator,
/// which faithfully translates gibberish. To the user the app just looks broken, with nothing
/// anywhere saying why.
///
/// OcrService used to fall back to the Windows profile engine whenever the Russian pack was
/// missing — walking straight into it. It must never substitute a language.
/// </summary>
public class OcrEngineChoiceTests
{
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void The_engine_is_never_a_language_we_did_not_ask_for(string requested)
    {
        var ocr = new OcrService(requested);

        // Machine-independent: the pack may or may not be installed here (CI has no Russian one).
        // What must hold either way is that an engine, IF we got one, speaks what we asked for.
        if (ocr.IsAvailable)
            Assert.StartsWith(requested, ocr.ActiveLanguage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unavailable_language_leaves_us_with_no_engine_rather_than_a_latin_stand_in()
    {
        // No OCR pack exists for this tag anywhere, so the only way to come back "available" is by
        // silently substituting the machine's own language — exactly what must not happen.
        var ocr = new OcrService("zz");

        Assert.False(ocr.IsAvailable);
        Assert.Null(ocr.ActiveLanguage);
    }

    [Fact]
    public void An_engine_with_no_language_reads_nothing_instead_of_inventing_it()
    {
        var ocr = new OcrService("zz");
        using var bitmap = new System.Drawing.Bitmap(200, 60);

        Assert.Empty(ocr.ReadLinesAsync(bitmap).GetAwaiter().GetResult());
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PWRUHelper.Services;

/// <summary>
/// Reads text off a bitmap using the Windows built-in OCR engine
/// (Windows.Media.Ocr) — free, on-device, no GPU. Requires the Russian OCR
/// language pack to be installed for best Cyrillic results.
/// </summary>
public class OcrService
{
    // Built on FIRST USE, not in the constructor. Creating a Windows OCR engine costs 26–38 ms
    // (measured in-app, not in isolation — a bare WinRT benchmark says ~100 ms, but most of that is
    // loading the projections, which this app pays anyway). This service is constructed from a
    // MainWindow field initialiser, so that work used to sit in front of the very first paint, on
    // every launch, for every user — including the ones who only ever open the Phrasebook.
    // Worth being honest about the size of this: ~30 ms out of a ~1200 ms warm start. The reason to
    // do it is that an optional subsystem shouldn't be built before the window exists, not the ms.
    // Lazy<T>'s default mode is already thread-safe (ExecutionAndPublication), which matters here:
    // the first touch can come from the live loop's worker thread as easily as from the UI thread.
    // CreateEngine never throws, so no failed-value caching to worry about.
    private readonly Lazy<OcrEngine?> _engine;
    private readonly string _languageTag;

    // Enlarge captures whose longest side is under this many pixels, up to MaxUpscale×.
    // 1600 keeps upscaled images comfortably below the engine's MaxImageDimension while
    // making small chat text big enough to read reliably.
    private const int UpscaleTargetPx = 1600;
    private const double MaxUpscale = 3.0;

    public OcrService(string languageTag = "ru")
    {
        _languageTag = languageTag;
        _engine = new Lazy<OcrEngine?>(() => CreateEngine(languageTag));
    }

    /// <summary>True when an OCR engine is ready (language pack present). The first call to this
    /// (or to <see cref="ActiveLanguage"/> / <see cref="ReadLinesAsync"/>) is what builds the
    /// engine — call it off the UI thread if you don't want to block for ~30 ms.</summary>
    public bool IsAvailable => _engine.Value != null;

    /// <summary>The BCP-47 tag the active engine recognizes, or null if none.</summary>
    public string? ActiveLanguage => _engine.Value?.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// Create the engine for <paramref name="languageTag"/> — and NEVER a substitute for it.
    ///
    /// This used to fall back to <c>TryCreateFromUserProfileLanguages()</c>, i.e. to whatever
    /// Windows is set to. On a French or English machine without the Russian pack, that hands back
    /// a LATIN engine — and a Latin engine does not fail on Cyrillic. It confidently misreads it as
    /// Latin look-alikes: «нашел кто поможет?» comes back as "Hawen KTO norsaoxer?", which then
    /// sails into the translator as gibberish and comes out as gibberish. (Measured, not guessed:
    /// the fr-FR engine reproduces that string exactly from a real chat screenshot; the ru engine
    /// reads the same image perfectly.)
    ///
    /// Silent garbage is far worse than no reading at all: the app can see it has no engine, say so,
    /// and offer the one-click pack install. So the profile engine is only accepted when it happens
    /// to speak the language we asked for anyway (a Russian Windows).
    /// </summary>
    private static OcrEngine? CreateEngine(string languageTag)
    {
        try
        {
            var lang = new Language(languageTag);
            if (OcrEngine.IsLanguageSupported(lang))
            {
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e != null) return e;
            }
        }
        catch { /* fall through */ }

        try
        {
            var profile = OcrEngine.TryCreateFromUserProfileLanguages();
            var tag = profile?.RecognizerLanguage?.LanguageTag;
            if (tag != null && tag.StartsWith(languageTag, StringComparison.OrdinalIgnoreCase))
                return profile;      // the machine's own language IS the one we need
        }
        catch { /* no engine at all */ }

        return null;                 // caller surfaces "the pack isn't installed", and reads nothing
    }

    /// <summary>
    /// Run OCR on a captured region. Returns the recognized lines of text
    /// (empty list if the engine is unavailable or nothing was read).
    /// </summary>
    public async Task<List<string>> ReadLinesAsync(Bitmap bitmap)
    {
        var engine = _engine.Value;                     // builds it on the first read, then cached
        if (engine == null) return new List<string>();

        // Pick a scale factor before recognition:
        //  • DOWNSCALE huge captures — Windows.Media.Ocr rejects images whose width or
        //    height exceeds OcrEngine.MaxImageDimension (~2600 px), RecognizeAsync throws.
        //  • UPSCALE small captures — the engine reads game chat far more accurately when
        //    glyphs are bigger, and a tight "last few chat lines" selection is usually only
        //    a couple hundred pixels tall. Enlarging it (bicubic) is the single biggest
        //    accuracy win for the typical use case.
        Bitmap? scaled = null;
        var source = bitmap;
        int maxDim = (int)OcrEngine.MaxImageDimension;
        int longSide = Math.Max(bitmap.Width, bitmap.Height);

        double factor = 1.0;
        if (maxDim > 0 && longSide > maxDim)
            factor = (double)maxDim / longSide;                       // shrink to fit the engine limit
        else if (longSide > 0 && longSide < UpscaleTargetPx)
            factor = Math.Min((double)UpscaleTargetPx / longSide, MaxUpscale);   // enlarge small captures

        if (factor is > 1.01 or < 0.99)
        {
            int newWidth = Math.Max(1, (int)Math.Round(bitmap.Width * factor));
            int newHeight = Math.Max(1, (int)Math.Round(bitmap.Height * factor));
            scaled = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, 0, 0, newWidth, newHeight);
            }
            source = scaled;
        }

        OcrResult result;
        try
        {
            using var software = await ToSoftwareBitmapAsync(source);
            result = await engine.RecognizeAsync(software);
        }
        finally
        {
            scaled?.Dispose();
        }

        // NEVER trust result.Lines' ORDER — see OcrLayout for the measurement. The engine happily
        // reads text it has decided is two columns column-by-column, which on a chat-shaped image
        // (equal-width nicknames, so every body starts at the same x) hands back every nickname
        // first and every message body after. Project each line onto its geometry and let
        // OcrLayout rebuild the reading order. OcrLine itself carries no rectangle, so the line's
        // box is the union of its words'.
        var boxes = new List<OcrTextBox>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.X < left) left = r.X;
                if (r.Y < top) top = r.Y;
                if (r.X + r.Width > right) right = r.X + r.Width;
                if (r.Y + r.Height > bottom) bottom = r.Y + r.Height;
            }
            // A line with no words has no text either (OcrLine.Text is built from them), so the
            // check above already dropped it — but geometry that never got written would order
            // wildly, so leave nothing to chance.
            if (left > right || top > bottom) { left = top = right = bottom = 0; }

            boxes.Add(new OcrTextBox(text!, left, top, right - left, bottom - top));
        }
        return OcrLayout.OrderIntoLines(boxes);
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        return await decoder.GetSoftwareBitmapAsync();
    }
}

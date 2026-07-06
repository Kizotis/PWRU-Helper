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
    private OcrEngine? _engine;
    private readonly string _languageTag;

    // Enlarge captures whose longest side is under this many pixels, up to MaxUpscale×.
    // 1600 keeps upscaled images comfortably below the engine's MaxImageDimension while
    // making small chat text big enough to read reliably.
    private const int UpscaleTargetPx = 1600;
    private const double MaxUpscale = 3.0;

    public OcrService(string languageTag = "ru")
    {
        _languageTag = languageTag;
        _engine = CreateEngine(languageTag);
    }

    /// <summary>True when an OCR engine is ready (language pack present).</summary>
    public bool IsAvailable => _engine != null;

    /// <summary>The BCP-47 tag the active engine recognizes, or null if none.</summary>
    public string? ActiveLanguage => _engine?.RecognizerLanguage.LanguageTag;

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
        catch { /* fall through to user-profile engine */ }

        // Fall back to whatever the user's Windows language profile supports.
        try { return OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { return null; }
    }

    /// <summary>
    /// Run OCR on a captured region. Returns the recognized lines of text
    /// (empty list if the engine is unavailable or nothing was read).
    /// </summary>
    public async Task<List<string>> ReadLinesAsync(Bitmap bitmap)
    {
        if (_engine == null) return new List<string>();

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
            result = await _engine.RecognizeAsync(software);
        }
        finally
        {
            scaled?.Dispose();
        }

        var lines = new List<string>();
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
        }
        return lines;
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

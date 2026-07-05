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

        // Windows.Media.Ocr rejects images whose width or height exceeds
        // OcrEngine.MaxImageDimension (~2600 px) — RecognizeAsync would throw.
        // Downscale proportionally so the largest side fits within the limit.
        Bitmap? scaled = null;
        var source = bitmap;
        int maxDim = (int)OcrEngine.MaxImageDimension;
        if (maxDim > 0 && (bitmap.Width > maxDim || bitmap.Height > maxDim))
        {
            double factor = (double)maxDim / Math.Max(bitmap.Width, bitmap.Height);
            int newWidth = Math.Max(1, (int)(bitmap.Width * factor));
            int newHeight = Math.Max(1, (int)(bitmap.Height * factor));
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

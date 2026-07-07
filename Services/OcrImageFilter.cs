using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PWRUHelper.Services;

/// <summary>
/// Optional pre-OCR image clean-up to make chat text stand out from a busy, moving 3D game
/// scene behind it. Two modes, both producing a high-contrast greyscale image the OCR engine
/// reads more reliably:
///
///  • <see cref="BoostContrast"/> — universal. Uses each pixel's brightness (max of R/G/B, so
///    bright text of ANY channel colour is kept, not just white) and a gamma curve that crushes
///    the darker background toward black while leaving bright glyphs bright. No configuration.
///
///  • <see cref="KeepColor"/> — per-channel. Keeps only pixels close to a chosen chat colour
///    (white for the target, black for everything else), so a single channel can be isolated.
///
/// Pure and side-effect-free (returns a NEW bitmap; never mutates the input), so it is unit
/// tested. Uses LockBits for speed since it can run on every live frame.
/// </summary>
public static class OcrImageFilter
{
    /// <summary>Brightness-based contrast boost. <paramref name="gamma"/> &gt; 1 darkens the
    /// background more aggressively (2.5 is a good default).</summary>
    public static Bitmap BoostContrast(Bitmap src, double gamma = 2.5)
    {
        // Precompute the gamma curve for all 256 brightness values.
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
            lut[v] = (byte)Math.Clamp(Math.Round(255.0 * Math.Pow(v / 255.0, gamma)), 0, 255);

        return Transform(src, (r, g, b) =>
        {
            byte v = Math.Max(r, Math.Max(g, b));   // brightness = max channel (hue-independent)
            return lut[v];
        });
    }

    /// <summary>Keep only pixels within <paramref name="tolerance"/> (RGB Euclidean distance) of
    /// <paramref name="target"/> — those become white, everything else black.</summary>
    public static Bitmap KeepColor(Bitmap src, Color target, int tolerance)
    {
        long tolSq = (long)tolerance * tolerance;
        return Transform(src, (r, g, b) => WithinTolerance(r, g, b, target, tolSq) ? (byte)255 : (byte)0);
    }

    /// <summary>True when (r,g,b) is within <paramref name="tolerance"/> of the target colour.</summary>
    internal static bool WithinTolerance(byte r, byte g, byte b, Color target, long toleranceSquared)
    {
        long dr = r - target.R, dg = g - target.G, db = b - target.B;
        return dr * dr + dg * dg + db * db <= toleranceSquared;
    }

    // Apply a per-pixel R/G/B → grey mapping and return a fresh 24bpp bitmap. Uses Marshal.Copy
    // (no unsafe code, so no project-wide AllowUnsafeBlocks) — captures are small, so the two
    // buffer copies per frame are cheap.
    private static Bitmap Transform(Bitmap src, Func<byte, byte, byte, byte> map)
    {
        int w = src.Width, h = src.Height;
        var rect = new Rectangle(0, 0, w, h);

        // Read the source as a predictable 4 bytes/pixel regardless of its incoming format.
        using var src32 = src.PixelFormat == PixelFormat.Format32bppArgb
            ? null
            : src.Clone(rect, PixelFormat.Format32bppArgb);
        var source = src32 ?? src;

        var sData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int sStride = sData.Stride;
        var sBuf = new byte[sStride * h];
        Marshal.Copy(sData.Scan0, sBuf, 0, sBuf.Length);
        source.UnlockBits(sData);

        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        int dStride = dData.Stride;
        var dBuf = new byte[dStride * h];
        for (int y = 0; y < h; y++)
        {
            int s = y * sStride;
            int d = y * dStride;
            for (int x = 0; x < w; x++)
            {
                byte b = sBuf[s + x * 4 + 0];
                byte g = sBuf[s + x * 4 + 1];
                byte r = sBuf[s + x * 4 + 2];
                byte grey = map(r, g, b);
                dBuf[d + x * 3 + 0] = grey;
                dBuf[d + x * 3 + 1] = grey;
                dBuf[d + x * 3 + 2] = grey;
            }
        }
        Marshal.Copy(dBuf, 0, dData.Scan0, dBuf.Length);
        dst.UnlockBits(dData);
        return dst;
    }
}

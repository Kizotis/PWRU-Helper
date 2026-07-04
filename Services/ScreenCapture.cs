using System.Drawing;

namespace PWRUHelper.Services;

/// <summary>Grabs a rectangle of the screen as a bitmap (physical pixels).</summary>
public static class ScreenCapture
{
    /// <param name="x">Left, in physical screen pixels.</param>
    /// <param name="y">Top, in physical screen pixels.</param>
    /// <param name="width">Width in physical pixels.</param>
    /// <param name="height">Height in physical pixels.</param>
    public static Bitmap Capture(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }
}
